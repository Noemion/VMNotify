use crate::detector::Detector;
use serde::{Deserialize, Serialize};
use std::time::Instant;

/// Add backends here without changing the wire protocol or Windows client.
pub trait AttentionDetector: Send {
    fn pulse(&mut self, now: Instant) -> bool;
    fn tick(&mut self, now: Instant) -> bool;
}

impl AttentionDetector for Detector {
    fn pulse(&mut self, now: Instant) -> bool {
        Detector::pulse(self, now)
    }
    fn tick(&mut self, now: Instant) -> bool {
        Detector::tick(self, now)
    }
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct App {
    pub id: String,
    pub name: String,
    pub process: String,
    #[serde(default)]
    pub executable: Option<String>,
}

impl App {
    pub fn detector(&self) -> Box<dyn AttentionDetector> {
        Box::new(Detector::default())
    }
    pub fn installed(&self) -> bool {
        self.executable
            .as_ref()
            .is_some_and(|p| std::path::Path::new(p).is_file())
    }
}

#[derive(Clone, Serialize, PartialEq, Eq)]
pub struct AvailableApp {
    pub id: String,
    pub name: String,
    pub running: bool,
    pub adapter: &'static str,
    pub capability: &'static str,
}

pub fn builtin_apps() -> Vec<App> {
    vec![App {
        id: "lanxin".into(),
        name: "蓝信".into(),
        process: "LxGtk3Plugin".into(),
        executable: Some("/opt/apps/cn.lanxin/files/bin/LxMainNew".into()),
    }]
}

pub fn inventory(apps: &[App], running: impl Fn(&str) -> bool) -> Vec<AvailableApp> {
    apps.iter()
        .filter_map(|app| {
            let running = running(&app.id);
            (running || app.installed()).then(|| AvailableApp {
                id: app.id.clone(),
                name: app.name.clone(),
                running,
                adapter: "status-notifier-flash",
                capability: "attention-only",
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn inventory_excludes_unsupported_or_absent_apps() {
        let apps = vec![App {
            id: "test".into(),
            name: "Test".into(),
            process: "test".into(),
            executable: None,
        }];
        assert!(inventory(&apps, |_| false).is_empty());
        let found = inventory(&apps, |id| id == "test");
        assert_eq!(found.len(), 1);
        assert!(found[0].running);
        assert_eq!(found[0].capability, "attention-only");
    }
}
