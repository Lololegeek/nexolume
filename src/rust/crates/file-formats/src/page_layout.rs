use crate::docx_styles::{attr, child, word, WORD};
use document_core::PageLayout;
use roxmltree::Document;

pub fn read_word(xml: &str) -> Option<PageLayout> {
    let doc = Document::parse(xml).ok()?;
    let section = doc.descendants().filter(|n| word(*n, "sectPr")).last()?;
    let mut layout = PageLayout::default();
    let size = child(section, "pgSz");
    let margins = child(section, "pgMar");
    for (node, name, target) in [
        (size, "w", &mut layout.width),
        (size, "h", &mut layout.height),
        (margins, "top", &mut layout.top),
        (margins, "right", &mut layout.right),
        (margins, "bottom", &mut layout.bottom),
        (margins, "left", &mut layout.left),
    ] {
        if let Some(value) = node
            .and_then(|n| attr(n, name))
            .and_then(|v| v.parse::<f64>().ok())
        {
            *target = value / 15.0;
        }
    }
    if let Some(cols) = child(section, "cols") {
        if let Some(n) = attr(cols, "num").and_then(|v| v.parse::<u32>().ok()) {
            layout.columns = n.clamp(1, 4);
        }
        if let Some(space) = attr(cols, "space").and_then(|v| v.parse::<f64>().ok()) {
            layout.column_gap = space / 15.0;
        }
    }
    layout.validate().ok()?;
    Some(layout)
}
/// Rewrites the `w:cols` element of the last section so it matches `layout`; a no-op for single-column pages without one.
fn apply_columns(xml: &str, layout: &PageLayout) -> String {
    let Some(start) = xml.rfind("<w:sectPr") else { return xml.to_string() };
    let Some(end) = xml[start..].find("</w:sectPr>").map(|i| start + i) else { return xml.to_string() };
    let mut section = xml[start..end].to_string();
    if let Some(i) = section.find("<w:cols") {
        let gt = section[i..].find('>').map(|j| i + j).ok_or(()).ok();
        let Some(gt) = gt else { return xml.to_string() };
        let to = if section.as_bytes()[gt - 1] == b'/' {
            gt + 1
        } else {
            match section[gt..].find("</w:cols>") {
                Some(j) => gt + j + 9,
                None => return xml.to_string(),
            }
        };
        section.replace_range(i..to, "");
    } else if layout.columns <= 1 {
        return xml.to_string();
    }
    if layout.columns > 1 {
        let element = format!("<w:cols w:num=\"{}\" w:space=\"{}\"/>", layout.columns, (layout.column_gap * 15.0).round() as u32);
        let at = section.find("<w:pgMar").and_then(|i| section[i..].find("/>").map(|j| i + j + 2)).unwrap_or(section.len());
        section.insert_str(at, &element);
    }
    format!("{}{}{}", &xml[..start], section, &xml[end..])
}
pub fn word_elements(layout: &PageLayout) -> (String, String) {
    let n = |v: f64| (v * 15.0).round() as u32;
    (format!("<w:pgSz xmlns:w=\"{WORD}\" w:w=\"{}\" w:h=\"{}\" w:orient=\"{}\"/>",n(layout.width),n(layout.height),if layout.width>layout.height {"landscape"}else{"portrait"}),
    format!("<w:pgMar xmlns:w=\"{WORD}\" w:top=\"{}\" w:right=\"{}\" w:bottom=\"{}\" w:left=\"{}\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>",n(layout.top),n(layout.right),n(layout.bottom),n(layout.left)))
}
pub fn patch_word(xml: &str, layout: &PageLayout) -> Result<String, String> {
    layout.validate()?;
    let doc = Document::parse(xml).map_err(|e| e.to_string())?;
    let body = doc
        .descendants()
        .find(|n| word(*n, "body"))
        .ok_or("Missing Word body")?;
    let namespace = body.tag_name().namespace().unwrap_or(WORD);
    let (size, margins) = word_elements(layout);
    let size = size.replace(WORD, namespace);
    let margins = margins.replace(WORD, namespace);
    let mut edits = vec![];
    if let Some(section) = child(body, "sectPr") {
        let text = &xml[section.range()];
        if text.ends_with("/>") {
            edits.push((
                section.range(),
                format!("<w:sectPr xmlns:w=\"{namespace}\">{size}{margins}</w:sectPr>"),
            ));
        } else {
            let mut additions: std::collections::BTreeMap<usize, String> =
                std::collections::BTreeMap::new();
            for (name, value) in [("pgSz", size), ("pgMar", margins)] {
                if let Some(old) = child(section, name) {
                    let attributes = if name == "pgSz" {
                        vec![("w", layout.width * 15.0), ("h", layout.height * 15.0)]
                    } else {
                        vec![
                            ("top", layout.top * 15.0),
                            ("right", layout.right * 15.0),
                            ("bottom", layout.bottom * 15.0),
                            ("left", layout.left * 15.0),
                        ]
                    };
                    let mut attributes: Vec<_> = attributes
                        .iter()
                        .map(|(name, v)| (*name, format!("{:.0}", v)))
                        .collect();
                    if name == "pgSz" {
                        attributes.push((
                            "orient",
                            if layout.width > layout.height {
                                "landscape"
                            } else {
                                "portrait"
                            }
                            .into(),
                        ));
                    }
                    edits.push((old.range(), patch_attributes(xml, old, &attributes)?));
                } else {
                    let preceding = if name == "pgSz" {
                        vec![
                            "headerReference",
                            "footerReference",
                            "footnotePr",
                            "endnotePr",
                            "type",
                        ]
                    } else {
                        vec![
                            "headerReference",
                            "footerReference",
                            "footnotePr",
                            "endnotePr",
                            "type",
                            "pgSz",
                        ]
                    };
                    let at = section
                        .children()
                        .find(|n| n.is_element() && !preceding.contains(&n.tag_name().name()))
                        .map(|n| n.range().start)
                        .unwrap_or(
                            section.range().start + text.rfind("</").ok_or("Invalid section")?,
                        );
                    additions.entry(at).or_default().push_str(&value);
                }
            }
            for (at, value) in additions {
                edits.push((at..at, value));
            }
        }
    } else {
        let at = body.range().start + xml[body.range()].rfind("</").ok_or("Invalid body")?;
        edits.push((
            at..at,
            format!("<w:sectPr xmlns:w=\"{namespace}\">{size}{margins}</w:sectPr>"),
        ));
    }
    let mut output = xml.to_string();
    edits.sort_by_key(|(r, _)| std::cmp::Reverse(r.start));
    for (range, text) in edits {
        output.replace_range(range, &text);
    }
    Ok(apply_columns(&output, layout))
}
fn patch_attributes(
    xml: &str,
    node: roxmltree::Node<'_, '_>,
    values: &[(&str, String)],
) -> Result<String, String> {
    let start = node.range().start;
    let mut output = xml[node.range()].to_string();
    let mut edits = vec![];
    let namespace = node.tag_name().namespace().unwrap_or(WORD);
    let prefix = node.lookup_prefix(namespace).unwrap_or("nxlpage");
    let mut additions = if node.lookup_prefix(namespace).is_none() {
        format!(" xmlns:nxlpage=\"{namespace}\"")
    } else {
        String::new()
    };
    for (name, value) in values {
        if let Some(a) = node
            .attributes()
            .find(|a| a.name() == *name && a.namespace() == Some(namespace))
        {
            let r = a.range_value();
            edits.push((r.start - start..r.end - start, value.clone()));
        } else {
            additions.push_str(&format!(" {prefix}:{name}=\"{value}\""));
        }
    }
    let mut at = output.find('>').ok_or("Invalid page property")?;
    if output.as_bytes()[at - 1] == b'/' {
        at -= 1;
    }
    output.insert_str(at, &additions);
    edits.sort_by_key(|(r, _)| std::cmp::Reverse(r.start));
    for (r, value) in edits {
        output.replace_range(r, &value);
    }
    Ok(output)
}

const FO: &str = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
pub fn read_odt(xml: &str) -> Option<PageLayout> {
    let doc = Document::parse(xml).ok()?;
    let p = doc
        .descendants()
        .find(|n| n.is_element() && n.tag_name().name() == "page-layout-properties")?;
    let mut layout = PageLayout::default();
    for (name, target) in [
        ("page-width", &mut layout.width),
        ("page-height", &mut layout.height),
        ("margin-top", &mut layout.top),
        ("margin-right", &mut layout.right),
        ("margin-bottom", &mut layout.bottom),
        ("margin-left", &mut layout.left),
    ] {
        if let Some(value) = p.attribute((FO, name)).and_then(length) {
            *target = value;
        }
    }
    if let Some(columns) = p.children().find(|n| n.is_element() && n.tag_name().name() == "columns") {
        if let Some(n) = columns.attribute((FO, "column-count")).and_then(|v| v.parse::<u32>().ok()) {
            layout.columns = n.clamp(1, 4);
        }
        if let Some(gap) = columns.attribute((FO, "column-gap")).and_then(length) {
            layout.column_gap = gap;
        }
    }
    layout.validate().ok()?;
    Some(layout)
}
fn length(v: &str) -> Option<f64> {
    for (unit, scale) in [
        ("cm", 96.0 / 2.54),
        ("mm", 96.0 / 25.4),
        ("in", 96.0),
        ("pt", 96.0 / 72.0),
        ("px", 1.0),
    ] {
        if let Some(n) = v.strip_suffix(unit) {
            return n.parse::<f64>().ok().map(|v| v * scale);
        }
    }
    None
}
pub fn odt_styles(p: &PageLayout) -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?><office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="{FO}" office:version="1.3"><office:styles/><office:automatic-styles><style:page-layout style:name="Page"><style:page-layout-properties fo:page-width="{}pt" fo:page-height="{}pt" fo:margin-top="{}pt" fo:margin-right="{}pt" fo:margin-bottom="{}pt" fo:margin-left="{}pt" style:print-orientation="{}">{}</style:page-layout-properties></style:page-layout></office:automatic-styles><office:master-styles><style:master-page style:name="Standard" style:page-layout-name="Page"/></office:master-styles></office:document-styles>"#,
        p.width * 0.75,
        p.height * 0.75,
        p.top * 0.75,
        p.right * 0.75,
        p.bottom * 0.75,
        p.left * 0.75,
        if p.width > p.height {
            "landscape"
        } else {
            "portrait"
        },
        if p.columns > 1 {
            format!("<style:columns fo:column-count=\"{}\" fo:column-gap=\"{}pt\"/>", p.columns, p.column_gap * 0.75)
        } else {
            String::new()
        }
    )
}
