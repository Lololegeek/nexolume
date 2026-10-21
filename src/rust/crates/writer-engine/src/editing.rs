use command_system::Command;
use document_core::Paragraph;
use serde_json::{json, Value};
fn command(name: &str, params: Value) -> Command {
    Command {
        command: name.into(),
        params,
    }
}
fn utf16_boundary(text: &str, offset: u64) -> Result<usize, String> {
    let mut n = 0u64;
    for (index, c) in text.char_indices() {
        if n == offset {
            return Ok(index);
        }
        n += c.len_utf16() as u64;
    }
    if n == offset {
        Ok(text.len())
    } else {
        Err("Invalid UTF-16 selection boundary".into())
    }
}
pub fn expand(paragraphs: &[Paragraph], c: &Command) -> Result<Option<Vec<Command>>, String> {
    let p = &c.params;
    if c.command == "ReplaceRanges" {
        let id = p["id"].as_str().ok_or("Missing paragraph id")?;
        let paragraph = paragraphs
            .iter()
            .find(|p| p.id == id)
            .ok_or("Unknown paragraph")?;
        let edits = p["edits"].as_array().ok_or("Missing text edits")?;
        if edits.is_empty() || edits.len() > 100000 {
            return Err("Invalid text edit count".into());
        }
        let ranges = edits
            .iter()
            .map(|e| {
                Ok((
                    e["start"].as_u64().ok_or("Missing edit start")? as usize,
                    e["end"].as_u64().ok_or("Missing edit end")? as usize,
                    e["text"].as_str().ok_or("Missing edit text")?,
                ))
            })
            .collect::<Result<Vec<_>, String>>()?;
        let next = crate::rich_text::replace_many(paragraph, ranges)?;
        return Ok(Some(vec![command(
            "SetParagraph",
            json!({"id":id,"text":next.text,"runs":next.runs}),
        )]));
    }
    if c.command == "FormatText" || c.command == "ReplaceText" {
        let id = p["id"].as_str().ok_or("Missing paragraph id")?;
        let paragraph = paragraphs
            .iter()
            .find(|p| p.id == id)
            .ok_or("Unknown paragraph")?;
        let start = p["start"].as_u64().ok_or("Missing selection start")? as usize;
        let end = p["end"].as_u64().ok_or("Missing selection end")? as usize;
        if start > end {
            return Err("Invalid text range".into());
        }
        let params = if c.command == "FormatText" {
            json!({"id":id,"runs":crate::rich_text::format(paragraph,start,end,&p["style"])?})
        } else {
            let inserted = p["text"].as_str().ok_or("Missing inserted text")?;
            let next = crate::rich_text::replace(paragraph, start, end, inserted)?;
            json!({"id":id,"text":next.text,"runs":next.runs})
        };
        return Ok(Some(vec![command("SetParagraph", params)]));
    }
    if c.command == "ReplaceAll" {
        let find = p["find"].as_str().ok_or("Missing search text")?;
        let replacement = p["replacement"].as_str().ok_or("Missing replacement")?;
        if find.is_empty() || find.len() > 8192 || replacement.len() > 8192 {
            return Err("Invalid search or replacement length".into());
        }
        let mut commands = vec![];
        for paragraph in paragraphs {
            if paragraph.text.contains(find) {
                let mut ranges = vec![];
                let mut last_byte = 0;
                let mut last_unit = 0;
                let find_units = find.encode_utf16().count();
                for (byte, _) in paragraph.text.match_indices(find) {
                    let start = last_unit + paragraph.text[last_byte..byte].encode_utf16().count();
                    ranges.push((start, start + find_units, replacement));
                    last_byte = byte + find.len();
                    last_unit = start + find_units;
                    if ranges.len() > 100000 {
                        return Err("Too many replacements".into());
                    }
                }
                let next = crate::rich_text::replace_many(paragraph, ranges)?;
                commands.push(command(
                    "SetParagraph",
                    json!({"id":paragraph.id,"text":next.text,"runs":next.runs}),
                ));
            }
        }
        if commands.is_empty() {
            return Err("No matching text".into());
        }
        if commands.len() > 1000 {
            return Err("Too many matching paragraphs".into());
        }
        return Ok(Some(commands));
    }
    if c.command != "SplitParagraph" && c.command != "MergeParagraph" {
        return Ok(None);
    }
    let id = p["id"].as_str().ok_or("Missing paragraph id")?;
    let index = paragraphs
        .iter()
        .position(|p| p.id == id)
        .ok_or("Unknown paragraph")?;
    let current = &paragraphs[index];
    if current.image.is_some() || current.table.is_some() || (index > 0 && (paragraphs[index - 1].image.is_some() || paragraphs[index - 1].table.is_some()) && c.command == "MergeParagraph") {
        return Err("Image and table paragraphs cannot be split or merged".into());
    }
    if c.command == "SplitParagraph" {
        let next = p["new_id"].as_str().ok_or("Missing new paragraph id")?;
        if next.is_empty() || paragraphs.iter().any(|p| p.id == next) {
            return Err("Duplicate or empty paragraph id".into());
        }
        let start = utf16_boundary(
            &current.text,
            p["start"].as_u64().ok_or("Missing selection start")?,
        )?;
        let end = utf16_boundary(
            &current.text,
            p["end"].as_u64().ok_or("Missing selection end")?,
        )?;
        if start > end {
            return Err("Invalid selection".into());
        }
        let mut next_style = current.style.clone();
        next_style.page_break_before = false;
        return Ok(Some(vec![
            command(
                "SetParagraph",
                json!({"id":id,"text":&current.text[..start],"runs":crate::rich_text::slice(current,0,p["start"].as_u64().unwrap() as usize)}),
            ),
            command(
                "InsertParagraph",
                json!({"id":next,"index":index+1,"text":&current.text[end..],"style":next_style,"runs":crate::rich_text::slice(current,p["end"].as_u64().unwrap() as usize,current.text.encode_utf16().count())}),
            ),
        ]));
    }
    if index == 0 {
        return Err("No previous paragraph".into());
    }
    let previous = &paragraphs[index - 1];
    let joined = format!("{}{}", previous.text, current.text);
    let offset = previous.text.encode_utf16().count();
    let mut runs = previous.runs.clone();
    let mut points = vec![0, current.text.encode_utf16().count()];
    for run in &current.runs {
        points.extend([run.start, run.start + run.length]);
    }
    points.sort_unstable();
    points.dedup();
    for pair in points.windows(2) {
        let style = crate::rich_text::at(current, pair[0]);
        if style != document_core::CharacterStyle::from(&previous.style) {
            crate::rich_text::push(&mut runs, offset + pair[0], pair[1] - pair[0], style);
        }
    }
    if joined.len() > 2_000_000 {
        return Err("Merged paragraph too large".into());
    }
    Ok(Some(vec![
        command(
            "SetParagraph",
            json!({"id":previous.id,"text":joined,"runs":runs}),
        ),
        command("DeleteParagraph", json!({"id":id})),
    ]))
}
#[cfg(test)]
mod tests {
    use super::*;
    use document_core::TextStyle;
    #[test]
    fn unicode_split_and_merge() {
        let mut ps = vec![Paragraph {
            runs: vec![],
            image: None,
            table: None,
            id: "p".into(),
            text: "été 🦀 suite".into(),
            style: TextStyle::default(),
        }];
        let (_, changes) = crate::apply(
            &mut ps,
            &command(
                "SplitParagraph",
                json!({"id":"p","new_id":"p2","start":6,"end":6}),
            ),
        )
        .unwrap();
        assert_eq!(changes.len(), 2);
        assert_eq!(ps[0].text, "été 🦀");
        assert_eq!(ps[1].text, " suite");
        crate::apply(&mut ps, &command("MergeParagraph", json!({"id":"p2"}))).unwrap();
        assert_eq!(ps[0].text, "été 🦀 suite");
        assert!(crate::apply(
            &mut ps,
            &command(
                "SplitParagraph",
                json!({"id":"p","new_id":"p3","start":5,"end":5})
            )
        )
        .is_err());
    }
    #[test]
    fn replace_validates_before_mutation() {
        let mut ps = vec![Paragraph {
            runs: vec![],
            image: None,
            table: None,
            id: "p".into(),
            text: "été été".into(),
            style: TextStyle::default(),
        }];
        crate::apply(
            &mut ps,
            &command("ReplaceAll", json!({"find":"été","replacement":"hiver"})),
        )
        .unwrap();
        assert_eq!(ps[0].text, "hiver hiver");
        assert!(crate::apply(
            &mut ps,
            &command("ReplaceAll", json!({"find":"","replacement":"x"}))
        )
        .is_err());
    }
}
