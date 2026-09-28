mod adapters;
mod detector;

use adapters::{App, AttentionDetector, AvailableApp};
use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    process::Stdio,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::Command,
    time::{interval, timeout},
};

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct Config {
    apps: Vec<App>,
}

struct Tracked {
    app: App,
    detector: Box<dyn AttentionDetector>,
}

#[derive(Serialize)]
struct Event<'a> {
    v: u8,
    kind: &'a str,
    timestamp_ms: u128,
    #[serde(skip_serializing_if = "Option::is_none")]
    app_id: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    app_name: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    apps: Option<&'a [AvailableApp]>,
}

async fn emit(kind: &str, app: Option<&App>) -> Result<()> {
    emit_event(kind, app, None).await
}

async fn emit_event(kind: &str, app: Option<&App>, apps: Option<&[AvailableApp]>) -> Result<()> {
    let event = Event {
        v: 1,
        kind,
        timestamp_ms: SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis(),
        app_id: app.map(|a| a.id.as_str()),
        app_name: app.map(|a| a.name.as_str()),
        apps,
    };
    let mut bytes = serde_json::to_vec(&event)?;
    bytes.push(b'\n');
    // Backpressure is bounded by a deadline; SSH loss cannot grow an event queue.
    timeout(Duration::from_secs(5), async {
        let mut out = tokio::io::stdout();
        out.write_all(&bytes).await?;
        out.flush().await
    })
    .await
    .context("event output stalled")??;
    Ok(())
}

async fn call(dest: &str, path: &str, method: &str, args: &[&str]) -> Result<String> {
    let out = timeout(
        Duration::from_secs(3),
        Command::new("/usr/bin/gdbus")
            .args([
                "call",
                "--session",
                "--dest",
                dest,
                "--object-path",
                path,
                "--method",
                method,
            ])
            .args(args)
            .kill_on_drop(true)
            .output(),
    )
    .await
    .context("D-Bus call timed out")??;
    if !out.status.success() {
        bail!(
            "D-Bus call failed: {}",
            String::from_utf8_lossy(&out.stderr)
        );
    }
    Ok(String::from_utf8(out.stdout)?)
}

fn quoted(text: &str) -> impl Iterator<Item = &str> {
    text.split('\'').skip(1).step_by(2)
}

async fn discover(apps: &[App]) -> Result<HashMap<String, App>> {
    let items = call(
        "org.kde.StatusNotifierWatcher",
        "/StatusNotifierWatcher",
        "org.freedesktop.DBus.Properties.Get",
        &[
            "org.kde.StatusNotifierWatcher",
            "RegisteredStatusNotifierItems",
        ],
    )
    .await?;
    let mut found = HashMap::new();
    for item in quoted(&items).take(64) {
        let Some((service, _)) = item.split_once('/') else {
            continue;
        };
        let pid = call(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus.GetConnectionUnixProcessID",
            &[service],
        )
        .await;
        let Ok(pid) = pid else { continue };
        let Some(pid) = pid
            .split(|c: char| !c.is_ascii_digit())
            .rfind(|s| !s.is_empty())
        else {
            continue;
        };
        let Ok(comm) = std::fs::read_to_string(format!("/proc/{pid}/comm")) else {
            continue;
        };
        let Some(app) = apps.iter().find(|a| a.process == comm.trim()) else {
            continue;
        };
        let owner = call(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus.GetNameOwner",
            &[service],
        )
        .await?;
        if let Some(owner) = quoted(&owner).next() {
            found.insert(owner.to_owned(), app.clone());
        };
    }
    Ok(found)
}

fn pulse_sender(line: &str) -> Option<&str> {
    let fields: Vec<_> = line.split_whitespace().collect();
    if fields.len() >= 8
        && fields[0] == "sig"
        && fields[6] == "org.kde.StatusNotifierItem"
        && fields[7] == "NewIcon"
    {
        Some(fields[3])
    } else {
        None
    }
}

fn config() -> Result<(Config, bool)> {
    let mut cfg = Config {
        apps: adapters::builtin_apps(),
    };
    let mut demo = false;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--config" => {
                cfg = serde_json::from_str(&std::fs::read_to_string(
                    args.next().context("--config needs a path")?,
                )?)?
            }
            "--demo" => demo = true,
            "--version" => {
                println!("vmnotify-agent {}", env!("CARGO_PKG_VERSION"));
                std::process::exit(0);
            }
            "--help" => {
                println!("vmnotify-agent [--config FILE] [--demo]\nRun as the logged-in Linux desktop user. Emits JSON Lines on stdout.");
                std::process::exit(0);
            }
            _ => bail!("unknown argument: {arg}"),
        }
    }
    if cfg.apps.is_empty() || cfg.apps.len() > 64 {
        bail!("configure 1..64 apps");
    }
    let mut ids = std::collections::HashSet::new();
    for app in &cfg.apps {
        if app.id.is_empty()
            || app.id.len() > 64
            || !app
                .id
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"-_.".contains(&b))
            || app.name.is_empty()
            || app.name.chars().count() > 64
            || app.name.chars().any(char::is_control)
            || app.process.is_empty()
            || app.process.len() > 15
            || !ids.insert(&app.id)
        {
            bail!("invalid or duplicate app configuration");
        }
    }
    Ok((cfg, demo))
}

async fn run(cfg: Config, demo: bool) -> Result<()> {
    if demo {
        emit("ready", None).await?;
        let inventory = adapters::inventory(&cfg.apps, |_| true);
        emit_event("apps", None, Some(&inventory)).await?;
        emit("attention", cfg.apps.first()).await?;
        emit("cleared", cfg.apps.first()).await?;
        return Ok(());
    }
    #[cfg(not(target_os = "linux"))]
    bail!("live monitoring requires Linux; use --demo for protocol testing");
    #[cfg(target_os = "linux")]
    monitor(cfg).await
}

#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
async fn shutdown_signal() -> Result<()> {
    #[cfg(target_os = "linux")]
    {
        let mut terminate =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
        tokio::select! {
            result = tokio::signal::ctrl_c() => result?,
            _ = terminate.recv() => {},
        }
    }
    #[cfg(not(target_os = "linux"))]
    tokio::signal::ctrl_c().await?;
    Ok(())
}

#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
async fn monitor(cfg: Config) -> Result<()> {
    let mut child = Command::new("/usr/bin/dbus-monitor")
        .args([
            "--session",
            "--profile",
            "type='signal',interface='org.kde.StatusNotifierItem',member='NewIcon'",
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .kill_on_drop(true)
        .spawn()?;
    let mut lines = BufReader::new(child.stdout.take().context("missing monitor stdout")?).lines();
    let mut tracked: HashMap<String, Tracked> = HashMap::new();
    // Discovery performs IPC independently: slow applications cannot block event reads or heartbeats.
    let (discovery_tx, mut discovery_rx) = tokio::sync::watch::channel(None);
    let discovery_cfg = cfg.clone();
    let discovery = tokio::spawn(async move {
        loop {
            let result = match timeout(Duration::from_secs(12), discover(&discovery_cfg.apps)).await
            {
                Ok(result) => result.map_err(|e| format!("{e:#}")),
                Err(_) => Err("application discovery timed out".to_owned()),
            };
            if discovery_tx.send(Some(result)).is_err() {
                break;
            }
            tokio::time::sleep(Duration::from_secs(5)).await;
        }
    });
    struct AbortOnDrop(tokio::task::JoinHandle<()>);
    impl Drop for AbortOnDrop {
        fn drop(&mut self) {
            self.0.abort();
        }
    }
    let _discovery = AbortOnDrop(discovery);
    let mut tick = interval(Duration::from_millis(250));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut heartbeat = interval(Duration::from_secs(15));
    let mut discovery_failed = false;
    let mut previous_inventory: Option<Vec<AvailableApp>> = None;
    let shutdown = shutdown_signal();
    tokio::pin!(shutdown);
    emit("ready", None).await?;
    loop {
        tokio::select! {
            result = &mut shutdown => { result?; break; },
            line = lines.next_line() => {
                let Some(line) = line? else { bail!("D-Bus monitor exited: {}", child.wait().await?); };
                if let Some(entry) = pulse_sender(&line).and_then(|s| tracked.get_mut(s)) {
                    if entry.detector.pulse(Instant::now()) { emit("attention", Some(&entry.app)).await?; }
                }
            }
            changed = discovery_rx.changed() => {
                changed.context("discovery worker stopped")?;
                let result = discovery_rx.borrow_and_update().clone().context("empty discovery result")?;
                match result {
                    Ok(found) => {
                        if discovery_failed { emit("ready", None).await?; }
                        discovery_failed = false;
                        for (owner, entry) in &tracked {
                            if !found.contains_key(owner) { emit("cleared", Some(&entry.app)).await?; }
                        }
                        tracked.retain(|owner, _| found.contains_key(owner));
                        for (owner, app) in found { tracked.entry(owner).or_insert_with(|| Tracked { detector: app.detector(), app }); }
                    }
                    Err(err) => {
                        if !discovery_failed { eprintln!("discovery unavailable: {err:#}"); emit("degraded", None).await?; }
                        discovery_failed = true;
                        tracked.clear();
                    }
                }
                let available = adapters::inventory(&cfg.apps, |id| tracked.values().any(|t| t.app.id == id));
                if previous_inventory.as_ref() != Some(&available) {
                    emit_event("apps", None, Some(&available)).await?;
                    previous_inventory = Some(available);
                }
            }
            _ = tick.tick() => {
                for entry in tracked.values_mut() {
                    if entry.detector.tick(Instant::now()) { emit("cleared", Some(&entry.app)).await?; }
                }
            }
            _ = heartbeat.tick() => emit("heartbeat", None).await?,
        }
    }
    child.kill().await.ok();
    child.wait().await.ok();
    Ok(())
}

#[tokio::main]
async fn main() -> Result<()> {
    let (cfg, demo) = config()?;
    #[cfg(target_os = "linux")]
    if std::env::var_os("DBUS_SESSION_BUS_ADDRESS").is_none() {
        use std::os::unix::fs::MetadataExt;
        let uid = std::fs::metadata("/proc/self")?.uid();
        std::env::set_var(
            "DBUS_SESSION_BUS_ADDRESS",
            format!("unix:path=/run/user/{uid}/bus"),
        );
    }
    run(cfg, demo).await
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parse_only_icon_signals() {
        assert_eq!(pulse_sender("sig\t123\t9\t:1.312\t<none>\t/StatusNotifierItem\torg.kde.StatusNotifierItem\tNewIcon"), Some(":1.312"));
        assert_eq!(pulse_sender("#type timestamp"), None);
        assert_eq!(
            pulse_sender("sig 123 9 :1.312 <none> /StatusNotifierItem other NewIcon"),
            None
        );
    }
}
