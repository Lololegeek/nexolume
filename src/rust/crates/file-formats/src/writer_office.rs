use document_core::{id, Paragraph, TextStyle};
use roxmltree::{Document as XmlDocument, Node};
use std::io::{Cursor, Read, Write};
use zip::{write::FileOptions, ZipArchive, ZipWriter};

const MAX_ENTRY: u64 = 12 * 1024 * 1024;

fn xml_entry<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    name: &str,
) -> Result<String, String> {
    let mut entry = archive
        .by_name(name)
        .map_err(|_| format!("Archive is missing {name}"))?;
    if entry.size() > MAX_ENTRY {
        return Err(format!("Office XML part is too large: {name}"));
    }
    let mut text = String::new();
    entry
        .read_to_string(&mut text)
        .map_err(|e| format!("Invalid XML part {name}: {e}"))?;
    Ok(text)
}

fn local(node: Node<'_, '_>, name: &str) -> bool {
    node.tag_name().name() == name
}
pub(crate) fn text_descendants(node: Node<'_, '_>, out: &mut String) {
    for child in node.children() {
        if child.is_text() {
            out.push_str(child.text().unwrap_or(""));
        } else if child.is_element() {
            match child.tag_name().name() {
                "annotation" => {}
                "tab" => out.push('\t'),
                "br" | "line-break" => out.push('\n'),
                "s" => {
                    let count = child
                        .attribute(("urn:oasis:names:tc:opendocument:xmlns:text:1.0", "c"))
                        .and_then(|s| s.parse::<usize>().ok())
                        .unwrap_or(1)
                        .min(1000);
                    out.extend(std::iter::repeat_n(' ', count));
                }
                _ => text_descendants(child, out),
            }
        }
    }
}
fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
fn write_zip_with(
    parts: Vec<(&str, String)>,
    binary: Vec<(String, Vec<u8>)>,
    mimetype_first: bool,
) -> Result<Vec<u8>, String> {
    let mut writer = ZipWriter::new(Cursor::new(Vec::new()));
    let options: FileOptions<'_, ()> =
        FileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    if mimetype_first {
        writer
            .start_file(
                "mimetype",
                FileOptions::<()>::default().compression_method(zip::CompressionMethod::Stored),
            )
            .map_err(|e| e.to_string())?;
        writer
            .write_all(b"application/vnd.oasis.opendocument.text")
            .map_err(|e| e.to_string())?;
    }
    for (name, text) in parts {
        if mimetype_first && name == "mimetype" {
            continue;
        }
        writer
            .start_file(name, options)
            .map_err(|e| e.to_string())?;
        writer
            .write_all(text.as_bytes())
            .map_err(|e| e.to_string())?;
    }
    for (name, bytes) in binary {
        writer
            .start_file(
                name,
                FileOptions::<()>::default().compression_method(zip::CompressionMethod::Stored),
            )
            .map_err(|e| e.to_string())?;
        writer.write_all(&bytes).map_err(|e| e.to_string())?;
    }
    writer
        .finish()
        .map(|cursor| cursor.into_inner())
        .map_err(|e| e.to_string())
}

fn length_px(value: &str) -> Option<f64> {
    let value = value.trim();
    let split = value.find(|c: char| c.is_ascii_alphabetic()).unwrap_or(value.len());
    let number = value[..split].parse::<f64>().ok()?;
    let px = match &value[split..] {
        "cm" => number * 96.0 / 2.54,
        "mm" => number * 96.0 / 25.4,
        "in" => number * 96.0,
        "pt" => number * 96.0 / 72.0,
        "px" | "" => number,
        _ => return None,
    };
    (px.is_finite() && px >= 8.0).then_some(px.min(6000.0))
}
fn read_image<R: Read + std::io::Seek>(
    archive: &mut ZipArchive<R>,
    node: Node<'_, '_>,
) -> Option<document_core::ImageData> {
    use base64::Engine as _;
    let svg_ns = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    let xlink_ns = "http://www.w3.org/1999/xlink";
    let frame = node.children().find(|n| local(*n, "frame"))?;
    let href = frame
        .children()
        .find(|n| local(*n, "image"))?
        .attribute((xlink_ns, "href"))?;
    if href.contains("..") || href.starts_with('/') || href.contains(':') {
        return None;
    }
    let mime = crate::docx::image_mime(href.rsplit_once('.')?.1)?;
    let mut entry = archive.by_name(href).ok()?;
    if entry.size() > 8 * 1024 * 1024 {
        return None;
    }
    let mut bytes = vec![];
    entry.read_to_end(&mut bytes).ok()?;
    let alt = frame
        .children()
        .find(|n| local(*n, "desc"))
        .map(|n| n.text().unwrap_or("").chars().take(500).collect())
        .unwrap_or_default();
    Some(document_core::ImageData {
        mime: mime.into(),
        data: base64::engine::general_purpose::STANDARD.encode(bytes),
        width: frame.attribute((svg_ns, "width")).and_then(length_px).unwrap_or(320.0),
        height: frame.attribute((svg_ns, "height")).and_then(length_px).unwrap_or(240.0),
        alt,
    })
}
fn odt_table(node: Node<'_, '_>) -> Option<document_core::TableData> {
    if !local(node, "table") || !node.parent().is_some_and(|p| local(p, "text")) {
        return None;
    }
    let table_ns = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    let mut rows: Vec<Vec<String>> = vec![];
    let mut header = false;
    let collect = |row: Node<'_, '_>, rows: &mut Vec<Vec<String>>| -> Option<()> {
        if rows.len() >= 200 {
            return None;
        }
        let mut cells = vec![];
        for cell in row.children().filter(|n| n.is_element()) {
            if !local(cell, "table-cell") || cells.len() >= 20 {
                return None;
            }
            for attribute in ["number-columns-spanned", "number-rows-spanned", "number-columns-repeated"] {
                if cell.attribute((table_ns, attribute)).is_some_and(|v| v != "1") {
                    return None;
                }
            }
            let mut lines = vec![];
            for part in cell.children().filter(|n| n.is_element()) {
                if !(local(part, "p") || local(part, "h")) || part.descendants().any(|n| local(n, "frame")) {
                    return None;
                }
                let mut line = String::new();
                text_descendants(part, &mut line);
                lines.push(line);
            }
            let text = lines.join("\n");
            if text.chars().count() > 5000 {
                return None;
            }
            cells.push(text);
        }
        if cells.is_empty() || rows.first().is_some_and(|r| r.len() != cells.len()) {
            return None;
        }
        rows.push(cells);
        Some(())
    };
    for child in node.children().filter(|n| n.is_element()) {
        if local(child, "table-row") {
            collect(child, &mut rows)?;
        } else if local(child, "table-header-rows") {
            header = rows.is_empty();
            for row in child.children().filter(|n| n.is_element()) {
                if !local(row, "table-row") {
                    return None;
                }
                collect(row, &mut rows)?;
            }
        } else if !local(child, "table-column") && !local(child, "table-columns") {
            return None;
        }
    }
    (!rows.is_empty()).then_some(document_core::TableData { rows, header })
}
fn table_xml(table: &document_core::TableData, index: usize, styles: &mut String) -> String {
    let columns = table.rows.first().map_or(1, Vec::len).max(1);
    styles.push_str(&format!("<style:style style:name=\"NxTable{index}\" style:family=\"table\"><style:table-properties style:width=\"16.000cm\" table:align=\"left\"/></style:style><style:style style:name=\"NxCol{index}\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"{:.3}cm\"/></style:style>", 16.0 / columns as f64));
    if index == 0 || !styles.contains("NxCell") {
        styles.push_str("<style:style style:name=\"NxCell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.1cm\" fo:border=\"0.5pt solid #808080\"/></style:style><style:style style:name=\"NxHead\" style:family=\"paragraph\"><style:text-properties fo:font-weight=\"bold\"/></style:style>");
    }
    let rows = table
        .rows
        .iter()
        .enumerate()
        .map(|(row, cells)| {
            let style = if table.header && row == 0 { " text:style-name=\"NxHead\"" } else { "" };
            let cells = cells
                .iter()
                .map(|cell| {
                    let lines = cell
                        .split('\n')
                        .map(|line| format!("<text:p{style}>{}</text:p>", xml_escape(line)))
                        .collect::<String>();
                    format!("<table:table-cell table:style-name=\"NxCell\" office:value-type=\"string\">{lines}</table:table-cell>")
                })
                .collect::<String>();
            if table.header && row == 0 {
                format!("<table:table-header-rows><table:table-row>{cells}</table:table-row></table:table-header-rows>")
            } else {
                format!("<table:table-row>{cells}</table:table-row>")
            }
        })
        .collect::<String>();
    format!("<table:table table:name=\"Tableau{index}\" table:style-name=\"NxTable{index}\"><table:table-column table:style-name=\"NxCol{index}\" table:number-columns-repeated=\"{columns}\"/>{rows}</table:table>")
}
pub fn import_odt(bytes: &[u8], title: &str) -> Result<document_core::Document, String> {
    let mut archive =
        ZipArchive::new(Cursor::new(bytes)).map_err(|e| format!("Invalid ODT archive: {e}"))?;
    if archive.len() > 4096 {
        return Err("ODT archive contains too many parts".into());
    }
    let xml = xml_entry(&mut archive, "content.xml")?;
    let doc =
        XmlDocument::parse(&xml).map_err(|e| format!("Invalid OpenDocument text XML: {e}"))?;
    let mut paragraphs = Vec::new();
    let mut comments = Vec::new();
    let mut revisions = Vec::new();
    let changes = crate::review::odt_changes(&doc);
    let tables: std::collections::HashMap<_, _> = doc
        .descendants()
        .filter_map(|n| odt_table(n).map(|t| (n.id(), t)))
        .collect();
    for node in doc.descendants().filter(|n| {
        tables.contains_key(&n.id())
            || ((local(*n, "p") || local(*n, "h"))
                && n.ancestors().any(|a| local(a, "text"))
                && !n.ancestors().any(|a| tables.contains_key(&a.id()) || local(a, "annotation") || local(a, "changed-region")))
    }) {
        if let Some(table) = tables.get(&node.id()) {
            paragraphs.push(Paragraph {
                runs: vec![],
                image: None,
                table: Some(table.clone()),
                id: id(),
                text: String::new(),
                style: TextStyle::default(),
            });
            continue;
        }
        let pid = id();
        comments.extend(crate::review::odt_comments(node, &pid));
        revisions.extend(crate::review::odt_revision(node, &pid, &changes));
        let mut text = String::new();
        text_descendants(node, &mut text);
        let mut style = TextStyle::default();
        if local(node, "h") {
            style.font_size = 22.0;
            style.bold = true;
        }
        let text_ns = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        let style_ns = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        let fo_ns = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
        if let Some(name) = node.attribute((text_ns, "style-name")) {
            if let Some(definition) = doc
                .descendants()
                .find(|n| local(*n, "style") && n.attribute((style_ns, "name")) == Some(name))
            {
                if let Some(properties) = definition
                    .children()
                    .find(|n| local(*n, "paragraph-properties"))
                {
                    style.page_break_before =
                        properties.attribute((fo_ns, "break-before")) == Some("page");
                    if let Some(fill) = properties.attribute((fo_ns, "background-color")).filter(|v| v.len() == 7 && v.starts_with('#')) {
                        style.shading = fill.to_string();
                    }
                    let solid = |name: &str| properties.attribute((fo_ns, name)).is_some_and(|v| v != "none");
                    if solid("border") {
                        style.border = "box".into();
                    } else if solid("border-bottom") {
                        style.border = "bottom".into();
                    } else if solid("border-left") {
                        style.border = "left".into();
                    }
                    if let Some(alignment) = properties.attribute((fo_ns, "text-align")) {
                        style.alignment = match alignment {
                            "center" => "center",
                            "end" | "right" => "right",
                            "justify" => "justify",
                            _ => "left",
                        }
                        .into();
                    }
                    if let Some(margin) = properties
                        .attribute((fo_ns, "margin-left"))
                        .and_then(|v| v.trim_end_matches("cm").parse::<f64>().ok())
                    {
                        style.indent = (margin.round() as u8).min(8);
                    }
                }
            }
        }
        let inherited = style.clone();
        if let Some(span) = node.descendants().find(|n| local(*n, "span")) {
            if let Some(name) = span.attribute((text_ns, "style-name")) {
                if let Some(definition) = doc
                    .descendants()
                    .find(|n| local(*n, "style") && n.attribute((style_ns, "name")) == Some(name))
                {
                    if let Some(properties) =
                        definition.children().find(|n| local(*n, "text-properties"))
                    {
                        style.bold = properties.attribute((fo_ns, "font-weight")) == Some("bold");
                        style.italic =
                            properties.attribute((fo_ns, "font-style")) == Some("italic");
                        style.underline = properties
                            .attribute((style_ns, "text-underline-style"))
                            .is_some_and(|v| v != "none");
                        if let Some(color) = properties.attribute((fo_ns, "color")) {
                            style.color = color.into();
                        }
                        if let Some(size) = properties
                            .attribute((fo_ns, "font-size"))
                            .and_then(|v| v.trim_end_matches("pt").parse::<f64>().ok())
                        {
                            style.font_size = (size * 4.0 / 3.0).clamp(6.0, 96.0);
                        }
                        if let Some(font) = properties.attribute((style_ns, "font-name")) {
                            style.font_family = font.into();
                        }
                    }
                }
            }
        }
        let mut paragraph = Paragraph {
            runs: vec![],
            image: None,
            table: None,
            id: pid,
            text,
            style,
        };
        crate::odt_rich::read(node, doc.root_element(), &mut paragraph, &inherited);
        if let Some(image) = read_image(&mut archive, node) {
            paragraph.text.clear();
            paragraph.runs.clear();
            paragraph.image = Some(image);
        }
        paragraphs.push(paragraph);
    }
    if paragraphs.is_empty() {
        paragraphs.push(Paragraph {
            runs: vec![],
            image: None,
            table: None,
            id: id(),
            text: String::new(),
            style: TextStyle::default(),
        });
    }
    let mut result = document_core::Document::new(
        "writer",
        if title.is_empty() {
            "Document importé"
        } else {
            title
        },
    )?;
    result.page_layout = xml_entry(&mut archive, "styles.xml")
        .ok()
        .and_then(|s| crate::page_layout::read_odt(&s))
        .or_else(|| crate::page_layout::read_odt(&xml));
    result.comments = comments;
    result.revisions = revisions;
    result.content = document_core::Content::Writer { paragraphs };
    Ok(result)
}

pub fn export_odt(
    title: &str,
    paragraphs: &[Paragraph],
    layout: &document_core::PageLayout,
    notes: &[(usize, &document_core::Footnote)],
    review: &crate::review::Review,
) -> Result<Vec<u8>, String> {
    use base64::Engine as _;
    let mut body = String::new();
    let mut styles = String::new();
    let mut media: Vec<(String, Vec<u8>)> = vec![];
    let mut manifest_media = String::new();
    for (index, p) in paragraphs.iter().enumerate() {
        let s = &p.style;
        let alignment = match s.alignment.as_str() {
            "center" => "center",
            "right" => "end",
            "justify" => "justify",
            _ => "start",
        };
        styles.push_str(&format!("<style:style style:name=\"P{index}\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"{alignment}\" fo:margin-left=\"{}cm\" fo:margin-bottom=\"{}pt\"/></style:style><style:style style:name=\"T{index}\" style:family=\"text\"><style:text-properties fo:font-weight=\"{}\" fo:font-style=\"{}\" style:text-underline-style=\"{}\" fo:color=\"{}\" fo:font-size=\"{}pt\" style:font-name=\"{}\"/></style:style>", s.indent, s.spacing_after.max(0.0), if s.bold { "bold" } else { "normal" }, if s.italic { "italic" } else { "normal" }, if s.underline { "solid" } else { "none" }, xml_escape(&s.color), (s.font_size * 0.75).max(1.0), xml_escape(&s.font_family)));
        if s.shading != "none" || s.border != "none" {
            let mut extra = String::new();
            if s.shading != "none" {
                extra.push_str(&format!("fo:background-color=\"{}\" ", xml_escape(&s.shading)));
            }
            match s.border.as_str() {
                "box" => extra.push_str("fo:border=\"0.75pt solid #808080\" "),
                "bottom" => extra.push_str("fo:border-bottom=\"0.75pt solid #808080\" "),
                "left" => extra.push_str("fo:border-left=\"0.75pt solid #808080\" "),
                _ => {}
            }
            styles = styles.replace(&format!("<style:style style:name=\"P{index}\" style:family=\"paragraph\"><style:paragraph-properties "), &format!("<style:style style:name=\"P{index}\" style:family=\"paragraph\"><style:paragraph-properties {extra}"));
        }
        if s.page_break_before {
            styles = styles.replace(&format!("<style:style style:name=\"P{index}\" style:family=\"paragraph\"><style:paragraph-properties "), &format!("<style:style style:name=\"P{index}\" style:family=\"paragraph\"><style:paragraph-properties fo:break-before=\"page\" "));
        }
        if let Some(table) = &p.table {
            body.push_str(&table_xml(table, index, &mut styles));
            continue;
        }
        let contents = if let Some(image) = &p.image {
            let key = crate::docx::image_key(p);
            let ext = crate::docx::image_extension(&image.mime);
            let bytes = base64::engine::general_purpose::STANDARD
                .decode(&image.data)
                .map_err(|e| e.to_string())?;
            media.push((format!("Pictures/{key}.{ext}"), bytes));
            manifest_media.push_str(&format!("<manifest:file-entry manifest:full-path=\"Pictures/{key}.{ext}\" manifest:media-type=\"{}\"/>", xml_escape(&image.mime)));
            format!("<draw:frame draw:name=\"{key}\" text:anchor-type=\"as-char\" svg:width=\"{:.3}cm\" svg:height=\"{:.3}cm\"><draw:image xlink:href=\"Pictures/{key}.{ext}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/><svg:desc>{}</svg:desc></draw:frame>", image.width * 2.54 / 96.0, image.height * 2.54 / 96.0, xml_escape(&image.alt))
        } else {
            crate::odt_rich::contents(p, index, &mut styles)
        };
        let (before, after) = crate::review::odt_paragraph(review, index);
        let contents = format!("{before}{contents}{}{after}", crate::footnotes::odt_notes(notes, index));
        body.push_str(&format!(
            "<text:p text:style-name=\"P{index}\">{contents}</text:p>"
        ));
    }
    let font_names = paragraphs
        .iter()
        .flat_map(|p| {
            std::iter::once(p.style.font_family.clone())
                .chain(p.runs.iter().map(|r| r.style.font_family.clone()))
        })
        .collect::<std::collections::BTreeSet<_>>();
    let fonts = font_names
        .iter()
        .map(|name| {
            let name = xml_escape(name);
            format!("<style:font-face style:name=\"{name}\" svg:font-family=\"{name}\"/>")
        })
        .collect::<String>();
    let tracked = crate::review::odt_tracked_changes(review);
    let content = format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" office:version=\"1.3\"><office:font-face-decls>{fonts}</office:font-face-decls><office:automatic-styles>{styles}</office:automatic-styles><office:body><office:text>{tracked}{body}</office:text></office:body></office:document-content>");
    let manifest = format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>{manifest_media}</manifest:manifest>");
    let meta = format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" office:version=\"1.3\"><office:meta><dc:title>{}</dc:title></office:meta></office:document-meta>", xml_escape(title));
    write_zip_with(
        vec![
            ("content.xml", content),
            ("styles.xml", crate::page_layout::odt_styles(layout)),
            ("META-INF/manifest.xml", manifest),
            ("meta.xml", meta),
        ],
        media,
        true,
    )
}

#[cfg(test)]
mod tests {
    #[test]
    fn docx_and_odt_round_trip_text_and_supported_paragraph_formatting() {
        let mut doc = document_core::Document::new("writer", "Essai Nexolume").unwrap();
        let expected = "Bonjour Nexolume & Word";
        if let document_core::Content::Writer { paragraphs } = &mut doc.content {
            paragraphs[0].text = expected.into();
            paragraphs[0].style.bold = true;
            paragraphs[0].style.italic = true;
            paragraphs[0].style.underline = true;
            paragraphs[0].style.alignment = "center".into();
        }
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Essai Nexolume").unwrap();
            match opened.content {
                document_core::Content::Writer { paragraphs } => {
                    assert_eq!(paragraphs[0].text, expected);
                    assert!(paragraphs[0].style.bold);
                    assert!(paragraphs[0].style.italic);
                    assert!(paragraphs[0].style.underline);
                    assert_eq!(paragraphs[0].style.alignment, "center");
                }
                _ => panic!("wrong kind"),
            }
        }
    }

    #[test]
    fn comments_and_revisions_round_trip_through_docx_and_odt() {
        let mut doc = document_core::Document::new("writer", "Revue").unwrap();
        let document_core::Content::Writer { paragraphs } = &mut doc.content else {
            panic!("wrong kind")
        };
        paragraphs[0].text = "Texte initial".into();
        let mut second = paragraphs[0].clone();
        second.id = "second".into();
        second.text = "Ajout final".into();
        paragraphs.push(second);
        let first_id = paragraphs[0].id.clone();
        let second_id = paragraphs[1].id.clone();
        doc.comments = vec![
            document_core::Comment {
                id: "c1".into(),
                paragraph: first_id.clone(),
                author: "Ana".into(),
                text: "A relire".into(),
                date: "2026-01-02T10:00:00Z".into(),
                resolved: false,
            },
            document_core::Comment {
                id: "c2".into(),
                paragraph: second_id.clone(),
                author: "Luc".into(),
                text: "Fait".into(),
                date: "2026-01-03T09:00:00Z".into(),
                resolved: true,
            },
        ];
        doc.revisions = vec![
            document_core::Revision {
                id: "r1".into(),
                paragraph: first_id.clone(),
                author: "Luc".into(),
                date: "2026-01-03T09:00:00Z".into(),
                kind: "edit".into(),
                old: "Ancien texte".into(),
            },
            document_core::Revision {
                id: "r2".into(),
                paragraph: second_id.clone(),
                author: "Eve".into(),
                date: "2026-01-04T08:00:00Z".into(),
                kind: "insert".into(),
                old: String::new(),
            },
        ];
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Revue").unwrap();
            let document_core::Content::Writer { paragraphs } = &opened.content else {
                panic!("wrong kind")
            };
            let texts: Vec<_> = paragraphs.iter().map(|p| p.text.as_str()).collect();
            assert_eq!(texts, ["Texte initial", "Ajout final"], "{ext}");
            let (first, second) = (paragraphs[0].id.clone(), paragraphs[1].id.clone());
            let comments: Vec<_> = opened
                .comments
                .iter()
                .map(|c| (c.author.as_str(), c.text.as_str(), c.resolved, c.paragraph.as_str()))
                .collect();
            assert_eq!(
                comments,
                [("Ana", "A relire", false, first.as_str()), ("Luc", "Fait", true, second.as_str())],
                "{ext}"
            );
            let revisions: Vec<_> = opened
                .revisions
                .iter()
                .map(|r| (r.kind.as_str(), r.author.as_str(), r.old.as_str(), r.paragraph.as_str()))
                .collect();
            assert_eq!(
                revisions,
                [("edit", "Luc", "Ancien texte", first.as_str()), ("insert", "Eve", "", second.as_str())],
                "{ext}"
            );
        }
    }

    #[test]
    fn images_round_trip_through_docx_and_odt() {
        let png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        let mut doc = document_core::Document::new("writer", "Images").unwrap();
        if let document_core::Content::Writer { paragraphs } = &mut doc.content {
            let mut image = paragraphs[0].clone();
            image.id = "image".into();
            image.image = Some(document_core::ImageData {
                mime: "image/png".into(),
                data: png.into(),
                width: 192.0,
                height: 96.0,
                alt: "Pixel & co".into(),
            });
            paragraphs.push(image);
        }
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Images").unwrap();
            let document_core::Content::Writer { paragraphs } = opened.content else {
                panic!("wrong kind")
            };
            let image = paragraphs[1].image.clone().expect(ext);
            assert_eq!(image.data, png, "{ext}");
            assert!((image.width - 192.0).abs() < 0.5 && (image.height - 96.0).abs() < 0.5, "{ext}");
            assert_eq!(image.alt, "Pixel & co", "{ext}");
            assert!(paragraphs[1].text.is_empty(), "{ext}");
        }
    }

    #[test]
    fn hyperlinks_round_trip_through_docx_odt_and_edited_packages() {
        let mut doc = document_core::Document::new("writer", "Liens").unwrap();
        let id = {
            let document_core::Content::Writer { paragraphs } = &mut doc.content else { panic!() };
            paragraphs[0].text = "Voir Nexolume & plus".into();
            paragraphs[0].id.clone()
        };
        let command = |id: &str| serde_json::from_value(serde_json::json!({"command":"FormatText","params":{"id":id,"start":5,"end":13,"style":{"link":"https://example.com/a?b=1&c=2","underline":true,"color":"#0563C1"}}})).unwrap();
        if let document_core::Content::Writer { paragraphs } = &mut doc.content {
            writer_engine::apply(paragraphs, &command(&id)).unwrap();
            assert!(writer_engine::apply(paragraphs, &serde_json::from_value(serde_json::json!({"command":"FormatText","params":{"id":id,"start":0,"end":3,"style":{"link":"javascript:alert(1)"}}})).unwrap()).is_err());
        }
        let links = |d: &document_core::Document| {
            let document_core::Content::Writer { paragraphs } = &d.content else { panic!() };
            paragraphs[0].runs.iter().filter(|r| !r.style.link.is_empty()).map(|r| (r.start, r.length, r.style.link.clone())).collect::<Vec<_>>()
        };
        let expected = vec![(5, 8, "https://example.com/a?b=1&c=2".to_string())];
        assert_eq!(links(&doc), expected);
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Liens").unwrap();
            assert_eq!(links(&opened), expected, "{ext}");
            if ext == "docx" {
                // Edit an opened package: add a second link and remove none.
                let mut edited = opened.clone();
                if let document_core::Content::Writer { paragraphs } = &mut edited.content {
                    let id = paragraphs[0].id.clone();
                    let second = serde_json::from_value(serde_json::json!({"command":"FormatText","params":{"id":id,"start":0,"end":4,"style":{"link":"mailto:a@b.fr"}}})).unwrap();
                    writer_engine::apply(paragraphs, &second).unwrap();
                }
                let bytes = super::super::export_office(&edited, "docx").unwrap();
                let again = super::super::import_office("docx", &bytes, "Liens").unwrap();
                assert_eq!(links(&again).len(), 2);
                assert_eq!(links(&again)[0].2, "mailto:a@b.fr");
            }
        }
    }

    #[test]
    fn paragraph_shading_and_borders_round_trip() {
        let mut doc = document_core::Document::new("writer", "Cadre").unwrap();
        if let document_core::Content::Writer { paragraphs } = &mut doc.content {
            paragraphs[0].text = "Encadré".into();
            paragraphs[0].style.shading = "#FFF2CC".into();
            paragraphs[0].style.border = "box".into();
            let bad = serde_json::from_value(serde_json::json!({"command":"SetParagraph","params":{"id":paragraphs[0].id,"text":"x","style":{"border":"wavy"}}})).unwrap();
            assert!(writer_engine::apply(paragraphs, &bad).is_err());
        }
        let read = |d: &document_core::Document| {
            let document_core::Content::Writer { paragraphs } = &d.content else { panic!() };
            (paragraphs[0].style.shading.clone(), paragraphs[0].style.border.clone())
        };
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Cadre").unwrap();
            assert_eq!(read(&opened), ("#FFF2CC".into(), "box".into()), "{ext}");
            if ext == "docx" {
                let mut edited = opened.clone();
                if let document_core::Content::Writer { paragraphs } = &mut edited.content {
                    paragraphs[0].style.border = "bottom".into();
                    paragraphs[0].style.shading = "none".into();
                }
                let bytes = super::super::export_office(&edited, "docx").unwrap();
                let again = super::super::import_office("docx", &bytes, "Cadre").unwrap();
                assert_eq!(read(&again), ("none".into(), "bottom".into()));
            }
        }
    }

    #[test]
    fn tables_round_trip_through_docx_odt_and_edited_packages() {
        use document_core::{Content, TableData};
        let table = TableData {
            rows: vec![
                vec!["Nom".into(), "Prix".into()],
                vec!["Café & thé".into(), "3,50
TTC".into()],
            ],
            header: true,
        };
        let mut doc = document_core::Document::new("writer", "Tableaux").unwrap();
        if let Content::Writer { paragraphs } = &mut doc.content {
            let mut t = paragraphs[0].clone();
            t.id = "table".into();
            t.text.clear();
            t.table = Some(table.clone());
            paragraphs.push(t);
            let mut after = paragraphs[0].clone();
            after.id = "after".into();
            after.text = "Fin".into();
            paragraphs.push(after);
        }
        for ext in ["docx", "odt"] {
            let bytes = super::super::export_office(&doc, ext).unwrap();
            let opened = super::super::import_office(ext, &bytes, "Tableaux").unwrap();
            let Content::Writer { paragraphs } = &opened.content else {
                panic!("wrong kind")
            };
            assert_eq!(paragraphs.len(), 3, "{ext}");
            assert_eq!(paragraphs[1].table.as_ref(), Some(&table), "{ext}");
            assert_eq!(paragraphs[2].text, "Fin", "{ext}");
            assert!(paragraphs[1].text.is_empty(), "{ext}");
        }
        let bytes = super::super::export_office(&doc, "docx").unwrap();
        let mut opened = super::super::import_office("docx", &bytes, "Tableaux").unwrap();
        let unchanged = super::super::export_office(&opened, "docx").unwrap();
        assert_eq!(unchanged, bytes);
        if let Content::Writer { paragraphs } = &mut opened.content {
            paragraphs[1].table.as_mut().unwrap().rows[1][0] = "Thé".into();
            paragraphs[2].text = "Fin modifiée".into();
            let mut extra = paragraphs[1].clone();
            extra.id = "extra".into();
            paragraphs.push(extra);
        }
        let edited = super::super::export_office(&opened, "docx").unwrap();
        let reopened = super::super::import_office("docx", &edited, "Tableaux").unwrap();
        let Content::Writer { paragraphs } = &reopened.content else {
            panic!("wrong kind")
        };
        assert_eq!(paragraphs.len(), 4);
        assert_eq!(paragraphs[1].table.as_ref().unwrap().rows[1][0], "Thé");
        assert_eq!(paragraphs[2].text, "Fin modifiée");
        assert!(paragraphs[3].table.is_some());
        let native = crate::encode(&reopened).unwrap();
        let decoded = crate::decode(&native).unwrap();
        assert_eq!(decoded.content, reopened.content);
    }
}
