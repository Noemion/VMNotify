use crate::{
    adapters::{automatic_app, App},
    call, quoted,
};
use anyhow::Result;
use std::collections::HashMap;

#[derive(Clone)]
pub struct Instance {
    pub app: App,
    pub owner: String,
    pub path: String,
    pub pixels: bool,
    pub icon_name: bool,
    pub status: bool,
}

pub fn instance_key(owner: &str, path: &str) -> String {
    format!("{owner}{path}")
}

pub fn address(item: &str) -> Option<(&str, &str)> {
    let (service, path) = item
        .find('/')
        .map_or((item, "/StatusNotifierItem"), |i| item.split_at(i));
    if service.is_empty() || !path.starts_with('/') {
        return None;
    }
    Some((service, path))
}

pub async fn property(owner: &str, path: &str, name: &str) -> Result<String> {
    call(
        owner,
        path,
        "org.freedesktop.DBus.Properties.Get",
        &["org.kde.StatusNotifierItem", name],
    )
    .await
}

// gdbus prints GVariant strings using either quote style and backslash escapes.
pub fn variant_string(text: &str) -> Option<String> {
    let start = text.find(['\'', '"'])?;
    let quote = text.as_bytes()[start] as char;
    let mut out = String::new();
    let mut chars = text[start + 1..].chars();
    while let Some(c) = chars.next() {
        if c == quote {
            return Some(out);
        }
        if c == '\\' {
            out.push(match chars.next()? {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                c => c,
            });
        } else {
            out.push(c);
        }
    }
    None
}

pub fn has_pixels(text: &str) -> bool {
    // Empty a(iiay) is not a usable source of icon changes.
    text.len() <= 1024 * 1024
        && text.contains('[')
        && text.contains("(")
        && text
            .split('[')
            .nth(1)
            .is_some_and(|s| s.trim_start().starts_with('('))
}

async fn probe(item: String, apps: Vec<App>) -> Option<Instance> {
    let (service, path) = address(&item)?;
    let owner = call(
        "org.freedesktop.DBus",
        "/org/freedesktop/DBus",
        "org.freedesktop.DBus.GetNameOwner",
        &[service],
    )
    .await
    .ok()?;
    let owner = variant_string(&owner)?;
    let pid = call(
        "org.freedesktop.DBus",
        "/org/freedesktop/DBus",
        "org.freedesktop.DBus.GetConnectionUnixProcessID",
        &[&owner],
    )
    .await
    .ok()?;
    let pid = pid
        .split(|c: char| !c.is_ascii_digit())
        .rfind(|s| !s.is_empty())?;
    let comm = std::fs::read_to_string(format!("/proc/{pid}/comm")).ok()?;
    let comm = comm.trim();
    let (pixels, icon_name, status, id) = tokio::join!(
        property(&owner, path, "IconPixmap"),
        property(&owner, path, "IconName"),
        property(&owner, path, "Status"),
        property(&owner, path, "Id")
    );
    // Readable-but-empty icons can become available after the first notification.
    let reads_pixels = pixels.is_ok();
    let reads_name = icon_name.is_ok();
    let pixels = pixels.as_ref().is_ok_and(|s| has_pixels(s));
    let icon_name = icon_name
        .ok()
        .and_then(|s| variant_string(&s))
        .is_some_and(|s| !s.is_empty());
    let status = status
        .ok()
        .and_then(|s| variant_string(&s))
        .is_some_and(|s| matches!(s.as_str(), "Passive" | "Active" | "NeedsAttention"));
    if !pixels && !icon_name && !status {
        return None;
    }
    let app = apps
        .iter()
        .find(|a| a.process == comm)
        .cloned()
        .unwrap_or_else(|| {
            let executable = std::fs::read_link(format!("/proc/{pid}/exe"))
                .ok()
                .map(|p| {
                    p.to_string_lossy()
                        .trim_end_matches(" (deleted)")
                        .to_owned()
                })
                .unwrap_or_default();
            let name = id
                .ok()
                .and_then(|s| variant_string(&s))
                .filter(|s| !s.trim().is_empty())
                .unwrap_or_else(|| comm.into());
            automatic_app(&executable, comm, &name)
        });
    Some(Instance {
        app,
        owner,
        path: path.into(),
        pixels: reads_pixels,
        icon_name: reads_name,
        status,
    })
}

pub async fn discover(apps: &[App]) -> Result<HashMap<String, Instance>> {
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
    let mut probes = tokio::task::JoinSet::new();
    for item in quoted(&items).take(64) {
        let apps = apps.to_vec();
        let item = item.to_owned();
        probes.spawn(async move {
            tokio::time::timeout(std::time::Duration::from_secs(10), probe(item, apps))
                .await
                .ok()
                .flatten()
        });
    }
    let mut found = HashMap::new();
    while let Some(result) = probes.join_next().await {
        if let Ok(Some(instance)) = result {
            found.insert(instance_key(&instance.owner, &instance.path), instance);
        }
    }
    Ok(found)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn handles_service_only_and_custom_paths() {
        assert_eq!(address(":1.4"), Some((":1.4", "/StatusNotifierItem")));
        assert_eq!(address(":1.4/custom/icon"), Some((":1.4", "/custom/icon")));
        assert_ne!(instance_key(":1.4", "/one"), instance_key(":1.4", "/two"));
    }
    #[test]
    fn parses_properties_without_accepting_empty_icons() {
        assert_eq!(
            variant_string("(<'NeedsAttention'>,)"),
            Some("NeedsAttention".into())
        );
        assert_eq!(variant_string("(<\"A's app\">,)"), Some("A's app".into()));
        assert!(!has_pixels("(<@a(iiay) []>,)"));
        assert!(has_pixels("(<[(16, 16, [byte 0x00])]>,)"));
    }
    #[test]
    fn identity_survives_restart_and_inventory_deduplicates_instances() {
        let first = automatic_app("/opt/chat/app", "chat", "Chat");
        let second = automatic_app("/opt/chat/app", "chat", "Different title");
        assert_eq!(first.id, second.id);
        assert_ne!(first.id, automatic_app("/opt/other/app", "chat", "Chat").id);
        let list = crate::adapters::live_inventory(&[], [&first, &second].into_iter());
        assert_eq!(list.len(), 1);
        assert!(list[0].running);
        assert!(!list[0].verified);
    }
}
