use serde::{Deserialize, Serialize};
use serde_json::Value;
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Command {
    pub command: String,
    #[serde(default)]
    pub params: Value,
}
#[derive(Deserialize)]
pub struct Request {
    pub protocol: u32,
    pub command: String,
    #[serde(default)]
    pub document: Option<String>,
    #[serde(default)]
    pub expected_revision: Option<u64>,
    #[serde(default)]
    pub params: Value,
}
