//! PowerPoint (PPTX) import and export.
//!
//! Import flattens every slide onto the 960×540 Nexolume surface: shapes, text, pictures, groups,
//! tables (as text), inherited layout/master decoration, backgrounds, notes and transitions.
//! Export keeps the original package: unchanged slides are copied byte for byte, edited or new
//! slides are regenerated against the original layouts, so themes, masters and media survive.
use crate::ooxml::{self, attr, child, children, desc, rid, Archive, Rel};
use base64::Engine as _;
use document_core::{Content, Document, ImageData, OfficeSource, Slide, SlideObject};
use roxmltree::{Document as Xml, Node};
use std::{
    collections::{BTreeMap, HashMap},
    rc::Rc,
};

const MAX_SLIDES: usize = 500;
const MAX_OBJECTS: usize = 1500;
const IMAGE_BUDGET: usize = 40 * 1024 * 1024;
const SURFACE_W: f64 = 960.0;
const SURFACE_H: f64 = 540.0;
const NS_A: &str = "http://schemas.openxmlformats.org/drawingml/2006/main";
const NS_R: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const NS_P: &str = "http://schemas.openxmlformats.org/presentationml/2006/main";
const REL: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const CT_SLIDE: &str = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
const CT_NOTES: &str = "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml";

// ---------------------------------------------------------------- shared reading model

#[derive(Clone, Copy, Debug)]
struct Frame {
    x: f64,
    y: f64,
    w: f64,
    h: f64,
    rot: f64,
    flip_h: bool,
    flip_v: bool,
}

/// Affine map from a part's (possibly grouped) coordinates to slide EMU.
#[derive(Clone, Copy)]
struct Tf {
    ax: f64,
    bx: f64,
    ay: f64,
    by: f64,
}
const IDENTITY: Tf = Tf { ax: 0.0, bx: 1.0, ay: 0.0, by: 1.0 };

impl Tf {
    fn apply(&self, f: Frame) -> Frame {
        Frame {
            x: self.ax + self.bx * f.x,
            y: self.ay + self.by * f.y,
            w: f.w * self.bx,
            h: f.h * self.by,
            ..f
        }
    }
}

#[derive(Clone, Default, Debug)]
struct Tp {
    size: Option<f64>,
    bold: Option<bool>,
    italic: Option<bool>,
    underline: Option<bool>,
    colour: Option<(String, f64)>,
    align: Option<String>,
    bullet: Option<Option<String>>,
}

impl Tp {
    fn or(mut self, o: &Tp) -> Tp {
        self.size = self.size.or(o.size);
        self.bold = self.bold.or(o.bold);
        self.italic = self.italic.or(o.italic);
        self.underline = self.underline.or(o.underline);
        if self.colour.is_none() {
            self.colour = o.colour.clone();
        }
        if self.align.is_none() {
            self.align = o.align.clone();
        }
        if self.bullet.is_none() {
            self.bullet = o.bullet.clone();
        }
        self
    }
}

#[derive(Clone, Debug)]
struct Ph {
    ty: String,
    idx: Option<String>,
    frame: Option<Frame>,
    props: Tp,
}

#[derive(Clone, Debug, PartialEq)]
enum Background {
    Colour(String),
    Image(SlideObject),
}

#[derive(Default)]
struct Layer {
    objects: Vec<SlideObject>,
    background: Option<Background>,
    phs: Vec<Ph>,
    parent: Option<String>,
    show_master: bool,
    /// Master text styles: title, body, other.
    styles: [Tp; 3],
}

struct Ctx {
    theme: BTreeMap<String, String>,
    map: BTreeMap<String, String>,
    /// EMU → px scale and letterbox offsets.
    s: f64,
    ox: f64,
    oy: f64,
}

impl Ctx {
    fn scheme(&self, name: &str) -> Option<String> {
        let key = self.map.get(name).map(String::as_str).unwrap_or(name);
        self.theme.get(key).cloned().or_else(|| match key {
            "dk1" | "tx1" => Some("#000000".into()),
            "lt1" | "bg1" => Some("#FFFFFF".into()),
            _ => None,
        })
    }
    fn px(&self, f: Frame) -> (f64, f64, f64, f64) {
        (self.ox + f.x * self.s, self.oy + f.y * self.s, f.w * self.s, f.h * self.s)
    }
}

fn num(node: Node<'_, '_>, name: &str) -> Option<f64> {
    attr(node, name).and_then(|v| v.parse::<f64>().ok()).filter(|v| v.is_finite())
}

fn frame_of(xfrm: Node<'_, '_>) -> Option<Frame> {
    let off = child(xfrm, "off")?;
    let ext = child(xfrm, "ext")?;
    Some(Frame {
        x: num(off, "x")?,
        y: num(off, "y")?,
        w: num(ext, "cx")?.max(0.0),
        h: num(ext, "cy")?.max(0.0),
        rot: num(xfrm, "rot").unwrap_or(0.0) / 60000.0,
        flip_h: ooxml::flag(xfrm, "flipH"),
        flip_v: ooxml::flag(xfrm, "flipV"),
    })
}

/// Colour held by a fill-like element (solidFill, fgClr, gs, fillRef…), with its alpha.
fn colour(holder: Node<'_, '_>, ctx: &Ctx, ph_clr: Option<&(String, f64)>) -> Option<(String, f64)> {
    let c = holder.children().find(|n| n.is_element())?;
    let base = match c.tag_name().name() {
        "srgbClr" => attr(c, "val").and_then(ooxml::hex),
        "sysClr" => attr(c, "lastClr").and_then(ooxml::hex),
        "schemeClr" => match attr(c, "val")? {
            "phClr" => ph_clr.map(|p| p.0.clone()).or_else(|| ctx.scheme("accent1")),
            v => ctx.scheme(v),
        },
        "prstClr" => match attr(c, "val")? {
            "black" => Some("#000000".into()),
            "white" => Some("#FFFFFF".into()),
            "red" => Some("#FF0000".into()),
            "green" => Some("#008000".into()),
            "blue" => Some("#0000FF".into()),
            "yellow" => Some("#FFFF00".into()),
            "gray" | "grey" => Some("#808080".into()),
            _ => None,
        },
        "scrgbClr" => {
            let p = |n: &str| ((num(c, n).unwrap_or(0.0) / 100000.0).clamp(0.0, 1.0) * 255.0).round() as u8;
            Some(format!("#{:02X}{:02X}{:02X}", p("r"), p("g"), p("b")))
        }
        _ => None,
    }?;
    let mut out = base;
    let mut alpha = 1.0;
    let (mut lm, mut lo) = (None, None);
    for m in c.children().filter(|n| n.is_element()) {
        let v = num(m, "val").unwrap_or(100000.0) / 100000.0;
        match m.tag_name().name() {
            "lumMod" => lm = Some(v),
            "lumOff" => lo = Some(v),
            "tint" => out = ooxml::mix_white(&out, v),
            "shade" => out = ooxml::shade(&out, v),
            "alpha" => alpha = v,
            _ => (),
        }
    }
    if lm.is_some() || lo.is_some() {
        out = ooxml::lum(&out, lm.unwrap_or(1.0), lo.unwrap_or(0.0));
    }
    Some((out, alpha.clamp(0.0, 1.0)))
}

enum Fill {
    Unset,
    None,
    Solid(String, f64),
    Picture(String),
}

fn fill_of(sppr: Option<Node<'_, '_>>, ctx: &Ctx) -> Fill {
    let Some(sppr) = sppr else { return Fill::Unset };
    for c in sppr.children().filter(|n| n.is_element()) {
        match c.tag_name().name() {
            "noFill" => return Fill::None,
            "solidFill" => {
                return colour(c, ctx, None).map_or(Fill::Unset, |(c, a)| Fill::Solid(c, a))
            }
            "gradFill" => {
                return desc(c, "gs")
                    .and_then(|gs| colour(gs, ctx, None))
                    .map_or(Fill::Unset, |(c, a)| Fill::Solid(c, a))
            }
            "pattFill" => {
                return child(c, "fgClr")
                    .and_then(|n| colour(n, ctx, None))
                    .map_or(Fill::Unset, |(c, a)| Fill::Solid(c, a))
            }
            "blipFill" => {
                return desc(c, "blip")
                    .and_then(|b| rid(b, "embed"))
                    .map_or(Fill::Unset, |r| Fill::Picture(r.to_string()))
            }
            _ => (),
        }
    }
    Fill::Unset
}

fn run_props(n: Node<'_, '_>, ctx: &Ctx) -> Tp {
    let b = |name: &str| attr(n, name).map(|v| !matches!(v, "0" | "false"));
    Tp {
        size: num(n, "sz").map(|v| v / 100.0),
        bold: b("b"),
        italic: b("i"),
        underline: attr(n, "u").map(|v| v != "none"),
        colour: child(n, "solidFill").and_then(|f| colour(f, ctx, None)),
        align: None,
        bullet: None,
    }
}

fn level_props(lvl: Node<'_, '_>, ctx: &Ctx) -> Tp {
    let mut tp = child(lvl, "defRPr").map(|d| run_props(d, ctx)).unwrap_or_default();
    tp.align = attr(lvl, "algn").map(align);
    tp.bullet = bullet(lvl);
    tp
}

fn bullet(ppr: Node<'_, '_>) -> Option<Option<String>> {
    for c in ppr.children().filter(|n| n.is_element()) {
        match c.tag_name().name() {
            "buNone" => return Some(None),
            "buChar" => return Some(Some(attr(c, "char").unwrap_or("•").chars().take(2).collect())),
            "buAutoNum" => return Some(Some("#".into())),
            _ => (),
        }
    }
    None
}

fn align(v: &str) -> String {
    match v {
        "ctr" => "center",
        "r" => "right",
        _ => "left",
    }
    .into()
}

fn list_style(body: Option<Node<'_, '_>>, ctx: &Ctx) -> Tp {
    body.and_then(|b| child(b, "lstStyle"))
        .and_then(|l| child(l, "lvl1pPr"))
        .map(|l| level_props(l, ctx))
        .unwrap_or_default()
}

fn style_group(ty: &str) -> usize {
    match ty {
        "title" | "ctrTitle" => 0,
        "body" | "subTitle" | "obj" => 1,
        _ => 2,
    }
}

fn clean(s: &str, max: usize) -> String {
    let mut out: String = s
        .chars()
        .filter(|c| *c == '\n' || *c == '\t' || !c.is_control())
        .collect();
    if out.len() > max {
        let mut end = max;
        while !out.is_char_boundary(end) {
            end -= 1;
        }
        out.truncate(end);
    }
    out
}

/// Plain text of a txBody (paragraphs separated by newlines), with bullets and numbering.
fn body_text(body: Node<'_, '_>, inherited_bullet: &Option<Option<String>>) -> String {
    let mut lines = vec![];
    let mut number = 0;
    for p in children(body, "p") {
        let mut line = String::new();
        for r in p.children().filter(|n| n.is_element()) {
            match r.tag_name().name() {
                "r" | "fld" => {
                    if let Some(t) = child(r, "t") {
                        line.push_str(t.text().unwrap_or(""));
                    }
                }
                "br" => line.push('\n'),
                _ => (),
            }
        }
        let ppr = child(p, "pPr");
        let level = ppr.and_then(|p| num(p, "lvl")).unwrap_or(0.0).clamp(0.0, 8.0) as usize;
        let bu = ppr.and_then(bullet).or_else(|| inherited_bullet.clone()).flatten();
        if !line.trim().is_empty() {
            if let Some(b) = bu {
                if b == "#" {
                    number += 1;
                    line = format!("{number}. {line}");
                } else {
                    line = format!("{b} {line}");
                }
            }
            line = format!("{}{line}", "    ".repeat(level));
        }
        lines.push(line);
    }
    while lines.last().is_some_and(|l| l.is_empty()) {
        lines.pop();
    }
    lines.join("\n")
}

/// Formatting of the first run carrying text (or the end-of-paragraph properties).
fn first_run(body: Node<'_, '_>, ctx: &Ctx) -> Tp {
    let mut align_attr = None;
    for p in children(body, "p") {
        let ppr = child(p, "pPr");
        if align_attr.is_none() {
            align_attr = ppr.and_then(|p| attr(p, "algn")).map(align);
        }
        if let Some(r) = children(p, "r").find(|r| child(*r, "t").and_then(|t| t.text()).is_some_and(|t| !t.trim().is_empty())) {
            let mut tp = child(r, "rPr").map(|n| run_props(n, ctx)).unwrap_or_default();
            tp.align = ppr.and_then(|p| attr(p, "algn")).map(align).or(align_attr);
            return tp;
        }
    }
    let mut tp = desc(body, "endParaRPr").map(|n| run_props(n, ctx)).unwrap_or_default();
    tp.align = align_attr;
    tp
}

fn mime_of(path: &str) -> Option<&'static str> {
    let ext = path.rsplit('.').next()?.to_ascii_lowercase();
    match ext.as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" | "jpe" => Some("image/jpeg"),
        "gif" => Some("image/gif"),
        _ => None,
    }
}

fn pixel_size(data: &[u8]) -> Option<(f64, f64)> {
    if data.len() > 24 && data.starts_with(b"\x89PNG") {
        let w = u32::from_be_bytes(data[16..20].try_into().ok()?);
        let h = u32::from_be_bytes(data[20..24].try_into().ok()?);
        return (w > 0 && h > 0).then_some((w as f64, h as f64));
    }
    if data.len() > 10 && data.starts_with(b"GIF") {
        let w = u16::from_le_bytes([data[6], data[7]]);
        let h = u16::from_le_bytes([data[8], data[9]]);
        return (w > 0 && h > 0).then_some((w as f64, h as f64));
    }
    if data.starts_with(&[0xFF, 0xD8]) {
        let mut i = 2;
        while i + 9 < data.len() {
            if data[i] != 0xFF {
                i += 1;
                continue;
            }
            let marker = data[i + 1];
            let len = u16::from_be_bytes([data[i + 2], data[i + 3]]) as usize;
            if matches!(marker, 0xC0..=0xC3 | 0xC5..=0xC7 | 0xC9..=0xCB | 0xCD..=0xCF) {
                let h = u16::from_be_bytes([data[i + 5], data[i + 6]]);
                let w = u16::from_be_bytes([data[i + 7], data[i + 8]]);
                return (w > 0 && h > 0).then_some((w as f64, h as f64));
            }
            i += 2 + len;
        }
    }
    None
}

fn blank(kind: &str) -> SlideObject {
    SlideObject {
        id: document_core::id(),
        kind: kind.into(),
        text: String::new(),
        x: 0.0,
        y: 0.0,
        width: 10.0,
        height: 10.0,
        fill: "#000000".into(),
        font_size: 28.0,
        bold: false,
        italic: false,
        underline: false,
        align: "left".into(),
        line: "none".into(),
        line_width: 2.0,
        rotation: 0.0,
        opacity: 1.0,
        image: None,
    }
}

fn place(o: &mut SlideObject, ctx: &Ctx, f: Frame) {
    let (x, y, w, h) = ctx.px(f);
    o.x = x.clamp(-100000.0, 100000.0);
    o.y = y.clamp(-100000.0, 100000.0);
    o.width = w.clamp(10.0, 5000.0);
    o.height = h.clamp(10.0, 5000.0);
    let mut rot = f.rot % 360.0;
    if rot > 180.0 {
        rot -= 360.0;
    }
    o.rotation = rot;
}

fn geometry(prst: &str) -> &'static str {
    match prst {
        "ellipse" | "flowChartConnector" | "donut" | "pie" | "chord" | "arc" | "blockArc" | "flowChartOr" | "flowChartSummingJunction" | "smileyFace" => "ellipse",
        "roundRect" | "round1Rect" | "round2SameRect" | "round2DiagRect" | "snipRoundRect" | "snip1Rect" | "snip2SameRect" | "snip2DiagRect" | "flowChartAlternateProcess" | "flowChartTerminator" | "plaque" | "wedgeRoundRectCallout" => "rounded",
        "triangle" | "rtTriangle" | "flowChartExtract" | "flowChartMerge" => "triangle",
        "diamond" | "flowChartDecision" => "diamond",
        "line" | "straightConnector1" | "bentConnector2" | "bentConnector3" | "bentConnector4" | "bentConnector5" | "curvedConnector2" | "curvedConnector3" | "curvedConnector4" | "curvedConnector5" | "lineInv" => "line",
        _ => "rectangle",
    }
}

struct Imported {
    path: String,
    slide: Slide,
    layout: Option<String>,
    notes: Option<String>,
}

struct Reader<'a> {
    zip: Archive<'a>,
    ctx: Ctx,
    presentation: String,
    width: f64,
    height: f64,
    images: usize,
    layers: HashMap<String, Rc<Layer>>,
    media: HashMap<String, Option<(String, String, f64, f64)>>,
}

fn parse_err(part: &str) -> impl Fn(roxmltree::Error) -> String + '_ {
    move |e| format!("Partie PowerPoint illisible ({part}) : {e}")
}

impl<'a> Reader<'a> {
    fn new(bytes: &'a [u8]) -> Result<Self, String> {
        let mut zip = ooxml::open(bytes, "PowerPoint")?;
        let presentation = ooxml::rels(&mut zip, "")?
            .into_iter()
            .find(|r| r.kind == "officeDocument")
            .map(|r| r.target)
            .unwrap_or_else(|| "ppt/presentation.xml".into());
        let xml = ooxml::text(&mut zip, &presentation)?
            .ok_or("Fichier PowerPoint invalide : présentation introuvable")?;
        let doc = Xml::parse(&xml).map_err(parse_err(&presentation))?;
        let size = desc(doc.root_element(), "sldSz");
        let width = size.and_then(|s| num(s, "cx")).filter(|v| *v > 0.0).unwrap_or(12192000.0);
        let height = size.and_then(|s| num(s, "cy")).filter(|v| *v > 0.0).unwrap_or(6858000.0);
        let s = (SURFACE_W / width).min(SURFACE_H / height);
        let rels = ooxml::rels(&mut zip, &presentation)?;
        let master = rels.iter().find(|r| r.kind == "slideMaster").map(|r| r.target.clone());
        let mut theme_part = rels.iter().find(|r| r.kind == "theme").map(|r| r.target.clone());
        let mut map = BTreeMap::new();
        if let Some(master) = &master {
            if let Some(t) = ooxml::rels(&mut zip, master)?.into_iter().find(|r| r.kind == "theme") {
                theme_part = Some(t.target);
            }
            if let Some(mx) = ooxml::text(&mut zip, master)? {
                if let Ok(md) = Xml::parse(&mx) {
                    if let Some(cm) = child(md.root_element(), "clrMap") {
                        for a in cm.attributes() {
                            map.insert(a.name().to_string(), a.value().to_string());
                        }
                    }
                }
            }
        }
        let theme = ooxml::theme(&mut zip, theme_part.as_deref());
        Ok(Self {
            zip,
            ctx: Ctx {
                theme,
                map,
                s,
                ox: (SURFACE_W - width * s) / 2.0,
                oy: (SURFACE_H - height * s) / 2.0,
            },
            presentation,
            width,
            height,
            images: 0,
            layers: HashMap::new(),
            media: HashMap::new(),
        })
    }

    fn slide_paths(&mut self) -> Result<Vec<String>, String> {
        let xml = ooxml::text(&mut self.zip, &self.presentation)?.unwrap_or_default();
        let doc = Xml::parse(&xml).map_err(parse_err(&self.presentation))?;
        let rels = ooxml::rels(&mut self.zip, &self.presentation)?;
        let mut out = vec![];
        if let Some(list) = desc(doc.root_element(), "sldIdLst") {
            for s in children(list, "sldId") {
                if let Some(r) = rid(s, "id").and_then(|id| rels.iter().find(|r| r.id == id && r.kind == "slide")) {
                    out.push(r.target.clone());
                }
            }
        }
        Ok(out)
    }

    fn picture(&mut self, rels: &[Rel], id: &str) -> Option<(String, String, f64, f64)> {
        let target = rels.iter().find(|r| r.id == id)?.target.clone();
        if let Some(found) = self.media.get(&target) {
            let found = found.clone()?;
            if self.images + found.1.len() > IMAGE_BUDGET {
                return None;
            }
            self.images += found.1.len();
            return Some(found);
        }
        let mime = mime_of(&target);
        let loaded = mime.and_then(|mime| {
            let data = ooxml::bytes(&mut self.zip, &target).ok()??;
            let (w, h) = pixel_size(&data).unwrap_or((100.0, 100.0));
            Some((mime.to_string(), base64::engine::general_purpose::STANDARD.encode(&data), w, h))
        });
        self.media.insert(target, loaded.clone());
        let found = loaded?;
        if self.images + found.1.len() > IMAGE_BUDGET {
            return None;
        }
        self.images += found.1.len();
        Some(found)
    }

    fn image_object(&mut self, rels: &[Rel], id: &str, f: Frame, alt: &str, alpha: f64) -> Option<SlideObject> {
        let (mime, data, w, h) = self.picture(rels, id)?;
        let mut o = blank("image");
        place(&mut o, &self.ctx, f);
        o.opacity = alpha.clamp(0.05, 1.0);
        o.image = Some(ImageData { mime, data, width: w, height: h, alt: clean(alt, 500) });
        Some(o)
    }

    fn background(&mut self, root: Node<'_, '_>, rels: &[Rel]) -> Option<Background> {
        let bg = desc(root, "bg")?;
        if let Some(pr) = child(bg, "bgPr") {
            return match fill_of(Some(pr), &self.ctx) {
                Fill::Solid(c, _) => Some(Background::Colour(c)),
                Fill::Picture(id) => {
                    let f = Frame { x: 0.0, y: 0.0, w: self.width, h: self.height, rot: 0.0, flip_h: false, flip_v: false };
                    self.image_object(rels, &id, f, "Arrière-plan", 1.0).map(Background::Image)
                }
                _ => None,
            };
        }
        let r = child(bg, "bgRef")?;
        colour(r, &self.ctx, None).map(|(c, _)| Background::Colour(c))
    }

    /// Layout or master: decorative shapes, placeholders, background and text styles.
    fn layer(&mut self, path: &str) -> Result<Rc<Layer>, String> {
        if let Some(l) = self.layers.get(path) {
            return Ok(l.clone());
        }
        let mut layer = Layer { show_master: true, ..Default::default() };
        self.layers.insert(path.to_string(), Rc::new(Layer::default()));
        if let Some(xml) = ooxml::text(&mut self.zip, path)? {
            let doc = Xml::parse(&xml).map_err(parse_err(path))?;
            let root = doc.root_element();
            let rels = ooxml::rels(&mut self.zip, path)?;
            layer.parent = rels
                .iter()
                .find(|r| r.kind == "slideMaster")
                .map(|r| r.target.clone());
            layer.show_master = attr(root, "showMasterSp").map_or(true, |v| v != "0");
            layer.background = self.background(root, &rels);
            if let Some(styles) = child(root, "txStyles") {
                for (i, name) in ["titleStyle", "bodyStyle", "otherStyle"].iter().enumerate() {
                    if let Some(l) = child(styles, name).and_then(|s| child(s, "lvl1pPr")) {
                        layer.styles[i] = level_props(l, &self.ctx);
                    }
                }
            }
            if let Some(tree) = desc(root, "spTree") {
                for n in tree.children().filter(|n| n.is_element()) {
                    if let Some(ph) = desc(n, "ph").filter(|_| n.tag_name().name() == "sp") {
                        let sppr = child(n, "spPr");
                        layer.phs.push(Ph {
                            ty: attr(ph, "type").unwrap_or("obj").into(),
                            idx: attr(ph, "idx").map(str::to_string),
                            frame: sppr.and_then(|s| child(s, "xfrm")).and_then(frame_of),
                            props: list_style(child(n, "txBody"), &self.ctx),
                        });
                    } else {
                        self.node(n, IDENTITY, &rels, None, &mut layer.objects);
                    }
                }
            }
        }
        let layer = Rc::new(layer);
        self.layers.insert(path.to_string(), layer.clone());
        Ok(layer)
    }

    /// Inherited decoration and background for a slide built on `layout`.
    fn inherited(&mut self, layout: Option<&str>) -> Result<(Vec<SlideObject>, Option<Background>, Option<Rc<Layer>>, Option<Rc<Layer>>), String> {
        let Some(layout) = layout else { return Ok((vec![], None, None, None)) };
        let l = self.layer(layout)?;
        let m = match &l.parent {
            Some(p) => Some(self.layer(p)?),
            None => None,
        };
        let mut objects = vec![];
        if l.show_master {
            if let Some(m) = &m {
                objects.extend(m.objects.iter().cloned());
            }
        }
        objects.extend(l.objects.iter().cloned());
        let bg = l.background.clone().or_else(|| m.as_ref().and_then(|m| m.background.clone()));
        Ok((objects, bg, Some(l), m))
    }

    fn node(&mut self, n: Node<'_, '_>, tf: Tf, rels: &[Rel], phs: Option<(&Layer, &Layer)>, out: &mut Vec<SlideObject>) {
        if out.len() >= MAX_OBJECTS {
            return;
        }
        match n.tag_name().name() {
            "sp" => self.shape(n, tf, rels, phs, out, false),
            "cxnSp" => self.shape(n, tf, rels, phs, out, true),
            "pic" => self.pic(n, tf, rels, out),
            "grpSp" => {
                let mut inner = tf;
                if let Some(x) = child(n, "grpSpPr").and_then(|g| child(g, "xfrm")) {
                    if let (Some(f), Some(co), Some(ce)) = (frame_of(x), child(x, "chOff"), child(x, "chExt")) {
                        let cx = num(co, "x").unwrap_or(0.0);
                        let cy = num(co, "y").unwrap_or(0.0);
                        let kx = num(ce, "cx").filter(|v| *v > 0.0).map_or(1.0, |c| f.w / c);
                        let ky = num(ce, "cy").filter(|v| *v > 0.0).map_or(1.0, |c| f.h / c);
                        inner = Tf {
                            ax: tf.ax + tf.bx * (f.x - cx * kx),
                            bx: tf.bx * kx,
                            ay: tf.ay + tf.by * (f.y - cy * ky),
                            by: tf.by * ky,
                        };
                    }
                }
                for c in n.children().filter(|c| c.is_element()) {
                    self.node(c, inner, rels, phs, out);
                }
            }
            "graphicFrame" => self.table(n, tf, out),
            "AlternateContent" => {
                if let Some(branch) = child(n, "Choice").or_else(|| child(n, "Fallback")) {
                    for c in branch.children().filter(|c| c.is_element()) {
                        self.node(c, tf, rels, phs, out);
                    }
                }
            }
            _ => (),
        }
    }

    fn pic(&mut self, n: Node<'_, '_>, tf: Tf, rels: &[Rel], out: &mut Vec<SlideObject>) {
        let Some(f) = child(n, "spPr").and_then(|s| child(s, "xfrm")).and_then(frame_of) else { return };
        let Some(blip) = desc(n, "blip") else { return };
        let Some(id) = rid(blip, "embed") else { return };
        let alt = desc(n, "cNvPr").and_then(|c| attr(c, "descr").or_else(|| attr(c, "name"))).unwrap_or("");
        let alpha = child(blip, "alphaModFix").and_then(|a| num(a, "amt")).map_or(1.0, |v| v / 100000.0);
        if let Some(mut o) = self.image_object(rels, id, tf.apply(f), alt, alpha) {
            if let Some(ln) = child(n, "spPr").and_then(|s| child(s, "ln")) {
                if let Some((c, _)) = child(ln, "solidFill").and_then(|s| colour(s, &self.ctx, None)) {
                    o.line = c;
                    o.line_width = (num(ln, "w").unwrap_or(12700.0) * self.ctx.s).clamp(0.5, 40.0);
                }
            }
            out.push(o);
        }
    }

    fn table(&mut self, n: Node<'_, '_>, tf: Tf, out: &mut Vec<SlideObject>) {
        let Some(tbl) = desc(n, "tbl") else { return };
        let Some(f) = child(n, "xfrm").and_then(frame_of) else { return };
        let rows: Vec<String> = children(tbl, "tr")
            .map(|tr| {
                children(tr, "tc")
                    .filter(|tc| !ooxml::flag(*tc, "hMerge") && !ooxml::flag(*tc, "vMerge"))
                    .map(|tc| child(tc, "txBody").map(|b| body_text(b, &None).replace('\n', " ")).unwrap_or_default())
                    .collect::<Vec<_>>()
                    .join("  |  ")
            })
            .collect();
        let size = desc(tbl, "rPr").and_then(|r| num(r, "sz")).map_or(14.0, |v| v / 100.0);
        let mut o = blank("text");
        place(&mut o, &self.ctx, tf.apply(f));
        o.text = clean(&rows.join("\n"), 100000);
        o.font_size = (size * 12700.0 * self.ctx.s).clamp(10.0, 160.0);
        o.fill = self.ctx.scheme("tx1").unwrap_or_else(|| "#000000".into());
        o.line = "#BFBFBF".into();
        o.line_width = 1.0;
        out.push(o);
    }

    fn shape(&mut self, n: Node<'_, '_>, tf: Tf, rels: &[Rel], phs: Option<(&Layer, &Layer)>, out: &mut Vec<SlideObject>, connector: bool) {
        let sppr = child(n, "spPr");
        let ph = desc(n, "nvPr").and_then(|p| child(p, "ph"));
        let ph_ty = ph.map(|p| attr(p, "type").unwrap_or("obj").to_string());
        let ph_idx = ph.and_then(|p| attr(p, "idx")).map(str::to_string);
        let (lph, mph) = match (&ph_ty, phs) {
            (Some(ty), Some((layout, master))) => {
                let lp = layout
                    .phs
                    .iter()
                    .find(|p| ph_idx.is_some() && p.idx == ph_idx && (style_group(&p.ty) == style_group(ty) || ty == "obj" || p.ty == "obj"))
                    .or_else(|| layout.phs.iter().find(|p| &p.ty == ty))
                    .or_else(|| layout.phs.iter().find(|p| ph_idx.is_some() && p.idx == ph_idx))
                    .or_else(|| layout.phs.iter().find(|p| style_group(&p.ty) == style_group(ty) && style_group(ty) < 2))
                    .cloned();
                let key = lp.as_ref().map(|p| p.ty.clone()).unwrap_or_else(|| ty.clone());
                let mp = master
                    .phs
                    .iter()
                    .find(|p| p.ty == key)
                    .or_else(|| master.phs.iter().find(|p| style_group(&p.ty) == style_group(&key) && style_group(&key) < 2))
                    .cloned();
                (lp, mp)
            }
            _ => (None, None),
        };
        let frame = sppr
            .and_then(|s| child(s, "xfrm"))
            .and_then(frame_of)
            .or_else(|| lph.as_ref().and_then(|p| p.frame))
            .or_else(|| mph.as_ref().and_then(|p| p.frame));
        let Some(frame) = frame.map(|f| tf.apply(f)) else { return };
        let style = child(n, "style");
        let prst = sppr
            .and_then(|s| child(s, "prstGeom"))
            .and_then(|g| attr(g, "prst"))
            .unwrap_or(if connector { "line" } else { "rect" });
        let kind = if connector { "line" } else { geometry(prst) };
        // Outline.
        let ln = sppr.and_then(|s| child(s, "ln"));
        let ln_ref = style.and_then(|s| child(s, "lnRef")).filter(|r| num(*r, "idx").unwrap_or(0.0) > 0.0);
        let mut line = None;
        match ln.and_then(|l| l.children().find(|c| c.is_element() && matches!(c.tag_name().name(), "noFill" | "solidFill" | "gradFill"))) {
            Some(c) if c.tag_name().name() == "noFill" => (),
            Some(c) if c.tag_name().name() == "solidFill" => line = colour(c, &self.ctx, None),
            Some(c) => line = desc(c, "gs").and_then(|g| colour(g, &self.ctx, None)),
            None => line = ln_ref.and_then(|r| colour(r, &self.ctx, None)),
        }
        let line_width = ln
            .and_then(|l| num(l, "w"))
            .unwrap_or(9525.0)
            * self.ctx.s;
        // Fill.
        let fill_ref = style.and_then(|s| child(s, "fillRef"));
        let mut fill = fill_of(sppr, &self.ctx);
        if matches!(fill, Fill::Unset) && ph_ty.is_none() {
            if let Some(r) = fill_ref.filter(|r| num(*r, "idx").unwrap_or(0.0) > 0.0) {
                if let Some((c, a)) = colour(r, &self.ctx, None) {
                    fill = Fill::Solid(c, a);
                }
            }
        }
        if kind == "line" {
            let Some((c, a)) = line.clone().or_else(|| match &fill {
                Fill::Solid(c, a) => Some((c.clone(), *a)),
                _ => None,
            }) else {
                return;
            };
            let (x, y, w, h) = self.ctx.px(frame);
            let (mut dx, mut dy) = (w, h);
            if frame.flip_h {
                dx = -dx;
            }
            if frame.flip_v {
                dy = -dy;
            }
            let length = (dx * dx + dy * dy).sqrt().max(10.0);
            let angle = dy.atan2(dx).to_degrees() + frame.rot;
            let mut o = blank("line");
            o.x = (x + w / 2.0 - length / 2.0).clamp(-100000.0, 100000.0);
            o.y = (y + h / 2.0 - 5.0).clamp(-100000.0, 100000.0);
            o.width = length.min(5000.0);
            o.height = 10.0;
            let mut rot = angle % 360.0;
            if rot > 180.0 {
                rot -= 360.0;
            }
            if rot < -180.0 {
                rot += 360.0;
            }
            o.rotation = rot;
            o.fill = c.clone();
            o.line = c;
            o.line_width = line_width.clamp(1.0, 40.0);
            o.opacity = a.clamp(0.05, 1.0);
            out.push(o);
            return;
        }
        let picture_fill = if let Fill::Picture(id) = &fill {
            self.image_object(rels, &id.clone(), frame, "", 1.0)
        } else {
            None
        };
        let has_line = line.is_some();
        let solid = match &fill {
            Fill::Solid(c, a) => Some((c.clone(), *a)),
            _ => None,
        };
        if let Some(o) = picture_fill {
            out.push(o);
        } else if solid.is_some() || has_line {
            let mut o = blank(kind);
            place(&mut o, &self.ctx, frame);
            match &solid {
                Some((c, a)) => {
                    o.fill = c.clone();
                    o.opacity = a.clamp(0.05, 1.0);
                }
                None if kind == "rectangle" => o.kind = "text".into(),
                None => o.fill = self.slide_colour(),
            }
            if let Some((c, _)) = &line {
                o.line = c.clone();
                o.line_width = line_width.clamp(0.5, 40.0);
            } else {
                o.line = "none".into();
            }
            out.push(o);
        }
        // Text.
        let Some(body) = child(n, "txBody") else { return };
        let group = ph_ty.as_deref().map(style_group).unwrap_or(2);
        let master_style = phs.map(|(_, m)| m.styles[group].clone()).unwrap_or_default();
        let shape_list = list_style(Some(body), &self.ctx);
        let inherited = shape_list
            .clone()
            .or(&lph.as_ref().map(|p| p.props.clone()).unwrap_or_default())
            .or(&mph.as_ref().map(|p| p.props.clone()).unwrap_or_default())
            .or(&master_style);
        let bullets = if group == 1 { inherited.bullet.clone() } else { shape_list.bullet.clone() };
        let text = body_text(body, &bullets);
        if text.trim().is_empty() {
            return;
        }
        let font_colour = style
            .and_then(|s| child(s, "fontRef"))
            .and_then(|r| colour(r, &self.ctx, None));
        let run = first_run(body, &self.ctx);
        let run_colour = run.colour.clone();
        let tp = run.or(&inherited);
        let scale = child(body, "bodyPr")
            .and_then(|b| child(b, "normAutofit"))
            .and_then(|a| num(a, "fontScale"))
            .map_or(1.0, |v| v / 100000.0);
        let size = tp.size.unwrap_or(match group {
            0 => 44.0,
            1 => 28.0,
            _ => 18.0,
        }) * scale;
        let mut o = blank("text");
        place(&mut o, &self.ctx, frame);
        o.text = clean(&text, 100000);
        o.font_size = (size * 12700.0 * self.ctx.s).clamp(10.0, 160.0);
        o.bold = tp.bold.unwrap_or(false);
        o.italic = tp.italic.unwrap_or(false);
        o.underline = tp.underline.unwrap_or(false);
        o.align = tp.align.unwrap_or_else(|| "left".into());
        let (c, a) = run_colour
            .or(if ph_ty.is_none() { font_colour.clone() } else { None })
            .or(tp.colour)
            .or(font_colour)
            .unwrap_or_else(|| (self.ctx.scheme("tx1").unwrap_or_else(|| "#000000".into()), 1.0));
        o.fill = c;
        o.opacity = if solid.is_some() { 1.0 } else { a.clamp(0.05, 1.0) };
        out.push(o);
    }

    fn slide_colour(&self) -> String {
        self.ctx.scheme("bg1").unwrap_or_else(|| "#FFFFFF".into())
    }

    fn notes(&mut self, path: &str) -> Result<String, String> {
        let Some(xml) = ooxml::text(&mut self.zip, path)? else { return Ok(String::new()) };
        let doc = Xml::parse(&xml).map_err(parse_err(path))?;
        let body = doc.root_element().descendants().find(|n| {
            n.is_element()
                && n.tag_name().name() == "sp"
                && desc(*n, "ph").is_some_and(|p| attr(p, "type") == Some("body"))
        });
        Ok(body
            .and_then(|sp| child(sp, "txBody"))
            .map(|b| clean(&body_text(b, &None), 20000))
            .unwrap_or_default())
    }

    fn slide(&mut self, path: &str) -> Result<Imported, String> {
        let xml = ooxml::text(&mut self.zip, path)?.ok_or_else(|| format!("Diapositive introuvable : {path}"))?;
        let doc = Xml::parse(&xml).map_err(parse_err(path))?;
        let root = doc.root_element();
        let rels = ooxml::rels(&mut self.zip, path)?;
        let layout = rels.iter().find(|r| r.kind == "slideLayout").map(|r| r.target.clone());
        let notes_path = rels.iter().find(|r| r.kind == "notesSlide").map(|r| r.target.clone());
        let (mut objects, inherited_bg, l, m) = self.inherited(layout.as_deref())?;
        if attr(root, "showMasterSp") == Some("0") {
            objects.clear();
        }
        let bg = self.background(root, &rels).or(inherited_bg);
        let mut background = "#FFFFFF".to_string();
        match bg {
            Some(Background::Colour(c)) => background = c,
            Some(Background::Image(img)) => objects.insert(0, img),
            None => (),
        }
        let empty = Layer::default();
        let phs = Some((l.as_deref().unwrap_or(&empty), m.as_deref().unwrap_or(&empty)));
        if let Some(tree) = desc(root, "spTree") {
            for n in tree.children().filter(|n| n.is_element()) {
                self.node(n, IDENTITY, &rels, phs, &mut objects);
            }
        }
        let notes = match &notes_path {
            Some(p) => self.notes(p)?,
            None => String::new(),
        };
        let transition = desc(root, "transition")
            .map(|t| match t.children().find(|c| c.is_element()).map(|c| c.tag_name().name().to_string()) {
                None => "none",
                Some(n) if matches!(n.as_str(), "fade" | "dissolve" | "cut") => "fade",
                Some(n) if matches!(n.as_str(), "push" | "wipe" | "cover" | "pull" | "split" | "strips" | "blinds" | "randomBar" | "comb" | "checker" | "zoom" | "wheel" | "circle" | "diamond" | "plus" | "wedge" | "newsflash") => "slide",
                Some(_) => "fade",
            })
            .unwrap_or("none");
        let mut slide = Slide::new(document_core::id(), objects);
        slide.background = background;
        slide.notes = notes;
        slide.transition = transition.into();
        Ok(Imported { path: path.to_string(), slide, layout, notes: notes_path })
    }

    fn slides(&mut self) -> Result<Vec<Imported>, String> {
        let paths = self.slide_paths()?;
        if paths.len() > MAX_SLIDES {
            return Err(format!("La présentation contient plus de {MAX_SLIDES} diapositives"));
        }
        paths.iter().map(|p| self.slide(p)).collect()
    }
}

pub fn import(bytes: &[u8], title: &str) -> Result<Document, String> {
    let mut reader = Reader::new(bytes)?;
    let imported = reader.slides()?;
    let mut doc = Document::new("slides", title)?;
    if !imported.is_empty() {
        doc.content = Content::Slides { slides: imported.into_iter().map(|i| i.slide).collect() };
    }
    doc.office_source = Some(OfficeSource {
        format: "pptx".into(),
        archive: base64::engine::general_purpose::STANDARD.encode(bytes),
        main_part: reader.presentation.clone(),
        paragraphs: vec![],
        page_layout: None,
        comments: vec![],
        revisions: vec![],
    });
    Ok(doc)
}

// ---------------------------------------------------------------- export

fn normalised(s: &Slide) -> Slide {
    let mut s = s.clone();
    s.id.clear();
    for o in &mut s.objects {
        o.id.clear();
    }
    s
}

fn same_object(a: &SlideObject, b: &SlideObject) -> bool {
    let mut a = a.clone();
    a.id = b.id.clone();
    a == *b
}

fn relative(from_dir: &str, target: &str) -> String {
    let from: Vec<&str> = from_dir.split('/').filter(|p| !p.is_empty()).collect();
    let to: Vec<&str> = target.split('/').filter(|p| !p.is_empty()).collect();
    let common = from.iter().zip(&to).take_while(|(a, b)| a == b).count();
    let mut parts = vec![".."; from.len() - common];
    parts.extend(&to[common..]);
    parts.join("/")
}

fn esc(s: &str) -> String {
    ooxml::escape(s)
}

fn hex6(c: &str) -> String {
    c.trim_start_matches('#').to_ascii_uppercase()
}

fn srgb(c: &str, alpha: f64) -> String {
    if alpha < 0.999 {
        format!("<a:srgbClr val=\"{}\"><a:alpha val=\"{}\"/></a:srgbClr>", hex6(c), (alpha * 100000.0).round() as i64)
    } else {
        format!("<a:srgbClr val=\"{}\"/>", hex6(c))
    }
}

struct Writer<'r> {
    s: f64,
    ox: f64,
    oy: f64,
    rels: &'r mut Vec<(String, String, String)>,
    media: &'r mut BTreeMap<String, Vec<u8>>,
    media_names: &'r mut usize,
    slide_dir: String,
}

impl Writer<'_> {
    fn emu(&self, v: f64) -> i64 {
        (v / self.s).round() as i64
    }
    fn xfrm(&self, o: &SlideObject, tag: &str) -> String {
        let rot = ((o.rotation % 360.0) + 360.0) % 360.0;
        let rot = if rot.abs() > 0.001 { format!(" rot=\"{}\"", (rot * 60000.0).round() as i64) } else { String::new() };
        format!(
            "<{tag}{rot}><a:off x=\"{}\" y=\"{}\"/><a:ext cx=\"{}\" cy=\"{}\"/></{tag}>",
            self.emu(o.x - self.ox),
            self.emu(o.y - self.oy),
            self.emu(o.width).max(0),
            self.emu(o.height).max(0)
        )
    }
    fn line(&self, o: &SlideObject) -> String {
        if o.line == "none" || o.line_width <= 0.0 {
            "<a:ln><a:noFill/></a:ln>".into()
        } else {
            format!("<a:ln w=\"{}\"><a:solidFill>{}</a:solidFill></a:ln>", self.emu(o.line_width), srgb(&o.line, 1.0))
        }
    }
    fn paragraphs(&self, o: &SlideObject) -> String {
        let pt = o.font_size / (12700.0 * self.s);
        let sz = ((pt * 100.0).round() as i64).clamp(100, 400000);
        let algn = match o.align.as_str() {
            "center" => " algn=\"ctr\"",
            "right" => " algn=\"r\"",
            _ => "",
        };
        let rpr = format!(
            "lang=\"fr-FR\" sz=\"{sz}\"{}{}{} dirty=\"0\"",
            if o.bold { " b=\"1\"" } else { "" },
            if o.italic { " i=\"1\"" } else { "" },
            if o.underline { " u=\"sng\"" } else { "" }
        );
        let colour = srgb(&o.fill, o.opacity);
        o.text
            .split('\n')
            .map(|line| {
                if line.is_empty() {
                    format!("<a:p><a:pPr{algn}/><a:endParaRPr {rpr}/></a:p>")
                } else {
                    format!(
                        "<a:p><a:pPr{algn}/><a:r><a:rPr {rpr}><a:solidFill>{colour}</a:solidFill></a:rPr><a:t>{}</a:t></a:r></a:p>",
                        esc(line)
                    )
                }
            })
            .collect()
    }
    fn object(&mut self, o: &SlideObject, id: usize) -> String {
        let name = |label: &str| format!("<p:cNvPr id=\"{id}\" name=\"{label} {id}\"/>");
        match o.kind.as_str() {
            "text" => format!(
                "<p:sp><p:nvSpPr>{}<p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr>{}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/>{}</p:spPr><p:txBody><a:bodyPr wrap=\"square\" lIns=\"{}\" tIns=\"{}\" rIns=\"{}\" bIns=\"{}\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr><a:lstStyle/>{}</p:txBody></p:sp>",
                name("Texte"),
                self.xfrm(o, "a:xfrm"),
                self.line(o),
                self.emu(10.0),
                self.emu(6.0),
                self.emu(10.0),
                self.emu(6.0),
                self.paragraphs(o)
            ),
            "line" => {
                let mut flat = o.clone();
                flat.y = o.y + o.height / 2.0;
                flat.height = 0.0;
                let colour = if o.line != "none" { o.line.clone() } else { o.fill.clone() };
                format!(
                    "<p:cxnSp><p:nvCxnSpPr>{}<p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>{}<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom><a:ln w=\"{}\" cap=\"rnd\"><a:solidFill>{}</a:solidFill></a:ln></p:spPr></p:cxnSp>",
                    name("Connecteur"),
                    self.xfrm(&flat, "a:xfrm"),
                    self.emu(o.line_width.max(1.0)),
                    srgb(&colour, o.opacity)
                )
            }
            "image" => {
                let Some(img) = &o.image else { return String::new() };
                let Ok(data) = base64::engine::general_purpose::STANDARD.decode(&img.data) else { return String::new() };
                let ext = match img.mime.as_str() {
                    "image/jpeg" => "jpeg",
                    "image/gif" => "gif",
                    _ => "png",
                };
                *self.media_names += 1;
                let path = format!("ppt/media/nexolume{}.{ext}", self.media_names);
                let rel = format!("rIdImg{}", self.rels.len() + 1);
                self.rels.push((rel.clone(), format!("{REL}/image"), relative(&self.slide_dir, &path)));
                self.media.insert(path, data);
                let alpha = if o.opacity < 0.999 { format!("<a:alphaModFix amt=\"{}\"/>", (o.opacity * 100000.0).round() as i64) } else { String::new() };
                let line = if o.line != "none" { self.line(o) } else { String::new() };
                format!(
                    "<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Image {id}\" descr=\"{}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"{rel}\">{alpha}</a:blip><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>{}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>{line}</p:spPr></p:pic>",
                    esc(&img.alt),
                    self.xfrm(o, "a:xfrm")
                )
            }
            kind => {
                let prst = match kind {
                    "ellipse" => "ellipse",
                    "rounded" => "roundRect",
                    "triangle" => "triangle",
                    "diamond" => "diamond",
                    _ => "rect",
                };
                let av = if kind == "rounded" { "<a:avLst><a:gd name=\"adj\" fmla=\"val 20000\"/></a:avLst>" } else { "<a:avLst/>" };
                format!(
                    "<p:sp><p:nvSpPr>{}<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>{}<a:prstGeom prst=\"{prst}\">{av}</a:prstGeom><a:solidFill>{}</a:solidFill>{}</p:spPr><p:txBody><a:bodyPr rtlCol=\"0\" anchor=\"ctr\"/><a:lstStyle/><a:p><a:endParaRPr lang=\"fr-FR\"/></a:p></p:txBody></p:sp>",
                    name("Forme"),
                    self.xfrm(o, "a:xfrm"),
                    srgb(&o.fill, o.opacity),
                    self.line(o)
                )
            }
        }
    }
}

const GROUP: &str = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

fn header(root: &str) -> String {
    format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<{root} xmlns:a=\"{NS_A}\" xmlns:r=\"{NS_R}\" xmlns:p=\"{NS_P}\"")
}

fn rels_xml(rels: &[(String, String, String)]) -> String {
    let mut out = String::from("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
    for (id, ty, target) in rels {
        out.push_str(&format!("<Relationship Id=\"{id}\" Type=\"{ty}\" Target=\"{}\"/>", esc(target)));
    }
    out.push_str("</Relationships>");
    out
}

fn notes_paragraphs(text: &str) -> String {
    text.split('\n')
        .map(|l| {
            if l.is_empty() {
                "<a:p><a:endParaRPr lang=\"fr-FR\"/></a:p>".to_string()
            } else {
                format!("<a:p><a:r><a:rPr lang=\"fr-FR\" dirty=\"0\"/><a:t>{}</a:t></a:r></a:p>", esc(l))
            }
        })
        .collect()
}

/// Removes the element named `tag` together with its parent when the parent is an `ext` wrapper.
fn drop_extension(xml: &str, tag: &str) -> String {
    let Ok(doc) = Xml::parse(xml) else { return xml.into() };
    let range = doc
        .descendants()
        .find(|n| n.is_element() && n.tag_name().name() == tag)
        .map(|n| match n.parent_element() {
            Some(p) if p.tag_name().name() == "ext" => p.range(),
            _ => n.range(),
        });
    match range {
        Some(r) => format!("{}{}", &xml[..r.start], &xml[r.end..]),
        None => xml.into(),
    }
}

pub fn export(document: &Document, slides: &[Slide]) -> Result<Vec<u8>, String> {
    let (bytes, original) = match &document.office_source {
        Some(s) if s.format == "pptx" => (
            base64::engine::general_purpose::STANDARD
                .decode(&s.archive)
                .map_err(|e| e.to_string())?,
            true,
        ),
        _ => (template(&document.title), false),
    };
    let mut reader = Reader::new(&bytes)?;
    let originals = reader.slides()?;
    if original
        && originals.len() == slides.len()
        && originals.iter().zip(slides).all(|(o, s)| normalised(&o.slide) == normalised(s))
    {
        return Ok(bytes);
    }
    let presentation = reader.presentation.clone();
    let pres_dir = ooxml::folder(&presentation).to_string();
    let pres_rels = ooxml::rels(&mut reader.zip, &presentation)?;
    let notes_master = pres_rels.iter().find(|r| r.kind == "notesMaster").map(|r| r.target.clone());
    let default_layout = match originals.first().and_then(|o| o.layout.clone()) {
        Some(l) => Some(l),
        None => match pres_rels.iter().find(|r| r.kind == "slideMaster") {
            Some(m) => ooxml::rels(&mut reader.zip, &m.target)?
                .into_iter()
                .find(|r| r.kind == "slideLayout")
                .map(|r| r.target),
            None => None,
        },
    };
    let names: Vec<String> = reader.zip.file_names().map(str::to_string).collect();
    let mut used = vec![false; originals.len()];
    let mut parts: BTreeMap<String, Vec<u8>> = BTreeMap::new();
    let mut media = BTreeMap::new();
    let mut media_names = names.iter().filter(|n| n.starts_with("ppt/media/nexolume")).count() + 1000;
    let mut slide_number = 0;
    let mut order: Vec<String> = vec![];
    let mut new_parts: Vec<(String, &str)> = vec![];
    for (i, slide) in slides.iter().enumerate() {
        let wanted = normalised(slide);
        if let Some(j) = (0..originals.len()).find(|&j| !used[j] && normalised(&originals[j].slide) == wanted) {
            used[j] = true;
            order.push(originals[j].path.clone());
            continue;
        }
        let layout = originals.get(i).and_then(|o| o.layout.clone()).or_else(|| default_layout.clone());
        let path = loop {
            slide_number += 1;
            let p = format!("ppt/slides/slide{slide_number}.xml");
            if !names.contains(&p) {
                break p;
            }
        };
        let slide_dir = ooxml::folder(&path).to_string();
        let (inherited, inherited_bg, _, _) = reader.inherited(layout.as_deref())?;
        let mut objects: &[SlideObject] = &slide.objects;
        let mut bg_image = None;
        if let Some(Background::Image(img)) = &inherited_bg {
            if objects.first().is_some_and(|o| same_object(o, img)) {
                bg_image = Some(());
                objects = &objects[1..];
            }
        }
        let prefix = inherited.iter().zip(objects).take_while(|(a, b)| same_object(b, a)).count();
        if prefix == inherited.len() {
            objects = &objects[prefix..];
        }
        let inherited_colour = match &inherited_bg {
            Some(Background::Colour(c)) => Some(c.clone()),
            Some(Background::Image(_)) if bg_image.is_some() => Some(slide.background.clone()),
            Some(Background::Image(_)) => None,
            None => Some("#FFFFFF".into()),
        };
        let mut rels: Vec<(String, String, String)> = vec![];
        if let Some(l) = &layout {
            rels.push(("rId1".into(), format!("{REL}/slideLayout"), relative(&slide_dir, l)));
        }
        let mut writer = Writer {
            s: reader.ctx.s,
            ox: reader.ctx.ox,
            oy: reader.ctx.oy,
            rels: &mut rels,
            media: &mut media,
            media_names: &mut media_names,
            slide_dir: slide_dir.clone(),
        };
        let shapes: String = objects.iter().enumerate().map(|(k, o)| writer.object(o, k + 2)).collect();
        let bg = if inherited_colour.as_deref() == Some(slide.background.as_str()) {
            String::new()
        } else {
            format!("<p:bg><p:bgPr><a:solidFill>{}</a:solidFill><a:effectLst/></p:bgPr></p:bg>", srgb(&slide.background, 1.0))
        };
        let transition = match slide.transition.as_str() {
            "fade" => "<p:transition spd=\"med\"><p:fade/></p:transition>",
            "slide" => "<p:transition spd=\"med\"><p:push dir=\"l\"/></p:transition>",
            _ => "",
        };
        let xml = format!(
            "{}><p:cSld>{bg}<p:spTree>{GROUP}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>{transition}</p:sld>",
            header("p:sld")
        );
        if !slide.notes.trim().is_empty() {
            if let Some(master) = &notes_master {
                let notes_path = format!("ppt/notesSlides/notesSlide_nx{slide_number}.xml");
                rels.push((format!("rIdN{}", rels.len() + 1), format!("{REL}/notesSlide"), relative(&slide_dir, &notes_path)));
                let notes_dir = ooxml::folder(&notes_path).to_string();
                let notes = format!(
                    "{}><p:cSld><p:spTree>{GROUP}<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{}</p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>",
                    header("p:notes"),
                    notes_paragraphs(&slide.notes)
                );
                parts.insert(
                    ooxml::rels_path(&notes_path),
                    rels_xml(&[
                        ("rId1".into(), format!("{REL}/notesMaster"), relative(&notes_dir, master)),
                        ("rId2".into(), format!("{REL}/slide"), relative(&notes_dir, &path)),
                    ])
                    .into_bytes(),
                );
                parts.insert(notes_path.clone(), notes.into_bytes());
                new_parts.push((notes_path, CT_NOTES));
            }
        }
        parts.insert(ooxml::rels_path(&path), rels_xml(&rels).into_bytes());
        parts.insert(path.clone(), xml.into_bytes());
        new_parts.push((path.clone(), CT_SLIDE));
        order.push(path);
    }
    // Parts of slides that were deleted or replaced.
    let mut skip = vec![];
    for (j, o) in originals.iter().enumerate() {
        if !used[j] {
            skip.push(o.path.clone());
            skip.push(ooxml::rels_path(&o.path));
            if let Some(n) = &o.notes {
                skip.push(n.clone());
                skip.push(ooxml::rels_path(n));
            }
        }
    }
    let changed_set = used.iter().any(|u| !u);
    // presentation.xml.rels
    let rels_name = ooxml::rels_path(&presentation);
    let raw_rels = ooxml::text(&mut reader.zip, &rels_name)?.unwrap_or_default();
    let mut pres_rel_list: Vec<(String, String, String)> = vec![];
    let mut slide_rel: HashMap<String, String> = HashMap::new();
    if let Ok(doc) = Xml::parse(&raw_rels) {
        for r in doc.root_element().children().filter(|n| n.is_element()) {
            let (Some(id), Some(ty), Some(target)) = (r.attribute("Id"), r.attribute("Type"), r.attribute("Target")) else { continue };
            if ty.ends_with("/slide") {
                if let Some(resolved) = ooxml::resolve(&pres_dir, target) {
                    if order.contains(&resolved) {
                        slide_rel.insert(resolved, id.to_string());
                    }
                }
                continue;
            }
            if r.attribute("TargetMode") == Some("External") {
                continue;
            }
            pres_rel_list.push((id.into(), ty.into(), target.into()));
        }
    }
    let mut next_rel = 1;
    for path in &order {
        if !slide_rel.contains_key(path) {
            let id = loop {
                let id = format!("rIdS{next_rel}");
                next_rel += 1;
                if !pres_rel_list.iter().any(|r| r.0 == id) && !slide_rel.values().any(|v| *v == id) {
                    break id;
                }
            };
            slide_rel.insert(path.clone(), id);
        }
    }
    for path in &order {
        pres_rel_list.push((slide_rel[path].clone(), format!("{REL}/slide"), relative(&pres_dir, path)));
    }
    parts.insert(rels_name, rels_xml(&pres_rel_list).into_bytes());
    // presentation.xml: rebuilt slide list, keeping the original slide ids.
    let pres_xml = ooxml::text(&mut reader.zip, &presentation)?.unwrap_or_default();
    let mut old_ids: HashMap<String, String> = HashMap::new();
    let mut max_id = 255u64;
    let pdoc = Xml::parse(&pres_xml).map_err(parse_err(&presentation))?;
    let original_rels = ooxml::rels(&mut reader.zip, &presentation)?;
    let root = pdoc.root_element();
    let p = root.lookup_prefix(NS_P).unwrap_or("p");
    let r = root.lookup_prefix(NS_R).unwrap_or("r");
    let pp = if p.is_empty() { String::new() } else { format!("{p}:") };
    if let Some(list) = desc(root, "sldIdLst") {
        for s in children(list, "sldId") {
            let id = attr(s, "id").unwrap_or("0");
            max_id = max_id.max(id.parse().unwrap_or(0));
            if let Some(rel) = rid(s, "id").and_then(|x| original_rels.iter().find(|r| r.id == x)) {
                old_ids.insert(rel.target.clone(), id.to_string());
            }
        }
    }
    let list: String = order
        .iter()
        .map(|path| {
            let id = old_ids.get(path).cloned().unwrap_or_else(|| {
                max_id += 1;
                max_id.to_string()
            });
            format!("<{pp}sldId id=\"{id}\" {r}:id=\"{}\"/>", slide_rel[path])
        })
        .collect();
    let list = format!("<{pp}sldIdLst>{list}</{pp}sldIdLst>");
    let mut new_pres = match desc(root, "sldIdLst") {
        Some(n) => {
            let range = n.range();
            format!("{}{list}{}", &pres_xml[..range.start], &pres_xml[range.end..])
        }
        None => match desc(root, "sldSz") {
            Some(n) => {
                let at = n.range().start;
                format!("{}{list}{}", &pres_xml[..at], &pres_xml[at..])
            }
            None => return Err("Présentation PowerPoint sans taille de diapositive".into()),
        },
    };
    drop(pdoc);
    if changed_set {
        new_pres = drop_extension(&new_pres, "sectionLst");
        new_pres = ooxml::remove_element(&new_pres, "custShowLst");
    }
    parts.insert(presentation.clone(), new_pres.into_bytes());
    // [Content_Types].xml
    let types = ooxml::text(&mut reader.zip, "[Content_Types].xml")?.unwrap_or_default();
    let tdoc = Xml::parse(&types).map_err(parse_err("[Content_Types].xml"))?;
    let mut defaults: Vec<(String, String)> = vec![];
    let mut overrides: Vec<(String, String)> = vec![];
    for n in tdoc.root_element().children().filter(|n| n.is_element()) {
        match n.tag_name().name() {
            "Default" => defaults.push((n.attribute("Extension").unwrap_or("").into(), n.attribute("ContentType").unwrap_or("").into())),
            "Override" => {
                let part = n.attribute("PartName").unwrap_or("").to_string();
                if !skip.iter().any(|s| format!("/{s}") == part) {
                    overrides.push((part, n.attribute("ContentType").unwrap_or("").into()));
                }
            }
            _ => (),
        }
    }
    for (ext, ty) in [("png", "image/png"), ("jpeg", "image/jpeg"), ("gif", "image/gif"), ("rels", "application/vnd.openxmlformats-package.relationships+xml"), ("xml", "application/xml")] {
        if !defaults.iter().any(|d| d.0.eq_ignore_ascii_case(ext)) {
            defaults.push((ext.into(), ty.into()));
        }
    }
    for (path, ty) in &new_parts {
        overrides.push((format!("/{path}"), ty.to_string()));
    }
    let mut ct = String::from("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
    for (e, t) in defaults {
        ct.push_str(&format!("<Default Extension=\"{}\" ContentType=\"{}\"/>", esc(&e), esc(&t)));
    }
    for (p, t) in overrides {
        ct.push_str(&format!("<Override PartName=\"{}\" ContentType=\"{}\"/>", esc(&p), esc(&t)));
    }
    ct.push_str("</Types>");
    drop(tdoc);
    parts.insert("[Content_Types].xml".into(), ct.into_bytes());
    parts.extend(media);
    let mut zip = reader.zip;
    ooxml::write(Some(&mut zip), parts, &skip)
}

fn template(title: &str) -> Vec<u8> {
    let mut parts = BTreeMap::new();
    let mut put = |name: &str, text: String| {
        parts.insert(name.to_string(), text.into_bytes());
    };
    let decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    let ct = |p: &str| format!("application/vnd.openxmlformats-officedocument.presentationml.{p}+xml");
    put(
        "[Content_Types].xml",
        format!(
            "{decl}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default Extension=\"gif\" ContentType=\"image/gif\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/notesMasters/notesMaster1.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/ppt/theme/theme2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/><Override PartName=\"/ppt/presProps.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/viewProps.xml\" ContentType=\"{}\"/><Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"{}\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>",
            ct("presentation.main"),
            ct("slideMaster"),
            ct("slideLayout"),
            ct("notesMaster"),
            ct("presProps"),
            ct("viewProps"),
            ct("tableStyles")
        ),
    );
    put("_rels/.rels", format!("{decl}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{REL}/officeDocument\" Target=\"ppt/presentation.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>"));
    put("docProps/core.xml", format!("{decl}<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{}</dc:title><dc:creator>Nexolume</dc:creator></cp:coreProperties>", esc(title)));
    put(
        "ppt/presentation.xml",
        format!("{}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:notesMasterIdLst><p:notesMasterId r:id=\"rId2\"/></p:notesMasterIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:defaultTextStyle></p:presentation>", header("p:presentation")),
    );
    put(
        "ppt/_rels/presentation.xml.rels",
        format!("{decl}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{REL}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId2\" Type=\"{REL}/notesMaster\" Target=\"notesMasters/notesMaster1.xml\"/><Relationship Id=\"rId3\" Type=\"{REL}/theme\" Target=\"theme/theme1.xml\"/><Relationship Id=\"rId4\" Type=\"{REL}/presProps\" Target=\"presProps.xml\"/><Relationship Id=\"rId5\" Type=\"{REL}/viewProps\" Target=\"viewProps.xml\"/><Relationship Id=\"rId6\" Type=\"{REL}/tableStyles\" Target=\"tableStyles.xml\"/></Relationships>"),
    );
    put("ppt/presProps.xml", format!("{}/>", header("p:presentationPr")));
    put("ppt/viewProps.xml", format!("{}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>", header("p:viewPr")));
    put("ppt/tableStyles.xml", format!("{decl}<a:tblStyleLst xmlns:a=\"{NS_A}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>"));
    let clr_map = "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";
    put(
        "ppt/slideMasters/slideMaster1.xml",
        format!("{}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GROUP}</p:spTree></p:cSld>{clr_map}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"4400\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL=\"228600\" indent=\"-228600\"><a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/><a:defRPr sz=\"2800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>", header("p:sldMaster")),
    );
    put(
        "ppt/slideMasters/_rels/slideMaster1.xml.rels",
        format!("{decl}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{REL}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"{REL}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>"),
    );
    put(
        "ppt/slideLayouts/slideLayout1.xml",
        format!("{} type=\"blank\" preserve=\"1\"><p:cSld name=\"Vide\"><p:spTree>{GROUP}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>", header("p:sldLayout")),
    );
    put(
        "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
        format!("{decl}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{REL}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>"),
    );
    put(
        "ppt/notesMasters/notesMaster1.xml",
        format!("{}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GROUP}<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"685800\" y=\"4400550\"/><a:ext cx=\"5486400\" cy=\"3600450\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"fr-FR\"/></a:p></p:txBody></p:sp></p:spTree></p:cSld>{clr_map}<p:notesStyle><a:lvl1pPr><a:defRPr sz=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:notesStyle></p:notesMaster>", header("p:notesMaster")),
    );
    put(
        "ppt/notesMasters/_rels/notesMaster1.xml.rels",
        format!("{decl}<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{REL}/theme\" Target=\"../theme/theme2.xml\"/></Relationships>"),
    );
    let theme = theme_xml();
    put("ppt/theme/theme1.xml", theme.clone());
    put("ppt/theme/theme2.xml", theme);
    ooxml::write(None, parts, &[]).unwrap_or_default()
}

fn theme_xml() -> String {
    let colours = [
        ("dk1", "<a:sysClr val=\"windowText\" lastClr=\"000000\"/>"),
        ("lt1", "<a:sysClr val=\"window\" lastClr=\"FFFFFF\"/>"),
        ("dk2", "<a:srgbClr val=\"243249\"/>"),
        ("lt2", "<a:srgbClr val=\"EEF2F0\"/>"),
        ("accent1", "<a:srgbClr val=\"285CB3\"/>"),
        ("accent2", "<a:srgbClr val=\"C97F4A\"/>"),
        ("accent3", "<a:srgbClr val=\"2D806C\"/>"),
        ("accent4", "<a:srgbClr val=\"8A5BB8\"/>"),
        ("accent5", "<a:srgbClr val=\"C2474E\"/>"),
        ("accent6", "<a:srgbClr val=\"D9A43B\"/>"),
        ("hlink", "<a:srgbClr val=\"285CB3\"/>"),
        ("folHlink", "<a:srgbClr val=\"8A5BB8\"/>"),
    ];
    let scheme: String = colours.iter().map(|(n, c)| format!("<a:{n}>{c}</a:{n}>")).collect();
    let solid = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
    let fills = solid.repeat(3);
    let lines: String = [6350, 12700, 19050]
        .iter()
        .map(|w| format!("<a:ln w=\"{w}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">{solid}<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>"))
        .collect();
    let effects = "<a:effectStyle><a:effectLst/></a:effectStyle>".repeat(3);
    let font = |face: &str| format!("<a:latin typeface=\"{face}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>");
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<a:theme xmlns:a=\"{NS_A}\" name=\"Nexolume\"><a:themeElements><a:clrScheme name=\"Nexolume\">{scheme}</a:clrScheme><a:fontScheme name=\"Nexolume\"><a:majorFont>{}</a:majorFont><a:minorFont>{}</a:minorFont></a:fontScheme><a:fmtScheme name=\"Nexolume\"><a:fillStyleLst>{fills}</a:fillStyleLst><a:lnStyleLst>{lines}</a:lnStyleLst><a:effectStyleLst>{effects}</a:effectStyleLst><a:bgFillStyleLst>{fills}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>",
        font("Aptos Display"),
        font("Aptos")
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> Document {
        let mut doc = Document::new("slides", "Essai").unwrap();
        let mut title = blank("text");
        title.text = "Bonjour\nle monde".into();
        title.x = 80.0;
        title.y = 60.0;
        title.width = 600.0;
        title.height = 120.0;
        title.font_size = 40.0;
        title.bold = true;
        title.align = "center".into();
        title.fill = "#243249".into();
        let mut shape = blank("ellipse");
        shape.x = 300.0;
        shape.y = 250.0;
        shape.width = 200.0;
        shape.height = 150.0;
        shape.fill = "#C97F4A".into();
        shape.line = "#000000".into();
        shape.rotation = 30.0;
        let mut image = blank("image");
        image.x = 10.0;
        image.y = 10.0;
        image.width = 50.0;
        image.height = 50.0;
        // 1×1 PNG.
        image.image = Some(ImageData {
            mime: "image/png".into(),
            data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==".into(),
            width: 1.0,
            height: 1.0,
            alt: "Point".into(),
        });
        let mut slide = Slide::new(document_core::id(), vec![title, shape, image]);
        slide.notes = "Penser à sourire".into();
        slide.transition = "fade".into();
        slide.background = "#EEF2F0".into();
        let second = Slide::new(document_core::id(), vec![]);
        doc.content = Content::Slides { slides: vec![slide, second] };
        doc
    }

    fn slides(doc: &Document) -> &Vec<Slide> {
        match &doc.content {
            Content::Slides { slides } => slides,
            _ => panic!("not slides"),
        }
    }

    #[test]
    fn new_deck_round_trips() {
        let doc = sample();
        let bytes = export(&doc, slides(&doc)).unwrap();
        let back = import(&bytes, "Essai").unwrap();
        let (a, b) = (slides(&doc), slides(&back));
        assert_eq!(b.len(), 2);
        assert_eq!(b[0].notes, "Penser à sourire");
        assert_eq!(b[0].transition, "fade");
        assert_eq!(b[0].background, "#EEF2F0");
        assert_eq!(b[0].objects.len(), 3);
        let t = &b[0].objects[0];
        assert_eq!(t.text, "Bonjour\nle monde");
        assert!(t.bold);
        assert_eq!(t.align, "center");
        assert_eq!(t.fill, "#243249");
        assert!((t.font_size - 40.0).abs() < 0.6);
        assert!((t.x - a[0].objects[0].x).abs() < 0.1);
        let e = &b[0].objects[1];
        assert_eq!(e.kind, "ellipse");
        assert!((e.rotation - 30.0).abs() < 0.01);
        assert_eq!(e.line, "#000000");
        assert_eq!(b[0].objects[2].kind, "image");
        for s in b {
            slides_engine::validate_slide(s).unwrap();
            for o in &s.objects {
                slides_engine::validate(o).unwrap();
            }
        }
        // Saving again without edits returns the same package.
        let again = export(&back, b).unwrap();
        assert_eq!(again, bytes);
    }

    #[test]
    fn edits_keep_other_slides() {
        let doc = sample();
        let bytes = export(&doc, slides(&doc)).unwrap();
        let back = import(&bytes, "Essai").unwrap();
        let mut edited = slides(&back).clone();
        edited[1].objects.push({
            let mut o = blank("rectangle");
            o.x = 100.0;
            o.y = 100.0;
            o.width = 100.0;
            o.height = 100.0;
            o.fill = "#285CB3".into();
            o
        });
        edited.swap(0, 1);
        let out = export(&back, &edited).unwrap();
        let again = import(&out, "Essai").unwrap();
        let s = slides(&again);
        assert_eq!(s.len(), 2);
        assert_eq!(s[0].objects.len(), 1);
        assert_eq!(s[0].objects[0].fill, "#285CB3");
        assert_eq!(s[1].notes, "Penser à sourire");
        assert_eq!(normalised(&s[1]), normalised(&slides(&back)[0]));
    }

    /// `NEXOLUME_PPTX=in.pptx cargo test -p file-formats real_file -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn real_file() {
        let path = std::env::var("NEXOLUME_PPTX").unwrap();
        let bytes = std::fs::read(&path).unwrap();
        let doc = import(&bytes, "Réel").unwrap();
        for (i, s) in slides(&doc).iter().enumerate() {
            slides_engine::validate_slide(s).unwrap();
            println!("slide {i} bg={} transition={} notes={:?}", s.background, s.transition, s.notes);
            for o in &s.objects {
                slides_engine::validate(o).unwrap();
                println!("  {} ({:.0},{:.0} {:.0}x{:.0}) fill={} line={} size={:.1} {:?}", o.kind, o.x, o.y, o.width, o.height, o.fill, o.line, o.font_size, o.text);
            }
        }
        assert_eq!(export(&doc, slides(&doc)).unwrap(), bytes);
        let mut edited = slides(&doc).clone();
        edited[0].objects.iter_mut().filter(|o| o.kind == "text").for_each(|o| o.text.push_str(" (modifié)"));
        edited.push(Slide::new(document_core::id(), vec![]));
        std::fs::write(format!("{path}.out.pptx"), export(&doc, &edited).unwrap()).unwrap();
    }
}
