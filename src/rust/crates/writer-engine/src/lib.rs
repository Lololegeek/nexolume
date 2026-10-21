use change_tracking::Delta;
use command_system::Command;
use document_core::{Paragraph, TextStyle};
use serde_json::json;
mod editing;
pub mod rich_text;
pub fn validate(paragraph: &Paragraph) -> Result<(), String> {
    let s = &paragraph.style;
    if paragraph.id.is_empty()
        || paragraph.text.len() > 2_000_000
        || !s.font_size.is_finite()
        || !(6.0..=96.0).contains(&s.font_size)
        || s.font_family.is_empty()
        || s.font_family.len() > 100
        || !["left", "center", "right", "justify"].contains(&s.alignment.as_str())
    {
        return Err("Invalid paragraph text or style".into());
    }
    let color = |v: &str| {
        v.len() == 7 && v.starts_with('#') && v[1..].bytes().all(|b| b.is_ascii_hexdigit())
    };
    if !color(&s.color)
        || (s.highlight != "none" && !color(&s.highlight))
        || (s.shading != "none" && !color(&s.shading))
        || !["none", "box", "bottom", "left"].contains(&s.border.as_str())
        || !["none", "bullet", "number"].contains(&s.list.as_str())
        || s.indent > 8
        || !s.line_spacing.is_finite()
        || !(1.0..=3.0).contains(&s.line_spacing)
        || !s.spacing_after.is_finite()
        || !(0.0..=72.0).contains(&s.spacing_after)
    {
        return Err("Invalid paragraph layout or color".into());
    }
    if let Some(table) = &paragraph.table {
        let columns = table.rows.first().map_or(0, |r| r.len());
        if paragraph.image.is_some()
            || !paragraph.text.is_empty()
            || table.rows.is_empty()
            || table.rows.len() > 200
            || columns == 0
            || columns > 20
            || table.rows.iter().any(|r| r.len() != columns || r.iter().any(|c| c.chars().count() > 5000))
        {
            return Err("Invalid table".into());
        }
    }
    if let Some(image) = &paragraph.image {
        let size = |v: f64| v.is_finite() && (8.0..=6000.0).contains(&v);
        if !["image/png", "image/jpeg", "image/gif"].contains(&image.mime.as_str())
            || image.data.is_empty()
            || image.data.len() > 24_000_000
            || !image
                .data
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'+' | b'/' | b'='))
            || !size(image.width)
            || !size(image.height)
            || image.alt.len() > 1000
            || !paragraph.text.is_empty()
        {
            return Err("Invalid image".into());
        }
    }
    rich_text::validate(paragraph)
}
pub fn apply_one(
    paragraphs: &mut Vec<Paragraph>,
    command: &Command,
) -> Result<(Command, Delta), String> {
    let p = &command.params;
    let id = p["id"].as_str().ok_or("Missing paragraph id")?;
    match command.command.as_str() {
        "SetParagraph" => {
            let index = paragraphs
                .iter()
                .position(|x| x.id == id)
                .ok_or("Unknown paragraph")?;
            let mut next = paragraphs[index].clone();
            if let Some(text) = p.get("text") {
                next.text = text.as_str().ok_or("Invalid text")?.into();
                if !p.get("runs").is_some() {
                    next.runs = rich_text::text_changed(&paragraphs[index], &next.text);
                }
            }
            if let Some(style) = p.get("style") {
                next.style = serde_json::from_value::<TextStyle>(style.clone())
                    .map_err(|e| e.to_string())?;
                let old_style = serde_json::to_value(&paragraphs[index].style).unwrap();
                let new_style = serde_json::to_value(&next.style).unwrap();
                for run in &mut next.runs {
                    let mut value = serde_json::to_value(&run.style).unwrap();
                    for key in [
                        "bold",
                        "italic",
                        "underline",
"strike",
"superscript",
"subscript",
"small_caps",
                        "font_family",
                        "font_size",
                        "color",
                        "highlight",
                    ] {
                        if old_style[key] != new_style[key] {
                            value[key] = new_style[key].clone();
                        }
                    }
                    run.style = serde_json::from_value(value).map_err(|e| e.to_string())?;
                }
            }
            if let Some(runs) = p.get("runs") {
                next.runs = serde_json::from_value(runs.clone()).map_err(|e| e.to_string())?;
            }
            if let Some(table) = p.get("table") {
                next.table = serde_json::from_value(table.clone()).map_err(|e| e.to_string())?;
            }
            if let Some(image) = p.get("image") {
                next.image = serde_json::from_value(image.clone()).map_err(|e| e.to_string())?;
            }
            if !next.style.font_size.is_finite()
                || !(6.0..=96.0).contains(&next.style.font_size)
                || next.style.font_family.is_empty()
                || next.style.font_family.len() > 100
                || !["left", "center", "right", "justify"].contains(&next.style.alignment.as_str())
            {
                return Err("Invalid paragraph style".into());
            }
            validate(&next)?;
            let old = std::mem::replace(&mut paragraphs[index], next.clone());
            let mut previous = serde_json::to_value(&old).unwrap();
            previous["runs"] = json!(old.runs);
            previous["image"] = json!(old.image);
            previous["table"] = json!(old.table);
            Ok((
                Command {
                    command: "SetParagraph".into(),
                    params: previous,
                },
                Delta::new("ParagraphChanged", json!({"index":index,"paragraph":next})),
            ))
        }
        "InsertParagraph" => {
            if paragraphs.iter().any(|x| x.id == id) {
                return Err("Duplicate paragraph id".into());
            }
            let index = p["index"].as_u64().ok_or("Missing paragraph index")? as usize;
            if index > paragraphs.len() {
                return Err("Invalid paragraph index".into());
            }
            let paragraph:Paragraph=serde_json::from_value(json!({"id":id,"text":p["text"].as_str().unwrap_or(""),"runs":p.get("runs").cloned().unwrap_or(json!([])),"image":p.get("image").cloned().unwrap_or(json!(null)),"table":p.get("table").cloned().unwrap_or(json!(null)),"style":p.get("style").cloned().unwrap_or(json!({"bold":false,"italic":false,"underline":false}))})).map_err(|e|e.to_string())?;
            validate(&paragraph)?;
            paragraphs.insert(index, paragraph.clone());
            Ok((
                Command {
                    command: "DeleteParagraph".into(),
                    params: json!({"id":id}),
                },
                Delta::new(
                    "ParagraphInserted",
                    json!({"index":index,"paragraph":paragraph}),
                ),
            ))
        }
        "DeleteParagraph" => {
            if paragraphs.len() <= 1 {
                return Err("A document needs at least one paragraph".into());
            }
            let index = paragraphs
                .iter()
                .position(|x| x.id == id)
                .ok_or("Unknown paragraph")?;
            let old = paragraphs.remove(index);
            let mut params = serde_json::to_value(old).unwrap();
            params["index"] = json!(index);
            Ok((
                Command {
                    command: "InsertParagraph".into(),
                    params,
                },
                Delta::new("ParagraphRemoved", json!({"id":id})),
            ))
        }
        _ => Err("Unsupported Writer command".into()),
    }
}
pub fn apply(
    paragraphs: &mut Vec<Paragraph>,
    command: &Command,
) -> Result<(Command, Vec<Delta>), String> {
    if let Some(commands) = editing::expand(paragraphs, command)? {
        let mut inverses = vec![];
        let mut changes = vec![];
        for c in &commands {
            match apply_one(paragraphs, c) {
                Ok((inverse, delta)) => {
                    inverses.push(inverse);
                    changes.push(delta);
                }
                Err(e) => {
                    for inverse in inverses.iter().rev() {
                        apply_one(paragraphs, inverse)?;
                    }
                    return Err(e);
                }
            }
        }
        inverses.reverse();
        Ok((
            Command {
                command: "Batch".into(),
                params: json!({"commands":inverses}),
            },
            changes,
        ))
    } else {
        apply_one(paragraphs, command).map(|(inverse, delta)| (inverse, vec![delta]))
    }
}
