use super::*;
use serde_json::{json, Value};
fn send(
    e: &mut engine::Engine,
    command: &str,
    document: Option<&str>,
    revision: Option<u64>,
    params: Value,
) -> Value {
    e.execute(serde_json::from_value(json!({"protocol":1,"command":command,"document":document,"expected_revision":revision,"params":params})).unwrap())
}
#[test]
fn writer_history_transactions_and_roundtrip() {
    let mut e = engine::Engine::default();
    let created = send(
        &mut e,
        "CreateDocument",
        None,
        None,
        json!({"kind":"writer","title":"Été"}),
    );
    let id = created["document"].as_str().unwrap();
    let p = created["snapshot"]["content"]["paragraphs"][0]["id"]
        .as_str()
        .unwrap();
    let edit = send(
        &mut e,
        "SetParagraph",
        Some(id),
        Some(0),
        json!({"id":p,"text":"Bonjour 🦀\nDeux lignes","group":"typing"}),
    );
    assert_eq!(edit["ok"], true);
    assert!(edit.get("snapshot").is_none());
    let style = send(
        &mut e,
        "SetParagraph",
        Some(id),
        Some(1),
        json!({"id":p,"style":{"bold":true,"italic":true,"underline":true}}),
    );
    assert_eq!(style["ok"], true);
    assert_eq!(
        send(&mut e, "Undo", Some(id), Some(2), json!({}))["ok"],
        true
    );
    let redo = send(&mut e, "Redo", Some(id), Some(3), json!({}));
    assert_eq!(
        redo["changes"][0]["data"]["paragraph"]["style"]["bold"],
        true
    );
    let stale = send(
        &mut e,
        "SetParagraph",
        Some(id),
        Some(1),
        json!({"id":p,"text":"bad"}),
    );
    assert_eq!(stale["error"]["code"], "RevisionMismatch");
    let failed = send(
        &mut e,
        "Batch",
        Some(id),
        Some(4),
        json!({"commands":[{"command":"SetParagraph","params":{"id":p,"text":"rollback"}},{"command":"DeleteParagraph","params":{"id":"invalid"}}]}),
    );
    assert_eq!(failed["ok"], false);
    let saved = send(&mut e, "SaveDocument", Some(id), Some(4), json!({}));
    let text = saved["json"].as_str().unwrap();
    assert!(text.contains("Bonjour"));
    assert!(!text.contains("rollback"));
    let opened = send(&mut e, "OpenDocument", None, None, json!({"json":text}));
    assert_eq!(
        opened["snapshot"]["content"],
        serde_json::from_str::<Value>(text).unwrap()["content"]
    );
    assert_ne!(opened["document"], created["document"]);
}
#[test]
fn ffi_lifecycle_errors_and_double_free() {
    unsafe {
        for _ in 0..100 {
            let handle = office_create();
            let request=br#"{"protocol":1,"command":"CreateDocument","params":{"kind":"writer","title":"Unicode"}}"#;
            let mut b = Buffer::default();
            assert_eq!(
                office_execute(handle, request.as_ptr(), request.len(), &mut b),
                0
            );
            let reply: Value =
                serde_json::from_slice(std::slice::from_raw_parts(b.ptr, b.len)).unwrap();
            assert_eq!(reply["ok"], true);
            office_free(b.ptr);
            office_free(b.ptr);
            let malformed = b"{";
            assert_ne!(office_execute(handle, malformed.as_ptr(), 1, &mut b), 0);
            assert_ne!(office_execute(handle, std::ptr::null(), 1, &mut b), 0);
            office_destroy(handle);
            office_destroy(handle);
            assert_ne!(
                office_execute(handle, request.as_ptr(), request.len(), &mut b),
                0
            );
        }
    }
}
#[test]
fn rejects_unknown_schema() {
    assert!(file_formats::decode(r#"{"schema":99}"#).is_err());
}
