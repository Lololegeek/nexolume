//! Excel workbooks: the active worksheet becomes a Sheets document; export patches the original package.
use crate::ooxml::{self as ox, attr, child, children, desc, Archive};
use base64::Engine as _;
use document_core::{Cell, CellStyle, CondRule, Content, Document, OfficeSource, SheetChart, SheetMeta};
use roxmltree::{Document as Xml, Node};
use std::collections::{BTreeMap, BTreeSet};

const MAX_CELLS: usize = 100_000;
const MAIN_NS: &str = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
const REL_NS: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

#[derive(Clone, Default)]
struct Font {
    bold: bool,
    italic: bool,
    underline: bool,
    color: String,
}
#[derive(Clone, Default)]
struct Xf {
    num: u32,
    font: usize,
    fill: usize,
    align: String,
}
#[derive(Default)]
struct Styles {
    formats: BTreeMap<u32, String>,
    fonts: Vec<Font>,
    fills: Vec<String>,
    xfs: Vec<Xf>,
    dxfs: Vec<(String, String)>,
}
#[derive(Clone, Copy, PartialEq, Debug)]
enum DateKind {
    Date,
    Time,
    DateTime,
}
/// One `<c>` element as found in the original sheet.
#[derive(Clone)]
struct Record {
    raw: String,
    s: Option<String>,
    t: Option<String>,
    v: Option<String>,
    shared: Option<String>,
    cell: Cell,
}
struct Sheet {
    workbook: String,
    path: String,
    name: String,
    date1904: bool,
    styles: Styles,
    records: BTreeMap<String, Record>,
    rows: BTreeMap<u32, Vec<(String, String)>>,
    cells: BTreeMap<String, Cell>,
    meta: SheetMeta,
}

const INDEXED: [&str; 64] = [
    "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF", "000000", "FFFFFF",
    "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF", "800000", "008000", "000080", "808000",
    "800080", "008080", "C0C0C0", "808080", "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080",
    "0066CC", "CCCCFF", "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
    "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99", "3366FF", "33CCCC",
    "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696", "003366", "339966", "003300", "333300",
    "993300", "993366", "333399", "333333",
];
const THEME_ORDER: [&str; 12] = [
    "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink",
    "folHlink",
];

fn color(node: Option<Node<'_, '_>>, theme: &BTreeMap<String, String>) -> Option<String> {
    let n = node?;
    let base = if let Some(rgb) = attr(n, "rgb") {
        ox::hex(rgb)?
    } else if let Some(t) = attr(n, "theme") {
        let i: usize = t.parse().ok()?;
        theme.get(*THEME_ORDER.get(i)?).cloned().or_else(|| {
            Some(match i {
                0 => "#FFFFFF",
                1 => "#000000",
                2 => "#E7E6E6",
                3 => "#44546A",
                _ => return None,
            }
            .into())
        })?
    } else if let Some(i) = attr(n, "indexed") {
        format!("#{}", INDEXED.get(i.parse::<usize>().ok()?)?)
    } else {
        return None;
    };
    Some(match attr(n, "tint").and_then(|t| t.parse::<f64>().ok()) {
        Some(t) if t != 0.0 => ox::tint(&base, t.clamp(-1.0, 1.0)),
        _ => base,
    })
}

fn font(n: Node<'_, '_>, theme: &BTreeMap<String, String>) -> Font {
    let on = |name: &str| child(n, name).is_some_and(|c| attr(c, "val").is_none_or(|v| v != "0" && v != "false" && v != "none"));
    let ink = color(child(n, "color"), theme).filter(|c| c != "#000000").unwrap_or_else(|| "none".into());
    Font {
        bold: on("b"),
        italic: on("i"),
        underline: on("u"),
        color: ink,
    }
}

fn fill(n: Node<'_, '_>, theme: &BTreeMap<String, String>, dxf: bool) -> String {
    let Some(p) = child(n, "patternFill") else {
        return "none".into();
    };
    let kind = attr(p, "patternType").unwrap_or(if dxf { "solid" } else { "none" });
    if kind == "none" || (!dxf && kind == "gray125") {
        return "none".into();
    }
    let pick = if dxf {
        color(child(p, "bgColor"), theme).or_else(|| color(child(p, "fgColor"), theme))
    } else {
        color(child(p, "fgColor"), theme).or_else(|| color(child(p, "bgColor"), theme))
    };
    pick.unwrap_or_else(|| "none".into())
}

fn read_styles(xml: Option<&str>, theme: &BTreeMap<String, String>) -> Styles {
    let mut s = Styles::default();
    let Some(Ok(doc)) = xml.map(Xml::parse) else {
        s.fonts.push(Font { color: "none".into(), ..Font::default() });
        s.fills.push("none".into());
        s.xfs.push(Xf { align: "general".into(), ..Xf::default() });
        return s;
    };
    let root = doc.root_element();
    if let Some(n) = child(root, "numFmts") {
        for f in children(n, "numFmt") {
            if let (Some(id), Some(code)) = (attr(f, "numFmtId").and_then(|v| v.parse().ok()), attr(f, "formatCode")) {
                s.formats.insert(id, code.to_string());
            }
        }
    }
    if let Some(n) = child(root, "fonts") {
        s.fonts = children(n, "font").map(|f| font(f, theme)).collect();
    }
    if let Some(n) = child(root, "fills") {
        s.fills = children(n, "fill").map(|f| fill(f, theme, false)).collect();
    }
    if let Some(n) = child(root, "cellXfs") {
        s.xfs = children(n, "xf")
            .map(|x| {
                let num = |name| attr(x, name).and_then(|v| v.parse().ok()).unwrap_or(0);
                let align = child(x, "alignment")
                    .and_then(|a| attr(a, "horizontal"))
                    .map(|h| match h {
                        "left" | "right" | "center" => h,
                        "centerContinuous" => "center",
                        _ => "general",
                    })
                    .unwrap_or("general")
                    .to_string();
                Xf {
                    num: num("numFmtId") as u32,
                    font: num("fontId"),
                    fill: num("fillId"),
                    align,
                }
            })
            .collect();
    }
    if let Some(n) = child(root, "dxfs") {
        s.dxfs = children(n, "dxf")
            .map(|d| {
                let back = child(d, "fill").map(|f| fill(f, theme, true)).unwrap_or_else(|| "none".into());
                let ink = child(d, "font")
                    .and_then(|f| color(child(f, "color"), theme))
                    .unwrap_or_else(|| "none".into());
                (back, ink)
            })
            .collect();
    }
    if s.fonts.is_empty() {
        s.fonts.push(Font { color: "none".into(), ..Font::default() });
    }
    if s.xfs.is_empty() {
        s.xfs.push(Xf { align: "general".into(), ..Xf::default() });
    }
    s
}

/// Category understood by the Sheets editor, plus whether the code shows a date or time.
fn category(id: u32, code: Option<&str>) -> (&'static str, Option<DateKind>) {
    match id {
        0 | 49 => return ("general", None),
        1 => return ("integer", None),
        2 => return ("number", None),
        3 | 4 | 37..=40 => return ("thousands", None),
        5..=8 | 41..=44 => return ("currency", None),
        9 | 10 => return ("percent", None),
        11 | 48 => return ("scientific", None),
        14..=17 | 27..=31 | 34..=36 | 50..=58 => return ("general", Some(DateKind::Date)),
        18..=21 | 32 | 33 | 45..=47 => return ("general", Some(DateKind::Time)),
        22 => return ("general", Some(DateKind::DateTime)),
        _ => (),
    }
    let Some(code) = code else { return ("general", None) };
    let section = code.split(';').next().unwrap_or("");
    let mut plain = String::new();
    let mut chars = section.chars().peekable();
    let mut currency = false;
    while let Some(c) = chars.next() {
        match c {
            '"' => {
                for q in chars.by_ref() {
                    if q == '"' {
                        break;
                    }
                    if matches!(q, '€' | '$' | '£' | '¥') {
                        currency = true;
                    }
                }
            }
            '\\' => {
                if let Some(q) = chars.next() {
                    if matches!(q, '€' | '$' | '£' | '¥') {
                        currency = true;
                    }
                }
            }
            '[' => {
                let mut inner = String::new();
                for q in chars.by_ref() {
                    if q == ']' {
                        break;
                    }
                    inner.push(q);
                }
                if inner.starts_with('$') && inner.len() > 1 {
                    currency = true;
                }
                if matches!(inner.to_ascii_lowercase().as_str(), "h" | "hh" | "m" | "mm" | "s" | "ss") {
                    plain.push('h');
                }
            }
            '€' | '$' | '£' | '¥' => currency = true,
            c => plain.push(c),
        }
    }
    let lower = plain.to_ascii_lowercase();
    if lower.contains('y') || lower.contains('d') {
        let time = lower.contains('h') || lower.contains('s');
        return ("general", Some(if time { DateKind::DateTime } else { DateKind::Date }));
    }
    if lower.contains('h') || (lower.contains('s') && lower.contains('m')) {
        return ("general", Some(DateKind::Time));
    }
    if lower.contains("e+") || lower.contains("e-") {
        ("scientific", None)
    } else if plain.contains('%') {
        ("percent", None)
    } else if currency {
        ("currency", None)
    } else if plain.contains("#,##") || plain.contains("# ##") {
        ("thousands", None)
    } else if plain.contains("0.0") || plain.contains("0,0") || plain.contains("#.#") {
        ("number", None)
    } else if plain.contains('0') || plain.contains('#') {
        ("integer", None)
    } else {
        ("general", None)
    }
}

fn civil(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}
fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (m as i64 + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn date_text(serial: f64, kind: DateKind, date1904: bool) -> Option<String> {
    if !serial.is_finite() || !(0.0..3_000_000.0).contains(&serial) {
        return None;
    }
    let whole = serial.floor();
    let mut seconds = ((serial - whole) * 86400.0).round() as i64;
    let mut whole = whole as i64;
    if seconds >= 86400 {
        seconds -= 86400;
        whole += 1;
    }
    let time = format!("{:02}:{:02}", seconds / 3600, (seconds / 60) % 60)
        + &if seconds % 60 != 0 { format!(":{:02}", seconds % 60) } else { String::new() };
    if kind == DateKind::Time {
        return Some(time);
    }
    let base = if date1904 { -24_107 } else if whole >= 61 { -25_569 } else { -25_568 };
    let (y, m, d) = civil(base + whole);
    let date = format!("{d:02}/{m:02}/{y:04}");
    Some(if kind == DateKind::DateTime && seconds != 0 { format!("{date} {time}") } else { date })
}

/// Parses `dd/mm/yyyy[ hh:mm[:ss]]` or `hh:mm[:ss]` back to an Excel serial.
fn date_serial(text: &str, date1904: bool) -> Option<f64> {
    let text = text.trim();
    let (date, time) = match text.split_once(' ') {
        Some((a, b)) => (Some(a), Some(b)),
        None if text.contains(':') => (None, Some(text)),
        None => (Some(text), None),
    };
    let mut serial = 0.0;
    if let Some(date) = date {
        let parts: Vec<&str> = date.split('/').collect();
        if parts.len() != 3 {
            return None;
        }
        let d: u32 = parts[0].parse().ok()?;
        let m: u32 = parts[1].parse().ok()?;
        let y: i64 = parts[2].parse().ok()?;
        if !(1..=31).contains(&d) || !(1..=12).contains(&m) || !(1900..=9999).contains(&y) {
            return None;
        }
        let days = days_from_civil(y, m, d);
        let value = if date1904 { days + 24_107 } else { days + 25_569 };
        serial = if !date1904 && value < 61 { value - 1 } else { value } as f64;
    }
    if let Some(time) = time {
        let parts: Vec<u32> = time.split(':').map(|p| p.parse().ok()).collect::<Option<_>>()?;
        if parts.len() < 2 || parts.len() > 3 || parts[0] > 23 || parts[1] > 59 || parts.get(2).is_some_and(|s| *s > 59) {
            return None;
        }
        serial += (parts[0] * 3600 + parts[1] * 60 + parts.get(2).copied().unwrap_or(0)) as f64 / 86400.0;
    }
    Some(serial)
}

fn tidy(n: f64) -> String {
    let n: f64 = format!("{n:.14e}").parse().unwrap_or(n);
    if n == 0.0 {
        "0".into()
    } else {
        n.to_string()
    }
}

fn is_cell_ref(token: &str) -> Option<(bool, u32, bool, u32)> {
    let b = token.as_bytes();
    let mut i = 0;
    let abs_col = b.first() == Some(&b'$');
    if abs_col {
        i += 1;
    }
    let start = i;
    while i < b.len() && b[i].is_ascii_alphabetic() {
        i += 1;
    }
    if i == start || i - start > 3 {
        return None;
    }
    let col = token[start..i].to_ascii_uppercase();
    let abs_row = b.get(i) == Some(&b'$');
    if abs_row {
        i += 1;
    }
    let digits = &token[i..];
    if digits.is_empty() || !digits.bytes().all(|c| c.is_ascii_digit()) {
        return None;
    }
    let row: u32 = digits.parse().ok()?;
    let (c, _) = sheets_engine::parser::address(&format!("{col}1")).ok()?;
    (row >= 1).then_some((abs_col, c, abs_row, row))
}

/// Walks formula text outside strings and quoted sheet names, letting `f` rewrite each identifier token.
fn map_tokens(formula: &str, mut f: impl FnMut(&str, Option<char>, Option<char>) -> String) -> String {
    let chars: Vec<char> = formula.chars().collect();
    let mut out = String::with_capacity(formula.len());
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c == '"' || c == '\'' {
            out.push(c);
            i += 1;
            while i < chars.len() {
                out.push(chars[i]);
                if chars[i] == c {
                    if chars.get(i + 1) == Some(&c) {
                        out.push(c);
                        i += 2;
                        continue;
                    }
                    i += 1;
                    break;
                }
                i += 1;
            }
            continue;
        }
        if c.is_alphanumeric() || c == '$' || c == '_' || c == '.' {
            let start = i;
            while i < chars.len() && (chars[i].is_alphanumeric() || matches!(chars[i], '$' | '_' | '.')) {
                i += 1;
            }
            let token: String = chars[start..i].iter().collect();
            let before = start.checked_sub(1).map(|p| chars[p]);
            out.push_str(&f(&token, before, chars.get(i).copied()));
            continue;
        }
        out.push(c);
        i += 1;
    }
    out
}

fn shift(formula: &str, dc: i64, dr: i64) -> String {
    map_tokens(formula, |token, _, next| {
        if matches!(next, Some('(') | Some('!')) {
            return token.into();
        }
        match is_cell_ref(token) {
            Some((ac, c, ar, r)) => {
                let c = if ac { c as i64 } else { c as i64 + dc };
                let r = if ar { r as i64 } else { r as i64 + dr };
                if c < 1 || r < 1 || c > 16384 || r > 1_048_576 {
                    return "#REF!".into();
                }
                let name = sheets_engine::parser::name(c as u32, 1);
                let letters = name.trim_end_matches('1');
                format!("{}{letters}{}{r}", if ac { "$" } else { "" }, if ar { "$" } else { "" })
            }
            None => token.into(),
        }
    })
}

fn clean_formula(formula: &str, sheet: &str) -> String {
    let mut f = formula.replace("_xlfn._xlws.", "").replace("_xlfn.", "").replace("_xlws.", "").replace("_xludf.", "");
    let quoted = format!("'{}'!", sheet.replace('\'', "''"));
    f = f.replace(&quoted, "");
    if sheet.chars().all(|c| c.is_alphanumeric() || c == '_' || c == '.') {
        f = map_tokens(&f, |token, before, next| {
            if token == sheet && next == Some('!') && before != Some(':') {
                "\u{0}".into()
            } else {
                token.into()
            }
        })
        .replace("\u{0}!", "");
    }
    f
}

const FRENCH: &[(&str, &str)] = &[
    ("SOMME", "SUM"), ("NB", "COUNT"), ("MOYENNE", "AVERAGE"), ("PRODUIT", "PRODUCT"), ("MEDIANE", "MEDIAN"),
    ("GRANDE.VALEUR", "LARGE"), ("PETITE.VALEUR", "SMALL"), ("NBVAL", "COUNTA"), ("NB.VIDE", "COUNTBLANK"),
    ("RACINE", "SQRT"), ("ENT", "INT"), ("PUISSANCE", "POWER"), ("ARRONDI", "ROUND"), ("ARRONDI.SUP", "ROUNDUP"),
    ("ARRONDI.INF", "ROUNDDOWN"), ("TRONQUE", "TRUNC"), ("SI", "IF"), ("SIERREUR", "IFERROR"), ("ESTERREUR", "ISERROR"),
    ("ESTNUM", "ISNUMBER"), ("ESTTEXTE", "ISTEXT"), ("ESTVIDE", "ISBLANK"), ("VRAI", "TRUE"), ("FAUX", "FALSE"),
    ("NON", "NOT"), ("ET", "AND"), ("OU", "OR"), ("CONCATENER", "CONCATENATE"), ("NBCAR", "LEN"),
    ("MAJUSCULE", "UPPER"), ("MINUSCULE", "LOWER"), ("SUPPRESPACE", "TRIM"), ("CNUM", "VALUE"), ("GAUCHE", "LEFT"),
    ("DROITE", "RIGHT"), ("STXT", "MID"), ("NB.SI", "COUNTIF"), ("SOMME.SI", "SUMIF"), ("MOYENNE.SI", "AVERAGEIF"),
    ("RECHERCHEV", "VLOOKUP"), ("EQUIV", "MATCH"), ("SOMME.SI.ENS", "SUMIFS"), ("NB.SI.ENS", "COUNTIFS"),
    ("MOYENNE.SI.ENS", "AVERAGEIFS"), ("ECARTYPE", "STDEV"), ("ECARTYPE.P", "STDEVP"), ("VARIANCE", "VAR"),
    ("VAR.P", "VARP"), ("RANG", "RANK"), ("PLAFOND", "CEILING"), ("PLANCHER", "FLOOR"), ("ALEA", "RAND"),
    ("ALEA.ENTRE.BORNES", "RANDBETWEEN"), ("RECHERCHEH", "HLOOKUP"), ("SOMMEPROD", "SUMPRODUCT"),
    ("EST.PAIR", "ISEVEN"), ("EST.IMPAIR", "ISODD"), ("DEGRES", "DEGREES"), ("PGCD", "GCD"), ("PPCM", "LCM"),
    ("CHOISIR", "CHOOSE"), ("SI.NON.DISP", "IFNA"), ("SI.CONDITIONS", "IFS"), ("OUX", "XOR"), ("TROUVE", "FIND"),
    ("CHERCHE", "SEARCH"), ("SUBSTITUE", "SUBSTITUTE"), ("NOMPROPRE", "PROPER"), ("JOINDRE.TEXTE", "TEXTJOIN"),
    ("AUJOURDHUI", "TODAY"), ("MAINTENANT", "NOW"), ("ANNEE", "YEAR"), ("MOIS", "MONTH"), ("JOUR", "DAY"),
    ("JOURSEM", "WEEKDAY"), ("MAX", "MAX"), ("MIN", "MIN"), ("ABS", "ABS"), ("MOD", "MOD"), ("PI", "PI"),
    ("EXP", "EXP"), ("LN", "LN"), ("LOG", "LOG"), ("LOG10", "LOG10"), ("SIGNE", "SIGN"), ("RADIANS", "RADIANS"),
    ("CONCAT", "CONCAT"), ("REPT", "REPT"), ("TEXTE", "TEXT"), ("NB.JOURS.OUVRES", "NETWORKDAYS"),
    ("DATE", "DATE"), ("HEURE", "HOUR"), ("MINUTE", "MINUTE"),
];
const MODERN: &[&str] = &[
    "IFS", "IFNA", "XOR", "TEXTJOIN", "CONCAT", "SWITCH", "MAXIFS", "MINIFS", "XLOOKUP", "XMATCH", "FILTER", "SORT",
    "UNIQUE", "SEQUENCE", "STDEV.S", "STDEV.P", "VAR.S", "VAR.P", "CEILING.MATH", "FLOOR.MATH", "LET", "RANDARRAY",
    "DAYS", "ISOWEEKNUM",
];

/// Editor formula (French or English names, `;` separators) to the form Excel stores.
fn excel_formula(input: &str) -> String {
    let f = map_tokens(input, |token, _, next| {
        if next != Some('(') {
            return match token.to_ascii_uppercase().as_str() {
                "VRAI" => "TRUE".into(),
                "FAUX" => "FALSE".into(),
                _ => token.into(),
            };
        }
        let upper = token.to_ascii_uppercase();
        let english = FRENCH
            .iter()
            .find(|(fr, _)| *fr == upper)
            .map(|(_, en)| en.to_string())
            .unwrap_or(upper);
        if MODERN.contains(&english.as_str()) {
            format!("_xlfn.{english}")
        } else {
            english
        }
    });
    // Separators: `;` outside strings becomes `,`.
    let mut out = String::with_capacity(f.len());
    let mut quoted = false;
    for c in f.chars() {
        if c == '"' {
            quoted = !quoted;
        }
        out.push(if c == ';' && !quoted { ',' } else { c });
    }
    out
}

fn chart_id(i: usize) -> String {
    let raw: String = document_core::id().chars().filter(|c| c.is_ascii_hexdigit()).collect();
    let mixed = format!("{raw}{i:x}");
    let tail = &mixed[mixed.len().saturating_sub(12)..];
    format!("{tail:0>12}")
}

fn strip_ref(reference: &str, sheet: &str) -> Option<(u32, u32, u32, u32)> {
    let (owner, area) = match reference.rsplit_once('!') {
        Some((o, a)) => (Some(o.trim_matches('\'').replace("''", "'")), a),
        None => (None, reference),
    };
    if owner.is_some_and(|o| o != sheet) {
        return None;
    }
    let area = area.replace('$', "");
    let (a, b) = area.split_once(':').unwrap_or((&area, &area));
    let (c1, r1) = sheets_engine::parser::address(a).ok()?;
    let (c2, r2) = sheets_engine::parser::address(b).ok()?;
    Some((c1.min(c2), r1.min(r2), c1.max(c2), r1.max(r2)))
}

fn read_charts(archive: &mut Archive<'_>, sheet_path: &str, sheet: &str) -> Vec<SheetChart> {
    let mut out = Vec::new();
    let Ok(rels) = ox::rels(archive, sheet_path) else { return out };
    for drawing in rels.iter().filter(|r| r.kind == "drawing") {
        let Ok(chart_rels) = ox::rels(archive, &drawing.target) else { continue };
        for chart in chart_rels.iter().filter(|r| r.kind == "chart") {
            if out.len() >= 20 {
                return out;
            }
            let Ok(Some(xml)) = ox::text(archive, &chart.target) else { continue };
            let Ok(doc) = Xml::parse(&xml) else { continue };
            let Some(plot) = desc(doc.root_element(), "plotArea") else { continue };
            let Some((kind, group)) = plot.children().filter(|n| n.is_element()).find_map(|n| {
                Some((
                    match n.tag_name().name() {
                        "barChart" | "bar3DChart" => "bar",
                        "lineChart" | "line3DChart" | "areaChart" | "area3DChart" | "scatterChart" | "radarChart" => "line",
                        "pieChart" | "pie3DChart" | "doughnutChart" | "ofPieChart" => "pie",
                        _ => return None,
                    },
                    n,
                ))
            }) else {
                continue;
            };
            let mut bounds: Option<(u32, u32, u32, u32)> = None;
            let mut foreign = false;
            for f in group.descendants().filter(|n| n.is_element() && n.tag_name().name() == "f") {
                match f.text().and_then(|t| strip_ref(t.trim(), sheet)) {
                    Some((a, b, c, d)) => {
                        bounds = Some(match bounds {
                            Some((x, y, z, w)) => (x.min(a), y.min(b), z.max(c), w.max(d)),
                            None => (a, b, c, d),
                        })
                    }
                    None => foreign = true,
                }
            }
            let (Some((c1, r1, c2, r2)), false) = (bounds, foreign) else { continue };
            let title = desc(doc.root_element(), "title")
                .map(|t| {
                    t.descendants()
                        .filter(|n| n.is_element() && n.tag_name().name() == "t")
                        .filter_map(|n| n.text())
                        .collect::<String>()
                })
                .unwrap_or_default();
            let title: String = title.chars().take(150).collect();
            out.push(SheetChart {
                id: chart_id(out.len()),
                kind: kind.into(),
                range: format!("{}:{}", sheets_engine::parser::name(c1, r1), sheets_engine::parser::name(c2, r2)),
                title,
            });
        }
    }
    out
}

fn range_ok(range: &str) -> bool {
    let cell = |s: &str| sheets_engine::parser::address(s).is_ok() && s.chars().take_while(|c| c.is_ascii_uppercase()).count() <= 3;
    match range.split_once(':') {
        Some((a, b)) => cell(a) && cell(b),
        None => cell(range),
    }
}

fn read(bytes: &[u8]) -> Result<Sheet, String> {
    let mut archive = ox::open(bytes, "Excel")?;
    let root_rels = ox::rels(&mut archive, "")?;
    let workbook = root_rels
        .iter()
        .find(|r| r.kind == "officeDocument")
        .map(|r| r.target.clone())
        .unwrap_or_else(|| "xl/workbook.xml".into());
    let book_xml = ox::text(&mut archive, &workbook)?.ok_or("Classeur Excel introuvable dans le fichier")?;
    if book_xml.contains("strict.openxmlformats.org/spreadsheetml") {
        return Err("Le format Excel « Strict Open XML » n’est pas pris en charge ; réenregistrez le fichier en classeur Excel standard.".into());
    }
    let book = Xml::parse(&book_xml).map_err(|e| format!("Classeur Excel illisible : {e}"))?;
    let book_rels = ox::rels(&mut archive, &workbook)?;
    let date1904 = child(book.root_element(), "workbookPr").is_some_and(|p| ox::flag(p, "date1904"));
    let sheets: Vec<(String, String)> = child(book.root_element(), "sheets")
        .map(|s| {
            children(s, "sheet")
                .filter_map(|n| {
                    let id = ox::rid(n, "id")?;
                    let rel = book_rels.iter().find(|r| r.id == id && r.kind == "worksheet")?;
                    Some((attr(n, "name").unwrap_or("Feuil1").to_string(), rel.target.clone()))
                })
                .collect()
        })
        .unwrap_or_default();
    let active = desc(book.root_element(), "workbookView")
        .and_then(|v| attr(v, "activeTab"))
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(0);
    let (name, path) = sheets
        .get(active)
        .or_else(|| sheets.first())
        .cloned()
        .ok_or("Ce classeur ne contient aucune feuille de calcul lisible")?;
    let theme_part = book_rels.iter().find(|r| r.kind == "theme").map(|r| r.target.clone());
    let theme = ox::theme(&mut archive, theme_part.as_deref());
    let styles_part = book_rels.iter().find(|r| r.kind == "styles").map(|r| r.target.clone());
    let styles_xml = match &styles_part {
        Some(p) => ox::text(&mut archive, p)?,
        None => None,
    };
    let styles = read_styles(styles_xml.as_deref(), &theme);
    let mut shared = Vec::new();
    if let Some(p) = book_rels.iter().find(|r| r.kind == "sharedStrings") {
        if let Some(xml) = ox::text(&mut archive, &p.target)? {
            let doc = Xml::parse(&xml).map_err(|e| e.to_string())?;
            for si in children(doc.root_element(), "si") {
                shared.push(rich_text(si));
            }
        }
    }
    let sheet_xml = ox::text(&mut archive, &path)?.ok_or("Feuille Excel introuvable dans le fichier")?;
    let doc = Xml::parse(&sheet_xml).map_err(|e| format!("Feuille Excel illisible : {e}"))?;
    let root = doc.root_element();
    let mut records = BTreeMap::new();
    let mut rows = BTreeMap::new();
    let mut cells = BTreeMap::new();
    let mut masters: BTreeMap<String, (String, u32, u32)> = BTreeMap::new();
    let style_of = |s: Option<&str>| -> (CellStyle, Option<DateKind>) {
        let xf = s
            .and_then(|v| v.parse::<usize>().ok())
            .and_then(|i| styles.xfs.get(i))
            .cloned()
            .unwrap_or_default();
        let font = styles.fonts.get(xf.font).cloned().unwrap_or_default();
        let (format, date) = category(xf.num, styles.formats.get(&xf.num).map(String::as_str));
        (
            CellStyle {
                bold: font.bold,
                italic: font.italic,
                underline: font.underline,
                color: if font.color.is_empty() { "none".into() } else { font.color },
                fill: styles.fills.get(xf.fill).cloned().unwrap_or_else(|| "none".into()),
                align: if xf.align.is_empty() { "general".into() } else { xf.align },
                format: format.into(),
            },
            date,
        )
    };
    let mut cached: BTreeMap<String, String> = BTreeMap::new();
    if let Some(data) = child(root, "sheetData") {
        let mut next_row = 1u32;
        for row in children(data, "row") {
            let r = attr(row, "r").and_then(|v| v.parse().ok()).unwrap_or(next_row);
            next_row = r + 1;
            rows.insert(
                r,
                row.attributes()
                    .filter(|a| a.name() != "spans" && a.name() != "r")
                    .map(|a| (qualified(&sheet_xml, row, a.name(), a.namespace()), a.value().to_string()))
                    .collect(),
            );
            let mut next_col = 1u32;
            for c in children(row, "c") {
                let address = match attr(c, "r").map(str::to_ascii_uppercase) {
                    Some(a) => a,
                    None => sheets_engine::parser::name(next_col, r),
                };
                let Ok((col, row_n)) = sheets_engine::parser::address(&address) else { continue };
                next_col = col + 1;
                if records.len() >= MAX_CELLS {
                    return Err(format!(
                        "Cette feuille contient plus de {MAX_CELLS} cellules : elle est trop grande pour être ouverte sans perte."
                    ));
                }
                let s = attr(c, "s").map(str::to_string);
                let t = attr(c, "t").map(str::to_string);
                let v = child(c, "v").and_then(|n| n.text()).map(str::to_string);
                let f = child(c, "f");
                let (style, date) = style_of(s.as_deref());
                let mut shared_formula = None;
                let formula = f.and_then(|f| {
                    let text = f.text().map(str::to_string).unwrap_or_default();
                    if attr(f, "t") == Some("shared") {
                        let si = attr(f, "si")?.to_string();
                        if !text.trim().is_empty() {
                            masters.insert(si, (text.clone(), col, row_n));
                            shared_formula = Some(text.clone());
                            Some(text)
                        } else {
                            let (base, mc, mr) = masters.get(&si)?.clone();
                            let expanded = shift(&base, col as i64 - mc as i64, row_n as i64 - mr as i64);
                            shared_formula = Some(expanded.clone());
                            Some(expanded)
                        }
                    } else {
                        (!text.trim().is_empty()).then_some(text)
                    }
                });
                let value = match t.as_deref() {
                    Some("s") => v.as_deref().and_then(|i| i.parse::<usize>().ok()).and_then(|i| shared.get(i)).cloned(),
                    Some("inlineStr") => child(c, "is").map(rich_text),
                    Some("b") => v.as_deref().map(|b| if b == "1" || b == "true" { "VRAI".into() } else { "FAUX".into() }),
                    Some("e") | Some("str") | Some("d") => v.clone(),
                    _ => v.as_deref().and_then(|n| n.trim().parse::<f64>().ok()).map(|n| match date {
                        Some(kind) if formula.is_none() => date_text(n, kind, date1904).unwrap_or_else(|| tidy(n)),
                        _ => tidy(n),
                    }),
                };
                let mut input = match &formula {
                    Some(f) => format!("={}", clean_formula(f, &name)),
                    None => value.clone().unwrap_or_default(),
                };
                if input.len() > 8192 {
                    let mut end = 8192;
                    while !input.is_char_boundary(end) {
                        end -= 1;
                    }
                    input.truncate(end);
                }
                if formula.is_some() {
                    if let Some(v) = &value {
                        cached.insert(address.clone(), v.clone());
                    }
                }
                let empty_style = style == CellStyle::default();
                if input.is_empty() && empty_style {
                    continue;
                }
                let cell = Cell {
                    input,
                    display: String::new(),
                    style,
                };
                records.insert(
                    address.clone(),
                    Record {
                        raw: sheet_xml[c.range()].to_string(),
                        s,
                        t,
                        v,
                        shared: shared_formula,
                        cell: cell.clone(),
                    },
                );
                cells.insert(address, cell);
            }
        }
    }
    sheets_engine::recalculate(&mut cells, None);
    // Formulas the engine cannot evaluate (other sheets, unknown functions) keep Excel's last result.
    let mut fallback = false;
    for (address, value) in &cached {
        if let Some(cell) = cells.get_mut(address) {
            if cell.display.starts_with('#') && !value.starts_with('#') {
                cell.input = value.clone();
                fallback = true;
            }
        }
    }
    if fallback {
        sheets_engine::recalculate(&mut cells, None);
    }
    for (address, record) in records.iter_mut() {
        if let Some(cell) = cells.get(address) {
            record.cell.input = cell.input.clone();
        }
    }
    let mut meta = SheetMeta::default();
    if let Some(cols) = child(root, "cols") {
        for col in children(cols, "col") {
            let min: u32 = attr(col, "min").and_then(|v| v.parse().ok()).unwrap_or(0);
            let max: u32 = attr(col, "max").and_then(|v| v.parse().ok()).unwrap_or(0);
            if min == 0 || max < min || max - min > 256 || max > 16384 {
                continue;
            }
            let hidden = ox::flag(col, "hidden");
            let Some(width) = attr(col, "width").and_then(|v| v.parse::<f64>().ok()) else { continue };
            if !hidden && !ox::flag(col, "customWidth") && (width - 8.43).abs() < 0.01 {
                continue;
            }
            let px = if hidden { 8.0 } else { (width * 7.0 + 5.0).round().clamp(8.0, 800.0) };
            for i in min..=max {
                if meta.col_widths.len() < 2000 {
                    meta.col_widths.insert((i - 1).to_string(), px);
                }
            }
        }
    }
    if let Some(data) = child(root, "sheetData") {
        for row in children(data, "row") {
            let Some(r) = attr(row, "r").and_then(|v| v.parse::<u32>().ok()) else { continue };
            if meta.row_heights.len() >= 2000 {
                break;
            }
            if ox::flag(row, "hidden") {
                meta.row_heights.insert((r - 1).to_string(), 8.0);
            } else if ox::flag(row, "customHt") || ox::flag(row, "customHeight") {
                if let Some(ht) = attr(row, "ht").and_then(|v| v.parse::<f64>().ok()) {
                    meta.row_heights.insert((r - 1).to_string(), (ht * 4.0 / 3.0).round().clamp(8.0, 600.0));
                }
            }
        }
    }
    if let Some(pane) = desc(root, "sheetViews").and_then(|v| desc(v, "pane")) {
        if matches!(attr(pane, "state"), Some("frozen") | Some("frozenSplit")) {
            let n = |name| attr(pane, name).and_then(|v| v.parse::<f64>().ok()).unwrap_or(0.0).max(0.0) as u32;
            meta.frozen_rows = n("ySplit").min(100);
            meta.frozen_cols = n("xSplit").min(50);
        }
    }
    if let Some(merges) = child(root, "mergeCells") {
        meta.merges = children(merges, "mergeCell")
            .filter_map(|m| attr(m, "ref"))
            .map(str::to_ascii_uppercase)
            .filter(|r| r.contains(':') && range_ok(r))
            .take(1000)
            .collect();
    }
    for cf in children(root, "conditionalFormatting") {
        let ranges: Vec<String> = attr(cf, "sqref")
            .unwrap_or("")
            .split_whitespace()
            .map(str::to_ascii_uppercase)
            .filter(|r| range_ok(r))
            .collect();
        for rule in children(cf, "cfRule") {
            let formulas: Vec<String> = children(rule, "formula")
                .filter_map(|f| f.text())
                .map(|t| t.trim().trim_matches('"').to_string())
                .collect();
            let (op, value, value2) = match (attr(rule, "type"), attr(rule, "operator")) {
                (Some("cellIs"), Some("greaterThan")) => ("gt", formulas.first().cloned(), None),
                (Some("cellIs"), Some("lessThan")) => ("lt", formulas.first().cloned(), None),
                (Some("cellIs"), Some("equal")) => ("eq", formulas.first().cloned(), None),
                (Some("cellIs"), Some("between")) => ("between", formulas.first().cloned(), formulas.get(1).cloned()),
                (Some("containsText"), _) => ("contains", attr(rule, "text").map(str::to_string), None),
                (Some("containsBlanks"), _) => ("empty", Some(String::new()), None),
                _ => continue,
            };
            let Some(value) = value else { continue };
            let (back, ink) = attr(rule, "dxfId")
                .and_then(|d| d.parse::<usize>().ok())
                .and_then(|d| styles.dxfs.get(d))
                .cloned()
                .unwrap_or_else(|| ("none".into(), "none".into()));
            for range in &ranges {
                if meta.rules.len() >= 100 || value.len() >= 200 {
                    break;
                }
                meta.rules.push(CondRule {
                    range: range.clone(),
                    op: op.into(),
                    value: value.clone(),
                    value2: value2.clone().unwrap_or_default(),
                    fill: back.clone(),
                    color: ink.clone(),
                });
            }
        }
    }
    meta.charts = read_charts(&mut archive, &path, &name);
    Ok(Sheet {
        workbook,
        path,
        name,
        date1904,
        styles,
        records,
        rows,
        cells,
        meta,
    })
}

/// Attribute name as written in the source (keeps prefixes such as `x14ac:dyDescent`).
fn qualified(xml: &str, node: Node<'_, '_>, local: &str, ns: Option<&str>) -> String {
    if ns.is_none() {
        return local.into();
    }
    let start = &xml[node.range()];
    let head = &start[..start.find('>').unwrap_or(start.len())];
    head.split_whitespace()
        .filter_map(|p| p.split_once('=').map(|(k, _)| k))
        .find(|k| k.rsplit(':').next() == Some(local) && k.contains(':'))
        .unwrap_or(local)
        .to_string()
}

fn rich_text(node: Node<'_, '_>) -> String {
    let mut out = String::new();
    for n in node.descendants() {
        if n.is_element() && n.tag_name().name() == "t" && !n.ancestors().any(|a| a.tag_name().name() == "rPh") {
            out.push_str(n.text().unwrap_or(""));
        }
    }
    out
}

pub fn import(bytes: &[u8], title: &str) -> Result<Document, String> {
    let sheet = read(bytes)?;
    let mut doc = Document::new("sheets", title)?;
    doc.content = Content::Sheets { cells: sheet.cells };
    doc.sheet_meta = (sheet.meta != SheetMeta::default()).then_some(sheet.meta);
    doc.office_source = Some(OfficeSource {
        format: "xlsx".into(),
        archive: base64::engine::general_purpose::STANDARD.encode(bytes),
        main_part: sheet.path,
        paragraphs: vec![],
        page_layout: None,
        comments: vec![],
        revisions: vec![],
    });
    Ok(doc)
}

fn template(title: &str) -> Result<Vec<u8>, String> {
    let mut parts = BTreeMap::new();
    let mut put = |name: &str, text: String| {
        parts.insert(name.to_string(), text.into_bytes());
    };
    put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>".into());
    put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>".into());
    put("xl/workbook.xml", format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"Feuil1\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>"));
    put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>".into());
    put("xl/worksheets/sheet1.xml", format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"{MAIN_NS}\" xmlns:r=\"{REL_NS}\"><dimension ref=\"A1\"/><sheetData/><pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></worksheet>"));
    put("xl/styles.xml", format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<styleSheet xmlns=\"{MAIN_NS}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>"));
    put("docProps/core.xml", format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{}</dc:title><dc:creator>Nexolume</dc:creator></cp:coreProperties>", ox::escape(title)));
    ox::write(None, parts, &[])
}

/// Inserts `item` as the last child of the first `tag` element (creating it before `before` when missing) and fixes its count.
fn add_child(xml: &str, tag: &str, item: &str, before: &[&str]) -> Result<(String, usize), String> {
    let doc = Xml::parse(xml).map_err(|e| e.to_string())?;
    let root = doc.root_element();
    let prefix = prefix_of(xml, root);
    let result = if let Some(node) = child(root, tag) {
        let index = node.children().filter(|n| n.is_element()).count();
        let r = node.range();
        let raw = &xml[r.clone()];
        let text = if raw.ends_with("/>") {
            let head = &raw[..raw.len() - 2];
            format!("{}{head}>{item}</{prefix}{tag}>{}", &xml[..r.start], &xml[r.end..])
        } else {
            let end = r.start + raw.rfind("</").ok_or("XML invalide")?;
            format!("{}{item}{}", &xml[..end], &xml[end..])
        };
        (text, index)
    } else {
        let at = before
            .iter()
            .find_map(|b| child(root, b).map(|n| n.range().start))
            .unwrap_or_else(|| root.range().start + xml[root.range()].rfind("</").unwrap_or(0));
        (format!("{}<{prefix}{tag}>{item}</{prefix}{tag}>{}", &xml[..at], &xml[at..]), 0)
    };
    Ok((set_count(&result.0, tag)?, result.1))
}

fn set_count(xml: &str, tag: &str) -> Result<String, String> {
    let doc = Xml::parse(xml).map_err(|e| e.to_string())?;
    let Some(node) = child(doc.root_element(), tag) else { return Ok(xml.into()) };
    let count = node.children().filter(|n| n.is_element()).count();
    let r = node.range();
    let raw = &xml[r.clone()];
    let head_end = raw.find('>').ok_or("XML invalide")?;
    let head = &raw[..head_end];
    let new_head = match head.find(" count=\"") {
        Some(p) => {
            let rest = &head[p + 8..];
            let q = rest.find('"').unwrap_or(rest.len());
            format!("{} count=\"{count}\"{}", &head[..p], &rest[q + 1..])
        }
        None => {
            let trimmed = head.trim_end_matches('/');
            format!("{trimmed} count=\"{count}\"{}", if head.ends_with('/') { "/" } else { "" })
        }
    };
    Ok(format!("{}{new_head}{}", &xml[..r.start], &xml[r.start + head_end..]))
}

fn prefix_of(xml: &str, node: Node<'_, '_>) -> String {
    let raw = &xml[node.range()];
    let name = raw[1..].split([' ', '>', '/', '\n', '\r', '\t']).next().unwrap_or("");
    match name.split_once(':') {
        Some((p, _)) => format!("{p}:"),
        None => String::new(),
    }
}

/// Builds or reuses cell formats in styles.xml.
struct StyleWriter<'a> {
    xml: String,
    styles: &'a Styles,
    known: BTreeMap<String, String>,
    fills: Vec<String>,
    currency: Option<u32>,
}
fn style_key(s: &CellStyle) -> String {
    format!("{}|{}|{}|{}|{}|{}|{}", s.bold, s.italic, s.underline, s.format, s.align, s.color, s.fill)
}
impl StyleWriter<'_> {
    fn index(&mut self, style: &CellStyle, base: Option<&str>) -> Result<Option<String>, String> {
        let key = style_key(style);
        if let Some(s) = self.known.get(&key) {
            return Ok(Some(s.clone()));
        }
        if *style == CellStyle::default() && base.is_none() {
            return Ok(None);
        }
        let base_index: usize = base.and_then(|b| b.parse().ok()).unwrap_or(0);
        let doc = Xml::parse(&self.xml).map_err(|e| e.to_string())?;
        let root = doc.root_element();
        let q = prefix_of(&self.xml, root);
        let base_xf = child(root, "cellXfs").and_then(|x| children(x, "xf").nth(base_index));
        let base_info = self.styles.xfs.get(base_index).cloned().unwrap_or_default();
        let base_font = self.styles.fonts.get(base_info.font).cloned().unwrap_or_default();
        let num = attr_u(base_xf, "numFmtId").unwrap_or(0);
        let (base_category, _) = category(num, self.styles.formats.get(&num).map(String::as_str));
        let border = attr_u(base_xf, "borderId").unwrap_or(0);
        let parent = attr_u(base_xf, "xfId").unwrap_or(0);
        let font_raw = child(root, "fonts")
            .and_then(|f| children(f, "font").nth(base_info.font))
            .map(|f| (f, self.xml[f.range()].to_string()));
        let mut alignment_attrs: Vec<(String, String)> = base_xf
            .and_then(|x| child(x, "alignment"))
            .map(|a| a.attributes().filter(|a| a.name() != "horizontal").map(|a| (a.name().to_string(), a.value().to_string())).collect())
            .unwrap_or_default();
        let font_xml = if base_font.bold == style.bold
            && base_font.italic == style.italic
            && base_font.underline == style.underline
            && (base_font.color == style.color || (base_font.color.is_empty() && style.color == "none"))
        {
            None
        } else {
            let mut out = format!("<{q}font>");
            if style.bold {
                out += &format!("<{q}b/>");
            }
            if style.italic {
                out += &format!("<{q}i/>");
            }
            let keep = |name: &str| -> String {
                font_raw
                    .as_ref()
                    .and_then(|(f, _)| child(*f, name))
                    .map(|n| self.xml[n.range()].to_string())
                    .unwrap_or_default()
            };
            out += &keep("strike");
            if style.underline {
                out += &format!("<{q}u/>");
            }
            out += &keep("vertAlign");
            let size = keep("sz");
            out += if size.is_empty() { "" } else { &size };
            if size.is_empty() {
                out += &format!("<{q}sz val=\"11\"/>");
            }
            if style.color != "none" {
                out += &format!("<{q}color rgb=\"FF{}\"/>", &style.color[1..]);
            } else if base_font.color == "none" || base_font.color.is_empty() {
                out += &keep("color");
            }
            let name = keep("name");
            out += &name;
            if name.is_empty() {
                out += &format!("<{q}name val=\"Calibri\"/>");
            }
            out += &keep("family");
            out += &keep("charset");
            out += &keep("scheme");
            out += &format!("</{q}font>");
            Some(out)
        };
        drop(doc);
        let font_id = match font_xml {
            Some(f) => {
                let (xml, index) = add_child(&self.xml, "fonts", &f, &["fills", "borders", "cellStyleXfs", "cellXfs"])?;
                self.xml = xml;
                index
            }
            None => base_info.font,
        };
        let fill_id = if style.fill == "none" {
            if self.fills.get(base_info.fill).is_some_and(|f| f == "none") { base_info.fill } else { 0 }
        } else if self.fills.get(base_info.fill) == Some(&style.fill) {
            base_info.fill
        } else if let Some(i) = self.fills.iter().position(|f| *f == style.fill) {
            i
        } else {
            let item = format!(
                "<{q}fill><{q}patternFill patternType=\"solid\"><{q}fgColor rgb=\"FF{}\"/><{q}bgColor indexed=\"64\"/></{q}patternFill></{q}fill>",
                &style.fill[1..]
            );
            let (xml, index) = add_child(&self.xml, "fills", &item, &["borders", "cellStyleXfs", "cellXfs"])?;
            self.xml = xml;
            self.fills.push(style.fill.clone());
            index
        };
        let num_id = if base_category == style.format {
            num
        } else {
            match style.format.as_str() {
                "integer" => 1,
                "number" => 2,
                "thousands" => 4,
                "percent" => 10,
                "scientific" => 11,
                "currency" => match self.currency {
                    Some(id) => id,
                    None => {
                        let id = self.styles.formats.keys().copied().max().unwrap_or(163).max(163) + 1;
                        let item = format!("<{q}numFmt numFmtId=\"{id}\" formatCode=\"#,##0.00\\ &quot;€&quot;\"/>");
                        let (xml, _) = add_child(&self.xml, "numFmts", &item, &["fonts", "fills", "borders", "cellStyleXfs", "cellXfs"])?;
                        self.xml = xml;
                        self.currency = Some(id);
                        id
                    }
                },
                _ => 0,
            }
        };
        if style.align != "general" {
            alignment_attrs.insert(0, ("horizontal".into(), style.align.clone()));
        }
        let alignment = if alignment_attrs.is_empty() {
            String::new()
        } else {
            let attrs: String = alignment_attrs.iter().map(|(k, v)| format!(" {k}=\"{}\"", ox::escape(v))).collect();
            format!("<{q}alignment{attrs}/>")
        };
        let xf = format!(
            "<{q}xf numFmtId=\"{num_id}\" fontId=\"{font_id}\" fillId=\"{fill_id}\" borderId=\"{border}\" xfId=\"{parent}\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\"{}{}",
            if alignment.is_empty() { "" } else { " applyAlignment=\"1\"" },
            if alignment.is_empty() { "/>".to_string() } else { format!(">{alignment}</{q}xf>") }
        );
        let (xml, index) = add_child(&self.xml, "cellXfs", &xf, &["cellStyles", "dxfs", "tableStyles", "colors", "extLst"])?;
        self.xml = xml;
        let s = index.to_string();
        self.known.insert(key, s.clone());
        Ok(Some(s))
    }
}
fn attr_u(node: Option<Node<'_, '_>>, name: &str) -> Option<u32> {
    node.and_then(|n| attr(n, name)).and_then(|v| v.parse().ok())
}

fn number(input: &str) -> Option<f64> {
    let t = input.trim().replace('\u{a0}', "").replace(' ', "");
    if t.is_empty() {
        return None;
    }
    let (t, percent) = match t.strip_suffix('%') {
        Some(rest) => (rest.to_string(), true),
        None => (t, false),
    };
    let n: f64 = t.replace(',', ".").parse().ok()?;
    n.is_finite().then_some(if percent { n / 100.0 } else { n })
}

fn cell_xml(q: &str, address: &str, cell: &Cell, s: Option<&str>, date: Option<DateKind>, date1904: bool) -> String {
    let s_attr = s.map(|s| format!(" s=\"{s}\"")).unwrap_or_default();
    let input = cell.input.as_str();
    if input.is_empty() {
        return format!("<{q}c r=\"{address}\"{s_attr}/>");
    }
    if let Some(formula) = input.strip_prefix('=') {
        return format!("<{q}c r=\"{address}\"{s_attr}><{q}f>{}</{q}f></{q}c>", ox::escape(&excel_formula(formula)));
    }
    if date.is_some() {
        if let Some(serial) = date_serial(input, date1904) {
            return format!("<{q}c r=\"{address}\"{s_attr}><{q}v>{}</{q}v></{q}c>", tidy(serial));
        }
    }
    if let Some(n) = number(input) {
        return format!("<{q}c r=\"{address}\"{s_attr}><{q}v>{}</{q}v></{q}c>", tidy(n));
    }
    match input.trim().to_ascii_uppercase().as_str() {
        "VRAI" | "TRUE" => return format!("<{q}c r=\"{address}\"{s_attr} t=\"b\"><{q}v>1</{q}v></{q}c>"),
        "FAUX" | "FALSE" => return format!("<{q}c r=\"{address}\"{s_attr} t=\"b\"><{q}v>0</{q}v></{q}c>"),
        _ => (),
    }
    format!(
        "<{q}c r=\"{address}\"{s_attr} t=\"inlineStr\"><{q}is><{q}t xml:space=\"preserve\">{}</{q}t></{q}is></{q}c>",
        ox::escape(input)
    )
}

fn replace_node(xml: &str, tag: &str, replacement: &str, before: &[&str]) -> Result<String, String> {
    let doc = Xml::parse(xml).map_err(|e| e.to_string())?;
    let root = doc.root_element();
    if let Some(n) = child(root, tag) {
        let r = n.range();
        return Ok(format!("{}{replacement}{}", &xml[..r.start], &xml[r.end..]));
    }
    if replacement.is_empty() {
        return Ok(xml.into());
    }
    let at = before
        .iter()
        .find_map(|b| child(root, b).map(|n| n.range().start))
        .unwrap_or_else(|| root.range().start + xml[root.range()].rfind("</").unwrap_or(0));
    Ok(format!("{}{replacement}{}", &xml[..at], &xml[at..]))
}

const AFTER_MERGES: [&str; 23] = [
    "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup",
    "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing",
    "legacyDrawing", "legacyDrawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst",
];

pub fn export(document: &Document, cells: &BTreeMap<String, Cell>) -> Result<Vec<u8>, String> {
    let source = document
        .office_source
        .as_ref()
        .filter(|s| s.format == "xlsx")
        .map(|s| base64::engine::general_purpose::STANDARD.decode(&s.archive).map_err(|e| e.to_string()))
        .transpose()?;
    let original = match source {
        Some(bytes) => bytes,
        None => template(&document.title)?,
    };
    let sheet = read(&original)?;
    let meta = document.sheet_meta.clone().unwrap_or_default();
    let same_cell = |a: &Cell, b: &Cell| a.input == b.input && a.style == b.style;
    let unchanged_cells = cells.len() == sheet.records.len()
        && cells.iter().all(|(k, c)| sheet.records.get(k).is_some_and(|r| same_cell(c, &r.cell)));
    if unchanged_cells && meta == sheet.meta {
        return Ok(original);
    }
    let mut archive = ox::open(&original, "Excel")?;
    let book_rels = ox::rels(&mut archive, &sheet.workbook)?;
    let styles_part = book_rels.iter().find(|r| r.kind == "styles").map(|r| r.target.clone());
    let mut parts: BTreeMap<String, Vec<u8>> = BTreeMap::new();
    let mut skip = Vec::new();
    let mut content_types = ox::text(&mut archive, "[Content_Types].xml")?.unwrap_or_default();
    let styles_xml = match &styles_part {
        Some(p) => ox::text(&mut archive, p)?,
        None => None,
    };
    let (styles_xml, styles_path) = match (styles_xml, styles_part) {
        (Some(x), Some(p)) => (x, p),
        _ => {
            // A workbook without styles gets the template's stylesheet.
            let tpl = template(&document.title)?;
            let mut t = ox::open(&tpl, "Excel")?;
            let x = ox::text(&mut t, "xl/styles.xml")?.unwrap_or_default();
            let folder = ox::folder(&sheet.workbook);
            let path = if folder.is_empty() { "styles.xml".to_string() } else { format!("{folder}/styles.xml") };
            let rels_path = ox::rels_path(&sheet.workbook);
            let rels = ox::text(&mut archive, &rels_path)?.unwrap_or_else(|| "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"></Relationships>".into());
            let rel = "<Relationship Id=\"rIdNexStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>";
            let at = rels.rfind("</Relationships>").unwrap_or(rels.len());
            parts.insert(rels_path, format!("{}{rel}{}", &rels[..at], &rels[at..]).into_bytes());
            let at = content_types.rfind("</Types>").unwrap_or(content_types.len());
            content_types.insert_str(at, &format!("<Override PartName=\"/{path}\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"));
            (x, path)
        }
    };
    let mut writer = StyleWriter {
        xml: styles_xml.clone(),
        styles: &sheet.styles,
        known: BTreeMap::new(),
        fills: sheet.styles.fills.clone(),
        currency: sheet
            .styles
            .formats
            .iter()
            .find(|(_, code)| code.as_str() == "#,##0.00\\ \"€\"")
            .map(|(id, _)| *id),
    };
    for record in sheet.records.values() {
        if let Some(s) = &record.s {
            writer.known.entry(style_key(&record.cell.style)).or_insert_with(|| s.clone());
        }
    }
    writer.known.entry(style_key(&CellStyle::default())).or_insert_with(|| "0".into());
    let sheet_xml = ox::text(&mut archive, &sheet.path)?.ok_or("Feuille Excel introuvable")?;
    let q = {
        let doc = Xml::parse(&sheet_xml).map_err(|e| e.to_string())?;
        prefix_of(&sheet_xml, doc.root_element())
    };
    let date_kind = |s: Option<&str>| -> Option<DateKind> {
        let xf = sheet.styles.xfs.get(s?.parse::<usize>().ok()?)?;
        category(xf.num, sheet.styles.formats.get(&xf.num).map(String::as_str)).1
    };
    let mut by_row: BTreeMap<u32, Vec<(u32, String)>> = BTreeMap::new();
    let (mut max_col, mut max_row) = (1u32, 1u32);
    for (address, cell) in cells {
        let (col, row) = sheets_engine::parser::address(address)?;
        max_col = max_col.max(col);
        max_row = max_row.max(row);
        let record = sheet.records.get(address);
        let xml = match record {
            Some(r) if same_cell(cell, &r.cell) => match &r.shared {
                Some(formula) => {
                    let s_attr = r.s.as_ref().map(|s| format!(" s=\"{s}\"")).unwrap_or_default();
                    let t_attr = r.t.as_ref().map(|t| format!(" t=\"{t}\"")).unwrap_or_default();
                    let v = r.v.as_ref().map(|v| format!("<{q}v>{}</{q}v>", ox::escape(v))).unwrap_or_default();
                    format!("<{q}c r=\"{address}\"{s_attr}{t_attr}><{q}f>{}</{q}f>{v}</{q}c>", ox::escape(formula))
                }
                None => r.raw.clone(),
            },
            Some(r) => {
                let s = if r.cell.style == cell.style { r.s.clone() } else { writer.index(&cell.style, r.s.as_deref())? };
                cell_xml(&q, address, cell, s.as_deref(), date_kind(s.as_deref()), sheet.date1904)
            }
            None => {
                let s = writer.index(&cell.style, None)?;
                cell_xml(&q, address, cell, s.as_deref(), date_kind(s.as_deref()), sheet.date1904)
            }
        };
        by_row.entry(row).or_default().push((col, xml));
    }
    let heights_changed = meta.row_heights != sheet.meta.row_heights;
    let mut all_rows: BTreeSet<u32> = by_row.keys().copied().collect();
    all_rows.extend(sheet.rows.keys().copied());
    if heights_changed {
        all_rows.extend(meta.row_heights.keys().filter_map(|k| k.parse::<u32>().ok()).map(|r| r + 1));
    }
    let mut data = format!("<{q}sheetData>");
    for r in all_rows {
        let mut attrs: Vec<(String, String)> = sheet.rows.get(&r).cloned().unwrap_or_default();
        if heights_changed {
            attrs.retain(|(k, _)| !matches!(k.as_str(), "ht" | "customHeight" | "hidden"));
            if let Some(px) = meta.row_heights.get(&(r - 1).to_string()) {
                if *px <= 8.0 {
                    attrs.push(("hidden".into(), "1".into()));
                } else {
                    attrs.push(("ht".into(), format!("{}", (px * 0.75 * 100.0).round() / 100.0)));
                    attrs.push(("customHeight".into(), "1".into()));
                }
            }
        }
        let cells_xml = by_row.remove(&r).unwrap_or_default();
        if cells_xml.is_empty() && attrs.is_empty() {
            continue;
        }
        max_row = max_row.max(r);
        let attr_text: String = attrs.iter().map(|(k, v)| format!(" {k}=\"{}\"", ox::escape(v))).collect();
        let mut cells_xml = cells_xml;
        cells_xml.sort_by_key(|(c, _)| *c);
        data += &format!("<{q}row r=\"{r}\"{attr_text}>");
        for (_, c) in cells_xml {
            data += &c;
        }
        data += &format!("</{q}row>");
    }
    data += &format!("</{q}sheetData>");
    let mut xml = {
        let doc = Xml::parse(&sheet_xml).map_err(|e| e.to_string())?;
        let node = child(doc.root_element(), "sheetData");
        match node {
            Some(n) => format!("{}{data}{}", &sheet_xml[..n.range().start], &sheet_xml[n.range().end..]),
            None => replace_node(&sheet_xml, "sheetData", &data, &AFTER_MERGES)?,
        }
    };
    if meta.col_widths != sheet.meta.col_widths {
        let mut cols = String::new();
        for (k, px) in &meta.col_widths {
            let Ok(i) = k.parse::<u32>() else { continue };
            if *px <= 8.0 {
                cols += &format!("<{q}col min=\"{n}\" max=\"{n}\" width=\"0\" hidden=\"1\" customWidth=\"1\"/>", n = i + 1);
            } else {
                let width = ((px - 5.0) / 7.0 * 100.0).round() / 100.0;
                cols += &format!("<{q}col min=\"{n}\" max=\"{n}\" width=\"{width}\" customWidth=\"1\"/>", n = i + 1);
            }
        }
        let replacement = if cols.is_empty() { String::new() } else { format!("<{q}cols>{cols}</{q}cols>") };
        xml = replace_node(&xml, "cols", &replacement, &["sheetData"])?;
    }
    if meta.merges != sheet.meta.merges {
        let items: String = meta.merges.iter().map(|m| format!("<{q}mergeCell ref=\"{m}\"/>")).collect();
        let replacement = if items.is_empty() {
            String::new()
        } else {
            format!("<{q}mergeCells count=\"{}\">{items}</{q}mergeCells>", meta.merges.len())
        };
        xml = replace_node(&xml, "mergeCells", &replacement, &AFTER_MERGES)?;
    }
    if meta.frozen_rows != sheet.meta.frozen_rows || meta.frozen_cols != sheet.meta.frozen_cols {
        let pane = if meta.frozen_rows == 0 && meta.frozen_cols == 0 {
            String::new()
        } else {
            let active = match (meta.frozen_cols > 0, meta.frozen_rows > 0) {
                (true, true) => "bottomRight",
                (false, true) => "bottomLeft",
                _ => "topRight",
            };
            let mut p = format!("<{q}pane");
            if meta.frozen_cols > 0 {
                p += &format!(" xSplit=\"{}\"", meta.frozen_cols);
            }
            if meta.frozen_rows > 0 {
                p += &format!(" ySplit=\"{}\"", meta.frozen_rows);
            }
            p += &format!(
                " topLeftCell=\"{}\" activePane=\"{active}\" state=\"frozen\"/>",
                sheets_engine::parser::name(meta.frozen_cols + 1, meta.frozen_rows + 1)
            );
            p
        };
        let doc = Xml::parse(&xml).map_err(|e| e.to_string())?;
        let view = child(doc.root_element(), "sheetViews").and_then(|v| child(v, "sheetView"));
        xml = match view {
            Some(v) => {
                let r = v.range();
                let raw = &xml[r.clone()];
                let head_end = raw.find('>').ok_or("XML invalide")?;
                let self_closing = raw[..=head_end].ends_with("/>");
                let head = if self_closing { format!("{}>", &raw[..head_end - 1]) } else { raw[..=head_end].to_string() };
                let inner: String = if self_closing {
                    String::new()
                } else {
                    v.children()
                        .filter(|n| n.is_element() && !matches!(n.tag_name().name(), "pane" | "selection"))
                        .map(|n| xml[n.range()].to_string())
                        .collect()
                };
                let name = head[1..].split([' ', '>', '/']).next().unwrap_or("sheetView").to_string();
                format!("{}{head}{pane}{inner}</{name}>{}", &xml[..r.start], &xml[r.end..])
            }
            None => {
                let views = format!("<{q}sheetViews><{q}sheetView workbookViewId=\"0\">{pane}</{q}sheetView></{q}sheetViews>");
                drop(doc);
                replace_node(&xml, "sheetViews", &views, &["sheetFormatPr", "cols", "sheetData"])?
            }
        };
    }
    {
        let doc = Xml::parse(&xml).map_err(|e| e.to_string())?;
        if let Some(d) = child(doc.root_element(), "dimension") {
            let r = d.range();
            let reference = format!("A1:{}", sheets_engine::parser::name(max_col, max_row));
            let text = format!("<{q}dimension ref=\"{reference}\"/>");
            xml = format!("{}{text}{}", &xml[..r.start], &xml[r.end..]);
        }
    }
    Xml::parse(&xml).map_err(|e| format!("Feuille Excel générée invalide : {e}"))?;
    parts.insert(sheet.path.clone(), xml.into_bytes());
    if writer.xml != styles_xml {
        Xml::parse(&writer.xml).map_err(|e| format!("Styles Excel générés invalides : {e}"))?;
    }
    parts.insert(styles_path, writer.xml.into_bytes());
    // Cached results may be stale: drop the calculation chain and ask Excel to recalculate.
    if let Some(chain) = book_rels.iter().find(|r| r.kind == "calcChain") {
        skip.push(chain.target.clone());
        let rels_path = ox::rels_path(&sheet.workbook);
        if let Some(rels) = ox::text(&mut archive, &rels_path)? {
            let doc = Xml::parse(&rels).map_err(|e| e.to_string())?;
            let node = doc.root_element().children().find(|n| n.is_element() && n.attribute("Id") == Some(chain.id.as_str()));
            if let Some(n) = node {
                let r = n.range();
                let text = format!("{}{}", &rels[..r.start], &rels[r.end..]);
                parts.insert(rels_path.clone(), text.into_bytes());
            }
        }
        let doc = Xml::parse(&content_types).map_err(|e| e.to_string())?;
        let range = doc
            .root_element()
            .children()
            .find(|n| n.is_element() && n.attribute("PartName") == Some(format!("/{}", chain.target).as_str()))
            .map(|n| n.range());
        if let Some(r) = range {
            content_types = format!("{}{}", &content_types[..r.start], &content_types[r.end..]);
        }
    }
    parts.insert("[Content_Types].xml".into(), content_types.into_bytes());
    let book_xml = ox::text(&mut archive, &sheet.workbook)?.unwrap_or_default();
    let book_xml = {
        let doc = Xml::parse(&book_xml).map_err(|e| e.to_string())?;
        let root = doc.root_element();
        let q = prefix_of(&book_xml, root);
        match child(root, "calcPr") {
            Some(c) if attr(c, "fullCalcOnLoad").is_none() => {
                let r = c.range();
                let raw = &book_xml[r.clone()];
                let name_end = 1 + raw[1..].find([' ', '/', '>']).unwrap_or(raw.len() - 1);
                format!("{}{} fullCalcOnLoad=\"1\"{}", &book_xml[..r.start], &raw[..name_end], &book_xml[r.start + name_end..])
            }
            Some(_) => book_xml.clone(),
            None => {
                drop(doc);
                replace_node(
                    &book_xml,
                    "calcPr",
                    &format!("<{q}calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>"),
                    &["oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr", "smartTagTypes", "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst"],
                )?
            }
        }
    };
    parts.insert(sheet.workbook.clone(), book_xml.into_bytes());
    ox::write(Some(&mut archive), parts, &skip)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cell(input: &str) -> Cell {
        Cell { input: input.into(), display: String::new(), style: CellStyle::default() }
    }

    #[test]
    fn new_workbook_round_trips() {
        let mut doc = Document::new("sheets", "Budget").unwrap();
        let mut cells = BTreeMap::new();
        cells.insert("A1".to_string(), cell("Total"));
        cells.insert("B1".to_string(), cell("12,5"));
        cells.insert("B2".to_string(), cell("=SOMME(B1;3)"));
        let mut bold = cell("Gras");
        bold.style.bold = true;
        bold.style.fill = "#FFCC00".into();
        bold.style.format = "currency".into();
        cells.insert("C3".to_string(), bold);
        doc.content = Content::Sheets { cells: cells.clone() };
        doc.sheet_meta = Some(SheetMeta { frozen_rows: 1, merges: vec!["A5:B6".into()], ..SheetMeta::default() });
        let bytes = export(&doc, &cells).unwrap();
        let back = import(&bytes, "Budget").unwrap();
        let Content::Sheets { cells: got } = &back.content else { panic!() };
        assert_eq!(got["A1"].input, "Total");
        assert_eq!(got["B1"].input, "12.5");
        assert_eq!(got["B2"].input, "=SUM(B1,3)");
        assert_eq!(got["B2"].display, "15.5");
        assert!(got["C3"].style.bold);
        assert_eq!(got["C3"].style.fill, "#FFCC00");
        assert_eq!(got["C3"].style.format, "currency");
        let meta = back.sheet_meta.clone().unwrap();
        assert_eq!(meta.frozen_rows, 1);
        assert_eq!(meta.merges, vec!["A5:B6".to_string()]);
        // Unchanged re-export keeps the bytes.
        let Content::Sheets { cells: again } = &back.content else { panic!() };
        assert_eq!(export(&back, again).unwrap(), bytes);
    }

    #[test]
    fn shared_formulas_and_dates() {
        assert_eq!(shift("A1*$B$1+B2", 1, 2), "B3*$B$1+C4");
        assert_eq!(shift("SUM(A1:A3)", 0, 1), "SUM(A2:A4)");
        assert_eq!(date_text(45000.0, DateKind::Date, false).unwrap(), "15/03/2023");
        assert_eq!(date_serial("15/03/2023", false).unwrap(), 45000.0);
        assert_eq!(date_text(0.5, DateKind::Time, false).unwrap(), "12:00");
        assert_eq!(category(164, Some("#,##0.00\\ \"€\"")).0, "currency");
        assert_eq!(category(165, Some("dd/mm/yyyy")).1, Some(DateKind::Date));
        assert_eq!(category(166, Some("0.0%")).0, "percent");
        assert_eq!(excel_formula("SI(A1>0;\"a;b\";SI.CONDITIONS(A1;1))"), "IF(A1>0,\"a;b\",_xlfn.IFS(A1,1))");
        assert_eq!(clean_formula("_xlfn.IFS(Feuil1!A1>0,1)", "Feuil1"), "IFS(A1>0,1)");
    }
}
