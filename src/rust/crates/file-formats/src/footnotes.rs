use document_core::{Document, Footnote, Paragraph};

/// Footnotes of a Writer document in reading order, paired with the index of the text paragraph they close.
/// Notes anchored to a missing, table or image paragraph are dropped.
pub fn ordered<'a>(document: &'a Document, paragraphs: &[Paragraph]) -> Vec<(usize, &'a Footnote)> {
    let mut notes: Vec<(usize, &Footnote)> = document
        .footnotes
        .iter()
        .filter_map(|n| {
            paragraphs
                .iter()
                .position(|p| p.id == n.paragraph && p.table.is_none() && p.image.is_none())
                .map(|i| (i, n))
        })
        .collect();
    notes.sort_by_key(|(i, _)| *i);
    notes
}

/// `<w:footnoteReference>` runs to insert before `</w:p>` for the notes of paragraph `index`.
pub fn docx_references(notes: &[(usize, &Footnote)], index: usize) -> String {
    notes
        .iter()
        .enumerate()
        .filter(|(_, (i, _))| *i == index)
        .map(|(n, _)| {
            format!(
                "<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteReference w:id=\"{}\"/></w:r>",
                n + 1
            )
        })
        .collect()
}

pub fn docx_part(notes: &[(usize, &Footnote)], word: &str) -> String {
    let mut xml = format!("<w:footnotes xmlns:w=\"{word}\"><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>");
    for (n, (_, note)) in notes.iter().enumerate() {
        xml.push_str(&format!(
            "<w:footnote w:id=\"{}\"><w:p><w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> {}</w:t></w:r></w:p></w:footnote>",
            n + 1,
            crate::docx::escape(&note.text)
        ));
    }
    xml.push_str("</w:footnotes>");
    xml
}

/// `<text:note>` elements to append inside the ODT paragraph `index`.
pub fn odt_notes(notes: &[(usize, &Footnote)], index: usize) -> String {
    notes
        .iter()
        .enumerate()
        .filter(|(_, (i, _))| *i == index)
        .map(|(n, (_, note))| {
            format!(
                "<text:note text:id=\"ftn{0}\" text:note-class=\"footnote\"><text:note-citation>{0}</text:note-citation><text:note-body><text:p>{1}</text:p></text:note-body></text:note>",
                n + 1,
                crate::docx::escape(&note.text)
            )
        })
        .collect()
}
