use serde::Serialize;

#[derive(Clone, Debug, Default, PartialEq, Serialize)]
pub struct Features {
    pub icon_name: Option<String>,
    pub fingerprint: Option<String>,
    pub colorful: Option<bool>,
    pub colors: Vec<String>,
    pub preview: Option<Preview>,
}

#[derive(Clone, Debug, PartialEq, Serialize, serde::Deserialize)]
pub struct Preview {
    pub width: u32,
    pub height: u32,
    pub argb_hex: String,
}

fn thumbnail(w: u32, h: u32, bytes: &[u8]) -> Preview {
    use std::fmt::Write;
    let scale = w.max(h).max(32);
    let width = (w * 32 / scale).max(1);
    let height = (h * 32 / scale).max(1);
    let mut argb_hex = String::with_capacity((width * height * 8) as usize);
    for y in 0..height {
        for x in 0..width {
            let i = (((y * h / height) * w + x * w / width) * 4) as usize;
            for b in &bytes[i..i + 4] {
                let _ = write!(argb_hex, "{b:02x}");
            }
        }
    }
    Preview {
        width,
        height,
        argb_hex,
    }
}

// Parse gdbus' a(iiay) representation, including its optional `byte` annotation.
// IconPixmap is network-order ARGB (StatusNotifierItem specification).
fn pixmap(text: &str) -> Option<(u32, u32, Vec<u8>)> {
    if text.len() > 1024 * 1024 {
        return None;
    }
    let mut best = None;
    for part in text.split('(') {
        let Some((dimensions, rest)) = part.split_once('[') else {
            continue;
        };
        let mut dims = dimensions.split(',').map(str::trim);
        let (Some(w), Some(h)) = (dims.next(), dims.next()) else {
            continue;
        };
        let (Ok(w), Ok(h)) = (w.parse::<u32>(), h.parse::<u32>()) else {
            continue;
        };
        if w == 0 || h == 0 || w > 512 || h > 512 {
            continue;
        }
        let Some((data, _)) = rest.split_once(']') else {
            continue;
        };
        let bytes: Option<Vec<u8>> = data
            .split(',')
            .map(|s| {
                let s = s.trim().strip_prefix("byte ").unwrap_or(s.trim());
                if let Some(hex) = s.strip_prefix("0x") {
                    u8::from_str_radix(hex, 16).ok()
                } else {
                    s.parse().ok()
                }
            })
            .collect();
        let Some(bytes) = bytes else {
            continue;
        };
        if bytes.len() != (w * h * 4) as usize {
            continue;
        }
        if best.as_ref().is_none_or(|(bw, bh, _)| w * h > bw * bh) {
            best = Some((w, h, bytes));
        }
    }
    best
}

pub fn analyze(pixels: Option<&str>, name: Option<&str>) -> Features {
    let mut result = Features {
        icon_name: name
            .filter(|s| !s.is_empty() && s.len() <= 1024 && !s.chars().any(char::is_control))
            .map(str::to_owned),
        ..Features::default()
    };
    let mut hash = 0xcbf29ce484222325u64;
    let mut feed = |bytes: &[u8]| {
        for b in bytes {
            hash = (hash ^ u64::from(*b)).wrapping_mul(0x100000001b3);
        }
    };
    if let Some((w, h, bytes)) = pixels.and_then(pixmap) {
        result.preview = Some(thumbnail(w, h, &bytes));
        feed(b"argb-v1");
        feed(&w.to_be_bytes());
        feed(&h.to_be_bytes());
        let mut visible = 0u32;
        let mut chromatic = 0u32;
        let mut bins = std::collections::BTreeMap::<u16, (u32, u32, u32, u32)>::new();
        for p in bytes.as_chunks::<4>().0 {
            // Ignore RGB noise in fully transparent pixels for recorded-state identity.
            feed(if p[0] == 0 { &[0, 0, 0, 0] } else { p });
            if p[0] < 128 {
                continue;
            }
            visible += 1;
            let (r, g, b) = (p[1], p[2], p[3]);
            let max = r.max(g).max(b);
            let min = r.min(g).min(b);
            if max - min >= 30 && u32::from(max - min) * 100 >= u32::from(max) * 20 {
                chromatic += 1;
            }
            let key = (u16::from(r / 32) << 6) | (u16::from(g / 32) << 3) | u16::from(b / 32);
            let bin = bins.entry(key).or_default();
            bin.0 += 1;
            bin.1 += u32::from(r);
            bin.2 += u32::from(g);
            bin.3 += u32::from(b);
        }
        if visible > 0 {
            result.colorful = Some(chromatic * 100 >= visible * 5);
            // Keep significant colors, not an average that turns multicolored icons gray.
            let mut bins: Vec<_> = bins
                .into_values()
                .filter(|b| b.0 * 100 >= visible * 2)
                .collect();
            bins.sort_by_key(|b| std::cmp::Reverse(b.0));
            result.colors = bins
                .iter()
                .take(16)
                .map(|b| format!("#{:02X}{:02X}{:02X}", b.1 / b.0, b.2 / b.0, b.3 / b.0))
                .collect();
        }
        result.fingerprint = Some(format!("pix-v1-{hash:016x}"));
    } else if let Some(name) = name.filter(|s| !s.is_empty()) {
        feed(b"name-v1");
        feed(name.as_bytes());
        result.fingerprint = Some(format!("name-v1-{hash:016x}"));
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn thumbnails_preserve_argb_and_bound_dimensions() {
        let preview = thumbnail(2, 1, &[255, 255, 0, 0, 128, 0, 255, 0]);
        assert_eq!(preview.argb_hex, "ffff00008000ff00");
        let mut pixels = Vec::new();
        for _ in 0..64 {
            pixels.extend_from_slice(&[255, 255, 0, 0, 0, 0, 0, 255]);
        }
        let small = thumbnail(64, 2, &pixels);
        assert_eq!((small.width, small.height), (32, 1));
        assert_eq!(small.argb_hex, "ffff0000".repeat(32));
        assert!(serde_json::to_string(&small).unwrap().len() < 9000);
    }
    #[test]
    fn colors_and_invalid_data() {
        let gray = analyze(Some("(<[(1, 1, [byte 0xff, 0x80, 0x80, 0x80])],>,)"), None);
        assert_eq!(gray.colorful, Some(false));
        assert_eq!(gray.colors, ["#808080"]);
        let red = analyze(Some("(<[(1, 1, [byte 0xff, 0xff, 0x00, 0x00])]>,)"), None);
        assert_eq!(red.colorful, Some(true));
        assert_eq!(red.colors, ["#FF0000"]);
        assert_ne!(gray.fingerprint, red.fingerprint);
        assert_eq!(
            red.fingerprint,
            analyze(
                Some("(<[(1,1,[255,255,0,0])]>,)"),
                Some("renamed-but-same-pixels")
            )
            .fingerprint
        );
        assert_eq!(
            analyze(Some("(<[(1, 1, [byte 0x00, 0xff, 0, 0])]>,)"), None).colorful,
            None
        );
        assert_eq!(
            analyze(Some("(<[(2, 2, [byte 0xff])]>,)"), None),
            Features::default()
        );
        assert_eq!(analyze(None, Some("connected")).colorful, None);
        assert!(analyze(None, Some("connected")).fingerprint.is_some());
        assert_eq!(analyze(Some("(<@a(iiay) []>,)"), None), Features::default());
    }
}
