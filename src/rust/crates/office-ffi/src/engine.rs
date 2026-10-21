use change_tracking::Delta;
use command_system::{Command, Request};
use document_core::{Content, Document};
use history_engine::History;
use serde_json::{json, Value};
use std::collections::HashMap;
fn range_ok(range: &str) -> bool {
    let cell = |s: &str| {
        let letters = s.chars().take_while(|c| c.is_ascii_uppercase()).count();
        letters > 0 && letters <= 3 && s[letters..].parse::<u32>().is_ok_and(|n| n >= 1)
    };
    match range.split_once(':') {
        Some((a, b)) => cell(a) && cell(b),
        None => cell(range),
    }
}
fn validate_sheet_meta(m: &document_core::SheetMeta) -> Result<(), String> {
    let sizes_ok = |s: &std::collections::BTreeMap<String, f64>, max: f64| {
        s.len() <= 2000
            && s.iter()
                .all(|(k, v)| k.parse::<u32>().is_ok() && v.is_finite() && (8.0..=max).contains(v))
    };
    if !sizes_ok(&m.col_widths, 800.0)
        || !sizes_ok(&m.row_heights, 600.0)
        || m.frozen_rows > 100
        || m.frozen_cols > 50
        || m.merges.len() > 1000
        || !m.merges.iter().all(|r| r.contains(':') && range_ok(r))
        || m.rules.len() > 100
        || !m.rules.iter().all(|r| {
            range_ok(&r.range)
                && matches!(r.op.as_str(), "gt" | "lt" | "eq" | "between" | "contains" | "neg" | "empty")
                && r.value.len() < 200
                && r.value2.len() < 200
        })
        || m.charts.len() > 20
        || !m.charts.iter().all(|c| {
            matches!(c.kind.as_str(), "bar" | "line" | "pie") && range_ok(&c.range) && c.title.len() < 200
        })
    {
        return Err("Invalid sheet layout".into());
    }
    Ok(())
}
#[derive(Default)]
pub struct Engine {
    documents: HashMap<String, Session>,
}
struct Session {
    document: Document,
    history: History,
}
impl Session {
    fn apply(&mut self, c: &Command) -> Result<(Command, Vec<Delta>), String> {
        if c.command == "Batch" {
            let commands: Vec<Command> =
                serde_json::from_value(c.params["commands"].clone()).map_err(|e| e.to_string())?;
            if commands.len() > 1000 || commands.iter().any(|c| c.command == "Batch") {
                return Err("Nested or excessive transaction".into());
            }
            let (mut inverse, deltas) = self.batch(&commands)?;
            inverse.reverse();
            return Ok((
                Command {
                    command: "Batch".into(),
                    params: json!({"commands":inverse}),
                },
                deltas,
            ));
        }
        if c.command == "SetPageLayout" {
            if !matches!(self.document.content, Content::Writer { .. }) {
                return Err("Page layout requires Writer".into());
            }
            let next: Option<document_core::PageLayout> =
                serde_json::from_value(c.params["layout"].clone()).map_err(|e| e.to_string())?;
            if let Some(layout) = &next {
                layout.validate()?;
            }
            let old = std::mem::replace(&mut self.document.page_layout, next.clone());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"layout":old}),
                },
                vec![Delta::new("PageLayoutChanged", json!({"layout":next}))],
            ));
        }
        if c.command == "SetSheetMeta" {
            if !matches!(self.document.content, Content::Sheets { .. }) {
                return Err("Sheet layout requires Sheets".into());
            }
            let next: Option<document_core::SheetMeta> =
                serde_json::from_value(c.params["meta"].clone()).map_err(|e| e.to_string())?;
            if let Some(meta) = &next {
                validate_sheet_meta(meta)?;
            }
            let old = std::mem::replace(&mut self.document.sheet_meta, next.clone());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"meta":old}),
                },
                vec![Delta::new("SheetMetaChanged", json!({"meta":next}))],
            ));
        }
        if c.command == "SetFootnotes" {
            let Content::Writer { paragraphs } = &self.document.content else {
                return Err("Footnotes require Writer".into());
            };
            let next: Vec<document_core::Footnote> =
                serde_json::from_value(c.params["footnotes"].clone()).map_err(|e| e.to_string())?;
            let mut seen = std::collections::HashSet::new();
            if next.len() > 500
                || !next.iter().all(|n| {
                    !n.id.is_empty()
                        && n.id.len() <= 64
                        && seen.insert(n.id.clone())
                        && n.text.chars().count() <= 5000
                        && paragraphs.iter().any(|p| p.id == n.paragraph)
                })
            {
                return Err("Invalid footnotes".into());
            }
            let old = std::mem::replace(&mut self.document.footnotes, next.clone());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"footnotes":old}),
                },
                vec![Delta::new("FootnotesChanged", json!({"footnotes":next}))],
            ));
        }
        if c.command == "SetComments" {
            let Content::Writer { paragraphs } = &self.document.content else {
                return Err("Comments require Writer".into());
            };
            let next: Vec<document_core::Comment> =
                serde_json::from_value(c.params["comments"].clone()).map_err(|e| e.to_string())?;
            let mut seen = std::collections::HashSet::new();
            if next.len() > 2000
                || !next.iter().all(|n| {
                    !n.id.is_empty()
                        && n.id.len() <= 64
                        && seen.insert(n.id.clone())
                        && n.author.chars().count() <= 80
                        && n.text.chars().count() <= 5000
                        && paragraphs.iter().any(|p| p.id == n.paragraph)
                })
            {
                return Err("Invalid comments".into());
            }
            let old = std::mem::replace(&mut self.document.comments, next.clone());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"comments":old}),
                },
                vec![Delta::new("CommentsChanged", json!({"comments":next}))],
            ));
        }
        if c.command == "SetRevisions" {
            let Content::Writer { paragraphs } = &self.document.content else {
                return Err("Revisions require Writer".into());
            };
            let next: Vec<document_core::Revision> =
                serde_json::from_value(c.params["revisions"].clone()).map_err(|e| e.to_string())?;
            let mut seen = std::collections::HashSet::new();
            if next.len() > 2000
                || !next.iter().all(|n| {
                    !n.id.is_empty()
                        && n.id.len() <= 64
                        && seen.insert(n.id.clone())
                        && n.author.chars().count() <= 80
                        && matches!(n.kind.as_str(), "edit" | "insert") && n.old.chars().count() <= 200000
                        && paragraphs.iter().any(|p| p.id == n.paragraph)
                })
            {
                return Err("Invalid revisions".into());
            }
            let old = std::mem::replace(&mut self.document.revisions, next.clone());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"revisions":old}),
                },
                vec![Delta::new("RevisionsChanged", json!({"revisions":next}))],
            ));
        }
        if c.command == "RenameDocument" {
            let title = c.params["title"].as_str().ok_or("Missing title")?;
            if title.trim().is_empty() || title.len() > 4096 {
                return Err("Invalid title".into());
            }
            let old = std::mem::replace(&mut self.document.title, title.into());
            return Ok((
                Command {
                    command: c.command.clone(),
                    params: json!({"title":old}),
                },
                vec![Delta::new("DocumentRenamed", json!({"title":title}))],
            ));
        }
        match &mut self.document.content {
            Content::Writer { paragraphs } => writer_engine::apply(paragraphs, c),
            Content::Sheets { cells } => sheets_engine::apply(cells, c),
            Content::Slides { slides } => {
                slides_engine::apply(slides, c).map(|(i, d)| (i, vec![d]))
            }
        }
    }
    fn batch(&mut self, commands: &[Command]) -> Result<(Vec<Command>, Vec<Delta>), String> {
        let mut inverse = vec![];
        let mut deltas = vec![];
        for c in commands {
            match self.apply(c) {
                Ok((i, d)) => {
                    inverse.push(i);
                    deltas.extend(d);
                }
                Err(e) => {
                    for i in inverse.iter().rev() {
                        self.apply(i)?;
                    }
                    return Err(e);
                }
            }
        }
        Ok((inverse, deltas))
    }
}
impl Engine {
    pub fn execute(&mut self, request: Request) -> Value {
        match self.run(request) {
            Ok(v) => v,
            Err((code, message, revision)) => {
                json!({"protocol":1,"ok":false,"error":{"code":code,"message":message},"revision":revision,"changes":[]})
            }
        }
    }
    fn run(&mut self, r: Request) -> Result<Value, (String, String, u64)> {
        let fail = |m: &str| ("InvalidCommand".into(), m.to_string(), 0);
        if r.protocol != 1 {
            return Err(fail("Unsupported protocol"));
        }
        if r.command == "Ping" {
            return Ok(json!({"protocol":1,"ok":true,"engine":"Nexolume Rust"}));
        }
        if r.command == "CreateDocument" || r.command == "OpenDocument" {
            let mut doc = if r.command == "CreateDocument" {
                Document::new(
                    r.params["kind"].as_str().unwrap_or(""),
                    r.params["title"].as_str().unwrap_or("Sans titre"),
                )
                .map_err(|e| fail(&e))?
            } else {
                file_formats::decode(
                    r.params["json"]
                        .as_str()
                        .ok_or_else(|| fail("Missing file data"))?,
                )
                .map_err(|e| fail(&e))?
            };
            // Each open gets its own document handle; stable element identifiers are preserved.
            if self.documents.contains_key(&doc.id) {
                doc.id = document_core::id();
            }
            let result = json!({"protocol":1,"ok":true,"document":doc.id,"revision":doc.revision,"snapshot":doc,"changes":[]});
            self.documents.insert(
                doc.id.clone(),
                Session {
                    document: doc,
                    history: History::default(),
                },
            );
            return Ok(result);
        }
        if r.command == "ImportOfficeDocument" {
            let format = r.params["format"]
                .as_str()
                .ok_or_else(|| fail("Missing office format"))?;
            let data = r.params["data"]
                .as_str()
                .ok_or_else(|| fail("Missing office file data"))?;
            let title = r.params["title"].as_str().unwrap_or("Document importé");
            let doc =
                file_formats::import_office_base64(format, data, title).map_err(|e| fail(&e))?;
            let result = json!({"protocol":1,"ok":true,"document":doc.id,"revision":doc.revision,"snapshot":doc,"changes":[]});
            self.documents.insert(
                doc.id.clone(),
                Session {
                    document: doc,
                    history: History::default(),
                },
            );
            return Ok(result);
        }
        let id = r.document.ok_or_else(|| fail("Missing document handle"))?;
        if r.command == "CloseDocument" {
            if self.documents.remove(&id).is_none() {
                return Err(fail("Unknown document handle"));
            }
            return Ok(json!({"protocol":1,"ok":true,"changes":[]}));
        }
        let session = self
            .documents
            .get_mut(&id)
            .ok_or_else(|| fail("Unknown document handle"))?;
        let revision = session.document.revision;
        let err = |e: String| ("InvalidCommand".into(), e, revision);
        if r.command == "Snapshot" {
            return Ok(
                json!({"protocol":1,"ok":true,"document":id,"revision":revision,"snapshot":session.document,"changes":[],"canUndo":!session.history.undo.is_empty(),"canRedo":!session.history.redo.is_empty()}),
            );
        }
        if revision >= i64::MAX as u64 {
            return Err(err("Document revision exhausted".into()));
        }
        if r.expected_revision != Some(revision) {
            return Err((
                "RevisionMismatch".into(),
                "Resynchronize before editing".into(),
                revision,
            ));
        }
        if r.command == "SaveDocument" {
            if let Some(last) = session.history.undo.last_mut() {
                last.group = None;
            }
            return Ok(
                json!({"protocol":1,"ok":true,"revision":revision,"json":file_formats::encode(&session.document).map_err(err)?,"changes":[]}),
            );
        }
        if r.command == "ExportOfficeDocument" {
            let format = r.params["format"]
                .as_str()
                .ok_or_else(|| err("Missing office format".into()))?;
            return Ok(
                json!({"protocol":1,"ok":true,"revision":revision,"data":file_formats::export_office_base64(&session.document, format).map_err(err)?,"changes":[]}),
            );
        }
        let changes = if r.command == "Undo" || r.command == "Redo" {
            let mut entry = if r.command == "Undo" {
                session.history.undo.pop()
            } else {
                session.history.redo.pop()
            }
            .ok_or_else(|| err("History is empty".into()))?;
            let commands = if r.command == "Undo" {
                &entry.inverse
            } else {
                &entry.forward
            };
            match session.batch(commands) {
                Ok((mut inverse, d)) => {
                    inverse.reverse();
                    entry.group = None;
                    if r.command == "Undo" {
                        entry.forward = inverse;
                        session.history.redo.push(entry)
                    } else {
                        entry.inverse = inverse;
                        session.history.undo.push(entry)
                    };
                    d
                }
                Err(e) => {
                    if r.command == "Undo" {
                        session.history.undo.push(entry)
                    } else {
                        session.history.redo.push(entry)
                    };
                    return Err(err(e));
                }
            }
        } else {
            let commands = if r.command == "Batch" {
                serde_json::from_value::<Vec<Command>>(r.params["commands"].clone())
                    .map_err(|e| err(e.to_string()))?
            } else {
                vec![Command {
                    command: r.command,
                    params: r.params.clone(),
                }]
            };
            if commands.is_empty() || commands.len() > 1000 {
                return Err(err("Invalid transaction size".into()));
            }
            let (inverse, d) = session.batch(&commands).map_err(err)?;
            session.history.record(
                commands,
                inverse,
                r.params["group"].as_str().map(str::to_string),
            );
            d
        };
        session.document.revision += 1;
        Ok(
            json!({"protocol":1,"ok":true,"document":id,"revision":session.document.revision,"changes":changes,"canUndo":!session.history.undo.is_empty(),"canRedo":!session.history.redo.is_empty()}),
        )
    }
}
