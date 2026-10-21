use document_core::{id, Paragraph, TextStyle};
use roxmltree::Node;
use std::collections::{HashMap, HashSet};

pub const WORD: &str = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
pub fn word(n: Node<'_, '_>, name: &str) -> bool {
    n.is_element()
        && n.tag_name().name() == name
        && matches!(
            n.tag_name().namespace(),
            Some(WORD | "http://purl.oclc.org/ooxml/wordprocessingml/main")
        )
}
pub fn attr<'a>(n: Node<'a, '_>, name: &str) -> Option<&'a str> {
    n.attributes()
        .find(|a| {
            a.name() == name
                && matches!(
                    a.namespace(),
                    Some(WORD | "http://purl.oclc.org/ooxml/wordprocessingml/main")
                )
        })
        .map(|a| a.value())
}
pub fn child<'a, 'b>(n: Node<'a, 'b>, name: &str) -> Option<Node<'a, 'b>> {
    n.children().find(|n| word(*n, name))
}
/// A top-level, rectangular table of plain cells: the only tables edited as one paragraph.
pub fn simple_table(tbl: Node<'_, '_>) -> Option<document_core::TableData> {
    if !word(tbl, "tbl") || !tbl.parent().is_some_and(|p| word(p, "body")) {
        return None;
    }
    let mut rows = vec![];
    let mut header = false;
    for row in tbl.children().filter(|n| n.is_element()) {
        if word(row, "tblPr") || word(row, "tblGrid") {
            continue;
        }
        if !word(row, "tr") || rows.len() >= 200 {
            return None;
        }
        if rows.is_empty() {
            header = child(row, "trPr").and_then(|n| child(n, "tblHeader")).is_some();
        }
        let mut cells = vec![];
        for cell in row.children().filter(|n| n.is_element()) {
            if word(cell, "trPr") || word(cell, "tblPrEx") {
                continue;
            }
            if !word(cell, "tc") || cells.len() >= 20 {
                return None;
            }
            if let Some(p) = child(cell, "tcPr") {
                if child(p, "gridSpan").is_some()
                    || child(p, "vMerge").is_some()
                    || child(p, "hMerge").is_some()
                {
                    return None;
                }
            }
            let mut lines = vec![];
            for part in cell.children().filter(|n| n.is_element()) {
                if word(part, "tcPr") {
                    continue;
                }
                if !word(part, "p")
                    || part.descendants().any(|n| {
                        ["drawing", "pict", "object", "del", "moveFrom", "sdt"]
                            .iter()
                            .any(|name| word(n, name))
                    })
                {
                    return None;
                }
                lines.push(text_tokens(part).iter().map(|n| token_text(*n)).collect::<String>());
            }
            let text = lines.join("\n");
            if lines.is_empty() || text.chars().count() > 5000 {
                return None;
            }
            cells.push(text);
        }
        if cells.is_empty() || rows.first().is_some_and(|r: &Vec<String>| r.len() != cells.len()) {
            return None;
        }
        rows.push(cells);
    }
    (!rows.is_empty()).then_some(document_core::TableData { rows, header })
}
pub fn paragraphs<'a, 'b>(root: Node<'a, 'b>) -> Vec<Node<'a, 'b>> {
    let tables: HashSet<_> = root
        .descendants()
        .filter(|n| simple_table(*n).is_some())
        .map(|n| n.id())
        .collect();
    root.descendants()
        .filter(|n| {
            if word(*n, "tbl") {
                return tables.contains(&n.id());
            }
            word(*n, "p")
                && n.ancestors().any(|a| word(a, "body"))
                && !n
                    .ancestors()
                    .skip(1)
                    .any(|a| word(a, "p") || word(a, "del") || word(a, "moveFrom") || tables.contains(&a.id()))
        })
        .collect()
}
pub fn text_tokens<'a, 'b>(p: Node<'a, 'b>) -> Vec<Node<'a, 'b>> {
    p.descendants()
        .filter(|n| {
            ["t", "tab", "br", "cr"].iter().any(|name| word(*n, name))
                && n.ancestors().find(|n| word(*n, "p")) == Some(p)
                && !n.ancestors().any(|a| word(a, "del") || word(a, "moveFrom"))
        })
        .collect()
}
pub fn token_text<'a>(n: Node<'a, '_>) -> &'a str {
    if word(n, "t") {
        n.text().unwrap_or("")
    } else if word(n, "tab") {
        "\t"
    } else {
        "\n"
    }
}

pub struct Catalog<'a, 'b> {
    styles: HashMap<String, Node<'a, 'b>>,
    defaults: Option<Node<'a, 'b>>,
    default_style: Option<String>,
    numbering: Option<Node<'a, 'b>>,
    pub major_font: String,
    pub minor_font: String,
    links: HashMap<String, String>,
}
impl<'a, 'b> Catalog<'a, 'b> {
    pub fn new(
        styles: Option<Node<'a, 'b>>,
        numbering: Option<Node<'a, 'b>>,
        theme: Option<Node<'_, '_>>,
    ) -> Self {
        let mut definitions = HashMap::new();
        let mut default_style = None;
        if let Some(root) = styles {
            for s in root.children().filter(|n| word(*n, "style")) {
                if let Some(name) = attr(s, "styleId") {
                    definitions.insert(name.into(), s);
                    if attr(s, "type") == Some("paragraph") && attr(s, "default").is_some_and(on) {
                        default_style = Some(name.into());
                    }
                }
            }
        }
        let font = |name| {
            theme
                .and_then(|root| root.descendants().find(|n| n.tag_name().name() == name))
                .and_then(|n| n.children().find(|n| n.tag_name().name() == "latin"))
                .and_then(|n| n.attribute("typeface"))
                .filter(|s| !s.is_empty())
                .unwrap_or("Aptos")
                .to_string()
        };
        Self {
            styles: definitions,
            defaults: styles.and_then(|n| child(n, "docDefaults")),
            default_style,
            numbering,
            major_font: font("majorFont"),
            minor_font: font("minorFont"),
            links: HashMap::new(),
        }
    }
    /// Hyperlink targets by relationship id, as declared in the document relationships.
    pub fn with_links(mut self, links: HashMap<String, String>) -> Self {
        self.links = links;
        self
    }
    fn link_of(&self, token: Node<'_, '_>) -> Option<String> {
        let link = token.ancestors().find(|n| word(*n, "hyperlink"))?;
        let id = link.attributes().find(|a| a.name() == "id")?.value();
        self.links.get(id).cloned()
    }
    fn named(&self, name: &str, style: &mut TextStyle, seen: &mut HashSet<String>) {
        if seen.len() >= 32 || !seen.insert(name.into()) {
            return;
        }
        if let Some(node) = self.styles.get(name) {
            if let Some(base) = child(*node, "basedOn").and_then(|n| attr(n, "val")) {
                self.named(base, style, seen);
            }
            if let Some(p) = child(*node, "pPr") {
                self.paragraph_properties(p, style);
            }
            if let Some(r) = child(*node, "rPr") {
                self.run_properties(r, style);
            }
        }
    }
    pub fn read(&self, p: Node<'_, '_>) -> Paragraph {
        let tokens = text_tokens(p);
        let text = tokens.iter().map(|n| token_text(*n)).collect::<String>();
        let mut style = TextStyle::default();
        if let Some(d) = self.defaults {
            if let Some(r) = child(d, "rPrDefault").and_then(|n| child(n, "rPr")) {
                self.run_properties(r, &mut style);
            }
            if let Some(properties) = child(d, "pPrDefault").and_then(|n| child(n, "pPr")) {
                self.paragraph_properties(properties, &mut style);
            }
        }
        if let Some(name) = &self.default_style {
            self.named(name, &mut style, &mut HashSet::new());
        }
        if let Some(properties) = child(p, "pPr") {
            if let Some(name) = child(properties, "pStyle").and_then(|n| attr(n, "val")) {
                self.named(name, &mut style, &mut HashSet::new());
            }
            self.paragraph_properties(properties, &mut style);
            if let Some(r) = child(properties, "rPr") {
                self.run_properties(r, &mut style);
            }
        }
        let paragraph_style = style.clone();
        if let Some(run) = tokens
            .first()
            .and_then(|n| n.ancestors().find(|a| word(*a, "r")))
        {
            if let Some(properties) = child(run, "rPr") {
                if let Some(name) = child(properties, "rStyle").and_then(|n| attr(n, "val")) {
                    self.named(name, &mut style, &mut HashSet::new());
                }
                self.run_properties(properties, &mut style);
            }
        }
        if style.list != "none" {
            style.indent = style.indent.saturating_sub(1);
        }
        let mut runs = vec![];
        let mut offset = 0;
        for token in &tokens {
            let mut token_style = paragraph_style.clone();
            if let Some(properties) = token
                .ancestors()
                .find(|n| word(*n, "r"))
                .and_then(|r| child(r, "rPr"))
            {
                if let Some(name) = child(properties, "rStyle").and_then(|n| attr(n, "val")) {
                    self.named(name, &mut token_style, &mut HashSet::new());
                }
                self.run_properties(properties, &mut token_style);
            }
            let length = token_text(*token).encode_utf16().count();
            let mut character = document_core::CharacterStyle::from(&token_style);
            if let Some(link) = self.link_of(*token) {
                character.link = link;
                if !character.underline && character.color == paragraph_style.color {
                    character.underline = true;
                    character.color = "#0563C1".into();
                }
            }
            if character != document_core::CharacterStyle::from(&style) {
                writer_engine::rich_text::push(&mut runs, offset, length, character);
            }
            offset += length;
        }
        Paragraph {
            runs,
            image: None,
            table: None,
            id: id(),
            text,
            style,
        }
    }
    fn run_properties(&self, properties: Node<'_, '_>, style: &mut TextStyle) {
        for (key, value) in [("b", &mut style.bold), ("i", &mut style.italic)] {
            if let Some(n) = child(properties, key) {
                *value = attr(n, "val").is_none_or(on);
            }
        }
        if let Some(n) = child(properties, "strike") {
            style.strike = attr(n, "val").is_none_or(on);
        }
        if let Some(n) = child(properties, "vertAlign") {
            let v = attr(n, "val").unwrap_or("baseline");
            style.superscript = v == "superscript";
            style.subscript = v == "subscript";
        }
        if let Some(n) = child(properties, "u") {
            style.underline = attr(n, "val").is_none_or(|v| v != "none" && on(v));
        }
        if let Some(size) = child(properties, "sz")
            .and_then(|n| attr(n, "val"))
            .and_then(|v| v.parse::<f64>().ok())
            .filter(|v| v.is_finite())
        {
            style.font_size = (size * 2.0 / 3.0).clamp(6.0, 96.0);
        }
        if let Some(n) = child(properties, "rFonts") {
            if let Some(font) = attr(n, "ascii").or_else(|| attr(n, "hAnsi")) {
                style.font_family = font.chars().take(100).collect();
            } else if let Some(theme) = attr(n, "asciiTheme").or_else(|| attr(n, "hAnsiTheme")) {
                style.font_family = if theme.starts_with("major") {
                    &self.major_font
                } else {
                    &self.minor_font
                }
                .clone();
            }
        }
        if let Some(color) = child(properties, "color")
            .and_then(|n| attr(n, "val"))
            .filter(|v| hex(v))
        {
            style.color = format!("#{color}");
        }
        if let Some(color) = child(properties, "shd")
            .and_then(|n| attr(n, "fill"))
            .filter(|v| hex(v))
        {
            style.highlight = format!("#{color}");
        }
        if let Some(color) = child(properties, "highlight").and_then(|n| attr(n, "val")) {
            style.highlight = match color {
                "yellow" => "#FFFF00",
                "green" => "#00FF00",
                "cyan" => "#00FFFF",
                "magenta" => "#FF00FF",
                "blue" => "#0000FF",
                "red" => "#FF0000",
                "darkYellow" => "#808000",
                "lightGray" => "#C0C0C0",
                "none" => {
                    if child(properties, "shd")
                        .and_then(|n| attr(n, "fill"))
                        .is_some_and(hex)
                    {
                        &style.highlight
                    } else {
                        "none"
                    }
                }
                _ => &style.highlight,
            }
            .into();
        }
    }
    fn paragraph_properties(&self, properties: Node<'_, '_>, style: &mut TextStyle) {
        if let Some(p) = child(properties, "pageBreakBefore") {
            style.page_break_before =
                attr(p, "val").is_none_or(|v| !["0", "false", "off"].contains(&v));
        }
        if let Some(fill) = child(properties, "shd")
            .and_then(|n| attr(n, "fill"))
            .filter(|v| hex(v))
        {
            style.shading = format!("#{fill}");
        }
        if let Some(borders) = child(properties, "pBdr") {
            let has = |side: &str| {
                child(borders, side).and_then(|n| attr(n, "val")).is_some_and(|v| v != "nil" && v != "none")
            };
            style.border = if has("top") && has("bottom") && has("left") && has("right") {
                "box"
            } else if has("bottom") {
                "bottom"
            } else if has("left") {
                "left"
            } else {
                "none"
            }
            .into();
        }
        if let Some(value) = child(properties, "jc").and_then(|n| attr(n, "val")) {
            style.alignment = match value {
                "center" => "center",
                "right" | "end" => "right",
                "both" | "distribute" => "justify",
                _ => "left",
            }
            .into();
        }
        if let Some(left) = child(properties, "ind")
            .and_then(|n| attr(n, "left").or_else(|| attr(n, "start")))
            .and_then(|v| v.parse::<f64>().ok())
        {
            style.indent = (left / 360.0).round().clamp(0.0, 8.0) as u8;
        }
        if let Some(spacing) = child(properties, "spacing") {
            if let Some(after) = attr(spacing, "after").and_then(|v| v.parse::<f64>().ok()) {
                style.spacing_after = (after / 20.0).clamp(0.0, 72.0);
            }
            if attr(spacing, "lineRule").is_none_or(|v| v == "auto") {
                if let Some(line) = attr(spacing, "line").and_then(|v| v.parse::<f64>().ok()) {
                    style.line_spacing = (line / 240.0).clamp(1.0, 3.0);
                }
            }
        }
        if let Some(num) = child(properties, "numPr") {
            if let Some(num_id) = child(num, "numId").and_then(|n| attr(n, "val")) {
                if num_id == "0" {
                    style.list = "none".into();
                    return;
                }
                let level = child(num, "ilvl")
                    .and_then(|n| attr(n, "val"))
                    .unwrap_or("0");
                let abstract_id = self
                    .numbering
                    .and_then(|root| {
                        root.children()
                            .find(|n| word(*n, "num") && attr(*n, "numId") == Some(num_id))
                    })
                    .and_then(|n| child(n, "abstractNumId"))
                    .and_then(|n| attr(n, "val"));
                let definition = abstract_id.and_then(|id| {
                    self.numbering.and_then(|root| {
                        root.children().find(|n| {
                            word(*n, "abstractNum") && attr(*n, "abstractNumId") == Some(id)
                        })
                    })
                });
                let format = definition
                    .and_then(|n| {
                        n.children()
                            .find(|n| word(*n, "lvl") && attr(*n, "ilvl") == Some(level))
                    })
                    .and_then(|n| child(n, "numFmt"))
                    .and_then(|n| attr(n, "val"));
                style.list = if format == Some("bullet") {
                    "bullet"
                } else {
                    "number"
                }
                .into();
            }
        }
    }
}
fn on(value: &str) -> bool {
    !matches!(value, "0" | "false" | "off" | "none")
}
fn hex(value: &str) -> bool {
    value.len() == 6 && value.bytes().all(|b| b.is_ascii_hexdigit())
}
