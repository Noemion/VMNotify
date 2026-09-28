use std::time::{Duration, Instant};

/// NewIcon can be emitted for identical pixels even when flashing has stopped.
#[derive(Default)]
pub struct IconChanges(Option<u64>);
impl IconChanges {
    pub fn changed(&mut self, fingerprint: u64) -> bool {
        let changed = self.0.is_some_and(|previous| previous != fingerprint);
        self.0 = Some(fingerprint);
        changed
    }
}

/// Detect sustained flashing, not individual icon redraws. State is constant-sized.
#[derive(Default)]
pub struct Detector {
    first: Option<Instant>,
    last: Option<Instant>,
    count: u8,
    active: bool,
}

impl Detector {
    pub fn pulse(&mut self, now: Instant) -> bool {
        if self
            .last
            .is_some_and(|t| now.duration_since(t) < Duration::from_millis(150))
        {
            return false;
        }
        self.last = Some(now);
        if self.active {
            return false;
        }
        if self
            .first
            .is_none_or(|t| now.duration_since(t) > Duration::from_millis(1100))
        {
            self.first = Some(now);
            self.count = 0;
        }
        self.count += 1;
        if self.count >= 2 {
            self.active = true;
            return true;
        }
        false
    }

    pub fn tick(&mut self, now: Instant) -> bool {
        if self
            .last
            .is_some_and(|t| now.duration_since(t) >= Duration::from_millis(1250))
        {
            let was_active = self.active;
            *self = Self::default();
            return was_active;
        }
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn identical_redraws_clear_and_a_second_flash_notifies() {
        let start = Instant::now();
        let mut icon = IconChanges::default();
        let mut detector = Detector::default();
        let mut attention = 0;
        let mut cleared = 0;
        for i in 0..24 {
            let now = start + Duration::from_millis(i * 500);
            // Flash, then keep emitting NewIcon with static pixels, then flash again.
            let pixels = if (5..15).contains(&i) { 0 } else { i % 2 };
            if detector.tick(now) {
                cleared += 1;
            }
            if icon.changed(pixels) && detector.pulse(now) {
                attention += 1;
            }
        }
        assert_eq!(attention, 2);
        assert_eq!(cleared, 1);
    }
    #[test]
    fn static_icon_signals_never_start_attention() {
        let mut icon = IconChanges::default();
        for _ in 0..100 {
            assert!(!icon.changed(123));
        }
    }
    #[test]
    fn sustained_flash_emits_once_and_rearms_after_quiet() {
        let start = Instant::now();
        let mut d = Detector::default();
        for i in 0..20 {
            assert_eq!(d.pulse(start + Duration::from_millis(i * 500)), i == 1);
        }
        assert!(!d.tick(start + Duration::from_millis(10749)));
        assert!(d.tick(start + Duration::from_millis(10750)));
        for i in 0..2 {
            assert_eq!(
                d.pulse(start + Duration::from_millis(14000 + i * 500)),
                i == 1
            );
        }
    }
    #[test]
    fn isolated_redraws_and_bursts_do_not_notify() {
        let start = Instant::now();
        let mut d = Detector::default();
        for i in 0..100 {
            assert!(!d.pulse(start + Duration::from_millis(i)));
        }
        for i in 1..10 {
            assert!(!d.pulse(start + Duration::from_secs(i * 4)));
        }
    }
}
