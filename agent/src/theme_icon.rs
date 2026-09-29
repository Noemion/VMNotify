use crate::icon::Preview;
use std::{
    process::Stdio,
    time::{Duration, Instant},
};
use tokio::{io::AsyncReadExt, process::Command, time::timeout};

#[derive(Default)]
pub struct Cache {
    key: String,
    value: Option<Preview>,
    sampled: Option<Instant>,
}

impl Cache {
    pub async fn read(&mut self, name: &str, theme_path: &str) -> Option<Preview> {
        let key = format!("{name}\n{theme_path}");
        if self.key == key
            && self
                .sampled
                .is_some_and(|t| t.elapsed() < Duration::from_secs(60))
        {
            return self.value.clone();
        }
        self.key = key;
        self.sampled = Some(Instant::now());
        self.value = resolve(name, theme_path).await;
        self.value.clone()
    }
}

async fn resolve(name: &str, theme_path: &str) -> Option<Preview> {
    if name.is_empty() || name.len() > 1024 || theme_path.len() > 4096 {
        return None;
    }
    timeout(Duration::from_secs(2), async {
        let mut child = Command::new("python3")
            .args(["-c", include_str!("theme_icon.py"), name, theme_path])
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .kill_on_drop(true)
            .spawn()
            .ok()?;
        let mut bytes = Vec::new();
        child
            .stdout
            .take()?
            .take(9001)
            .read_to_end(&mut bytes)
            .await
            .ok()?;
        if bytes.len() > 9000 || !child.wait().await.ok()?.success() {
            return None;
        }
        let image: Preview = serde_json::from_slice(&bytes).ok()?;
        if image.width == 0
            || image.height == 0
            || image.width > 32
            || image.height > 32
            || image.argb_hex.len() != (image.width * image.height * 8) as usize
            || !image.argb_hex.bytes().all(|c| c.is_ascii_hexdigit())
        {
            return None;
        }
        Some(image)
    })
    .await
    .ok()
    .flatten()
}
