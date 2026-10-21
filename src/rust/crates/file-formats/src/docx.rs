use crate::docx_styles::{self as styles, Catalog, WORD};
use base64::Engine as _;
use document_core::{Content, Document, OfficeSource, Paragraph};
use roxmltree::Document as Xml;
use std::{
    collections::BTreeMap,
    io::{Cursor, Read, Write},
};
use zip::{write::SimpleFileOptions, ZipArchive, ZipWriter};

type Archive<'a> = ZipArchive<Cursor<&'a [u8]>>;
#[cfg(test)]
#[path = "docx_tests.rs"]
mod tests;
pub fn read_part(archive: &mut Archive<'_>, name: &str) -> Result<Option<String>, String> {
    let mut entry = match archive.by_name(name) {
        Ok(e) => e,
        Err(zip::result::ZipError::FileNotFound) => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    if entry.size() > 12 * 1024 * 1024 {
        return Err(format!("DOCX XML part exceeds 12 MiB: {name}"));
    }
    let mut text = String::new();
    entry.read_to_string(&mut text).map_err(|e| e.to_string())?;
    Ok(Some(text))
}
fn archive(bytes: &[u8]) -> Result<Archive<'_>, String> {
    if bytes.len() > 10 * 1024 * 1024 {
        return Err("DOCX exceeds 10 MiB".into());
    }
    let archive =
        ZipArchive::new(Cursor::new(bytes)).map_err(|e| format!("Invalid DOCX package: {e}"))?;
    if archive.len() > 4096 {
        return Err("Too many DOCX package parts".into());
    }
    Ok(archive)
}
fn resolve(base: &str, target: &str) -> Result<String, String> {
    if target.contains(':') || target.contains('\\') {
        return Err("Invalid internal DOCX relationship".into());
    }
    let mut parts: Vec<&str> = if target.starts_with('/') {
        vec![]
    } else {
        base.split('/').filter(|p| !p.is_empty()).collect()
    };
    for part in target.split('/') {
        match part {
            "" | "." => (),
            ".." => {
                parts.pop().ok_or("DOCX relationship escapes package")?;
            }
            p => parts.push(p),
        }
    }
    Ok(parts.join("/"))
}
fn related(rels: &str, base: &str, kind: &str) -> Result<Option<String>, String> {
    let xml = Xml::parse(rels).map_err(|e| e.to_string())?;
    xml.root_element()
        .children()
        .find(|n| {
            n.is_element()
                && n.attribute("Type")
                    .is_some_and(|t| t.ends_with(&format!("/{kind}")))
                && n.attribute("TargetMode") != Some("External")
        })
        .and_then(|n| n.attribute("Target"))
        .map(|t| resolve(base, t))
        .transpose()
}
fn folder(path: &str) -> &str {
    path.rsplit_once('/').map(|p| p.0).unwrap_or("")
}
fn rels_path(path: &str) -> String {
    let (dir, file) = path.rsplit_once('/').unwrap_or(("", path));
    format!(
        "{}{sep}_rels/{file}.rels",
        dir,
        sep = if dir.is_empty() { "" } else { "/" }
    )
}
pub fn import(bytes: &[u8], title: &str) -> Result<Document, String> {
    let mut package = archive(bytes)?;
    let root_rels =
        read_part(&mut package, "_rels/.rels")?.ok_or("Missing DOCX package relationships")?;
    let main = related(&root_rels, "", "officeDocument")?
        .ok_or("Missing DOCX main document relationship")?;
    let xml = read_part(&mut package, &main)?.ok_or("Missing Word document part")?;
    let doc = Xml::parse(&xml).map_err(|e| e.to_string())?;
    if !styles::word(doc.root_element(), "document") {
        return Err("The package is not a WordprocessingML document".into());
    }
    let rels = read_part(&mut package, &rels_path(&main))?.unwrap_or_else(empty_rels);
    let mut load = |kind: &str| -> Result<Option<String>, String> {
        if let Some(path) = related(&rels, folder(&main), kind)? {
            read_part(&mut package, &path)
        } else {
            Ok(None)
        }
    };
    let styles_text = load("styles")?;
    let numbering_text = load("numbering")?;
    let theme_text = load("theme")?;
    let comments_text = load("comments")?;
    let style_doc = styles_text
        .as_deref()
        .map(Xml::parse)
        .transpose()
        .map_err(|e| e.to_string())?;
    let numbering_doc = numbering_text
        .as_deref()
        .map(Xml::parse)
        .transpose()
        .map_err(|e| e.to_string())?;
    let theme_doc = theme_text
        .as_deref()
        .map(Xml::parse)
        .transpose()
        .map_err(|e| e.to_string())?;
    let links = Xml::parse(&rels)
        .map_err(|e| e.to_string())?
        .root_element()
        .children()
        .filter(|n| {
            n.is_element()
                && n.attribute("TargetMode") == Some("External")
                && n.attribute("Type").is_some_and(|t| t.ends_with("/hyperlink"))
        })
        .filter_map(|n| Some((n.attribute("Id")?.to_string(), n.attribute("Target")?.to_string())))
        .filter(|(_, target)| !target.is_empty() && writer_engine::rich_text::valid_link(target))
        .collect();
    let catalog = Catalog::new(
        style_doc.as_ref().map(Xml::root_element),
        numbering_doc.as_ref().map(Xml::root_element),
        theme_doc.as_ref().map(Xml::root_element),
    )
    .with_links(links);
    let nodes = styles::paragraphs(doc.root_element());
    let mut paragraphs: Vec<_> = nodes
        .iter()
        .map(|p| {
            let mut paragraph = catalog.read(*p);
            paragraph.table = styles::simple_table(*p);
            paragraph
        })
        .collect();
    read_images(&mut package, &rels, folder(&main), &nodes, &mut paragraphs)?;
    let mut result = Document::new(
        "writer",
        if title.trim().is_empty() {
            "Document Word"
        } else {
            title
        },
    )?;
    let source_paragraphs = paragraphs.clone();
    let (comments, revisions) =
        crate::review::read_docx(&nodes, &source_paragraphs, comments_text.as_deref())?;
    result.comments = comments.clone();
    result.revisions = revisions.clone();
    if paragraphs.is_empty() {
        if let Content::Writer {
            paragraphs: default,
        } = &result.content
        {
            paragraphs = default.clone();
        }
    }
    for p in &paragraphs {
        writer_engine::validate(p)?;
    }
    result.page_layout = crate::page_layout::read_word(&xml);
    result.office_source = Some(OfficeSource {
        page_layout: result.page_layout.clone(),
        format: "docx".into(),
        archive: base64::engine::general_purpose::STANDARD.encode(bytes),
        main_part: main,
        paragraphs: source_paragraphs,
        comments,
        revisions,
    });
    result.content = Content::Writer { paragraphs };
    Ok(result)
}
pub fn export(document: &Document, paragraphs: &[Paragraph]) -> Result<Vec<u8>, String> {
    for p in paragraphs {
        if p.text
            .chars()
            .any(|c| c < ' ' && !matches!(c, '\n' | '\r' | '\t'))
        {
            return Err("Le texte contient un caractère de contrôle interdit en XML.".into());
        }
    }
    if let Some(source) = document
        .office_source
        .as_ref()
        .filter(|s| {
            s.format == "docx" && s.comments == document.comments && s.revisions == document.revisions
        })
    {
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(&source.archive)
            .map_err(|e| e.to_string())?;
        let mut package = archive(&bytes)?;
        let xml = read_part(&mut package, &source.main_part)?
            .ok_or("Original DOCX document part is missing")?;
        let original_xml = Xml::parse(&xml).map_err(|e| e.to_string())?;
        if !styles::word(original_xml.root_element(), "document")
            || styles::paragraphs(original_xml.root_element()).len() != source.paragraphs.len()
        {
            return Err("Le fichier Word d’origine est invalide ou incomplet.".into());
        }
        if paragraphs == source.paragraphs && document.page_layout == source.page_layout {
            return Ok(bytes);
        }
        let rels_name = rels_path(&source.main_part);
        let rels = read_part(&mut package, &rels_name)?.unwrap_or_else(empty_rels);
        let numbering_path = related(&rels, folder(&source.main_part), "numbering")?
            .unwrap_or_else(|| {
                format!("{}/numbering.xml", folder(&source.main_part))
                    .trim_start_matches('/')
                    .to_string()
            });
        let numbering = read_part(&mut package, &numbering_path)?;
        let max_id = numbering
            .as_deref()
            .and_then(|s| Xml::parse(s).ok())
            .map(|d| {
                d.descendants()
                    .flat_map(|n| n.attributes())
                    .filter(|a| matches!(a.name(), "numId" | "abstractNumId"))
                    .filter_map(|a| a.value().parse::<u32>().ok())
                    .max()
                    .unwrap_or(0)
            })
            .unwrap_or(0);
        let bullet = max_id
            .checked_add(1)
            .ok_or("Numbering identifiers exhausted")?;
        let number = bullet
            .checked_add(1)
            .ok_or("Numbering identifiers exhausted")?;
        let needs_numbering = paragraphs.iter().any(|p| {
            p.style.list != "none"
                && source
                    .paragraphs
                    .iter()
                    .find(|old| old.id == p.id)
                    .is_none_or(|old| old.style.list != p.style.list)
        });
        let mut rewritten =
            crate::docx_edit::rewrite(&xml, &source.paragraphs, paragraphs, bullet, number)?;
        if document.page_layout != source.page_layout {
            rewritten = crate::page_layout::patch_word(
                &rewritten,
                &document.page_layout.clone().unwrap_or_default(),
            )?;
        }
        let mut replacements = BTreeMap::from([(source.main_part.clone(), rewritten)]);
        if needs_numbering {
            replacements.insert(
                numbering_path.clone(),
                numbering_xml(numbering.as_deref(), bullet, number)?,
            );
            if related(&rels, folder(&source.main_part), "numbering")?.is_none() {
                let target = numbering_path
                    .strip_prefix(&format!("{}/", folder(&source.main_part)))
                    .unwrap_or(&numbering_path);
                let id = (0..)
                    .map(|i| format!("NexolumeNumbering{i}"))
                    .find(|id| !rels.contains(id))
                    .unwrap();
                replacements.insert(rels_name.clone(), append(&rels, &format!("<Relationship xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"{}\"/>", escape(target)))?);
            }
            let types = read_part(&mut package, "[Content_Types].xml")?
                .ok_or("Missing DOCX content types")?;
            if !types.contains(&format!("/{numbering_path}")) {
                replacements.insert("[Content_Types].xml".into(), append(&types, &format!("<Override xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" PartName=\"/{numbering_path}\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>"))?);
            }
        }
        let (binary, image_rels, image_types) =
            image_parts(paragraphs, &source.paragraphs, folder(&source.main_part))?;
        let rels_now = replacements.get(&rels_name).cloned().unwrap_or(rels);
        let links = link_rels(paragraphs, &rels_now);
        let rels_now = if links.is_empty() { rels_now } else { append(&rels_now, &links)? };
        if !links.is_empty() || !binary.is_empty() {
            replacements.insert(rels_name.clone(), rels_now.clone());
        }
        if !binary.is_empty() {
            replacements.insert(rels_name, append(&rels_now, &image_rels)?);
            let types_now = match replacements.get("[Content_Types].xml") {
                Some(t) => t.clone(),
                None => read_part(&mut package, "[Content_Types].xml")?
                    .ok_or("Missing DOCX content types")?,
            };
            replacements.insert(
                "[Content_Types].xml".into(),
                add_image_types(&types_now, &image_types)?,
            );
        }
        return write_package(Some(&mut package), replacements, binary);
    }
    let notes = crate::footnotes::ordered(document, paragraphs);
    let review = crate::review::collect(document, paragraphs);
    let mut body = paragraphs
        .iter()
        .enumerate()
        .map(|(index, p)| {
            let xml = paragraph_xml(p, "w", 1, 2);
            let references = crate::footnotes::docx_references(&notes, index);
            let xml = match xml.rfind("</w:p>") {
                Some(end) if !references.is_empty() => format!("{}{references}{}", &xml[..end], &xml[end..]),
                _ => xml,
            };
            crate::review::docx_paragraph(&review, index, xml, &p.text)
        })
        .collect::<String>();
    if paragraphs.last().is_some_and(|p| p.table.is_some()) {
        body.push_str("<w:p/>");
    }
    let title = escape(&document.title);
    let parts = BTreeMap::from([
        ("[Content_Types].xml".into(), "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>".into()),
        ("_rels/.rels".into(), "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>".into()),
        ("word/document.xml".into(), format!("<w:document xmlns:w=\"{WORD}\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>")),
        ("word/_rels/document.xml.rels".into(), "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>".into()),
        ("word/numbering.xml".into(), numbering_xml(None, 1, 2)?),
        ("docProps/core.xml".into(), format!("<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title></cp:coreProperties>")),
    ]);
    let mut parts = parts;
    if !notes.is_empty() {
        parts.insert("word/footnotes.xml".into(), crate::footnotes::docx_part(&notes, WORD));
        let rels = parts.get_mut("word/_rels/document.xml.rels").unwrap();
        *rels = append(rels, "<Relationship Id=\"rIdFootnotes\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes\" Target=\"footnotes.xml\"/>")?;
        let types = parts.get_mut("[Content_Types].xml").unwrap();
        *types = append(types, "<Override PartName=\"/word/footnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml\"/>")?;
    }
    if !review.comments.is_empty() {
        parts.insert("word/comments.xml".into(), crate::review::docx_comments_part(&review, WORD));
        let rels = parts.get_mut("word/_rels/document.xml.rels").unwrap();
        *rels = append(rels, "<Relationship Id=\"rIdComments\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"comments.xml\"/>")?;
        let types = parts.get_mut("[Content_Types].xml").unwrap();
        *types = append(types, "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/>")?;
    }
    let links = link_rels(paragraphs, "");
    if !links.is_empty() {
        let rels = parts.get_mut("word/_rels/document.xml.rels").unwrap();
        *rels = append(rels, &links)?;
    }
    let (binary, image_rels, image_types) = image_parts(paragraphs, &[], "word")?;
    if !binary.is_empty() {
        let rels = parts.get_mut("word/_rels/document.xml.rels").unwrap();
        *rels = append(rels, &image_rels)?;
        let types = parts.get_mut("[Content_Types].xml").unwrap();
        *types = add_image_types(types, &image_types)?;
    }
    let main = parts.get_mut("word/document.xml").unwrap();
    *main =
        crate::page_layout::patch_word(main, &document.page_layout.clone().unwrap_or_default())?;
    write_package(None, parts, binary)
}
const IMAGE_REL: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
const EMU: f64 = 9525.0;
pub fn image_key(p: &Paragraph) -> String {
    let id: String = p
        .id
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .take(40)
        .collect();
    format!("nxi{id}")
}
pub fn image_extension(mime: &str) -> &'static str {
    match mime {
        "image/jpeg" => "jpeg",
        "image/gif" => "gif",
        _ => "png",
    }
}
pub fn image_mime(extension: &str) -> Option<&'static str> {
    match extension.to_ascii_lowercase().as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" => Some("image/jpeg"),
        "gif" => Some("image/gif"),
        _ => None,
    }
}
fn mime_static(mime: &str) -> &'static str {
    match mime {
        "image/jpeg" => "image/jpeg",
        "image/gif" => "image/gif",
        _ => "image/png",
    }
}
fn drawing_xml(p: &Paragraph, q: &str) -> String {
    let Some(image) = &p.image else {
        return String::new();
    };
    let key = image_key(p);
    let ext = image_extension(&image.mime);
    let cx = (image.width * EMU).round() as u64;
    let cy = (image.height * EMU).round() as u64;
    let number = key
        .bytes()
        .fold(1u32, |h, b| h.wrapping_mul(31).wrapping_add(b as u32))
        % 1_000_000
        + 1;
    let alt = escape(&image.alt);
    format!(
        "<{q}:r><{q}:drawing><wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{number}\" name=\"Image {number}\" descr=\"{alt}\"/><a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"{key}.{ext}\" descr=\"{alt}\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:embed=\"{key}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></{q}:drawing></{q}:r>"
    )
}
type ImageParts = (
    BTreeMap<String, Vec<u8>>,
    String,
    Vec<(&'static str, &'static str)>,
);
/// Media parts, relationships and content types for the images that differ from `known`.
fn image_parts(
    paragraphs: &[Paragraph],
    known: &[Paragraph],
    media_folder: &str,
) -> Result<ImageParts, String> {
    let mut binary = BTreeMap::new();
    let mut rels = String::new();
    let mut types: Vec<(&'static str, &'static str)> = vec![];
    let prefix = if media_folder.is_empty() {
        String::new()
    } else {
        format!("{media_folder}/")
    };
    for p in paragraphs {
        let Some(image) = &p.image else { continue };
        if known.iter().any(|k| k.id == p.id && k.image == p.image) {
            continue;
        }
        let ext = image_extension(&image.mime);
        let key = image_key(p);
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(&image.data)
            .map_err(|e| e.to_string())?;
        binary.insert(format!("{prefix}media/{key}.{ext}"), bytes);
        rels += &format!("<Relationship xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" Id=\"{key}\" Type=\"{IMAGE_REL}\" Target=\"media/{key}.{ext}\"/>");
        if !types.iter().any(|t| t.0 == ext) {
            types.push((ext, mime_static(&image.mime)));
        }
    }
    Ok((binary, rels, types))
}
fn add_image_types(types_xml: &str, types: &[(&str, &str)]) -> Result<String, String> {
    let mut out = types_xml.to_string();
    for (ext, mime) in types {
        if !out
            .to_ascii_lowercase()
            .contains(&format!("extension=\"{ext}\""))
        {
            out = append(&out, &format!("<Default xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" Extension=\"{ext}\" ContentType=\"{mime}\"/>"))?;
        }
    }
    Ok(out)
}
fn read_bytes(archive: &mut Archive<'_>, name: &str) -> Result<Option<Vec<u8>>, String> {
    let mut entry = match archive.by_name(name) {
        Ok(e) => e,
        Err(zip::result::ZipError::FileNotFound) => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    if entry.size() > 8 * 1024 * 1024 {
        return Err(format!("DOCX media part exceeds 8 MiB: {name}"));
    }
    let mut bytes = vec![];
    entry.read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    Ok(Some(bytes))
}
fn read_images(
    package: &mut Archive<'_>,
    rels: &str,
    base: &str,
    nodes: &[roxmltree::Node<'_, '_>],
    paragraphs: &mut [Paragraph],
) -> Result<(), String> {
    let rels_xml = Xml::parse(rels).map_err(|e| e.to_string())?;
    for (node, paragraph) in nodes.iter().zip(paragraphs.iter_mut()) {
        if !paragraph.text.is_empty() {
            continue;
        }
        let Some(drawing) = node.descendants().find(|n| styles::word(*n, "drawing")) else {
            continue;
        };
        let Some(embed) = drawing
            .descendants()
            .find(|n| n.is_element() && n.tag_name().name() == "blip")
            .and_then(|n| n.attributes().find(|a| a.name() == "embed"))
            .map(|a| a.value().to_string())
        else {
            continue;
        };
        let Some(target) = rels_xml
            .root_element()
            .children()
            .find(|n| {
                n.is_element()
                    && n.attribute("Id") == Some(embed.as_str())
                    && n.attribute("TargetMode") != Some("External")
            })
            .and_then(|n| n.attribute("Target"))
        else {
            continue;
        };
        let path = resolve(base, target)?;
        let Some(mime) = path.rsplit_once('.').and_then(|e| image_mime(e.1)) else {
            continue;
        };
        let Some(bytes) = read_bytes(package, &path)? else {
            continue;
        };
        let extent = drawing
            .descendants()
            .find(|n| n.is_element() && n.tag_name().name() == "extent");
        let emu = |name: &str| {
            extent
                .and_then(|n| n.attribute(name))
                .and_then(|v| v.parse::<f64>().ok())
                .map(|v| v / EMU)
                .filter(|v| v.is_finite() && *v >= 8.0)
        };
        let alt = drawing
            .descendants()
            .find(|n| n.is_element() && n.tag_name().name() == "docPr")
            .and_then(|n| n.attribute("descr"))
            .unwrap_or("")
            .chars()
            .take(500)
            .collect();
        paragraph.image = Some(document_core::ImageData {
            mime: mime.into(),
            data: base64::engine::general_purpose::STANDARD.encode(bytes),
            width: emu("cx").unwrap_or(320.0).min(6000.0),
            height: emu("cy").unwrap_or(240.0).min(6000.0),
            alt,
        });
    }
    Ok(())
}
const REL_NS: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const LINK_REL: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
/// Stable relationship id for a hyperlink target (FNV-1a hash of the URL).
pub fn link_id(url: &str) -> String {
    let hash = url
        .bytes()
        .fold(0xcbf29ce484222325u64, |h, b| (h ^ u64::from(b)).wrapping_mul(0x100000001b3));
    format!("NexolumeLink{hash:016x}")
}
/// External relationships for every hyperlink used by `paragraphs` that `rels` does not declare yet.
pub fn link_rels(paragraphs: &[Paragraph], rels: &str) -> String {
    let mut seen = std::collections::BTreeSet::new();
    let mut result = String::new();
    for p in paragraphs {
        for run in &p.runs {
            let id = link_id(&run.style.link);
            if !run.style.link.is_empty() && !rels.contains(&format!("\"{id}\"")) && seen.insert(id.clone()) {
                result += &format!("<Relationship xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" Id=\"{id}\" Type=\"{LINK_REL}\" Target=\"{}\" TargetMode=\"External\"/>", escape(&run.style.link));
            }
        }
    }
    result
}
pub fn escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
pub fn text_xml(s: &str, q: &str) -> String {
    format!(
        "<{q}:t xml:space=\"preserve\">{}</{q}:t>",
        escape(s)
            .replace(
                '\n',
                &format!("</{q}:t><{q}:br/><{q}:t xml:space=\"preserve\">")
            )
            .replace(
                '\t',
                &format!("</{q}:t><{q}:tab/><{q}:t xml:space=\"preserve\">")
            )
    )
}
pub fn run_properties_xml(s: &document_core::CharacterStyle, q: &str) -> String {
    let font = escape(&s.font_family);
    let size = (s.font_size * 1.5).round() as u32;
    let bold = u8::from(s.bold);
    let italic = u8::from(s.italic);
    let underline = if s.underline { "single" } else { "none" };
    let strike = u8::from(s.strike);
    let vert = if s.superscript {
        "superscript"
    } else if s.subscript {
        "subscript"
    } else {
        "baseline"
    };
    let color = s.color.trim_start_matches('#');
    let fill = if s.highlight == "none" {
        "auto"
    } else {
        s.highlight.trim_start_matches('#')
    };
    format!(
        r#"<{q}:rPr><{q}:rFonts {q}:ascii="{font}" {q}:hAnsi="{font}"/><{q}:b {q}:val="{bold}"/><{q}:i {q}:val="{italic}"/><{q}:color {q}:val="{color}"/><{q}:sz {q}:val="{size}"/><{q}:highlight {q}:val="none"/><{q}:strike {q}:val="{strike}"/><{q}:u {q}:val="{underline}"/><{q}:vertAlign {q}:val="{vert}"/><{q}:shd {q}:val="clear" {q}:fill="{fill}"/></{q}:rPr>"#
    )
}
pub fn run_xml(text: &str, s: &document_core::CharacterStyle, q: &str) -> String {
    format!(
        "<{q}:r>{}{}</{q}:r>",
        run_properties_xml(s, q),
        text_xml(text, q)
    )
}
pub fn table_xml(p: &Paragraph, table: &document_core::TableData, q: &str) -> String {
    let columns = table.rows.first().map_or(1, Vec::len).max(1);
    let width = 9000 / columns;
    let grid = (0..columns)
        .map(|_| format!("<{q}:gridCol {q}:w=\"{width}\"/>"))
        .collect::<String>();
    let border = ["top", "left", "bottom", "right", "insideH", "insideV"]
        .iter()
        .map(|side| format!("<{q}:{side} {q}:val=\"single\" {q}:sz=\"4\" {q}:space=\"0\" {q}:color=\"808080\"/>"))
        .collect::<String>();
    let base = document_core::CharacterStyle::from(&p.style);
    let rows = table
        .rows
        .iter()
        .enumerate()
        .map(|(index, row)| {
            let head = table.header && index == 0;
            let mut style = base.clone();
            style.bold = style.bold || head;
            let marker = if head {
                format!("<{q}:trPr><{q}:tblHeader/></{q}:trPr>")
            } else {
                String::new()
            };
            let cells = row
                .iter()
                .map(|cell| {
                    format!(
                        "<{q}:tc><{q}:tcPr><{q}:tcW {q}:w=\"{width}\" {q}:type=\"dxa\"/></{q}:tcPr><{q}:p><{q}:pPr><{q}:spacing {q}:after=\"0\"/></{q}:pPr>{}</{q}:p></{q}:tc>",
                        run_xml(cell, &style, q)
                    )
                })
                .collect::<String>();
            format!("<{q}:tr>{marker}{cells}</{q}:tr>")
        })
        .collect::<String>();
    format!(
        "<{q}:tbl xmlns:{q}=\"{WORD}\"><{q}:tblPr><{q}:tblW {q}:w=\"{}\" {q}:type=\"dxa\"/><{q}:tblBorders>{border}</{q}:tblBorders></{q}:tblPr><{q}:tblGrid>{grid}</{q}:tblGrid>{rows}</{q}:tbl>",
        width * columns
    )
}
pub fn paragraph_xml(p: &Paragraph, q: &str, bullet: u32, number: u32) -> String {
    if let Some(table) = &p.table {
        return table_xml(p, table, q);
    }
    let s = &p.style;
    let alignment = if s.alignment == "justify" {
        "both"
    } else {
        &s.alignment
    };
    let num = match s.list.as_str() {
        "bullet" => bullet,
        "number" => number,
        _ => 0,
    };
    let numbering = if num == 0 {
        String::new()
    } else {
        format!("<{q}:numPr><{q}:ilvl {q}:val=\"0\"/><{q}:numId {q}:val=\"{num}\"/></{q}:numPr>")
    };
    let after = (s.spacing_after * 20.0).round() as u32;
    let page_break = if s.page_break_before {
        format!("<{q}:pageBreakBefore/>")
    } else {
        String::new()
    };
    let borders = match s.border.as_str() {
        "box" => ["top", "left", "bottom", "right"].as_slice(),
        "bottom" => ["bottom"].as_slice(),
        "left" => ["left"].as_slice(),
        _ => [].as_slice(),
    }
    .iter()
    .map(|side| format!("<{q}:{side} {q}:val=\"single\" {q}:sz=\"6\" {q}:space=\"4\" {q}:color=\"808080\"/>"))
    .collect::<String>();
    let borders = if borders.is_empty() { borders } else { format!("<{q}:pBdr>{borders}</{q}:pBdr>") };
    let shading = if s.shading == "none" {
        String::new()
    } else {
        format!("<{q}:shd {q}:val=\"clear\" {q}:color=\"auto\" {q}:fill=\"{}\"/>", s.shading.trim_start_matches('#'))
    };
    let line = (s.line_spacing * 240.0).round() as u32;
    let left = (s.indent as u32 + u32::from(s.list != "none")) * 360;
    let mut contents = String::new();
    let mut open = String::new();
    for run in writer_engine::rich_text::segments(p) {
        let start = writer_engine::rich_text::boundary(&p.text, run.start).unwrap();
        let end = writer_engine::rich_text::boundary(&p.text, run.start + run.length).unwrap();
        if run.style.link != open {
            if !open.is_empty() {
                contents.push_str(&format!("</{q}:hyperlink>"));
            }
            if !run.style.link.is_empty() {
                contents.push_str(&format!("<{q}:hyperlink xmlns:r=\"{REL_NS}\" r:id=\"{}\" {q}:history=\"1\">", link_id(&run.style.link)));
            }
            open = run.style.link.clone();
        }
        contents.push_str(&run_xml(&p.text[start..end], &run.style, q));
    }
    if !open.is_empty() {
        contents.push_str(&format!("</{q}:hyperlink>"));
    }
    contents.push_str(&drawing_xml(p, q));
    format!(
        r#"<{q}:p xmlns:{q}="{WORD}"><{q}:pPr>{page_break}{numbering}{borders}{shading}<{q}:spacing {q}:after="{after}" {q}:line="{line}" {q}:lineRule="auto"/><{q}:ind {q}:left="{left}"/><{q}:jc {q}:val="{alignment}"/></{q}:pPr>{contents}</{q}:p>"#
    )
}
fn numbering_xml(original: Option<&str>, bullet: u32, number: u32) -> Result<String, String> {
    let mut definitions = String::new();
    let mut instances = String::new();
    for (id, format, label) in [(bullet, "bullet", "•"), (number, "decimal", "%1.")] {
        definitions.push_str(&format!(r#"<w:abstractNum xmlns:w="{WORD}" w:abstractNumId="{id}"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="{format}"/><w:lvlText w:val="{label}"/><w:lvlJc w:val="left"/><w:pPr><w:tabs><w:tab w:val="num" w:pos="360"/></w:tabs><w:ind w:left="360" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>"#));
        instances.push_str(&format!(
            r#"<w:num xmlns:w="{WORD}" w:numId="{id}"><w:abstractNumId w:val="{id}"/></w:num>"#
        ));
    }
    let Some(xml) = original else {
        return Ok(format!(
            r#"<w:numbering xmlns:w="{WORD}">{definitions}{instances}</w:numbering>"#
        ));
    };
    let parsed = Xml::parse(xml).map_err(|e| e.to_string())?;
    let root = parsed.root_element();
    let Some(end) = xml[root.range()]
        .rfind("</")
        .map(|i| i + root.range().start)
    else {
        return append(xml, &(definitions + &instances));
    };
    let def_at = root
        .children()
        .find(|n| styles::word(*n, "num") || styles::word(*n, "numIdMacAtCleanup"))
        .map(|n| n.range().start)
        .unwrap_or(end);
    let num_at = root
        .children()
        .find(|n| styles::word(*n, "numIdMacAtCleanup"))
        .map(|n| n.range().start)
        .unwrap_or(end);
    let mut result = xml.to_string();
    result.insert_str(num_at, &instances);
    result.insert_str(def_at, &definitions);
    Ok(result)
}
fn empty_rels() -> String {
    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>".into()
}
fn append(xml: &str, contents: &str) -> Result<String, String> {
    let parsed = Xml::parse(xml).map_err(|e| e.to_string())?;
    let range = parsed.root_element().range();
    let raw = &xml[range.clone()];
    if raw.ends_with("/>") {
        let name = raw[1..]
            .split([' ', '/', '>'])
            .next()
            .ok_or("Invalid XML root")?;
        Ok(format!(
            "{}>{contents}</{name}>{}",
            &xml[..range.end - 2],
            &xml[range.end..]
        ))
    } else {
        let end = range.start + raw.rfind("</").ok_or("Invalid XML root")?;
        Ok(format!("{}{contents}{}", &xml[..end], &xml[end..]))
    }
}
fn write_package(
    mut original: Option<&mut Archive<'_>>,
    mut parts: BTreeMap<String, String>,
    mut binary: BTreeMap<String, Vec<u8>>,
) -> Result<Vec<u8>, String> {
    let mut writer = ZipWriter::new(Cursor::new(Vec::new()));
    if let Some(archive) = original.as_mut() {
        for i in 0..archive.len() {
            let entry = archive.by_index(i).map_err(|e| e.to_string())?;
            if let Some(bytes) = binary.remove(entry.name()) {
                writer
                    .start_file(
                        entry.name(),
                        SimpleFileOptions::default()
                            .compression_method(zip::CompressionMethod::Stored),
                    )
                    .map_err(|e| e.to_string())?;
                writer.write_all(&bytes).map_err(|e| e.to_string())?;
            } else if let Some(text) = parts.remove(entry.name()) {
                writer
                    .start_file(
                        entry.name(),
                        SimpleFileOptions::default()
                            .compression_method(zip::CompressionMethod::Deflated),
                    )
                    .map_err(|e| e.to_string())?;
                writer
                    .write_all(text.as_bytes())
                    .map_err(|e| e.to_string())?;
            } else {
                writer.raw_copy_file(entry).map_err(|e| e.to_string())?;
            }
        }
    }
    for (name, text) in parts {
        writer
            .start_file(
                name,
                SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated),
            )
            .map_err(|e| e.to_string())?;
        writer
            .write_all(text.as_bytes())
            .map_err(|e| e.to_string())?;
    }
    for (name, bytes) in binary {
        writer
            .start_file(
                name,
                SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored),
            )
            .map_err(|e| e.to_string())?;
        writer.write_all(&bytes).map_err(|e| e.to_string())?;
    }
    writer
        .finish()
        .map(|c| c.into_inner())
        .map_err(|e| e.to_string())
}
