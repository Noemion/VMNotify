use std::time::{Duration, Instant};

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
            .is_none_or(|t| now.duration_since(t) > Duration::from_millis(2200))
        {
            self.first = Some(now);
            self.count = 0;
        }
        self.count += 1;
        if self.count >= 4 {
            self.active = true;
            return true;
        }
        false
    }

    pub fn tick(&mut self, now: Instant) -> bool {
        if self
            .last
            .is_some_and(|t| now.duration_since(t) >= Duration::from_secs(3))
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
    fn sustained_flash_emits_once_and_rearms_after_quiet() {
        let start = Instant::now();
        let mut d = Detector::default();
        for i in 0..20 {
            assert_eq!(d.pulse(start + Duration::from_millis(i * 500)), i == 3);
        }
        assert!(!d.tick(start + Duration::from_secs(12)));
        assert!(d.tick(start + Duration::from_secs(13)));
        for i in 0..4 {
            assert_eq!(
                d.pulse(start + Duration::from_millis(14000 + i * 500)),
                i == 3
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
