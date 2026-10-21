use document_core::{CharacterStyle, Paragraph, TextSpan};
use serde_json::Value;

pub fn boundary(text: &str, offset: usize) -> Result<usize, String> {
    let mut count = 0;
    for (index, ch) in text.char_indices() {
        if count == offset {
            return Ok(index);
        }
        count += ch.len_utf16();
    }
    if count == offset {
        Ok(text.len())
    } else {
        Err("Invalid UTF-16 text boundary".into())
    }
}
pub fn validate(p: &Paragraph) -> Result<(), String> {
    if p.runs.len() > 20000 {
        return Err("Too many text styles".into());
    }
    if p.runs.is_empty() {
        return Ok(());
    }
    let mut boundaries = std::collections::HashSet::from([0]);
    let mut position = 0;
    for ch in p.text.chars() {
        position += ch.len_utf16();
        boundaries.insert(position);
    }
    let mut end = 0;
    for run in &p.runs {
        if run.length == 0 || run.start < end {
            return Err("Overlapping or empty text style".into());
        }
        end = run
            .start
            .checked_add(run.length)
            .ok_or("Invalid text style range")?;
        if !boundaries.contains(&run.start) || !boundaries.contains(&end) {
            return Err("Invalid UTF-16 text style boundary".into());
        }
        let s = &run.style;
        let color = |v: &str| {
            v.len() == 7 && v.starts_with('#') && v[1..].bytes().all(|b| b.is_ascii_hexdigit())
        };
        if !s.font_size.is_finite()
            || !(6.0..=96.0).contains(&s.font_size)
            || s.font_family.is_empty()
            || s.font_family.len() > 100
            || !color(&s.color)
            || !(s.highlight == "none" || color(&s.highlight))
            || !valid_link(&s.link)
        {
            return Err("Invalid character style".into());
        }
    }
    Ok(())
}
/// A link is empty (no link) or a short http, https or mailto address without control characters.
pub fn valid_link(link: &str) -> bool {
    link.is_empty()
        || (link.len() <= 2000
            && !link.chars().any(|c| c.is_control() || c.is_whitespace())
            && ["http://", "https://", "mailto:"]
                .iter()
                .any(|scheme| link.len() > scheme.len() && link.to_ascii_lowercase().starts_with(scheme)))
}
pub fn at(p: &Paragraph, position: usize) -> CharacterStyle {
    p.runs
        .iter()
        .find(|r| position >= r.start && position < r.start + r.length)
        .map(|r| r.style.clone())
        .unwrap_or_else(|| (&p.style).into())
}
pub fn segments(p: &Paragraph) -> Vec<TextSpan> {
    let mut result = vec![];
    let mut offset = 0;
    let base = CharacterStyle::from(&p.style);
    for r in &p.runs {
        push(&mut result, offset, r.start - offset, base.clone());
        push(&mut result, r.start, r.length, r.style.clone());
        offset = r.start + r.length;
    }
    push(
        &mut result,
        offset,
        p.text.encode_utf16().count() - offset,
        base,
    );
    result
}
pub fn push(runs: &mut Vec<TextSpan>, start: usize, length: usize, style: CharacterStyle) {
    if length == 0 {
        return;
    }
    if let Some(last) = runs.last_mut() {
        if last.start + last.length == start && last.style == style {
            last.length += length;
            return;
        }
    }
    runs.push(TextSpan {
        start,
        length,
        style,
    });
}
pub fn slice(p: &Paragraph, start: usize, end: usize) -> Vec<TextSpan> {
    let mut result = vec![];
    for r in &p.runs {
        let left = start.max(r.start);
        let right = end.min(r.start + r.length);
        if left < right {
            push(&mut result, left - start, right - left, r.style.clone());
        }
    }
    result
}
pub fn format(
    p: &Paragraph,
    start: usize,
    end: usize,
    patch: &Value,
) -> Result<Vec<TextSpan>, String> {
    boundary(&p.text, start)?;
    boundary(&p.text, end)?;
    if start >= end {
        return Err("Select text to format".into());
    }
    let keys = [
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
        "link",
    ];
    let changes = patch.as_object().ok_or("Missing character formatting")?;
    if changes.is_empty() || changes.keys().any(|k| !keys.contains(&k.as_str())) {
        return Err("Unsupported character formatting".into());
    }
    let mut points = vec![0, p.text.encode_utf16().count(), start, end];
    for r in &p.runs {
        points.extend([r.start, r.start + r.length]);
    }
    points.sort_unstable();
    points.dedup();
    let base = CharacterStyle::from(&p.style);
    let mut result = vec![];
    for pair in points.windows(2) {
        let mut style = at(p, pair[0]);
        if pair[0] >= start && pair[1] <= end {
            let mut json = serde_json::to_value(style).unwrap();
            for (key, value) in changes {
                json[key] = value.clone();
            }
            style = serde_json::from_value(json)
                .map_err(|e| format!("Invalid character formatting: {e}"))?;
        }
        if style != base {
            push(&mut result, pair[0], pair[1] - pair[0], style);
        }
    }
    Ok(result)
}
pub fn replace_runs(p: &Paragraph, start: usize, end: usize, inserted: usize) -> Vec<TextSpan> {
    let total = p.text.encode_utf16().count();
    let mut result = slice(p, 0, start);
    let style = at(
        p,
        if end > start {
            start
        } else if start > 0 {
            start - 1
        } else {
            start
        },
    );
    if style != CharacterStyle::from(&p.style) {
        push(&mut result, start, inserted, style);
    }
    for r in slice(p, end, total) {
        push(&mut result, start + inserted + r.start, r.length, r.style);
    }
    result
}
pub fn replace(
    p: &Paragraph,
    start: usize,
    end: usize,
    inserted: &str,
) -> Result<Paragraph, String> {
    if start > end {
        return Err("Invalid text range".into());
    }
    let left = boundary(&p.text, start)?;
    let right = boundary(&p.text, end)?;
    if p.text.len() - (right - left) + inserted.len() > 2_000_000 {
        return Err("Paragraph exceeds maximum size".into());
    }
    let mut next = p.clone();
    next.runs = replace_runs(p, start, end, inserted.encode_utf16().count());
    next.text = format!("{}{}{}", &p.text[..left], inserted, &p.text[right..]);
    Ok(next)
}
pub fn replace_many(
    p: &Paragraph,
    mut edits: Vec<(usize, usize, &str)>,
) -> Result<Paragraph, String> {
    edits.sort_by_key(|(start, _, _)| *start);
    let mut requested = std::collections::BTreeSet::new();
    let mut previous = 0;
    for &(start, end, _) in &edits {
        if start > end || start < previous {
            return Err("Overlapping text edits".into());
        }
        requested.extend([start, end]);
        previous = end;
    }
    let mut positions = std::collections::HashMap::new();
    let mut units = 0;
    for (byte, ch) in p.text.char_indices() {
        if requested.contains(&units) {
            positions.insert(units, byte);
        }
        units += ch.len_utf16();
    }
    positions.insert(units, p.text.len());
    let mut size = p.text.len();
    for &(start, end, text) in &edits {
        let left = *positions
            .get(&start)
            .ok_or("Invalid UTF-16 edit boundary")?;
        let right = *positions.get(&end).ok_or("Invalid UTF-16 edit boundary")?;
        size = size
            .checked_sub(right - left)
            .and_then(|n| n.checked_add(text.len()))
            .ok_or("Invalid replacement size")?;
    }
    if size > 2_000_000 {
        return Err("Paragraph exceeds maximum size".into());
    }
    let segments = segments(p);
    let base = CharacterStyle::from(&p.style);
    let mut runs = vec![];
    let mut segment_index = 0;
    let mut output = String::with_capacity(size);
    let mut origin = 0;
    let mut byte = 0;
    let mut destination = 0;
    fn copy_styles(
        segments: &[TextSpan],
        index: &mut usize,
        start: usize,
        end: usize,
        destination: usize,
        base: &CharacterStyle,
        runs: &mut Vec<TextSpan>,
    ) {
        while *index < segments.len() {
            let r = &segments[*index];
            if r.start >= end {
                break;
            }
            let left = start.max(r.start);
            let right = end.min(r.start + r.length);
            if left < right && &r.style != base {
                push(
                    runs,
                    destination + left - start,
                    right - left,
                    r.style.clone(),
                );
            }
            if r.start + r.length <= end {
                *index += 1;
            } else {
                break;
            }
        }
    }
    for (start, end, text) in edits {
        let left = positions[&start];
        let right = positions[&end];
        output.push_str(&p.text[byte..left]);
        copy_styles(
            &segments,
            &mut segment_index,
            origin,
            start,
            destination,
            &base,
            &mut runs,
        );
        destination += start - origin;
        let style = if end > start {
            segments
                .get(segment_index)
                .map(|r| r.style.clone())
                .unwrap_or_else(|| base.clone())
        } else if segment_index > 0 && segments.get(segment_index).is_none_or(|r| r.start == start)
        {
            segments[segment_index - 1].style.clone()
        } else {
            segments
                .get(segment_index)
                .map(|r| r.style.clone())
                .unwrap_or_else(|| base.clone())
        };
        let inserted = text.encode_utf16().count();
        if style != base {
            push(&mut runs, destination, inserted, style);
        }
        output.push_str(text);
        destination += inserted;
        while segment_index < segments.len()
            && segments[segment_index].start + segments[segment_index].length <= end
        {
            segment_index += 1;
        }
        origin = end;
        byte = right;
    }
    output.push_str(&p.text[byte..]);
    copy_styles(
        &segments,
        &mut segment_index,
        origin,
        units,
        destination,
        &base,
        &mut runs,
    );
    let mut next = p.clone();
    next.text = output;
    next.runs = runs;
    Ok(next)
}
pub fn text_changed(p: &Paragraph, text: &str) -> Vec<TextSpan> {
    let before: Vec<_> = p.text.chars().collect();
    let after: Vec<_> = text.chars().collect();
    let prefix = before
        .iter()
        .zip(&after)
        .take_while(|(a, b)| a == b)
        .count();
    let suffix = before[prefix..]
        .iter()
        .rev()
        .zip(after[prefix..].iter().rev())
        .take_while(|(a, b)| a == b)
        .count();
    let units = |chars: &[char]| chars.iter().map(|c| c.len_utf16()).sum::<usize>();
    replace_runs(
        p,
        units(&before[..prefix]),
        units(&before[..before.len() - suffix]),
        units(&after[prefix..after.len() - suffix]),
    )
}
