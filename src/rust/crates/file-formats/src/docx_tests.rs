use super::*;
use document_core::TextStyle;

fn fixture() -> Vec<u8> {
    let mut parts = BTreeMap::new();
    parts.insert("_rels/.rels".into(), "<Relationships><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>".into());
    parts.insert("[Content_Types].xml".into(), "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>".into());
    parts.insert("word/_rels/document.xml.rels".into(), "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"s\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"n\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>".into());
    parts.insert("word/styles.xml".into(), format!("<w:styles xmlns:w=\"{WORD}\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\"/><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:pPr><w:spacing w:after=\"120\"/></w:pPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"HeadingBase\"><w:basedOn w:val=\"Normal\"/><w:rPr><w:b/><w:sz w:val=\"48\"/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:basedOn w:val=\"HeadingBase\"/><w:pPr><w:jc w:val=\"center\"/></w:pPr></w:style></w:styles>"));
    parts.insert("word/numbering.xml".into(), format!("<w:numbering xmlns:w=\"{WORD}\"><w:abstractNum w:abstractNumId=\"5\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum><w:num w:numId=\"8\"><w:abstractNumId w:val=\"5\"/></w:num></w:numbering>"));
    parts.insert("word/document.xml".into(), format!("<w:document xmlns:w=\"{WORD}\"><w:body><w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p><w:p><w:pPr><w:keepNext/><w:spacing w:before=\"60\" w:after=\"120\"/></w:pPr><w:r><w:t xml:space=\"preserve\">Hello </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>bold</w:t></w:r><w:hyperlink><w:r><w:rPr><w:i/></w:rPr><w:t xml:space=\"preserve\"> world</w:t></w:r></w:hyperlink><w:r><w:instrText> NOT VISIBLE </w:instrText><w:drawing/></w:r><w:del><w:r><w:delText>deleted</w:delText></w:r></w:del></w:p><w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"1\"/></w:tcPr><w:p><w:r><w:t>Table cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"8\"/></w:numPr></w:pPr><w:r><w:t>Bullet</w:t></w:r></w:p><w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/></w:sectPr></w:body></w:document>"));
    parts.insert(
        "word/header1.xml".into(),
        format!(
            "<w:hdr xmlns:w=\"{WORD}\"><w:p><w:r><w:t>Header retained</w:t></w:r></w:p></w:hdr>"
        ),
    );
    parts.insert(
        "word/media/image1.png".into(),
        "binary image payload retained byte for byte".into(),
    );
    write_package(None, parts, BTreeMap::new()).unwrap()
}
fn ps(doc: &mut Document) -> &mut Vec<Paragraph> {
    match &mut doc.content {
        Content::Writer { paragraphs } => paragraphs,
        _ => unreachable!(),
    }
}
fn parts(bytes: &[u8]) -> BTreeMap<String, Vec<u8>> {
    let mut zip = archive(bytes).unwrap();
    (0..zip.len())
        .map(|i| {
            let mut e = zip.by_index(i).unwrap();
            let mut bytes = vec![];
            e.read_to_end(&mut bytes).unwrap();
            (e.name().into(), bytes)
        })
        .collect()
}

#[test]
fn page_layout_edits_preserve_word_objects_and_reopen_after_native_migration() {
    let bytes = fixture();
    let mut document = import(&bytes, "Pages").unwrap();
    assert!(
        document.page_layout.as_ref().unwrap().width
            > document.page_layout.as_ref().unwrap().height
    );
    let mut legacy = serde_json::to_value(&document).unwrap();
    legacy.as_object_mut().unwrap().remove("page_layout");
    legacy["office_source"]
        .as_object_mut()
        .unwrap()
        .remove("page_layout");
    let migrated = crate::decode(&serde_json::to_string(&legacy).unwrap()).unwrap();
    assert_eq!(migrated.page_layout, document.page_layout);
    assert_eq!(
        export(
            &migrated,
            match &migrated.content {
                Content::Writer { paragraphs } => paragraphs,
                _ => unreachable!(),
            }
        )
        .unwrap(),
        bytes
    );
    document.page_layout = Some(document_core::PageLayout {
        width: 816.0,
        height: 1056.0,
        top: 48.0,
        right: 72.0,
        bottom: 60.0,
        left: 84.0,
        ..Default::default()
    });
    ps(&mut document)[1].style.page_break_before = true;
    let exported = export(
        &document,
        match &document.content {
            Content::Writer { paragraphs } => paragraphs,
            _ => unreachable!(),
        },
    )
    .unwrap();
    let reopened = import(&exported, "Pages").unwrap();
    assert_eq!(reopened.page_layout, document.page_layout);
    assert!(ps(&mut reopened.clone())[1].style.page_break_before);
    let original_parts = parts(&bytes);
    let updated_parts = parts(&exported);
    for (name, contents) in original_parts
        .iter()
        .filter(|(n, _)| n.as_str() != "word/document.xml")
    {
        assert_eq!(&updated_parts[name], contents, "{name}");
    }
    let main = String::from_utf8(updated_parts["word/document.xml"].clone()).unwrap();
    assert!(
        main.contains("<w:tbl>")
            && main.contains("<w:hyperlink>")
            && main.contains("<w:drawing/>")
            && main.contains("<w:keepNext/>")
            && main.contains("w:before=\"60\"")
    );
}

#[test]
fn selected_formatting_roundtrips_docx_odt_and_native() {
    let mut document = Document::new("writer", "Styles mixtes").unwrap();
    let paragraphs = ps(&mut document);
    paragraphs[0].text = "Avant été 🦀 fin".into();
    let id = paragraphs[0].id.clone();
    writer_engine::apply(paragraphs,&serde_json::from_value(serde_json::json!({"command":"FormatText","params":{"id":id,"start":6,"end":12,"style":{"bold":true,"italic":true,"underline":true,"font_size":32,"font_family":"Georgia","color":"#285CB3","highlight":"#FFF1A8"}}})).unwrap()).unwrap();
    let native = crate::decode(&crate::encode(&document).unwrap()).unwrap();
    for format in ["docx", "odt"] {
        let bytes = crate::export_office(&native, format).unwrap();
        let mut reopened = crate::import_office(format, &bytes, "Styles").unwrap();
        let paragraph = &ps(&mut reopened)[0];
        assert_eq!(paragraph.text, "Avant été 🦀 fin");
        let plain = writer_engine::rich_text::at(paragraph, 0);
        let rich = writer_engine::rich_text::at(paragraph, 6);
        let tail = writer_engine::rich_text::at(paragraph, 13);
        assert!(!plain.bold && !tail.bold, "{format} expanded selected bold");
        assert!(
            rich.bold && rich.italic && rich.underline,
            "{format} lost selection decorations"
        );
        assert_eq!(rich.font_size, 32.0);
        assert_eq!(rich.font_family, "Georgia");
        assert_eq!(rich.highlight, "#FFF1A8");
        assert_eq!(rich.color, "#285CB3");
    }
}

#[test]
fn formatting_imported_selection_preserves_objects_and_untouched_parts() {
    let source = fixture();
    let original = parts(&source);
    let mut doc = import(&source, "Import").unwrap();
    let paragraph = &ps(&mut doc)[1];
    assert!(writer_engine::rich_text::at(paragraph, 6).bold);
    assert!(writer_engine::rich_text::at(paragraph, 11).italic);
    let id = paragraph.id.clone();
    let current = ps(&mut doc);
    writer_engine::apply(current,&serde_json::from_value(serde_json::json!({"command":"FormatText","params":{"id":id,"start":2,"end":9,"style":{"color":"#B3263D","font_size":24}}})).unwrap()).unwrap();
    let exported = export(&doc, ps(&mut doc.clone())).unwrap();
    let changed = parts(&exported);
    for (name, bytes) in original {
        if name != "word/document.xml" {
            assert_eq!(changed[&name], bytes, "Changed unrelated part {name}");
        }
    }
    let xml = String::from_utf8(changed["word/document.xml"].clone()).unwrap();
    assert!(
        xml.contains("<w:drawing")
            && xml.contains("<w:hyperlink")
            && xml.contains("<w:tbl")
            && xml.contains("NOT VISIBLE")
    );
    let mut reopened = import(&exported, "Edited").unwrap();
    let paragraph = &ps(&mut reopened)[1];
    assert_eq!(paragraph.text, "Hello bold world");
    assert_eq!(writer_engine::rich_text::at(paragraph, 2).color, "#B3263D");
    assert_eq!(writer_engine::rich_text::at(paragraph, 2).font_size, 24.0);
    assert!(
        writer_engine::rich_text::at(paragraph, 7).bold
            && writer_engine::rich_text::at(paragraph, 12).italic
    );
}
#[test]
fn inherited_styles_units_and_visible_text_are_imported() {
    let mut doc = import(&fixture(), "Fixture").unwrap();
    let p = ps(&mut doc);
    assert_eq!(p[0].style.font_size, 32.0);
    assert!(p[0].style.bold);
    assert_eq!(p[0].style.alignment, "center");
    assert_eq!(p[1].text, "Hello bold world");
    assert!(!p[1].style.bold);
    assert_eq!(p[1].style.font_size, 16.0);
    assert_eq!(p[1].style.font_family, "Calibri");
    assert_eq!(p[3].style.list, "bullet");
}
#[test]
fn no_op_and_native_roundtrip_preserve_the_entire_package() {
    let bytes = fixture();
    let doc = import(&bytes, "Fixture").unwrap();
    let decoded = crate::decode(&crate::encode(&doc).unwrap()).unwrap();
    let Content::Writer { paragraphs } = &decoded.content else {
        unreachable!()
    };
    assert_eq!(export(&decoded, paragraphs).unwrap(), bytes);
}
#[test]
fn text_edits_preserve_runs_objects_tables_sections_and_other_parts() {
    let input = fixture();
    let original = parts(&input);
    for text in [
        "Hello brave world",
        "XHello bold world",
        "Hello bold world 🦀",
        "Hello bold world!",
        "",
        "été\t🦀\nété",
        "Hello world",
    ] {
        let mut doc = import(&input, "Fixture").unwrap();
        ps(&mut doc)[1].text = text.into();
        let out = export(&doc, ps(&mut doc.clone())).unwrap();
        let output = parts(&out);
        for (name, bytes) in &original {
            if name != "word/document.xml" {
                assert_eq!(output[name], *bytes, "changed {name}");
            }
        }
        let xml = String::from_utf8(output["word/document.xml"].clone()).unwrap();
        assert!(
            xml.contains("<w:tbl>")
                && xml.contains("landscape")
                && xml.contains("<w:drawing/>")
                && xml.contains("<w:b/>")
                && xml.contains("<w:keepNext/>")
        );
        assert_eq!(ps(&mut import(&out, "Edited").unwrap())[1].text, text);
    }
}
#[test]
fn paragraph_style_edits_retain_unrelated_properties_and_can_add_numbering() {
    let mut doc = import(&fixture(), "Fixture").unwrap();
    let p = &mut ps(&mut doc)[1];
    p.style.font_size = 20.0;
    p.style.line_spacing = 1.5;
    p.style.spacing_after = 10.0;
    p.style.list = "number".into();
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    let output = parts(&out);
    let xml = String::from_utf8(output["word/document.xml"].clone()).unwrap();
    assert!(
        xml.contains("w:val=\"30\"")
            && xml.contains("w:before=\"60\"")
            && xml.contains("<w:keepNext/>")
            && xml.contains("<w:drawing/>")
    );
    let mut reopened = import(&out, "Edited").unwrap();
    assert_eq!(ps(&mut reopened)[1].style.font_size, 20.0);
    assert_eq!(ps(&mut reopened)[1].style.list, "number");
}
#[test]
fn paragraph_insertion_and_same_container_reorder_preserve_table_cells() {
    let mut doc = import(&fixture(), "Fixture").unwrap();
    ps(&mut doc).swap(0, 1);
    ps(&mut doc).insert(
        1,
        Paragraph {
            runs: vec![],
            image: None,
            table: None,
            id: "new".into(),
            text: "Inserted".into(),
            style: TextStyle::default(),
        },
    );
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    let mut reopened = import(&out, "Edited").unwrap();
    let text: Vec<_> = ps(&mut reopened).iter().map(|p| p.text.as_str()).collect();
    assert_eq!(
        text,
        [
            "Hello bold world",
            "Inserted",
            "Title",
            "Table cell",
            "Bullet"
        ]
    );
}
#[test]
fn unsupported_cross_table_moves_fail_without_rewriting_the_document() {
    let input = fixture();
    let mut doc = import(&input, "Fixture").unwrap();
    let moved = ps(&mut doc).remove(0);
    ps(&mut doc).push(moved);
    assert!(export(&doc, ps(&mut doc.clone()))
        .unwrap_err()
        .contains("cellules de tableau"));
    assert_eq!(
        doc.office_source.as_ref().unwrap().archive,
        base64::engine::general_purpose::STANDARD.encode(&input)
    );
}
#[test]
fn deleting_a_body_paragraph_does_not_relocate_the_table() {
    let mut doc = import(&fixture(), "Fixture").unwrap();
    ps(&mut doc).remove(0);
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    let mut imported = import(&out, "Deleted").unwrap();
    let texts: Vec<_> = ps(&mut imported).iter().map(|p| p.text.as_str()).collect();
    assert_eq!(texts, ["Hello bold world", "Table cell", "Bullet"]);
}
const PNG_1X1: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
fn image_paragraph(id: &str) -> Paragraph {
    Paragraph {
        runs: vec![],
        image: Some(document_core::ImageData {
            mime: "image/png".into(),
            data: PNG_1X1.into(),
            width: 120.0,
            height: 80.0,
            alt: "Un pixel".into(),
        }),
        table: None,
        id: id.into(),
        text: String::new(),
        style: TextStyle::default(),
    }
}
#[test]
fn fresh_export_embeds_images_and_reimports_them() {
    let mut doc = Document::new("writer", "Images").unwrap();
    ps(&mut doc).push(image_paragraph("img1"));
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    let files = parts(&out);
    assert!(files.keys().any(|k| k.starts_with("word/media/nxi")));
    let types = String::from_utf8(files["[Content_Types].xml"].clone()).unwrap();
    assert!(types.contains("Extension=\"png\""));
    let mut reopened = import(&out, "Images").unwrap();
    let image = ps(&mut reopened)[1].image.clone().unwrap();
    assert_eq!((image.width, image.height, image.alt.as_str()), (120.0, 80.0, "Un pixel"));
    assert_eq!(image.data, PNG_1X1);
}
#[test]
fn imported_package_gets_new_and_removed_images() {
    let mut doc = import(&fixture(), "Fixture").unwrap();
    ps(&mut doc).insert(1, image_paragraph("img2"));
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    let files = parts(&out);
    assert_eq!(files["word/media/image1.png"], b"binary image payload retained byte for byte");
    assert!(files.keys().any(|k| k.starts_with("word/media/nxi")));
    let rels = String::from_utf8(files["word/_rels/document.xml.rels"].clone()).unwrap();
    assert!(rels.contains("relationships/image"));
    let mut reopened = import(&out, "Edited").unwrap();
    assert!(ps(&mut reopened)[1].image.is_some());
    assert_eq!(ps(&mut reopened).len(), 5);
    ps(&mut reopened).remove(1);
    let again = export(&reopened, ps(&mut reopened.clone())).unwrap();
    let mut last = import(&again, "Edited").unwrap();
    assert!(ps(&mut last).iter().all(|p| p.image.is_none()));
    assert_eq!(ps(&mut last).len(), 4);
}
/// A Word-style package: one commented paragraph, one edited paragraph, one inserted paragraph.
fn review_package() -> Vec<u8> {
    let mut parts = BTreeMap::new();
    parts.insert("_rels/.rels".into(), "<Relationships><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>".into());
    parts.insert("[Content_Types].xml".into(), "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>".into());
    parts.insert("word/_rels/document.xml.rels".into(), "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"c\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"comments.xml\"/></Relationships>".into());
    parts.insert("word/comments.xml".into(), format!("<w:comments xmlns:w=\"{WORD}\"><w:comment w:id=\"0\" w:author=\"Ana\" w:date=\"2026-01-02T10:00:00Z\"><w:p><w:r><w:t>[Résolu] A relire</w:t></w:r></w:p></w:comment></w:comments>"));
    parts.insert("word/document.xml".into(), format!(
        "<w:document xmlns:w=\"{WORD}\"><w:body>\
         <w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Premier</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>\
         <w:p><w:del w:id=\"1\" w:author=\"Luc\" w:date=\"2026-01-03T09:00:00Z\"><w:r><w:delText xml:space=\"preserve\">Ancien </w:delText></w:r></w:del><w:ins w:id=\"2\" w:author=\"Luc\" w:date=\"2026-01-03T09:00:00Z\"><w:r><w:t>Nouveau</w:t></w:r></w:ins></w:p>\
         <w:p><w:ins w:id=\"3\" w:author=\"Eve\" w:date=\"2026-01-04T08:00:00Z\"><w:r><w:t>Ajout</w:t></w:r></w:ins></w:p>\
         </w:body></w:document>"
    ));
    write_package(None, parts, BTreeMap::new()).unwrap()
}
#[test]
fn word_comments_and_tracked_changes_are_imported() {
    let mut doc = import(&review_package(), "Revue").unwrap();
    let ids: Vec<String> = ps(&mut doc).iter().map(|p| p.id.clone()).collect();
    let texts: Vec<String> = ps(&mut doc).iter().map(|p| p.text.clone()).collect();
    assert_eq!(texts, ["Premier", "Nouveau", "Ajout"]);
    let comments: Vec<_> = doc.comments.iter().map(|c| (c.author.as_str(), c.text.as_str(), c.resolved, c.paragraph.as_str())).collect();
    assert_eq!(comments, [("Ana", "A relire", true, ids[0].as_str())]);
    let revisions: Vec<_> = doc.revisions.iter().map(|r| (r.kind.as_str(), r.author.as_str(), r.old.as_str(), r.paragraph.as_str())).collect();
    assert_eq!(
        revisions,
        [("edit", "Luc", "Ancien ", ids[1].as_str()), ("insert", "Eve", "", ids[2].as_str())]
    );
}
#[test]
fn changed_comments_are_written_instead_of_the_original_package() {
    let bytes = review_package();
    let mut doc = import(&bytes, "Revue").unwrap();
    doc.comments[0].text = "Modifié".into();
    let out = export(&doc, ps(&mut doc.clone())).unwrap();
    assert_ne!(out, bytes);
    let reopened = import(&out, "Revue").unwrap();
    assert_eq!(reopened.comments.len(), 1);
    assert_eq!(reopened.comments[0].text, "Modifié");
    assert_eq!(reopened.comments[0].author, "Ana");
}
