mod engine;
#[cfg(test)]
mod tests;
use std::{
    collections::HashMap,
    panic::{catch_unwind, AssertUnwindSafe},
    sync::{Mutex, OnceLock},
};
#[repr(C)]
#[derive(Default)]
pub struct Buffer {
    pub ptr: *const u8,
    pub len: usize,
}
static ENGINES: OnceLock<Mutex<HashMap<u64, engine::Engine>>> = OnceLock::new();
static BUFFERS: OnceLock<Mutex<HashMap<usize, Box<[u8]>>>> = OnceLock::new();
static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
fn engines() -> &'static Mutex<HashMap<u64, engine::Engine>> {
    ENGINES.get_or_init(Default::default)
}
#[no_mangle]
pub extern "C" fn office_create() -> u64 {
    catch_unwind(|| {
        let id = NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        match engines().lock() {
            Ok(mut e) => {
                e.insert(id, engine::Engine::default());
                id
            }
            Err(_) => 0,
        }
    })
    .unwrap_or(0)
}
#[no_mangle]
pub extern "C" fn office_destroy(id: u64) {
    if let Ok(mut e) = engines().lock() {
        e.remove(&id);
    }
}
#[no_mangle]
pub extern "C" fn office_free(ptr: *const u8) {
    if let Ok(mut b) = BUFFERS.get_or_init(Default::default).lock() {
        b.remove(&(ptr as usize));
    }
}
#[no_mangle]
pub unsafe extern "C" fn office_execute(
    id: u64,
    input: *const u8,
    len: usize,
    out: *mut Buffer,
) -> i32 {
    if out.is_null() || input.is_null() || len > 16 * 1024 * 1024 {
        return 1;
    }
    *out = Buffer::default();
    match catch_unwind(AssertUnwindSafe(|| {
        let mut engines = engines().lock().map_err(|_| ())?;
        let engine = engines.get_mut(&id).ok_or(())?;
        let request: command_system::Request =
            serde_json::from_slice(std::slice::from_raw_parts(input, len)).map_err(|_| ())?;
        let bytes = serde_json::to_vec(&engine.execute(request))
            .unwrap()
            .into_boxed_slice();
        let buffer = Buffer {
            ptr: bytes.as_ptr(),
            len: bytes.len(),
        };
        BUFFERS
            .get_or_init(Default::default)
            .lock()
            .map_err(|_| ())?
            .insert(buffer.ptr as usize, bytes);
        *out = buffer;
        Ok(())
    })) {
        Ok(Ok(())) => 0,
        Ok(Err(())) => 2,
        Err(_) => 3,
    }
}
