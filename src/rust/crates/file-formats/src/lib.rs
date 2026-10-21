use document_core::{Content, Document};
mod docx;
mod docx_edit;
mod docx_styles;
mod footnotes;
mod odt_rich;
mod page_layout;
mod review;
mod writer_office;
use base64::Engine as _;

pub fn import_office(format: &str, bytes: &[u8], title: &str) -> Result<Document, String> {
    match format {
        "docx" => docx::import(bytes, title),
        "odt" => writer_office::import_odt(bytes, title),
        _ => Err(format!("Unsupported office format: {format}")),
    }
}

pub fn export_office(document: &Document, format: &str) -> Result<Vec<u8>, String> {
    match (&document.content, format) {
        (Content::Writer { paragraphs }, "docx") => docx::export(document, paragraphs),
        (Content::Writer { paragraphs }, "odt") => writer_office::export_odt(
            &document.title,
            paragraphs,
            &document.page_layout.clone().unwrap_or_default(),
            &footnotes::ordered(document, paragraphs),
            &review::collect(document, paragraphs),
        ),
        (Content::Writer { .. }, _) => Err(format!("Unsupported writer format: {format}")),
        _ => Err("This office format does not match the document type".into()),
    }
}

pub fn import_office_base64(format: &str, data: &str, title: &str) -> Result<Document, String> {
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(data)
        .map_err(|e| format!("Invalid encoded office data: {e}"))?;
    import_office(format, &bytes, title)
}

pub fn export_office_base64(document: &Document, format: &str) -> Result<String, String> {
    Ok(base64::engine::general_purpose::STANDARD.encode(export_office(document, format)?))
}
pub fn encode(document: &Document) -> Result<String, String> {
    let rich = matches!(&document.content, Content::Writer {paragraphs} if paragraphs.iter().any(|p|!p.runs.is_empty()))
        || document
            .office_source
            .as_ref()
            .is_some_and(|source| source.paragraphs.iter().any(|p| !p.runs.is_empty()));
    let mut data = serde_json::to_value(document).map_err(|e| e.to_string())?;
    let pages = document.page_layout.is_some()
        || matches!(&document.content, Content::Writer {paragraphs} if paragraphs.iter().any(|p|p.style.page_break_before || p.style.shading != "none" || p.style.border != "none"))
        || document.office_source.as_ref().is_some_and(|s| {
            s.page_layout.is_some() || s.paragraphs.iter().any(|p| p.style.page_break_before || p.style.shading != "none" || p.style.border != "none")
        });
    let images = matches!(&document.content, Content::Writer {paragraphs} if paragraphs.iter().any(|p|p.image.is_some() || p.table.is_some()))
        || document
            .office_source
            .as_ref()
            .is_some_and(|source| source.paragraphs.iter().any(|p| p.image.is_some() || p.table.is_some()));
    data["schema"] = serde_json::json!(if images {
        5
    } else if pages {
        4
    } else if rich {
        3
    } else {
        2
    });
    let encoded = serde_json::to_string_pretty(&data).map_err(|e| e.to_string())?;
    if encoded.len() > 16 * 1024 * 1024 {
        return Err(
            "Le document natif dépasse 16 Mio, y compris son fichier Word d’origine.".into(),
        );
    }
    Ok(encoded)
}
pub fn decode(text: &str) -> Result<Document, String> {
    if text.len() > 16 * 1024 * 1024 {
        return Err("File too large (16 MiB maximum)".into());
    }
    let mut doc: Document = serde_json::from_str(text).map_err(|e| e.to_string())?;
    if !(1..=5).contains(&doc.schema) {
        return Err("Unsupported file schema".into());
    }
    doc.schema = 2;
    // Native documents saved before page settings still contain the original DOCX.
    if doc.page_layout.is_none() {
        if let Some(source) = &mut doc.office_source {
            if source.format == "docx" && source.page_layout.is_none() {
                let bytes = base64::engine::general_purpose::STANDARD
                    .decode(&source.archive)
                    .map_err(|e| e.to_string())?;
                source.page_layout = docx::import(&bytes, &doc.title)?.page_layout;
                doc.page_layout = source.page_layout.clone();
            }
        }
    }
    if let Some(layout) = &doc.page_layout {
        layout.validate()?;
    }
    if doc.revision >= i64::MAX as u64 {
        return Err("Invalid document revision".into());
    }
    if doc.id.is_empty() || doc.title.len() > 4096 {
        return Err("Invalid document metadata".into());
    }
    let mut ids = std::collections::HashSet::new();
    match &doc.content {
        Content::Writer { paragraphs } => {
            if paragraphs.is_empty() {
                return Err("Missing paragraphs".into());
            }
            for p in paragraphs {
                writer_engine::validate(p)?;
                if p.id.is_empty() || !ids.insert(&p.id) {
                    return Err("Duplicate or empty paragraph id".into());
                }
            }
        }
        Content::Sheets { cells } => {
            for (key, cell) in cells {
                sheets_engine::parser::address(key)?;
                sheets_engine::validate_cell(cell)?;
                if cell.input.len() > 8192 {
                    return Err("Cell input too long".into());
                }
            }
        }
        Content::Slides { slides } => {
            if slides.is_empty() {
                return Err("Missing slides".into());
            }
            for s in slides {
                if s.id.is_empty() || !ids.insert(&s.id) {
                    return Err("Duplicate or empty slide id".into());
                }
                slides_engine::validate_slide(s)?;
                for o in &s.objects {
                    slides_engine::validate(o)?;
                    if !ids.insert(&o.id) {
                        return Err("Duplicate object id".into());
                    }
                }
            }
        }
    }
    drop(ids);
    if let Content::Sheets { cells } = &mut doc.content {
        sheets_engine::recalculate(cells, None);
    }
    Ok(doc)
}
