use crate::docx::escape;
use crate::docx_styles::{self as styles, WORD};
use document_core::{id, Comment, Document, Paragraph, Revision};
use roxmltree::{Document as Xml, Node};
use std::collections::{HashMap, HashSet};

const RESOLVED: &str = "[Résolu] ";
const TEXT: &str = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

/// Comments and tracked changes anchored to paragraph `index`; entries pointing at a missing, table or image paragraph are dropped.
pub struct Review<'a> {
    pub comments: Vec<(usize, usize, &'a Comment)>,
    pub revisions: Vec<(usize, usize, &'a Revision)>,
}

fn anchor(paragraphs: &[Paragraph], id: &str) -> Option<usize> {
    paragraphs
        .iter()
        .position(|p| p.id == id && p.table.is_none() && p.image.is_none())
}

pub fn collect<'a>(document: &'a Document, paragraphs: &[Paragraph]) -> Review<'a> {
    let mut comments: Vec<_> = document
        .comments
        .iter()
        .filter_map(|c| anchor(paragraphs, &c.paragraph).map(|i| (i, 0, c)))
        .collect();
    comments.sort_by_key(|(i, _, _)| *i);
    for (n, entry) in comments.iter_mut().enumerate() {
        entry.1 = n;
    }
    let mut revisions: Vec<_> = document
        .revisions
        .iter()
        .filter_map(|r| anchor(paragraphs, &r.paragraph).map(|i| (i, 0, r)))
        .collect();
    revisions.sort_by_key(|(i, _, _)| *i);
    for (n, entry) in revisions.iter_mut().enumerate() {
        entry.1 = n;
    }
    Review { comments, revisions }
}

fn initials(author: &str) -> String {
    author.chars().filter(|c| c.is_alphanumeric()).take(2).collect::<String>().to_uppercase()
}

fn date_attr(prefix: &str, date: &str) -> String {
    if date.is_empty() { String::new() } else { format!(" {prefix}=\"{}\"", escape(date)) }
}

fn run_text(text: &str, deleted: bool) -> String {
    let tag = if deleted { "w:delText" } else { "w:t" };
    format!("<w:r><{tag} xml:space=\"preserve\">{}</{tag}></w:r>", escape(text))
}

/// Wraps the `<w:p>` XML of paragraph `index` with comment ranges and `w:ins` / `w:del` revisions.
pub fn docx_paragraph(review: &Review, index: usize, xml: String, current_text: &str) -> String {
    let comments: Vec<_> = review.comments.iter().filter(|(i, _, _)| *i == index).collect();
    let revisions: Vec<_> = review.revisions.iter().filter(|(i, _, _)| *i == index).collect();
    if comments.is_empty() && revisions.is_empty() {
        return xml;
    }
    let Some(open_end) = xml.find('>') else { return xml };
    let Some(close) = xml.rfind("</w:p>") else { return xml };
    let body_start = match xml.find("</w:pPr>") {
        Some(i) if i < close => i + "</w:pPr>".len(),
        _ => open_end + 1,
    };
    let (head, rest) = xml.split_at(body_start);
    let rest = &rest[..close - body_start];
    let mut start = String::new();
    let mut end = String::new();
    for (_, n, _) in &comments {
        start.push_str(&format!("<w:commentRangeStart w:id=\"{n}\"/>"));
        end.push_str(&format!("<w:commentRangeEnd w:id=\"{n}\"/><w:r><w:commentReference w:id=\"{n}\"/></w:r>"));
    }
    let mut inner = rest.to_string();
    let _ = current_text;
    for (_, n, revision) in &revisions {
        let who = escape(&revision.author);
        let when = date_attr("w:date", &revision.date);
        let id = 1000 + n * 2;
        if revision.kind == "edit" && !revision.old.is_empty() {
            start.push_str(&format!("<w:del w:id=\"{id}\" w:author=\"{who}\"{when}>{}</w:del>", run_text(&revision.old, true)));
        }
        inner = format!("<w:ins w:id=\"{}\" w:author=\"{who}\"{when}>{inner}</w:ins>", id + 1);
    }
    format!("{head}{start}{inner}{end}</w:p>")
}

pub fn docx_comments_part(review: &Review, word: &str) -> String {
    let mut xml = format!("<w:comments xmlns:w=\"{word}\">");
    for (_, n, c) in &review.comments {
        let text = if c.resolved { format!("{RESOLVED}{}", c.text) } else { c.text.clone() };
        xml.push_str(&format!(
            "<w:comment w:id=\"{n}\" w:author=\"{}\" w:initials=\"{}\"{}><w:p>{}</w:p></w:comment>",
            escape(&c.author),
            escape(&initials(&c.author)),
            date_attr("w:date", &c.date),
            run_text(&text, false)
        ));
    }
    xml.push_str("</w:comments>");
    xml
}

/// `<text:tracked-changes>` block for the start of `office:text`.
pub fn odt_tracked_changes(review: &Review) -> String {
    if review.revisions.is_empty() {
        return String::new();
    }
    let mut xml = String::from("<text:tracked-changes>");
    for (_, n, r) in &review.revisions {
        let info = format!(
            "<office:change-info><dc:creator>{}</dc:creator>{}</office:change-info>",
            escape(&r.author),
            if r.date.is_empty() { String::new() } else { format!("<dc:date>{}</dc:date>", escape(&r.date)) }
        );
        xml.push_str(&format!("<text:changed-region text:id=\"ins{n}\"><text:insertion>{info}</text:insertion></text:changed-region>"));
        if r.kind == "edit" && !r.old.is_empty() {
            xml.push_str(&format!("<text:changed-region text:id=\"del{n}\"><text:deletion>{info}<text:p>{}</text:p></text:deletion></text:changed-region>", escape(&r.old)));
        }
    }
    xml.push_str("</text:tracked-changes>");
    xml
}

/// (before, after) markup to place around the content of ODT paragraph `index`.
pub fn odt_paragraph(review: &Review, index: usize) -> (String, String) {
    let mut before = String::new();
    let mut after = String::new();
    for (_, n, c) in review.comments.iter().filter(|(i, _, _)| *i == index) {
        let state = if c.resolved { RESOLVED } else { "" };
        before.push_str(&format!(
            "<office:annotation><dc:creator>{}</dc:creator>{}<text:p>{state}{}</text:p></office:annotation>",
            escape(&c.author),
            if c.date.is_empty() { String::new() } else { format!("<dc:date>{}</dc:date>", escape(&c.date)) },
            escape(&c.text)
        ));
        let _ = n;
    }
    for (_, n, r) in review.revisions.iter().filter(|(i, _, _)| *i == index) {
        if r.kind == "edit" && !r.old.is_empty() {
            before.push_str(&format!("<text:change text:change-id=\"del{n}\"/>"));
        }
        before.push_str(&format!("<text:change-start text:change-id=\"ins{n}\"/>"));
        after.push_str(&format!("<text:change-end text:change-id=\"ins{n}\"/>"));
    }
    (before, after)
}

fn split_resolved(text: String) -> (String, bool) {
    match text.strip_prefix(RESOLVED) {
        Some(rest) => (rest.to_string(), true),
        None => (text, false),
    }
}

fn word_text(node: Node, tag: &str) -> String {
    node.descendants().filter(|n| styles::word(*n, tag)).filter_map(|n| n.text()).collect()
}

/// Text of a WordprocessingML paragraph as it was before tracked changes: deleted runs in, inserted runs out.
fn original_text(paragraph: Node) -> String {
    paragraph
        .descendants()
        .filter(|n| {
            styles::word(*n, "delText") || (styles::word(*n, "t") && !n.ancestors().any(|a| styles::word(a, "ins")))
        })
        .filter_map(|n| n.text())
        .collect()
}

/// Comments and tracked changes of a DOCX body; `nodes[i]` is the `w:p` that produced `paragraphs[i]`.
pub fn read_docx(nodes: &[Node], paragraphs: &[Paragraph], comments_xml: Option<&str>) -> Result<(Vec<Comment>, Vec<Revision>), String> {
    let parts = comments_xml.map(Xml::parse).transpose().map_err(|e| e.to_string())?;
    let mut bodies: HashMap<String, Comment> = HashMap::new();
    for c in parts.iter().flat_map(|d| d.root_element().children()).filter(|n| styles::word(*n, "comment")) {
        let Some(key) = c.attribute((WORD, "id")) else { continue };
        let lines: Vec<String> = c.children().filter(|n| styles::word(*n, "p")).map(|p| word_text(p, "t")).collect();
        let (text, resolved) = split_resolved(lines.join("\n"));
        bodies.insert(
            key.to_string(),
            Comment {
                id: id(),
                paragraph: String::new(),
                author: c.attribute((WORD, "author")).unwrap_or_default().to_string(),
                text,
                date: c.attribute((WORD, "date")).unwrap_or_default().to_string(),
                resolved,
            },
        );
    }
    let mut comments = vec![];
    let mut revisions = vec![];
    let mut seen = HashSet::new();
    for (node, paragraph) in nodes.iter().zip(paragraphs) {
        if paragraph.table.is_some() || paragraph.image.is_some() {
            continue;
        }
        for n in node
            .descendants()
            .filter(|n| styles::word(*n, "commentRangeStart") || styles::word(*n, "commentReference"))
        {
            let Some(key) = n.attribute((WORD, "id")) else { continue };
            if !seen.insert(key.to_string()) {
                continue;
            }
            if let Some(mut comment) = bodies.remove(key) {
                comment.paragraph = paragraph.id.clone();
                comments.push(comment);
            }
        }
        let changes: Vec<Node> = node
            .descendants()
            .filter(|n| styles::word(*n, "ins") || styles::word(*n, "del"))
            .collect();
        let Some(first) = changes.first() else { continue };
        let old = original_text(*node);
        let edited = changes.iter().any(|n| styles::word(*n, "del")) && old != paragraph.text;
        revisions.push(Revision {
            id: id(),
            paragraph: paragraph.id.clone(),
            author: first.attribute((WORD, "author")).unwrap_or_default().to_string(),
            date: first.attribute((WORD, "date")).unwrap_or_default().to_string(),
            kind: if edited { "edit" } else { "insert" }.into(),
            old: if edited { old } else { String::new() },
        });
    }
    Ok((comments, revisions))
}

/// Author and date stored in an ODF `dc:creator` / `dc:date` pair under `node`.
fn odt_author(node: Node) -> (String, String) {
    let field = |name: &str| {
        node.children()
            .find(|n| named(*n, name))
            .map(|n| {
                let mut text = String::new();
                crate::writer_office::text_descendants(n, &mut text);
                text
            })
            .unwrap_or_default()
    };
    (field("creator"), field("date"))
}

fn named(node: Node, name: &str) -> bool {
    node.is_element() && node.tag_name().name() == name
}

/// A tracked change region of an ODT package: `old` is set for deletions.
pub struct Change {
    author: String,
    date: String,
    old: Option<String>,
}

/// Changed regions of an ODT `content.xml`, keyed by `text:id`.
pub fn odt_changes(doc: &Xml) -> HashMap<String, Change> {
    doc.descendants()
        .filter(|n| named(*n, "changed-region"))
        .filter_map(|region| {
            let key = region.attribute((TEXT, "id"))?.to_string();
            let change = region.children().find(|n| named(*n, "insertion") || named(*n, "deletion"))?;
            let (author, date) = change.children().find(|n| named(*n, "change-info")).map(odt_author).unwrap_or_default();
            let old = named(change, "deletion").then(|| {
                let mut text = String::new();
                for p in change.children().filter(|n| named(*n, "p")) {
                    if !text.is_empty() {
                        text.push('\n');
                    }
                    crate::writer_office::text_descendants(p, &mut text);
                }
                text
            });
            Some((key, Change { author, date, old }))
        })
        .collect()
}

/// Comments of the ODT paragraph `paragraph` (its `office:annotation` children).
pub fn odt_comments(node: Node, paragraph: &str) -> Vec<Comment> {
    node.descendants()
        .filter(|n| named(*n, "annotation"))
        .map(|a| {
            let (author, date) = odt_author(a);
            let lines: Vec<String> = a
                .children()
                .filter(|n| named(*n, "p"))
                .map(|p| {
                    let mut text = String::new();
                    crate::writer_office::text_descendants(p, &mut text);
                    text
                })
                .collect();
            let (text, resolved) = split_resolved(lines.join("\n"));
            Comment { id: id(), paragraph: paragraph.into(), author, text, date, resolved }
        })
        .collect()
}

/// The tracked change marked inside the ODT paragraph `paragraph`, if any.
pub fn odt_revision(node: Node, paragraph: &str, changes: &HashMap<String, Change>) -> Option<Revision> {
    let start = node.descendants().find(|n| named(*n, "change-start")).and_then(|n| n.attribute((TEXT, "change-id")));
    let removed = node.descendants().find(|n| named(*n, "change")).and_then(|n| n.attribute((TEXT, "change-id")));
    let inserted = start.and_then(|k| changes.get(k)).filter(|c| c.old.is_none());
    let deleted = removed.and_then(|k| changes.get(k)).filter(|c| c.old.is_some());
    let info = inserted.or(deleted)?;
    let (kind, old) = match deleted {
        Some(change) => ("edit", change.old.clone().unwrap_or_default()),
        None => ("insert", String::new()),
    };
    Some(Revision {
        id: id(),
        paragraph: paragraph.into(),
        author: info.author.clone(),
        date: info.date.clone(),
        kind: kind.into(),
        old,
    })
}
