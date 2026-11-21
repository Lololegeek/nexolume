//! Helpers shared by the XLSX and PPTX readers: package access, relationships, element lookup and colours.
use roxmltree::{Document as Xml, Node};
use std::{
    collections::BTreeMap,
    io::{Cursor, Read, Write},
};
use zip::{write::SimpleFileOptions, ZipArchive, ZipWriter};

pub type Archive<'a> = ZipArchive<Cursor<&'a [u8]>>;

pub fn open<'a>(bytes: &'a [u8], label: &str) -> Result<Archive<'a>, String> {
    if bytes.len() > 10 * 1024 * 1024 {
        return Err(format!("Le fichier {label} dépasse 10 Mio"));
    }
    let archive = ZipArchive::new(Cursor::new(bytes))
        .map_err(|e| format!("Fichier {label} invalide : {e}"))?;
    if archive.len() > 4096 {
        return Err(format!("Le fichier {label} contient trop de parties"));
    }
    Ok(archive)
}

pub fn text(archive: &mut Archive<'_>, name: &str) -> Result<Option<String>, String> {
    let mut entry = match archive.by_name(name) {
        Ok(e) => e,
        Err(zip::result::ZipError::FileNotFound) => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    if entry.size() > 24 * 1024 * 1024 {
        return Err(format!("Partie XML trop grande : {name}"));
    }
    let mut out = String::new();
    entry.read_to_string(&mut out).map_err(|e| e.to_string())?;
    Ok(Some(out))
}

pub fn bytes(archive: &mut Archive<'_>, name: &str) -> Result<Option<Vec<u8>>, String> {
    let mut entry = match archive.by_name(name) {
        Ok(e) => e,
        Err(zip::result::ZipError::FileNotFound) => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    if entry.size() > 12 * 1024 * 1024 {
        return Ok(None);
    }
    let mut out = Vec::new();
    entry.read_to_end(&mut out).map_err(|e| e.to_string())?;
    Ok(Some(out))
}

pub fn resolve(base: &str, target: &str) -> Option<String> {
    if target.contains(':') || target.contains('\\') {
        return None;
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
                parts.pop()?;
            }
            p => parts.push(p),
        }
    }
    Some(parts.join("/"))
}

pub fn folder(path: &str) -> &str {
    path.rsplit_once('/').map(|p| p.0).unwrap_or("")
}

pub fn rels_path(path: &str) -> String {
    let (dir, file) = path.rsplit_once('/').unwrap_or(("", path));
    if dir.is_empty() {
        format!("_rels/{file}.rels")
    } else {
        format!("{dir}/_rels/{file}.rels")
    }
}

#[derive(Clone, Debug)]
pub struct Rel {
    pub id: String,
    pub kind: String,
    pub target: String,
}

/// Internal relationships of `part`, with targets resolved to package paths.
pub fn rels(archive: &mut Archive<'_>, part: &str) -> Result<Vec<Rel>, String> {
    let Some(xml) = text(archive, &rels_path(part))? else {
        return Ok(vec![]);
    };
    let doc = Xml::parse(&xml).map_err(|e| e.to_string())?;
    let base = folder(part);
    Ok(doc
        .root_element()
        .children()
        .filter(|n| n.is_element() && n.attribute("TargetMode") != Some("External"))
        .filter_map(|n| {
            Some(Rel {
                id: n.attribute("Id")?.to_string(),
                kind: n.attribute("Type")?.rsplit('/').next()?.to_string(),
                target: resolve(base, n.attribute("Target")?)?,
            })
        })
        .collect())
}

pub fn child<'a, 'i>(node: Node<'a, 'i>, name: &str) -> Option<Node<'a, 'i>> {
    node.children()
        .find(|n| n.is_element() && n.tag_name().name() == name)
}

pub fn children<'a, 'i: 'a>(node: Node<'a, 'i>, name: &'a str) -> impl Iterator<Item = Node<'a, 'i>> + 'a {
    node.children()
        .filter(move |n| n.is_element() && n.tag_name().name() == name)
}

pub fn desc<'a, 'i>(node: Node<'a, 'i>, name: &str) -> Option<Node<'a, 'i>> {
    node.descendants()
        .find(|n| n.is_element() && n.tag_name().name() == name)
}

/// Value of an attribute by local name, whatever its namespace.
pub fn attr<'a>(node: Node<'a, '_>, name: &str) -> Option<&'a str> {
    node.attributes()
        .find(|a| a.name() == name)
        .map(|a| a.value())
}

/// `r:id`/`r:embed` style relationship attribute (namespaced).
pub fn rid<'a>(node: Node<'a, '_>, name: &str) -> Option<&'a str> {
    node.attributes()
        .find(|a| a.name() == name && a.namespace().is_some())
        .map(|a| a.value())
}

pub fn flag(node: Node<'_, '_>, name: &str) -> bool {
    match attr(node, name) {
        Some(v) => !matches!(v, "0" | "false" | "off"),
        None => false,
    }
}

/// Theme colour scheme keyed by DrawingML names (dk1, lt1, accent1…).
pub fn theme(archive: &mut Archive<'_>, part: Option<&str>) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    let Some(part) = part else { return out };
    let Ok(Some(xml)) = text(archive, part) else {
        return out;
    };
    let Ok(doc) = Xml::parse(&xml) else { return out };
    if let Some(scheme) = desc(doc.root_element(), "clrScheme") {
        for entry in scheme.children().filter(|n| n.is_element()) {
            let value = entry.children().filter(|n| n.is_element()).find_map(|c| {
                match c.tag_name().name() {
                    "srgbClr" => attr(c, "val").map(str::to_string),
                    "sysClr" => attr(c, "lastClr").map(str::to_string),
                    _ => None,
                }
            });
            if let Some(v) = value.and_then(|v| hex(&v)) {
                out.insert(entry.tag_name().name().to_string(), v);
            }
        }
    }
    out
}

/// Normalises RRGGBB / AARRGGBB to #RRGGBB.
pub fn hex(value: &str) -> Option<String> {
    let v = value.trim();
    let v = if v.len() == 8 { &v[2..] } else { v };
    (v.len() == 6 && v.bytes().all(|b| b.is_ascii_hexdigit())).then(|| format!("#{}", v.to_ascii_uppercase()))
}

fn rgb(c: &str) -> (f64, f64, f64) {
    let p = |i: usize| u8::from_str_radix(&c[i..i + 2], 16).unwrap_or(0) as f64 / 255.0;
    (p(1), p(3), p(5))
}

fn to_hsl((r, g, b): (f64, f64, f64)) -> (f64, f64, f64) {
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let l = (max + min) / 2.0;
    if (max - min).abs() < 1e-9 {
        return (0.0, 0.0, l);
    }
    let d = max - min;
    let s = if l > 0.5 { d / (2.0 - max - min) } else { d / (max + min) };
    let h = if max == r {
        (g - b) / d + if g < b { 6.0 } else { 0.0 }
    } else if max == g {
        (b - r) / d + 2.0
    } else {
        (r - g) / d + 4.0
    };
    (h / 6.0, s, l)
}

fn from_hsl((h, s, l): (f64, f64, f64)) -> String {
    let f = |p: f64, q: f64, mut t: f64| {
        if t < 0.0 {
            t += 1.0
        }
        if t > 1.0 {
            t -= 1.0
        }
        if t < 1.0 / 6.0 {
            p + (q - p) * 6.0 * t
        } else if t < 0.5 {
            q
        } else if t < 2.0 / 3.0 {
            p + (q - p) * (2.0 / 3.0 - t) * 6.0
        } else {
            p
        }
    };
    let (r, g, b) = if s == 0.0 {
        (l, l, l)
    } else {
        let q = if l < 0.5 { l * (1.0 + s) } else { l + s - l * s };
        let p = 2.0 * l - q;
        (f(p, q, h + 1.0 / 3.0), f(p, q, h), f(p, q, h - 1.0 / 3.0))
    };
    let c = |v: f64| (v.clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("#{:02X}{:02X}{:02X}", c(r), c(g), c(b))
}

/// Excel tint: darkens for negative values, lightens for positive ones.
pub fn tint(color: &str, tint: f64) -> String {
    let (h, s, l) = to_hsl(rgb(color));
    let l = if tint < 0.0 { l * (1.0 + tint) } else { l * (1.0 - tint) + tint };
    from_hsl((h, s, l))
}

/// DrawingML luminance modulation (values in 1/100000).
pub fn lum(color: &str, modulation: f64, offset: f64) -> String {
    let (h, s, l) = to_hsl(rgb(color));
    from_hsl((h, s, (l * modulation + offset).clamp(0.0, 1.0)))
}

pub fn shade(color: &str, factor: f64) -> String {
    let (r, g, b) = rgb(color);
    let c = |v: f64| ((v * factor).clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("#{:02X}{:02X}{:02X}", c(r), c(g), c(b))
}

pub fn mix_white(color: &str, factor: f64) -> String {
    let (r, g, b) = rgb(color);
    let c = |v: f64| ((v * factor + (1.0 - factor)).clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("#{:02X}{:02X}{:02X}", c(r), c(g), c(b))
}

pub fn escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\t' | '\n' | '\r' => out.push(c),
            c if (c as u32) < 0x20 => (),
            c => out.push(c),
        }
    }
    out
}

/// Copies `original` (when given), replacing `parts`, dropping `skip`, then appends new parts.
pub fn write(
    mut original: Option<&mut Archive<'_>>,
    mut parts: BTreeMap<String, Vec<u8>>,
    skip: &[String],
) -> Result<Vec<u8>, String> {
    let mut writer = ZipWriter::new(Cursor::new(Vec::new()));
    let deflate = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    if let Some(archive) = original.as_mut() {
        for i in 0..archive.len() {
            let entry = archive.by_index(i).map_err(|e| e.to_string())?;
            let name = entry.name().to_string();
            if skip.contains(&name) {
                continue;
            }
            if let Some(data) = parts.remove(&name) {
                writer.start_file(name, deflate).map_err(|e| e.to_string())?;
                writer.write_all(&data).map_err(|e| e.to_string())?;
            } else {
                writer.raw_copy_file(entry).map_err(|e| e.to_string())?;
            }
        }
    }
    for (name, data) in parts {
        writer.start_file(name, deflate).map_err(|e| e.to_string())?;
        writer.write_all(&data).map_err(|e| e.to_string())?;
    }
    writer
        .finish()
        .map(|c| c.into_inner())
        .map_err(|e| e.to_string())
}

/// Removes the first element named `tag` (with its content) from raw XML text.
pub fn remove_element(xml: &str, tag: &str) -> String {
    let Ok(doc) = Xml::parse(xml) else { return xml.into() };
    let range = doc
        .root_element()
        .descendants()
        .find(|n| n.is_element() && n.tag_name().name() == tag)
        .map(|n| n.range());
    match range {
        Some(r) => format!("{}{}", &xml[..r.start], &xml[r.end..]),
        None => xml.into(),
    }
}
