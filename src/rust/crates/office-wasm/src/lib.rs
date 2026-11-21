//! Browser host for the document engine.
//!
//! The browser build cannot load `office_ffi.dll`, so this crate exposes the very same
//! [`office_ffi::engine::Engine`] to JavaScript with wasm-bindgen. The protocol is unchanged:
//! one UTF-8 JSON request in, one JSON reply out, so .NET talks to both hosts identically.
//!
//! WebAssembly in browsers runs on one thread here, so engines live in a thread-local map
//! instead of the native mutex. Panics abort the instance (`panic = "abort"` on wasm32): the
//! panic hook records the message so the host can report it, and the JavaScript loader marks
//! the module unusable instead of pretending later calls succeed.
use office_ffi::engine::Engine;
use std::{cell::RefCell, collections::HashMap};

/// Same request ceiling as the native FFI (`office_execute`).
pub const MAX_REQUEST_BYTES: usize = 16 * 1024 * 1024;

thread_local! {
    static ENGINES: RefCell<HashMap<u32, Engine>> = RefCell::new(HashMap::new());
    static NEXT: RefCell<u32> = const { RefCell::new(1) };
    static LAST_PANIC: RefCell<Option<String>> = const { RefCell::new(None) };
}

/// Transport-level failures; engine-level failures stay inside the JSON reply (`ok: false`).
#[derive(Debug, PartialEq, Eq)]
pub enum HostError {
    RequestTooLarge,
    UnknownEngine,
    InvalidRequest(String),
}
impl std::fmt::Display for HostError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::RequestTooLarge => write!(f, "RequestTooLarge: request exceeds 16 MiB"),
            Self::UnknownEngine => write!(f, "UnknownEngine: engine handle is closed or invalid"),
            Self::InvalidRequest(m) => write!(f, "InvalidRequest: {m}"),
        }
    }
}

pub fn create_engine() -> u32 {
    let id = NEXT.with(|n| {
        let mut n = n.borrow_mut();
        let id = *n;
        *n = n.checked_add(1).unwrap_or(1);
        id
    });
    ENGINES.with(|e| e.borrow_mut().insert(id, Engine::default()));
    id
}

pub fn destroy_engine(id: u32) -> bool {
    ENGINES.with(|e| e.borrow_mut().remove(&id).is_some())
}

pub fn execute_request(id: u32, request: &str) -> Result<String, HostError> {
    if request.len() > MAX_REQUEST_BYTES {
        return Err(HostError::RequestTooLarge);
    }
    let request: command_system::Request =
        serde_json::from_str(request).map_err(|e| HostError::InvalidRequest(e.to_string()))?;
    ENGINES.with(|e| {
        let mut engines = e.borrow_mut();
        let engine = engines.get_mut(&id).ok_or(HostError::UnknownEngine)?;
        Ok(serde_json::to_string(&engine.execute(request)).expect("engine replies are valid JSON"))
    })
}

pub fn open_engines() -> usize {
    ENGINES.with(|e| e.borrow().len())
}

fn record_panic(info: &std::panic::PanicHookInfo<'_>) {
    let message = info.to_string();
    LAST_PANIC.with(|p| *p.borrow_mut() = Some(message));
}

#[cfg(target_arch = "wasm32")]
mod bindings {
    use wasm_bindgen::prelude::*;

    #[wasm_bindgen]
    extern "C" {
        #[wasm_bindgen(js_namespace = Date, js_name = now)]
        fn date_now() -> f64;
    }

    /// Installs the browser clock and panic hook. Called by the loader before any command.
    #[wasm_bindgen(start)]
    pub fn start() {
        document_core::clock::set_provider(date_now);
        std::panic::set_hook(Box::new(super::record_panic));
    }

    #[wasm_bindgen]
    pub fn create() -> u32 {
        super::create_engine()
    }

    #[wasm_bindgen]
    pub fn destroy(handle: u32) -> bool {
        super::destroy_engine(handle)
    }

    /// Executes one protocol request. Transport faults are thrown as JavaScript errors.
    #[wasm_bindgen]
    pub fn execute(handle: u32, request: &str) -> Result<String, JsError> {
        super::execute_request(handle, request).map_err(|e| JsError::new(&e.to_string()))
    }

    #[wasm_bindgen(js_name = openEngines)]
    pub fn open_engines() -> usize {
        super::open_engines()
    }

    /// Message of the panic that aborted the instance, if any.
    #[wasm_bindgen(js_name = lastPanic)]
    pub fn last_panic() -> Option<String> {
        super::LAST_PANIC.with(|p| p.borrow().clone())
    }

    #[wasm_bindgen(js_name = protocolVersion)]
    pub fn protocol_version() -> u32 {
        1
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    fn send(id: u32, request: Value) -> Value {
        serde_json::from_str(&execute_request(id, &request.to_string()).unwrap()).unwrap()
    }

    #[test]
    fn lifecycle_and_utf8_roundtrip() {
        let id = create_engine();
        let created = send(id, serde_json::json!({"protocol":1,"command":"CreateDocument","params":{"kind":"writer","title":"Été 🦀"}}));
        assert_eq!(created["ok"], true, "{created}");
        let doc = created["snapshot"]["id"].as_str().unwrap().to_string();
        assert_eq!(created["snapshot"]["title"], "Été 🦀");
        let closed = send(id, serde_json::json!({"protocol":1,"command":"CloseDocument","document":doc,"params":{}}));
        assert_eq!(closed["ok"], true, "{closed}");
        assert!(destroy_engine(id));
        assert!(!destroy_engine(id));
        assert_eq!(execute_request(id, "{}").unwrap_err(), HostError::InvalidRequest("missing field `protocol` at line 1 column 2".into()));
    }

    #[test]
    fn transport_errors_are_reported() {
        let id = create_engine();
        assert!(matches!(execute_request(id, "not json"), Err(HostError::InvalidRequest(_))));
        assert_eq!(execute_request(u32::MAX, r#"{"protocol":1,"command":"Snapshot","params":{}}"#), Err(HostError::UnknownEngine));
        let big = "x".repeat(MAX_REQUEST_BYTES + 1);
        assert_eq!(execute_request(id, &big), Err(HostError::RequestTooLarge));
        let failed = send(id, serde_json::json!({"protocol":1,"command":"Snapshot","document":"missing","params":{}}));
        assert_eq!(failed["ok"], false);
        destroy_engine(id);
    }
}
