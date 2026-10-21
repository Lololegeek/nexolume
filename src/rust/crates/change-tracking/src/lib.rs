use serde::{Deserialize, Serialize};
use serde_json::Value;
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Delta {
    pub kind: String,
    pub data: Value,
}
impl Delta {
    pub fn new(kind: &str, data: Value) -> Self {
        Self {
            kind: kind.into(),
            data,
        }
    }
}
