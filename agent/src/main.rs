mod adapters;
mod detector;
mod discovery;
use discovery::{discover, instance_key, property, variant_string, Instance};

use adapters::{App, AttentionDetector, AvailableApp};
use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    hash::{Hash, Hasher},
    process::Stdio,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tokio::{
    io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader},
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
    icon: detector::IconChanges,
    sample: tokio::sync::mpsc::Sender<()>,
    active: bool,
    status_active: bool,
    flashing: bool,
    _worker: AbortOnDrop,
}

struct AbortOnDrop(tokio::task::JoinHandle<()>);
impl Drop for AbortOnDrop {
    fn drop(&mut self) {
        self.0.abort();
    }
}

struct IconSample {
    key: String,
    fingerprint: Option<u64>,
    status: Option<bool>,
    at: Instant,
}

fn track(
    instance: Instance,
    key: String,
    results: tokio::sync::mpsc::Sender<IconSample>,
) -> Tracked {
    let (sample, mut requests) = tokio::sync::mpsc::channel(1);
    let _ = sample.try_send(());
    let app = instance.app.clone();
    let worker = tokio::spawn(async move {
        while requests.recv().await.is_some() {
            let (pixels, name, status) = tokio::join!(
                async {
                    if instance.pixels {
                        property(&instance.owner, &instance.path, "IconPixmap")
                            .await
                            .ok()
                    } else {
                        None
                    }
                },
                async {
                    if instance.icon_name {
                        property(&instance.owner, &instance.path, "IconName")
                            .await
                            .ok()
                    } else {
                        None
                    }
                },
                async {
                    if instance.status {
                        property(&instance.owner, &instance.path, "Status")
                            .await
                            .ok()
                    } else {
                        None
                    }
                }
            );
            let pixels = pixels.filter(|p| discovery::has_pixels(p));
            let name = name
                .and_then(|n| variant_string(&n))
                .filter(|n| !n.is_empty());
            let fingerprint = if pixels.is_some() || name.is_some() {
                let mut hash = std::collections::hash_map::DefaultHasher::new();
                pixels.hash(&mut hash);
                name.hash(&mut hash);
                Some(hash.finish())
            } else {
                None
            };
            let status = status
                .and_then(|s| variant_string(&s))
                .and_then(|s| match s.as_str() {
                    "NeedsAttention" => Some(true),
                    "Passive" | "Active" => Some(false),
                    _ => None,
                });
            if results
                .send(IconSample {
                    key: key.clone(),
                    fingerprint,
                    status,
                    at: Instant::now(),
                })
                .await
                .is_err()
            {
                break;
            }
        }
    });
    Tracked {
        detector: app.detector(),
        app,
        icon: detector::IconChanges::default(),
        sample,
        active: false,
        status_active: false,
        flashing: false,
        _worker: AbortOnDrop(worker),
    }
}

fn active_apps(tracked: &HashMap<String, Tracked>) -> HashMap<String, App> {
    tracked
        .values()
        .filter(|t| t.active)
        .map(|t| (t.app.id.clone(), t.app.clone()))
        .collect()
}

async fn emit_transitions(
    previous: &mut HashMap<String, App>,
    tracked: &HashMap<String, Tracked>,
) -> Result<()> {
    let current = active_apps(tracked);
    for (id, app) in previous.iter() {
        if !current.contains_key(id) {
            emit("cleared", Some(app)).await?;
        }
    }
    for (id, app) in &current {
        if !previous.contains_key(id) {
            emit("attention", Some(app)).await?;
        }
    }
    *previous = current;
    Ok(())
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
    timeout(Duration::from_secs(3), async {
        let mut child = Command::new("/usr/bin/gdbus")
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
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()?;
        async fn read_limited(
            reader: impl tokio::io::AsyncRead + Unpin,
            limit: u64,
        ) -> Result<Vec<u8>> {
            let mut bytes = Vec::new();
            reader.take(limit + 1).read_to_end(&mut bytes).await?;
            if bytes.len() as u64 > limit {
                bail!("D-Bus response exceeds size limit");
            }
            Ok(bytes)
        }
        let (stdout, stderr) = tokio::try_join!(
            read_limited(child.stdout.take().context("missing stdout")?, 1024 * 1024),
            read_limited(child.stderr.take().context("missing stderr")?, 8192)
        )?;
        if !child.wait().await?.success() {
            bail!("D-Bus call failed: {}", String::from_utf8_lossy(&stderr));
        }
        Ok(String::from_utf8(stdout)?)
    })
    .await
    .context("D-Bus call timed out")?
}

fn quoted(text: &str) -> impl Iterator<Item = &str> {
    text.split('\'').skip(1).step_by(2)
}

fn pulse_sender(line: &str) -> Option<String> {
    let fields: Vec<_> = line.split_whitespace().collect();
    if fields.len() >= 8
        && fields[0] == "sig"
        && fields[6] == "org.kde.StatusNotifierItem"
        && matches!(fields[7], "NewIcon" | "NewStatus" | "NewAttentionIcon")
    {
        Some(instance_key(fields[3], fields[5]))
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
                let custom: Config = serde_json::from_str(&std::fs::read_to_string(
                    args.next().context("--config needs a path")?,
                )?)?;
                let mut custom_ids = std::collections::HashSet::new();
                if custom.apps.iter().any(|a| !custom_ids.insert(&a.id)) {
                    bail!("duplicate app configuration");
                }
                for app in custom.apps {
                    cfg.apps.retain(|a| a.id != app.id);
                    cfg.apps.push(app);
                }
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
            "type='signal',interface='org.kde.StatusNotifierItem'",
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
    let _discovery = AbortOnDrop(discovery);
    let (icon_tx, mut icon_rx) = tokio::sync::mpsc::channel::<IconSample>(64);
    let mut tick = interval(Duration::from_millis(250));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut heartbeat = interval(Duration::from_secs(15));
    let mut discovery_failed = false;
    let mut previous_active = HashMap::new();
    let mut previous_inventory: Option<Vec<AvailableApp>> = None;
    let shutdown = shutdown_signal();
    tokio::pin!(shutdown);
    emit("ready", None).await?;
    loop {
        tokio::select! {
            result = &mut shutdown => { result?; break; },
            line = lines.next_line() => {
                let Some(line) = line? else { bail!("D-Bus monitor exited: {}", child.wait().await?); };
                if let Some(entry) = pulse_sender(&line).and_then(|s| tracked.get_mut(&s)) {
                    // Coalesce redraw requests; sampling must not block heartbeats.
                    let _ = entry.sample.try_send(());
                }
            }
            Some(sample) = icon_rx.recv() => {
                if let Some(entry) = tracked.get_mut(&sample.key) {
                    if entry.detector.tick(sample.at) { entry.flashing = false; }
                    if let Some(fingerprint) = sample.fingerprint {
                        if entry.icon.changed(fingerprint) && entry.detector.pulse(sample.at) { entry.flashing = true; }
                    }
                    entry.status_active = sample.status.unwrap_or(false);
                    entry.active = entry.status_active || entry.flashing;
                }
            }
            changed = discovery_rx.changed() => {
                changed.context("discovery worker stopped")?;
                let result = discovery_rx.borrow_and_update().clone().context("empty discovery result")?;
                match result {
                    Ok(found) => {
                        if discovery_failed { emit("ready", None).await?; }
                        discovery_failed = false;
                        tracked.retain(|key, _| found.contains_key(key));
                        for (key, instance) in found { tracked.entry(key.clone()).or_insert_with(|| track(instance, key, icon_tx.clone())); }
                    }
                    Err(err) => {
                        if !discovery_failed { eprintln!("discovery unavailable: {err:#}"); emit("degraded", None).await?; }
                        discovery_failed = true;
                        tracked.clear();
                    }
                }
                let available = adapters::live_inventory(&cfg.apps, tracked.values().map(|t| &t.app));
                if previous_inventory.as_ref() != Some(&available) {
                    emit_event("apps", None, Some(&available)).await?;
                    previous_inventory = Some(available);
                }
            }
            _ = tick.tick() => {
                for entry in tracked.values_mut() {
                    if entry.detector.tick(Instant::now()) { entry.flashing = false; }
                    entry.active = entry.status_active || entry.flashing;
                    let _ = entry.sample.try_send(());
                }
            }
            _ = heartbeat.tick() => emit("heartbeat", None).await?,
        }
        emit_transitions(&mut previous_active, &tracked).await?;
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
        assert_eq!(pulse_sender("sig\t123\t9\t:1.312\t<none>\t/StatusNotifierItem\torg.kde.StatusNotifierItem\tNewIcon"), Some(":1.312/StatusNotifierItem".into()));
        assert_eq!(pulse_sender("#type timestamp"), None);
        assert_eq!(
            pulse_sender("sig 123 9 :1.312 <none> /StatusNotifierItem other NewIcon"),
            None
        );
    }
}
