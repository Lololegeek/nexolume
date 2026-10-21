use document_core::{CharacterStyle, Paragraph, TextSpan, TextStyle};
use roxmltree::Node;
const STYLE: &str = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
const TEXT: &str = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
const XLINK: &str = "http://www.w3.org/1999/xlink";
const FO: &str = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

fn properties(node: Node<'_, '_>, root: Node<'_, '_>, s: &mut TextStyle, depth: usize) {
    if depth > 32 {
        return;
    }
    let Some(name) = node
        .attribute((TEXT, "style-name"))
        .or_else(|| node.attribute((STYLE, "parent-style-name")))
    else {
        return;
    };
    let Some(definition) = root.descendants().find(|n| {
        n.is_element()
            && n.tag_name().name() == "style"
            && n.attribute((STYLE, "name")) == Some(name)
    }) else {
        return;
    };
    if definition.attribute((STYLE, "parent-style-name")) != Some(name) {
        properties(definition, root, s, depth + 1);
    }
    let Some(p) = definition
        .children()
        .find(|n| n.is_element() && n.tag_name().name() == "text-properties")
    else {
        return;
    };
    if let Some(value) = p.attribute((FO, "font-weight")) {
        s.bold = value == "bold" || value.parse::<u32>().is_ok_and(|v| v >= 600);
    }
    if let Some(value) = p.attribute((FO, "font-style")) {
        s.italic = value == "italic" || value == "oblique";
    }
    if let Some(value) = p.attribute((STYLE, "text-underline-style")) {
        s.underline = value != "none";
    }
    if let Some(value) = p.attribute((FO, "color")) {
        s.color = value.into();
    }
    if let Some(value) = p.attribute((FO, "background-color")) {
        s.highlight = if value == "transparent" {
            "none".into()
        } else {
            value.into()
        };
    }
    if let Some(value) = p
        .attribute((FO, "font-size"))
        .and_then(|v| v.trim_end_matches("pt").parse::<f64>().ok())
    {
        s.font_size = (value * 4.0 / 3.0).clamp(6.0, 96.0);
    }
    if let Some(value) = p
        .attribute((STYLE, "font-name"))
        .or_else(|| p.attribute((FO, "font-family")))
    {
        s.font_family = value.trim_matches(['\'', '"']).into();
    }
}
pub fn read(
    node: Node<'_, '_>,
    root: Node<'_, '_>,
    paragraph: &mut Paragraph,
    inherited: &TextStyle,
) {
    let mut text = String::new();
    let mut runs = vec![];
    let mut offset = 0;
    fn visit(
        node: Node<'_, '_>,
        root: Node<'_, '_>,
        parent: &TextStyle,
        base: &CharacterStyle,
        link: &str,
        text: &mut String,
        runs: &mut Vec<TextSpan>,
        offset: &mut usize,
    ) {
        if node.is_element() && matches!(node.tag_name().name(), "annotation" | "annotation-end") {
            return;
        }
        let mut style = parent.clone();
        if node.is_element() {
            properties(node, root, &mut style, 0);
        }
        let mut link = link.to_string();
        if node.is_element() && node.tag_name().name() == "a" {
            if let Some(href) = node
                .attribute((XLINK, "href"))
                .filter(|h| !h.is_empty() && writer_engine::rich_text::valid_link(h))
            {
                link = href.into();
            }
        }
        let value = if node.is_text() {
            Some(node.text().unwrap_or("").to_string())
        } else if node.is_element() {
            match node.tag_name().name() {
                "tab" => Some("\t".into()),
                "line-break" => Some("\n".into()),
                "s" => Some(
                    " ".repeat(
                        node.attribute((TEXT, "c"))
                            .and_then(|v| v.parse::<usize>().ok())
                            .unwrap_or(1)
                            .min(1000),
                    ),
                ),
                _ => None,
            }
        } else {
            None
        };
        if let Some(value) = value {
            let size = value.encode_utf16().count();
            let mut character = CharacterStyle::from(&style);
            if !link.is_empty() {
                character.link = link.clone();
                if !character.underline && character.color == base.color {
                    character.underline = true;
                    character.color = "#0563C1".into();
                }
            }
            if &character != base {
                writer_engine::rich_text::push(runs, *offset, size, character);
            }
            text.push_str(&value);
            *offset += size;
        } else {
            for child in node.children() {
                visit(child, root, &style, base, &link, text, runs, offset);
            }
        }
    }
    visit(
        node,
        root,
        inherited,
        &CharacterStyle::from(&paragraph.style),
        "",
        &mut text,
        &mut runs,
        &mut offset,
    );
    paragraph.text = text;
    paragraph.runs = runs;
}
fn escape(s: &str) -> String {
    crate::docx::escape(s)
}
pub fn contents(p: &Paragraph, index: usize, styles: &mut String) -> String {
    let mut content = String::new();
    for (i, run) in writer_engine::rich_text::segments(p).iter().enumerate() {
        let s = &run.style;
        let name = format!("T{index}_{i}");
        let font = escape(&s.font_family);
        let color = escape(&s.color);
        let highlight = if s.highlight == "none" {
            "transparent"
        } else {
            &s.highlight
        };
        let size = s.font_size * 0.75;
        let bold = if s.bold { "bold" } else { "normal" };
        let italic = if s.italic { "italic" } else { "normal" };
        let underline = if s.underline { "solid" } else { "none" };
        styles.push_str(&format!(r#"<style:style style:name="{name}" style:family="text"><style:text-properties fo:font-weight="{bold}" fo:font-style="{italic}" style:text-underline-style="{underline}" fo:color="{color}" fo:background-color="{highlight}" fo:font-size="{size}pt" style:font-name="{font}" fo:font-family="{font}"/></style:style>"#));
        let start = writer_engine::rich_text::boundary(&p.text, run.start).unwrap();
        let end = writer_engine::rich_text::boundary(&p.text, run.start + run.length).unwrap();
        let text = escape(&p.text[start..end])
            .replace(' ', "<text:s/>")
            .replace('\n', "<text:line-break/>")
            .replace('\t', "<text:tab/>");
        let span = format!("<text:span text:style-name=\"{name}\">{text}</text:span>");
        if s.link.is_empty() {
            content.push_str(&span);
        } else {
            content.push_str(&format!(
                "<text:a xlink:type=\"simple\" xlink:href=\"{}\">{span}</text:a>",
                escape(&s.link)
            ));
        }
    }
    content
}
