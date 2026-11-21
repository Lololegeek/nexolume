//! Wall clock shared by every engine.
//!
//! `std::time::SystemTime::now()` is unavailable on `wasm32-unknown-unknown` (it panics), so the
//! browser host registers a provider backed by `Date.now()` before the first command runs.
//! Native builds keep using the operating-system clock.
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::OnceLock;

static PROVIDER: OnceLock<fn() -> f64> = OnceLock::new();
static SEQUENCE: AtomicU64 = AtomicU64::new(0x9E37_79B9);

/// Registers the millisecond Unix clock used when the platform has no system clock.
/// Only the first registration is kept, so the clock cannot change under running documents.
pub fn set_provider(provider: fn() -> f64) {
    let _ = PROVIDER.set(provider);
}

/// Milliseconds since the Unix epoch, with sub-millisecond precision when available.
pub fn unix_millis() -> f64 {
    if let Some(provider) = PROVIDER.get() {
        return provider();
    }
    #[cfg(not(all(target_arch = "wasm32", target_os = "unknown")))]
    {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs_f64() * 1000.0)
            .unwrap_or(0.0)
    }
    #[cfg(all(target_arch = "wasm32", target_os = "unknown"))]
    {
        0.0
    }
}

/// Nanoseconds since the Unix epoch (millisecond resolution in browsers).
pub fn unix_nanos() -> u128 {
    (unix_millis().max(0.0) * 1_000_000.0) as u128
}

/// A value that differs on every call even when the clock is coarse (browsers clamp timers).
pub fn entropy() -> u64 {
    let n = SEQUENCE.fetch_add(0x9E37_79B9_7F4A_7C15, Ordering::Relaxed);
    (unix_nanos() as u64) ^ n.rotate_left(17)
}
