use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct TextStyle {
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    pub strike: bool,
    pub superscript: bool,
    pub subscript: bool,
    pub small_caps: bool,
    pub font_family: String,
    pub font_size: f64,
    pub alignment: String,
    pub color: String,
    pub highlight: String,
    pub list: String,
    pub indent: u8,
    pub line_spacing: f64,
    pub spacing_after: f64,
    pub page_break_before: bool,
    /// Paragraph background: "none" or "#RRGGBB".
    pub shading: String,
    /// Paragraph border: "none", "box", "bottom" or "left".
    pub border: String,
}
impl Default for TextStyle {
    fn default() -> Self {
        Self {
            bold: false,
            italic: false,
            underline: false,
            strike: false,
            superscript: false,
            subscript: false,
            small_caps: false,
            font_family: "Aptos".into(),
            font_size: 16.0,
            alignment: "left".into(),
            color: "#111111".into(),
            highlight: "none".into(),
            list: "none".into(),
            indent: 0,
            line_spacing: 1.15,
            spacing_after: 8.0,
            page_break_before: false,
            shading: "none".into(),
            border: "none".into(),
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Paragraph {
    pub id: String,
    pub text: String,
    pub style: TextStyle,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub runs: Vec<TextSpan>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub image: Option<ImageData>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub table: Option<TableData>,
}
/// Table owned by a table paragraph: plain-text cells, rectangular, first row optionally a header.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct TableData {
    pub rows: Vec<Vec<String>>,
    #[serde(default)]
    pub header: bool,
}
/// Inline picture owned by an image paragraph; `data` is base64, sizes are CSS pixels.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct ImageData {
    pub mime: String,
    pub data: String,
    pub width: f64,
    pub height: f64,
    #[serde(default)]
    pub alt: String,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct TextSpan {
    pub start: usize,
    pub length: usize,
    pub style: CharacterStyle,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct CharacterStyle {
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    #[serde(default)]
    pub strike: bool,
    #[serde(default)]
    pub superscript: bool,
    #[serde(default)]
    pub subscript: bool,
    #[serde(default)]
    pub small_caps: bool,
    pub font_family: String,
    pub font_size: f64,
    pub color: String,
    pub highlight: String,
    /// Hyperlink target (http, https or mailto); empty when the text is not a link.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub link: String,
}
impl From<&TextStyle> for CharacterStyle {
    fn from(s: &TextStyle) -> Self {
        Self {
            bold: s.bold,
            italic: s.italic,
            underline: s.underline,
            strike: s.strike,
            superscript: s.superscript,
            subscript: s.subscript,
            small_caps: s.small_caps,
            font_family: s.font_family.clone(),
            font_size: s.font_size,
            color: s.color.clone(),
            highlight: s.highlight.clone(),
            link: String::new(),
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct CellStyle {
    pub bold: bool,
    pub format: String,
    pub italic: bool,
    pub underline: bool,
    /// "general", "left", "center" or "right".
    pub align: String,
    /// Text colour as #RRGGBB, or "none" for the default ink.
    pub color: String,
    /// Background colour as #RRGGBB, or "none".
    pub fill: String,
}
impl Default for CellStyle {
    fn default() -> Self {
        Self {
            bold: false,
            format: "general".into(),
            italic: false,
            underline: false,
            align: "general".into(),
            color: "none".into(),
            fill: "none".into(),
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Cell {
    pub input: String,
    pub display: String,
    #[serde(default)]
    pub style: CellStyle,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct SlideObject {
    pub id: String,
    pub kind: String,
    pub text: String,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
    pub fill: String,
    #[serde(default = "default_slide_font_size")]
    pub font_size: f64,
    #[serde(default)]
    pub bold: bool,
    #[serde(default)]
    pub italic: bool,
    #[serde(default)]
    pub underline: bool,
    /// "left", "center" or "right".
    #[serde(default = "default_slide_align")]
    pub align: String,
    /// Outline colour: "none" or #RRGGBB.
    #[serde(default = "default_none")]
    pub line: String,
    #[serde(default = "default_line_width")]
    pub line_width: f64,
    /// Rotation in degrees around the centre.
    #[serde(default)]
    pub rotation: f64,
    #[serde(default = "default_opacity")]
    pub opacity: f64,
    /// Picture shown by an "image" object; `width`/`height` are the frame, `image.width`/`height` the source size.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub image: Option<ImageData>,
}
fn default_slide_font_size() -> f64 {
    28.0
}
fn default_slide_align() -> String {
    "left".into()
}
fn default_none() -> String {
    "none".into()
}
fn default_line_width() -> f64 {
    2.0
}
fn default_opacity() -> f64 {
    1.0
}
fn default_background() -> String {
    "#FFFFFF".into()
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Slide {
    pub id: String,
    pub objects: Vec<SlideObject>,
    #[serde(default = "default_background")]
    pub background: String,
    #[serde(default)]
    pub notes: String,
    /// "none", "fade" or "slide".
    #[serde(default = "default_none")]
    pub transition: String,
}
impl Slide {
    pub fn new(id: String, objects: Vec<SlideObject>) -> Self {
        Self {
            id,
            objects,
            background: default_background(),
            notes: String::new(),
            transition: default_none(),
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum Content {
    Writer { paragraphs: Vec<Paragraph> },
    Sheets { cells: BTreeMap<String, Cell> },
    Slides { slides: Vec<Slide> },
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Document {
    pub schema: u32,
    pub id: String,
    pub title: String,
    pub revision: u64,
    pub content: Content,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub page_layout: Option<PageLayout>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub office_source: Option<OfficeSource>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sheet_meta: Option<SheetMeta>,
    /// Writer footnotes in reading order; each is anchored to the end of one paragraph.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub footnotes: Vec<Footnote>,
    /// Writer review comments anchored to one paragraph each.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub comments: Vec<Comment>,
    /// Writer tracked changes: one pending revision per edited paragraph.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub revisions: Vec<Revision>,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Comment {
    pub id: String,
    pub paragraph: String,
    pub author: String,
    pub text: String,
    #[serde(default)]
    pub date: String,
    #[serde(default)]
    pub resolved: bool,
}
/// A paragraph edited while tracking was on. `kind` is `edit` (original text kept in `old`) or `insert` (new paragraph).
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Revision {
    pub id: String,
    pub paragraph: String,
    pub author: String,
    #[serde(default)]
    pub date: String,
    pub kind: String,
    #[serde(default)]
    pub old: String,
}
/// Note shown at the foot of the document, referenced by a superscript number after `paragraph`.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Footnote {
    pub id: String,
    pub paragraph: String,
    pub text: String,
}
/// Presentation data of a spreadsheet: sizes keyed by 0-based index, frozen panes, merged ranges, rules and charts.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct SheetMeta {
    pub col_widths: BTreeMap<String, f64>,
    pub row_heights: BTreeMap<String, f64>,
    pub frozen_rows: u32,
    pub frozen_cols: u32,
    pub merges: Vec<String>,
    pub rules: Vec<CondRule>,
    pub charts: Vec<SheetChart>,
}
/// Conditional format: `op` is gt, lt, eq, between, contains or top-less "neg"; matching cells get `fill` and `color`.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct CondRule {
    pub range: String,
    pub op: String,
    pub value: String,
    pub value2: String,
    pub fill: String,
    pub color: String,
}
/// Chart over a range: `kind` is bar, line or pie; the first column holds labels, the others series.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct SheetChart {
    pub id: String,
    pub kind: String,
    pub range: String,
    pub title: String,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct OfficeSource {
    pub format: String,
    pub archive: String,
    pub main_part: String,
    pub paragraphs: Vec<Paragraph>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub page_layout: Option<PageLayout>,
    /// Comments and revisions as imported, to tell whether the original package still describes the document.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub comments: Vec<Comment>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub revisions: Vec<Revision>,
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(default)]
pub struct PageLayout {
    pub width: f64,
    pub height: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
    pub left: f64,
    /// Running header text; `{page}` and `{pages}` are replaced when laying out each page.
    pub header: String,
    pub footer: String,
    /// left, center or right.
    pub header_align: String,
    pub footer_align: String,
    /// Text columns (1 to 4) and the gap between them in CSS pixels.
    pub columns: u32,
    pub column_gap: f64,
}
impl Default for PageLayout {
    fn default() -> Self {
        Self {
            width: 793.7,
            height: 1122.52,
            top: 96.0,
            right: 96.0,
            bottom: 96.0,
            left: 96.0,
            header: String::new(),
            footer: String::new(),
            header_align: "center".into(),
            footer_align: "center".into(),
            columns: 1,
            column_gap: 36.0,
        }
    }
}
impl PageLayout {
    pub fn validate(&self) -> Result<(), String> {
        if [
            self.width,
            self.height,
            self.top,
            self.right,
            self.bottom,
            self.left,
        ]
        .iter()
        .any(|v| !v.is_finite())
            || !(200.0..=2400.0).contains(&self.width)
            || !(200.0..=2400.0).contains(&self.height)
            || [self.top, self.right, self.bottom, self.left]
                .iter()
                .any(|v| *v < 0.0)
            || self.width - self.left - self.right < 144.0
            || self.height - self.top - self.bottom < 144.0
            || !(1..=4).contains(&self.columns)
            || !self.column_gap.is_finite()
            || !(0.0..=200.0).contains(&self.column_gap)
            || (self.width - self.left - self.right - self.column_gap * (self.columns - 1) as f64) / (self.columns as f64) < 72.0
            || self.header.chars().count() > 200
            || self.footer.chars().count() > 200
            || ![&self.header_align, &self.footer_align].iter().all(|a| matches!(a.as_str(), "left" | "center" | "right"))
        {
            return Err("Invalid page dimensions or margins".into());
        }
        Ok(())
    }
}
pub fn id() -> String {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(1);
    format!(
        "{:x}-{:x}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}
impl Document {
    pub fn new(kind: &str, title: &str) -> Result<Self, String> {
        if title.trim().is_empty() || title.len() > 4096 {
            return Err("Invalid document title".into());
        }
        let content = match kind {
            "writer" => Content::Writer {
                paragraphs: vec![Paragraph {
                    id: id(),
                    text: String::new(),
                    style: TextStyle::default(),
                    runs: vec![],
                    image: None,
                    table: None,
                }],
            },
            "sheets" => Content::Sheets {
                cells: BTreeMap::new(),
            },
            "slides" => Content::Slides {
                slides: vec![Slide::new(id(), vec![])],
            },
            _ => return Err("Unknown document kind".into()),
        };
        Ok(Self {
            schema: 2,
            id: id(),
            title: title.into(),
            revision: 0,
            content,
            page_layout: None,
            office_source: None,
            sheet_meta: None,
            footnotes: Vec::new(),
            comments: Vec::new(),
            revisions: Vec::new(),
        })
    }
}
