use crate::{
    docx::{escape, paragraph_xml, text_xml},
    docx_styles::{child, paragraphs, text_tokens, token_text, word, WORD},
};
use document_core::Paragraph;
use roxmltree::{Document as Xml, Node};
use std::{collections::HashMap, ops::Range};

struct Edit {
    range: Range<usize>,
    text: String,
}
pub fn rewrite(
    xml: &str,
    original: &[Paragraph],
    current: &[Paragraph],
    bullet: u32,
    number: u32,
) -> Result<String, String> {
    let parsed = Xml::parse(xml).map_err(|e| e.to_string())?;
    let root = parsed.root_element();
    let nodes = paragraphs(root);
    if nodes.len() != original.len() {
        return Err(
            "Le document Word d’origine ne correspond plus aux paragraphes enregistrés.".into(),
        );
    }
    let q = if root.lookup_namespace_uri(Some("w")) == root.tag_name().namespace() {
        "w"
    } else {
        "nxl"
    };
    let sources: HashMap<_, _> = original
        .iter()
        .enumerate()
        .map(|(i, p)| (p.id.as_str(), i))
        .collect();
    let parent_key = |i: usize| nodes[i].parent().unwrap().range().start;
    let mut groups: HashMap<usize, Vec<&Paragraph>> = HashMap::new();
    let body = root
        .children()
        .find(|n| word(*n, "body"))
        .ok_or("Missing Word document body")?;
    let mut previous = None;
    for (i, p) in current.iter().enumerate() {
        let parent = if let Some(&index) = sources.get(p.id.as_str()) {
            parent_key(index)
        } else {
            previous
                .or_else(|| {
                    current[i + 1..]
                        .iter()
                        .find_map(|p| sources.get(p.id.as_str()).map(|i| parent_key(*i)))
                })
                .unwrap_or(body.range().start)
        };
        previous = Some(parent);
        groups.entry(parent).or_default().push(p);
    }
    let live: std::collections::HashSet<_> = current.iter().map(|p| p.id.as_str()).collect();
    let expected_parents: Vec<_> = original
        .iter()
        .enumerate()
        .filter(|(_, p)| live.contains(p.id.as_str()))
        .map(|(i, _)| parent_key(i))
        .collect();
    let desired_parents: Vec<_> = current
        .iter()
        .filter_map(|p| sources.get(p.id.as_str()).map(|i| parent_key(*i)))
        .collect();
    if expected_parents != desired_parents {
        return Err("Le déplacement de paragraphes entre des cellules de tableau et le corps Word n’est pas encore pris en charge.".into());
    }
    let live_parents: std::collections::HashSet<_> = original
        .iter()
        .enumerate()
        .filter(|(_, p)| live.contains(p.id.as_str()))
        .map(|(i, _)| parent_key(i))
        .collect();
    let mut bundles: HashMap<usize, Vec<String>> = HashMap::new();
    for (parent, desired) in groups {
        let mut chunks: Vec<String> = vec![];
        let mut leading = String::new();
        for p in desired {
            let rendered = render(xml, p, &sources, original, &nodes, q, bullet, number)?;
            if sources.contains_key(p.id.as_str()) {
                chunks.push(format!("{leading}{rendered}"));
                leading.clear();
            } else if let Some(last) = chunks.last_mut() {
                last.push_str(&rendered);
            } else {
                leading.push_str(&rendered);
            }
        }
        if !leading.is_empty() {
            chunks.push(leading);
        }
        bundles.insert(parent, chunks);
    }
    let mut used: HashMap<usize, usize> = HashMap::new();
    let mut edits = vec![];
    for (i, node) in nodes.iter().enumerate() {
        let parent = parent_key(i);
        let position = used.entry(parent).or_default();
        let desired = bundles.get(&parent).map(Vec::as_slice).unwrap_or(&[]);
        let mut replacement = String::new();
        let no_live_slot = !live_parents.contains(&parent);
        if (live.contains(original[i].id.as_str()) || (no_live_slot && *position == 0))
            && *position < desired.len()
        {
            replacement = desired[*position].clone();
            *position += 1;
        } else if original[i].image.is_none()
            && (child(*node, "pPr")
                .and_then(|n| child(n, "sectPr"))
                .is_some()
                || node
                    .descendants()
                    .any(|n| word(n, "drawing") || word(n, "pict") || word(n, "object")))
        {
            let mut empty = original[i].clone();
            empty.text.clear();
            replacement = patch_paragraph(xml, *node, &original[i], &empty, q, bullet, number)?;
        } else if word(node.parent().unwrap(), "tc") {
            replacement = format!("<{q}:p/>");
        }
        if replacement != xml[node.range()] {
            edits.push(Edit {
                range: node.range(),
                text: replacement,
            });
        }
    }
    for (parent, chunks) in &bundles {
        if used.get(parent).copied().unwrap_or(0) < chunks.len() {
            if *parent != body.range().start {
                return Err("Impossible de placer le paragraphe Word dans sa cellule.".into());
            }
            let at = child(body, "sectPr")
                .map(|n| n.range().start)
                .unwrap_or(closing(xml, body)?);
            edits.push(Edit {
                range: at..at,
                text: chunks[used.get(parent).copied().unwrap_or(0)..].join(""),
            });
        }
    }
    if q == "nxl" {
        let start_end =
            xml[root.range()].find('>').ok_or("Invalid Word root")? + root.range().start;
        edits.push(Edit {
            range: start_end..start_end,
            text: format!(
                " xmlns:nxl=\"{}\"",
                root.tag_name().namespace().unwrap_or(WORD)
            ),
        });
    }
    apply(xml, 0..xml.len(), edits)
}
fn render(
    xml: &str,
    p: &Paragraph,
    sources: &HashMap<&str, usize>,
    original: &[Paragraph],
    nodes: &[Node<'_, '_>],
    q: &str,
    bullet: u32,
    number: u32,
) -> Result<String, String> {
    if let Some(&i) = sources.get(p.id.as_str()) {
        patch_paragraph(xml, nodes[i], &original[i], p, q, bullet, number)
    } else {
        Ok(paragraph_xml(p, q, bullet, number))
    }
}
fn patch_paragraph(
    xml: &str,
    node: Node<'_, '_>,
    old: &Paragraph,
    p: &Paragraph,
    q: &str,
    bullet: u32,
    number: u32,
) -> Result<String, String> {
    if old == p {
        return Ok(xml[node.range()].into());
    }
    let links = |q: &Paragraph| {
        q.runs
            .iter()
            .map(|r| r.style.link.clone())
            .filter(|l| !l.is_empty())
            .collect::<Vec<_>>()
    };
    if old.image != p.image
        || old.table != p.table
        || links(old) != links(p)
        || xml[node.range()].ends_with("/>")
    {
        return Ok(paragraph_xml(p, q, bullet, number));
    }
    let mut edits = vec![];
    if old.text != p.text {
        let tokens = text_tokens(node);
        let actual = tokens.iter().map(|n| token_text(*n)).collect::<String>();
        if actual != old.text {
            return Err(
                "Le texte d’origine du paragraphe Word ne correspond pas au document.".into(),
            );
        }
        let before: Vec<_> = old.text.chars().collect();
        let after: Vec<_> = p.text.chars().collect();
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
        let insertion: String = after[prefix..after.len() - suffix].iter().collect();
        let old_end = before.len() - suffix;
        let mut position = 0;
        let mut inserted = false;
        for token in &tokens {
            let chars: Vec<_> = token_text(*token).chars().collect();
            let end = position + chars.len();
            let mut value = String::new();
            for (index, ch) in chars.iter().enumerate() {
                if position + index < prefix {
                    value.push(*ch);
                }
            }
            if !inserted && (prefix < end || (old_end == prefix && prefix == end)) {
                value.push_str(&insertion);
                inserted = true;
            }
            for (index, ch) in chars.iter().enumerate() {
                if position + index >= old_end {
                    value.push(*ch);
                }
            }
            if value != token_text(*token) {
                edits.push(Edit {
                    range: token.range(),
                    text: if value.is_empty() {
                        String::new()
                    } else {
                        text_xml(&value, q)
                    },
                });
            }
            position = end;
        }
        if tokens.is_empty() {
            let at = closing(xml, node)?;
            edits.push(Edit {
                range: at..at,
                text: format!("<{q}:r>{}</{q}:r>", text_xml(&p.text, q)),
            });
        }
    }
    let a = &old.style;
    let b = &p.style;
    let mut p_changes = vec![];
    if a.page_break_before != b.page_break_before {
        p_changes.push((
            "pageBreakBefore",
            format!(
                "<{q}:pageBreakBefore {q}:val=\"{}\"/>",
                u8::from(b.page_break_before)
            ),
        ));
    }
    if a.shading != b.shading {
        p_changes.push((
            "shd",
            if b.shading == "none" {
                format!("<{q}:shd {q}:val=\"clear\" {q}:color=\"auto\" {q}:fill=\"auto\"/>")
            } else {
                format!(
                    "<{q}:shd {q}:val=\"clear\" {q}:color=\"auto\" {q}:fill=\"{}\"/>",
                    b.shading.trim_start_matches('#')
                )
            },
        ));
    }
    if a.border != b.border {
        let sides: &[&str] = match b.border.as_str() {
            "box" => &["top", "left", "bottom", "right"],
            "bottom" => &["bottom"],
            "left" => &["left"],
            _ => &[],
        };
        let inner: String = sides
            .iter()
            .map(|side| format!("<{q}:{side} {q}:val=\"single\" {q}:sz=\"6\" {q}:space=\"4\" {q}:color=\"808080\"/>"))
            .collect();
        p_changes.push((
            "pBdr",
            if inner.is_empty() {
                format!("<{q}:pBdr><{q}:top {q}:val=\"nil\"/><{q}:left {q}:val=\"nil\"/><{q}:bottom {q}:val=\"nil\"/><{q}:right {q}:val=\"nil\"/></{q}:pBdr>")
            } else {
                format!("<{q}:pBdr>{inner}</{q}:pBdr>")
            },
        ));
    }
    if a.alignment != b.alignment {
        p_changes.push((
            "jc",
            property(
                node,
                "pPr",
                "jc",
                &[(
                    "val",
                    if b.alignment == "justify" {
                        "both".into()
                    } else {
                        b.alignment.clone()
                    },
                )],
                q,
            ),
        ));
    }
    let mut spacing = vec![];
    if a.spacing_after != b.spacing_after {
        spacing.push(("after", (b.spacing_after * 20.0).round().to_string()));
    }
    if a.line_spacing != b.line_spacing {
        spacing.push(("line", (b.line_spacing * 240.0).round().to_string()));
        spacing.push(("lineRule", "auto".into()));
    }
    if !spacing.is_empty() {
        p_changes.push(("spacing", property(node, "pPr", "spacing", &spacing, q)));
    }
    if a.indent != b.indent || a.list != b.list {
        p_changes.push((
            "ind",
            property(
                node,
                "pPr",
                "ind",
                &[
                    (
                        "left",
                        ((b.indent as u32 + u32::from(b.list != "none")) * 360).to_string(),
                    ),
                    (
                        "start",
                        ((b.indent as u32 + u32::from(b.list != "none")) * 360).to_string(),
                    ),
                ],
                q,
            ),
        ));
    }
    if a.list != b.list {
        let num = match b.list.as_str() {
            "bullet" => bullet,
            "number" => number,
            _ => 0,
        };
        p_changes.push((
            "numPr",
            format!(
                "<{q}:numPr><{q}:ilvl {q}:val=\"0\"/><{q}:numId {q}:val=\"{num}\"/></{q}:numPr>"
            ),
        ));
    }
    properties(xml, node, "pPr", p_changes, q, &mut edits)?;
    for run in node
        .descendants()
        .filter(|n| word(*n, "r") && n.ancestors().find(|a| word(*a, "p")) == Some(node))
    {
        if xml[run.range()].ends_with("/>") {
            continue;
        }
        let mut changes = vec![];
        for (name, changed, value) in [
            ("b", a.bold != b.bold, u8::from(b.bold).to_string()),
            ("i", a.italic != b.italic, u8::from(b.italic).to_string()),
            ("strike", a.strike != b.strike, u8::from(b.strike).to_string()),
            (
                "vertAlign",
                a.superscript != b.superscript || a.subscript != b.subscript,
                if b.superscript {
                    "superscript"
                } else if b.subscript {
                    "subscript"
                } else {
                    "baseline"
                }
                .into(),
            ),
            (
                "u",
                a.underline != b.underline,
                if b.underline { "single" } else { "none" }.into(),
            ),
            (
                "sz",
                a.font_size != b.font_size,
                (b.font_size * 1.5).round().to_string(),
            ),
            (
                "color",
                a.color != b.color,
                b.color.trim_start_matches('#').into(),
            ),
        ] {
            if changed {
                changes.push((name, property(run, "rPr", name, &[("val", value)], q)));
            }
        }
        if a.font_family != b.font_family {
            changes.push((
                "rFonts",
                property(
                    run,
                    "rPr",
                    "rFonts",
                    &[
                        ("ascii", b.font_family.clone()),
                        ("hAnsi", b.font_family.clone()),
                    ],
                    q,
                ),
            ));
        }
        if a.highlight != b.highlight {
            changes.push(("highlight", format!("<{q}:highlight {q}:val=\"none\"/>")));
            changes.push((
                "shd",
                property(
                    run,
                    "rPr",
                    "shd",
                    &[
                        ("val", "clear".into()),
                        (
                            "fill",
                            if b.highlight == "none" {
                                "auto".into()
                            } else {
                                b.highlight.trim_start_matches('#').into()
                            },
                        ),
                    ],
                    q,
                ),
            ));
        }
        properties(xml, run, "rPr", changes, q, &mut edits)?;
    }
    let updated = apply(xml, node.range(), edits)?;
    if old.runs != p.runs || (!p.runs.is_empty() && old.style != p.style) {
        apply_character_styles(xml, &updated, p, q)
    } else {
        Ok(updated)
    }
}
fn apply_character_styles(
    original_xml: &str,
    paragraph_xml: &str,
    paragraph: &Paragraph,
    q: &str,
) -> Result<String, String> {
    let original = Xml::parse(original_xml).map_err(|e| e.to_string())?;
    let namespaces = original
        .root_element()
        .namespaces()
        .map(|ns| match ns.name() {
            Some(name) => format!(" xmlns:{name}=\"{}\"", escape(ns.uri())),
            None => format!(" xmlns=\"{}\"", escape(ns.uri())),
        })
        .collect::<String>();
    let extra = if q == "nxl" {
        format!(
            " xmlns:nxl=\"{}\"",
            original
                .root_element()
                .tag_name()
                .namespace()
                .unwrap_or(WORD)
        )
    } else {
        String::new()
    };
    let wrapped = format!("<nexolumeRoot{namespaces}{extra}>{paragraph_xml}</nexolumeRoot>");
    let parsed = Xml::parse(&wrapped).map_err(|e| e.to_string())?;
    let p = parsed
        .root_element()
        .children()
        .find(|n| word(*n, "p"))
        .ok_or("Missing styled Word paragraph")?;
    let tokens = text_tokens(p);
    let actual = tokens.iter().map(|n| token_text(*n)).collect::<String>();
    if actual != paragraph.text {
        return Err("Le texte Word ne correspond pas aux styles à enregistrer.".into());
    }
    let segments = writer_engine::rich_text::segments(paragraph);
    let mut offset = 0;
    let mut edits = vec![];
    let mut run_ids = std::collections::HashSet::new();
    for token in &tokens {
        if let Some(run) = token.ancestors().find(|n| word(*n, "r")) {
            run_ids.insert(run.id());
        }
    }
    for run in p
        .descendants()
        .filter(|n| word(*n, "r") && run_ids.contains(&n.id()))
    {
        let raw = &wrapped[run.range()];
        let open_end = raw.find('>').ok_or("Invalid Word run")? + 1;
        let opening = &raw[..open_end];
        let name = raw[1..]
            .split([' ', '>', '\t', '\n'])
            .next()
            .ok_or("Invalid Word run name")?;
        let old_properties = child(run, "rPr")
            .map(|n| wrapped[n.range()].to_string())
            .unwrap_or_default();
        let mut replacement = String::new();
        for item in run
            .children()
            .filter(|n| n.is_element() && !word(*n, "rPr"))
        {
            if !tokens.iter().any(|token| token.id() == item.id()) {
                replacement.push_str(&format!(
                    "{opening}{old_properties}{}</{name}>",
                    &wrapped[item.range()]
                ));
                continue;
            }
            let value = token_text(item);
            let length = value.encode_utf16().count();
            let end = offset + length;
            for segment in &segments {
                let left = offset.max(segment.start);
                let right = end.min(segment.start + segment.length);
                if left >= right {
                    continue;
                }
                let first = writer_engine::rich_text::boundary(value, left - offset)?;
                let last = writer_engine::rich_text::boundary(value, right - offset)?;
                let generated = crate::docx::run_properties_xml(&segment.style, q);
                let generated_xml =
                    format!("<nexolumeRoot{namespaces}{extra}>{generated}</nexolumeRoot>");
                let props = Xml::parse(&generated_xml).map_err(|e| e.to_string())?;
                let changes = props
                    .root_element()
                    .children()
                    .find(|n| word(*n, "rPr"))
                    .unwrap()
                    .children()
                    .filter(|n| n.is_element())
                    .map(|n| (n.tag_name().name(), generated_xml[n.range()].to_string()))
                    .collect();
                let mut property_edits = vec![];
                properties(&wrapped, run, "rPr", changes, q, &mut property_edits)?;
                let properties = if let Some(existing) = child(run, "rPr") {
                    apply(&wrapped, existing.range(), property_edits)?
                } else {
                    property_edits.into_iter().map(|e| e.text).collect()
                };
                replacement.push_str(&format!(
                    "{opening}{properties}{}</{name}>",
                    text_xml(&value[first..last], q)
                ));
            }
            offset = end;
        }
        edits.push(Edit {
            range: run.range(),
            text: replacement,
        });
    }
    apply(&wrapped, p.range(), edits)
}
fn property(
    node: Node<'_, '_>,
    container: &str,
    name: &str,
    values: &[(&str, String)],
    q: &str,
) -> String {
    let mut result = format!("<{q}:{name}");
    if let Some(old) = child(node, container).and_then(|n| child(n, name)) {
        for a in old
            .attributes()
            .filter(|a| !values.iter().any(|(key, _)| *key == a.name()))
        {
            let prefix = a
                .namespace()
                .and_then(|ns| old.lookup_prefix(ns))
                .map(|p| format!("{p}:"))
                .unwrap_or_default();
            result.push_str(&format!(" {prefix}{}=\"{}\"", a.name(), escape(a.value())));
        }
    }
    for (key, value) in values {
        result.push_str(&format!(" {q}:{key}=\"{}\"", escape(value)));
    }
    result.push_str("/>");
    result
}
fn properties(
    xml: &str,
    node: Node<'_, '_>,
    container: &str,
    mut changes: Vec<(&str, String)>,
    q: &str,
    edits: &mut Vec<Edit>,
) -> Result<(), String> {
    if changes.is_empty() {
        return Ok(());
    }
    changes.sort_by_key(|(name, _)| property_rank(container, name));
    if let Some(properties) = child(node, container) {
        if xml[properties.range()].ends_with("/>") {
            edits.push(Edit {
                range: properties.range(),
                text: format!(
                    "<{q}:{container}>{}</{q}:{container}>",
                    changes.iter().map(|(_, s)| s.as_str()).collect::<String>()
                ),
            });
        } else {
            let mut additions: std::collections::BTreeMap<usize, String> =
                std::collections::BTreeMap::new();
            for (name, value) in changes {
                if let Some(old) = child(properties, name) {
                    edits.push(Edit {
                        range: old.range(),
                        text: value,
                    });
                } else {
                    let at = properties
                        .children()
                        .find(|n| {
                            n.is_element()
                                && property_rank(container, n.tag_name().name())
                                    > property_rank(container, name)
                        })
                        .map(|n| n.range().start)
                        .unwrap_or(closing(xml, properties)?);
                    additions.entry(at).or_default().push_str(&value);
                }
            }
            for (at, text) in additions {
                edits.push(Edit {
                    range: at..at,
                    text,
                });
            }
        }
    } else {
        let at =
            node.range().start + xml[node.range()].find('>').ok_or("Invalid Word element")? + 1;
        edits.push(Edit {
            range: at..at,
            text: format!(
                "<{q}:{container}>{}</{q}:{container}>",
                changes.iter().map(|(_, s)| s.as_str()).collect::<String>()
            ),
        });
    }
    Ok(())
}
fn property_rank(container: &str, name: &str) -> usize {
    let order = if container == "pPr" {
        &[
            "pStyle",
            "keepNext",
            "keepLines",
            "pageBreakBefore",
            "framePr",
            "widowControl",
            "numPr",
            "suppressLineNumbers",
            "pBdr",
            "shd",
            "tabs",
            "suppressAutoHyphens",
            "kinsoku",
            "wordWrap",
            "overflowPunct",
            "topLinePunct",
            "autoSpaceDE",
            "autoSpaceDN",
            "bidi",
            "adjustRightInd",
            "snapToGrid",
            "spacing",
            "ind",
            "contextualSpacing",
            "mirrorIndents",
            "suppressOverlap",
            "jc",
            "textDirection",
            "textAlignment",
            "textboxTightWrap",
            "outlineLvl",
            "divId",
            "cnfStyle",
            "rPr",
            "sectPr",
            "pPrChange",
        ][..]
    } else {
        &[
            "rStyle",
            "rFonts",
            "b",
            "bCs",
            "i",
            "iCs",
            "caps",
            "smallCaps",
            "strike",
            "dstrike",
            "outline",
            "shadow",
            "emboss",
            "imprint",
            "noProof",
            "snapToGrid",
            "vanish",
            "webHidden",
            "color",
            "spacing",
            "w",
            "kern",
            "position",
            "sz",
            "szCs",
            "highlight",
            "u",
            "effect",
            "bdr",
            "shd",
            "fitText",
            "vertAlign",
            "rtl",
            "cs",
            "em",
            "lang",
            "eastAsianLayout",
            "specVanish",
            "oMath",
            "rPrChange",
        ][..]
    };
    order.iter().position(|key| *key == name).unwrap_or(1000)
}
fn closing(xml: &str, node: Node<'_, '_>) -> Result<usize, String> {
    Ok(node.range().start
        + xml[node.range()]
            .rfind("</")
            .ok_or("Invalid Word element")?)
}
fn apply(xml: &str, range: Range<usize>, mut edits: Vec<Edit>) -> Result<String, String> {
    edits.sort_by_key(|e| (e.range.start, e.range.end));
    for pair in edits.windows(2) {
        if pair[0].range.end > pair[1].range.start {
            return Err("Overlapping Word document changes".into());
        }
    }
    let mut output = xml[range.clone()].to_string();
    for e in edits.into_iter().rev() {
        if e.range.start < range.start || e.range.end > range.end {
            return Err("Word document edit outside paragraph".into());
        }
        output.replace_range(
            e.range.start - range.start..e.range.end - range.start,
            &e.text,
        );
    }
    Ok(output)
}
