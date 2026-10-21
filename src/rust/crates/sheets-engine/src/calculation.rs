use crate::parser::{self, Expr};
use document_core::Cell;
use std::cmp::Ordering;
use std::collections::{BTreeMap, HashMap, HashSet};

#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    Empty,
    Num(f64),
    Text(String),
    Bool(bool),
}
type Res<T = Value> = Result<T, String>;

impl Value {
    pub fn display(&self) -> String {
        match self {
            Value::Empty => "0".into(),
            Value::Num(n) => fmt_num(*n),
            Value::Text(t) => t.clone(),
            Value::Bool(true) => "VRAI".into(),
            Value::Bool(false) => "FAUX".into(),
        }
    }
}
/// Shortest text for a number, with the binary noise (0.1+0.2) rounded away.
fn fmt_num(n: f64) -> String {
    let tidy = format!("{n:.14e}").parse::<f64>().unwrap_or(n);
    tidy.to_string()
}
fn to_num(v: &Value) -> Res<f64> {
    match v {
        Value::Empty => Ok(0.0),
        Value::Num(n) => Ok(*n),
        Value::Bool(b) => Ok(*b as u8 as f64),
        Value::Text(t) => t
            .trim()
            .replace(',', ".")
            .parse::<f64>()
            .ok()
            .filter(|n| n.is_finite())
            .ok_or_else(|| "#VALUE!".into()),
    }
}
fn to_text(v: &Value) -> String {
    match v {
        Value::Empty => String::new(),
        other => other.display(),
    }
}
fn to_bool(v: &Value) -> Res<bool> {
    match v {
        Value::Empty => Ok(false),
        Value::Bool(b) => Ok(*b),
        Value::Num(n) => Ok(*n != 0.0),
        Value::Text(t) => match t.to_uppercase().as_str() {
            "TRUE" | "VRAI" => Ok(true),
            "FALSE" | "FAUX" => Ok(false),
            _ => Err("#VALUE!".into()),
        },
    }
}
/// Excel ordering: numbers < text < booleans, text compared without case.
fn compare(a: &Value, b: &Value) -> Ordering {
    let norm = |v: &Value, other: &Value| match (v, other) {
        (Value::Empty, Value::Text(_)) => Value::Text(String::new()),
        (Value::Empty, Value::Bool(_)) => Value::Bool(false),
        (Value::Empty, _) => Value::Num(0.0),
        _ => v.clone(),
    };
    let (a, b) = (norm(a, b), norm(b, a));
    let rank = |v: &Value| match v {
        Value::Num(_) | Value::Empty => 0,
        Value::Text(_) => 1,
        Value::Bool(_) => 2,
    };
    match (&a, &b) {
        (Value::Num(x), Value::Num(y)) => x.partial_cmp(y).unwrap_or(Ordering::Equal),
        (Value::Text(x), Value::Text(y)) => x.to_lowercase().cmp(&y.to_lowercase()),
        (Value::Bool(x), Value::Bool(y)) => x.cmp(y),
        _ => rank(&a).cmp(&rank(&b)),
    }
}
fn glob(pattern: &[char], text: &[char]) -> bool {
    match pattern.split_first() {
        None => text.is_empty(),
        Some(('*', rest)) => (0..=text.len()).any(|i| glob(rest, &text[i..])),
        Some(('?', rest)) => !text.is_empty() && glob(rest, &text[1..]),
        Some((c, rest)) => text.first() == Some(c) && glob(rest, &text[1..]),
    }
}
/// COUNTIF-style criterion: `5`, `">3"`, `"<>x"`, `"ab*"`.
fn matches(criterion: &Value, v: &Value) -> bool {
    let Value::Text(text) = criterion else {
        return match (criterion, v) {
            (Value::Empty, Value::Empty) => true,
            (Value::Empty, _) | (_, Value::Empty) => false,
            _ => compare(criterion, v) == Ordering::Equal && matches!((criterion, v), (Value::Num(_), Value::Num(_)) | (Value::Bool(_), Value::Bool(_))),
        };
    };
    let (op, rest) = ["<=", ">=", "<>", "<", ">", "="]
        .iter()
        .find_map(|op| text.strip_prefix(op).map(|rest| (*op, rest)))
        .unwrap_or(("=", text.as_str()));
    let ordering_ok = |o: Ordering| match op {
        "<=" => o != Ordering::Greater,
        ">=" => o != Ordering::Less,
        "<" => o == Ordering::Less,
        ">" => o == Ordering::Greater,
        "<>" => o != Ordering::Equal,
        _ => o == Ordering::Equal,
    };
    if let Ok(n) = rest.trim().replace(',', ".").parse::<f64>() {
        return match v {
            Value::Num(x) => ordering_ok(x.partial_cmp(&n).unwrap_or(Ordering::Equal)),
            _ => op == "<>",
        };
    }
    let target: Vec<char> = rest.to_lowercase().chars().collect();
    let candidate: Vec<char> = to_text(v).to_lowercase().chars().collect();
    let hit = glob(&target, &candidate);
    match op {
        "<>" => !hit,
        "=" => hit,
        _ => matches!(v, Value::Text(_)) && ordering_ok(candidate.iter().collect::<String>().cmp(&target.iter().collect::<String>())),
    }
}

/// Days since 1970-01-01 for a proleptic Gregorian date.
fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let doy = (153 * ((m + 9) % 12) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}
fn civil_from_days(z: i64) -> (i64, i64, i64) {
    let z = z + 719468;
    let era = z.div_euclid(146097);
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (yoe + era * 400 + (m <= 2) as i64, m, d)
}
/// Spreadsheet serial number (1900 system) of the current instant.
fn now_serial() -> f64 {
    let secs = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0);
    secs / 86400.0 + 25569.0
}
fn random_unit() -> f64 {
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.subsec_nanos() as u64).unwrap_or(1);
    let mut x = nanos.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407) ^ 0x9E3779B97F4A7C15;
    x ^= x >> 33; x = x.wrapping_mul(0xff51afd7ed558ccd); x ^= x >> 33;
    (x >> 11) as f64 / (1u64 << 53) as f64
}
fn gcd(a: u64, b: u64) -> u64 { if b == 0 { a } else { gcd(b, a % b) } }

pub struct Calculator<'a> {
    pub cells: &'a BTreeMap<String, Cell>,
    pub expressions: &'a HashMap<String, Result<Expr, String>>,
    pub cache: HashMap<String, Res>,
    visiting: HashSet<String>,
}
/// A rectangular block of values stored column by column.
struct Grid {
    columns: usize,
    rows: usize,
    values: Vec<Value>,
}
impl Grid {
    fn at(&self, row: usize, column: usize) -> &Value {
        &self.values[column * self.rows + row]
    }
}
fn literal(input: &str) -> Value {
    let trimmed = input.trim();
    let decimal = trimmed.replace(',', ".");
    if trimmed.matches(',').count() <= 1 {
        if let Ok(n) = decimal.parse::<f64>() {
            if n.is_finite() {
                return Value::Num(n);
            }
        }
    }
    match trimmed.to_uppercase().as_str() {
        "TRUE" | "VRAI" => Value::Bool(true),
        "FALSE" | "FAUX" => Value::Bool(false),
        _ => Value::Text(input.to_string()),
    }
}
impl<'a> Calculator<'a> {
    pub fn new(
        cells: &'a BTreeMap<String, Cell>,
        expressions: &'a HashMap<String, Result<Expr, String>>,
    ) -> Self {
        Self {
            cells,
            expressions,
            cache: HashMap::new(),
            visiting: HashSet::new(),
        }
    }
    pub fn cell(&mut self, name: &str) -> Res {
        if let Some(value) = self.cache.get(name) {
            return value.clone();
        }
        if self.visiting.contains(name) {
            return Err("#CYCLE!".into());
        }
        if self.visiting.len() > 256 {
            return Err("#DEPTH!".into());
        }
        self.visiting.insert(name.into());
        let result = if let Some(expr) = self.expressions.get(name) {
            match expr {
                Ok(e) => self.eval(e, 0),
                Err(e) => Err(e.clone()),
            }
        } else {
            match self.cells.get(name) {
                None => Ok(Value::Empty),
                Some(c) if c.input.is_empty() => Ok(Value::Empty),
                Some(c) => Ok(literal(&c.input)),
            }
        };
        self.visiting.remove(name);
        self.cache.insert(name.into(), result.clone());
        result
    }
    fn eval(&mut self, expr: &Expr, depth: usize) -> Res {
        if depth > 256 {
            return Err("#DEPTH!".into());
        }
        let d = depth + 1;
        let value = match expr {
            Expr::Number(n) => Value::Num(*n),
            Expr::Text(t) => Value::Text(t.clone()),
            Expr::Bool(b) => Value::Bool(*b),
            Expr::Reference(r) => return self.cell(r),
            Expr::Range(..) => return Err("#VALUE!".into()),
            Expr::Negate(e) => Value::Num(-to_num(&self.eval(e, d)?)?),
            Expr::Percent(e) => Value::Num(to_num(&self.eval(e, d)?)? / 100.0),
            Expr::Binary(op, a, b) => {
                let a = self.eval(a, d)?;
                let b = self.eval(b, d)?;
                match op {
                    '&' => Value::Text(to_text(&a) + &to_text(&b)),
                    '=' => Value::Bool(compare(&a, &b) == Ordering::Equal),
                    'n' => Value::Bool(compare(&a, &b) != Ordering::Equal),
                    '<' => Value::Bool(compare(&a, &b) == Ordering::Less),
                    '>' => Value::Bool(compare(&a, &b) == Ordering::Greater),
                    'l' => Value::Bool(compare(&a, &b) != Ordering::Greater),
                    'g' => Value::Bool(compare(&a, &b) != Ordering::Less),
                    _ => {
                        let (a, b) = (to_num(&a)?, to_num(&b)?);
                        Value::Num(match op {
                            '+' => a + b,
                            '-' => a - b,
                            '*' => a * b,
                            '/' => {
                                if b == 0.0 {
                                    return Err("#DIV/0!".into());
                                }
                                a / b
                            }
                            '^' => a.powf(b),
                            _ => return Err("#PARSE!".into()),
                        })
                    }
                }
            }
            Expr::Function(name, args) => self.call(name, args, d)?,
        };
        match value {
            Value::Num(n) if !n.is_finite() => Err("#NUM!".into()),
            other => Ok(other),
        }
    }
    fn num(&mut self, args: &[Expr], i: usize, d: usize) -> Res<f64> {
        let v = self.eval(&args[i], d)?;
        to_num(&v)
    }
    fn grid(&mut self, expr: &Expr, d: usize) -> Res<Grid> {
        match expr {
            Expr::Range(a, b) => {
                let (a, b) = (parser::address(a)?, parser::address(b)?);
                let (columns, rows) = ((b.0 - a.0 + 1) as usize, (b.1 - a.1 + 1) as usize);
                let mut values = Vec::with_capacity(columns * rows);
                for name in parser::range_cells(&parser::name(a.0, a.1), &parser::name(b.0, b.1)) {
                    values.push(self.cell(&name)?);
                }
                Ok(Grid { columns, rows, values })
            }
            other => Ok(Grid { columns: 1, rows: 1, values: vec![self.eval(other, d)?] }),
        }
    }
    /// Every value of the arguments, ranges flattened.
    fn flat(&mut self, args: &[Expr], d: usize) -> Res<Vec<(Value, bool)>> {
        let mut out = vec![];
        for e in args {
            let direct = !matches!(e, Expr::Range(..) | Expr::Reference(_));
            out.extend(self.grid(e, d)?.values.into_iter().map(|v| (v, direct)));
        }
        Ok(out)
    }
    /// Numbers for SUM-like functions: literals are coerced, cells and ranges contribute only their numbers.
    fn numbers(&mut self, args: &[Expr], d: usize) -> Res<Vec<f64>> {
        let mut out = vec![];
        for (v, direct) in self.flat(args, d)? {
            match v {
                Value::Num(n) => out.push(n),
                other if direct && other != Value::Empty => out.push(to_num(&other)?),
                _ => {}
            }
        }
        Ok(out)
    }
    fn call(&mut self, name: &str, args: &[Expr], d: usize) -> Res {
        let arity = |min: usize, max: usize| {
            if args.len() < min || args.len() > max {
                Err("#ARGS!".to_string())
            } else {
                Ok(())
            }
        };
        let digits_factor = |this: &mut Self| -> Res<f64> {
            Ok(10f64.powi(if args.len() > 1 { this.num(args, 1, d)?.trunc() as i32 } else { 0 }))
        };
        Ok(match name {
            "SUM" | "SOMME" => Value::Num(self.numbers(args, d)?.iter().sum()),
            "COUNT" | "NB" => Value::Num(self.numbers(args, d)?.len() as f64),
            "AVERAGE" | "MOYENNE" => {
                let n = self.numbers(args, d)?;
                if n.is_empty() {
                    return Err("#DIV/0!".into());
                }
                Value::Num(n.iter().sum::<f64>() / n.len() as f64)
            }
            "MIN" => Value::Num(self.numbers(args, d)?.into_iter().reduce(f64::min).unwrap_or(0.0)),
            "MAX" => Value::Num(self.numbers(args, d)?.into_iter().reduce(f64::max).unwrap_or(0.0)),
            "PRODUCT" | "PRODUIT" => {
                let n = self.numbers(args, d)?;
                Value::Num(if n.is_empty() { 0.0 } else { n.iter().product() })
            }
            "MEDIAN" | "MEDIANE" => {
                let mut n = self.numbers(args, d)?;
                if n.is_empty() {
                    return Err("#NUM!".into());
                }
                n.sort_by(|a, b| a.partial_cmp(b).unwrap_or(Ordering::Equal));
                let mid = n.len() / 2;
                Value::Num(if n.len() % 2 == 1 { n[mid] } else { (n[mid - 1] + n[mid]) / 2.0 })
            }
            "LARGE" | "GRANDE.VALEUR" | "SMALL" | "PETITE.VALEUR" => {
                arity(2, 2)?;
                let mut n = self.numbers(&args[..1], d)?;
                let k = self.num(args, 1, d)?.trunc();
                if k < 1.0 || k as usize > n.len() {
                    return Err("#NUM!".into());
                }
                n.sort_by(|a, b| a.partial_cmp(b).unwrap_or(Ordering::Equal));
                if name.starts_with('L') || name.starts_with('G') {
                    n.reverse();
                }
                Value::Num(n[k as usize - 1])
            }
            "COUNTA" | "NBVAL" => Value::Num(self.flat(args, d)?.iter().filter(|(v, _)| *v != Value::Empty).count() as f64),
            "COUNTBLANK" | "NB.VIDE" => Value::Num(
                self.flat(args, d)?
                    .iter()
                    .filter(|(v, _)| *v == Value::Empty || *v == Value::Text(String::new()))
                    .count() as f64,
            ),
            "ABS" => { arity(1, 1)?; Value::Num(self.num(args, 0, d)?.abs()) }
            "SIGN" => { arity(1, 1)?; let x = self.num(args, 0, d)?; Value::Num(if x == 0.0 { 0.0 } else { x.signum() }) }
            "SQRT" | "RACINE" => {
                arity(1, 1)?;
                let x = self.num(args, 0, d)?;
                if x < 0.0 { return Err("#NUM!".into()); }
                Value::Num(x.sqrt())
            }
            "EXP" => { arity(1, 1)?; Value::Num(self.num(args, 0, d)?.exp()) }
            "LN" | "LOG10" => {
                arity(1, 1)?;
                let x = self.num(args, 0, d)?;
                if x <= 0.0 { return Err("#NUM!".into()); }
                Value::Num(if name == "LN" { x.ln() } else { x.log10() })
            }
            "LOG" => {
                arity(1, 2)?;
                let x = self.num(args, 0, d)?;
                let base = if args.len() > 1 { self.num(args, 1, d)? } else { 10.0 };
                if x <= 0.0 || base <= 0.0 || base == 1.0 { return Err("#NUM!".into()); }
                Value::Num(x.ln() / base.ln())
            }
            "PI" => { arity(0, 0)?; Value::Num(std::f64::consts::PI) }
            "INT" | "ENT" => { arity(1, 1)?; Value::Num(self.num(args, 0, d)?.floor()) }
            "POWER" | "PUISSANCE" => {
                arity(2, 2)?;
                Value::Num(self.num(args, 0, d)?.powf(self.num(args, 1, d)?))
            }
            "MOD" => {
                arity(2, 2)?;
                let (a, b) = (self.num(args, 0, d)?, self.num(args, 1, d)?);
                if b == 0.0 { return Err("#DIV/0!".into()); }
                Value::Num(a - b * (a / b).floor())
            }
            "ROUND" | "ARRONDI" | "ROUNDUP" | "ARRONDI.SUP" | "ROUNDDOWN" | "ARRONDI.INF" | "TRUNC" | "TRONQUE" => {
                arity(1, 2)?;
                let x = self.num(args, 0, d)?;
                let f = digits_factor(self)?;
                let scaled = x * f;
                Value::Num(match name {
                    "ROUND" | "ARRONDI" => scaled.round(),
                    "ROUNDUP" | "ARRONDI.SUP" => scaled.abs().ceil() * scaled.signum(),
                    _ => scaled.trunc(),
                } / f)
            }
            "IF" | "SI" => {
                arity(2, 3)?;
                let cond = to_bool(&self.eval(&args[0], d)?)?;
                if cond {
                    self.eval(&args[1], d)?
                } else if args.len() == 3 {
                    self.eval(&args[2], d)?
                } else {
                    Value::Bool(false)
                }
            }
            "IFERROR" | "SIERREUR" => {
                arity(2, 2)?;
                match self.eval(&args[0], d) {
                    Ok(v) => v,
                    Err(_) => self.eval(&args[1], d)?,
                }
            }
            "ISERROR" | "ESTERREUR" => { arity(1, 1)?; Value::Bool(self.eval(&args[0], d).is_err()) }
            "ISNUMBER" | "ESTNUM" => { arity(1, 1)?; Value::Bool(matches!(self.eval(&args[0], d)?, Value::Num(_))) }
            "ISTEXT" | "ESTTEXTE" => { arity(1, 1)?; Value::Bool(matches!(self.eval(&args[0], d)?, Value::Text(_))) }
            "ISBLANK" | "ESTVIDE" => { arity(1, 1)?; Value::Bool(self.eval(&args[0], d)? == Value::Empty) }
            "TRUE" | "VRAI" => Value::Bool(true),
            "FALSE" | "FAUX" => Value::Bool(false),
            "NOT" | "NON" => { arity(1, 1)?; Value::Bool(!to_bool(&self.eval(&args[0], d)?)?) }
            "AND" | "ET" | "OR" | "OU" => {
                let mut flags = vec![];
                for (v, _) in self.flat(args, d)? {
                    if v != Value::Empty && !matches!(v, Value::Text(_)) {
                        flags.push(to_bool(&v)?);
                    }
                }
                if flags.is_empty() { return Err("#VALUE!".into()); }
                Value::Bool(if name == "AND" || name == "ET" { flags.iter().all(|b| *b) } else { flags.iter().any(|b| *b) })
            }
            "CONCAT" | "CONCATENER" | "CONCATENATE" => {
                Value::Text(self.flat(args, d)?.iter().map(|(v, _)| to_text(v)).collect())
            }
            "LEN" | "NBCAR" => { arity(1, 1)?; Value::Num(to_text(&self.eval(&args[0], d)?).chars().count() as f64) }
            "UPPER" | "MAJUSCULE" => { arity(1, 1)?; Value::Text(to_text(&self.eval(&args[0], d)?).to_uppercase()) }
            "LOWER" | "MINUSCULE" => { arity(1, 1)?; Value::Text(to_text(&self.eval(&args[0], d)?).to_lowercase()) }
            "TRIM" | "SUPPRESPACE" => {
                arity(1, 1)?;
                Value::Text(to_text(&self.eval(&args[0], d)?).split_whitespace().collect::<Vec<_>>().join(" "))
            }
            "VALUE" | "CNUM" => { arity(1, 1)?; Value::Num(to_num(&self.eval(&args[0], d)?)?) }
            "LEFT" | "GAUCHE" | "RIGHT" | "DROITE" => {
                arity(1, 2)?;
                let text: Vec<char> = to_text(&self.eval(&args[0], d)?).chars().collect();
                let n = if args.len() > 1 { self.num(args, 1, d)? } else { 1.0 };
                if n < 0.0 { return Err("#VALUE!".into()); }
                let n = (n as usize).min(text.len());
                Value::Text(if name.starts_with('L') || name.starts_with('G') { text[..n].iter().collect() } else { text[text.len() - n..].iter().collect() })
            }
            "MID" | "STXT" => {
                arity(3, 3)?;
                let text: Vec<char> = to_text(&self.eval(&args[0], d)?).chars().collect();
                let (start, n) = (self.num(args, 1, d)?, self.num(args, 2, d)?);
                if start < 1.0 || n < 0.0 { return Err("#VALUE!".into()); }
                let from = (start as usize - 1).min(text.len());
                let to = (from + n as usize).min(text.len());
                Value::Text(text[from..to].iter().collect())
            }
            "COUNTIF" | "NB.SI" | "SUMIF" | "SOMME.SI" | "AVERAGEIF" | "MOYENNE.SI" => {
                arity(2, 3)?;
                let tested = self.grid(&args[0], d)?;
                let criterion = self.eval(&args[1], d)?;
                let summed = if args.len() == 3 { self.grid(&args[2], d)? } else { Grid { columns: tested.columns, rows: tested.rows, values: tested.values.clone() } };
                let (mut count, mut total) = (0usize, 0.0);
                for (i, v) in tested.values.iter().enumerate() {
                    if matches(&criterion, v) {
                        count += 1;
                        if let Some(Value::Num(n)) = summed.values.get(i) { total += n; }
                    }
                }
                match name {
                    "COUNTIF" | "NB.SI" => Value::Num(count as f64),
                    "SUMIF" | "SOMME.SI" => Value::Num(total),
                    _ if count == 0 => return Err("#DIV/0!".into()),
                    _ => Value::Num(total / count as f64),
                }
            }
            "VLOOKUP" | "RECHERCHEV" => {
                arity(3, 4)?;
                let key = self.eval(&args[0], d)?;
                let table = self.grid(&args[1], d)?;
                let column = self.num(args, 2, d)?.trunc();
                let approximate = if args.len() == 4 { to_bool(&self.eval(&args[3], d)?)? } else { true };
                if column < 1.0 || column as usize > table.columns { return Err("#REF!".into()); }
                let hit = if approximate {
                    (0..table.rows).filter(|r| compare(table.at(*r, 0), &key) != Ordering::Greater).last()
                } else {
                    (0..table.rows).find(|r| compare(table.at(*r, 0), &key) == Ordering::Equal)
                };
                match hit { Some(r) => table.at(r, column as usize - 1).clone(), None => return Err("#N/A".into()) }
            }
            "MATCH" | "EQUIV" => {
                arity(2, 3)?;
                let key = self.eval(&args[0], d)?;
                let list = self.grid(&args[1], d)?;
                let kind = if args.len() == 3 { self.num(args, 2, d)?.trunc() } else { 1.0 };
                let hit = match kind as i32 {
                    0 => list.values.iter().position(|v| compare(v, &key) == Ordering::Equal),
                    1 => list.values.iter().rposition(|v| compare(v, &key) != Ordering::Greater),
                    _ => list.values.iter().position(|v| compare(v, &key) != Ordering::Less),
                };
                match hit { Some(i) => Value::Num(i as f64 + 1.0), None => return Err("#N/A".into()) }
            }
            "INDEX" => {
                arity(2, 3)?;
                let table = self.grid(&args[0], d)?;
                let first = self.num(args, 1, d)?.trunc();
                let second = if args.len() == 3 { Some(self.num(args, 2, d)?.trunc()) } else { None };
                let (row, column) = match second {
                    Some(c) => (first, c),
                    None if table.columns == 1 => (first, 1.0),
                    None => (1.0, first),
                };
                if row < 1.0 || column < 1.0 || row as usize > table.rows || column as usize > table.columns { return Err("#REF!".into()); }
                table.at(row as usize - 1, column as usize - 1).clone()
            }
            "SUMIFS" | "SOMME.SI.ENS" | "COUNTIFS" | "NB.SI.ENS" | "AVERAGEIFS" | "MOYENNE.SI.ENS" => {
                let counting = name == "COUNTIFS" || name == "NB.SI.ENS";
                let first = if counting { 0 } else { 1 };
                if args.len() < first + 2 || (args.len() - first) % 2 != 0 { return Err("#ARGS!".into()); }
                let summed = if counting { None } else { Some(self.grid(&args[0], d)?) };
                let mut keep: Option<Vec<bool>> = None;
                for pair in args[first..].chunks(2) {
                    let range = self.grid(&pair[0], d)?;
                    let criterion = self.eval(&pair[1], d)?;
                    let hits: Vec<bool> = range.values.iter().map(|v| matches(&criterion, v)).collect();
                    keep = Some(match keep {
                        None => hits,
                        Some(old) => {
                            if old.len() != hits.len() { return Err("#VALUE!".into()); }
                            old.iter().zip(hits).map(|(a, b)| *a && b).collect()
                        }
                    });
                }
                let keep = keep.unwrap_or_default();
                let count = keep.iter().filter(|k| **k).count();
                match summed {
                    None => Value::Num(count as f64),
                    Some(s) => {
                        let total: f64 = keep.iter().enumerate().filter(|(_, k)| **k).filter_map(|(i, _)| match s.values.get(i) { Some(Value::Num(n)) => Some(*n), _ => None }).sum();
                        if name == "SUMIFS" || name == "SOMME.SI.ENS" {
                            Value::Num(total)
                        } else if count == 0 {
                            return Err("#DIV/0!".into());
                        } else {
                            Value::Num(total / count as f64)
                        }
                    }
                }
            }
            "STDEV" | "ECARTYPE" | "STDEVP" | "ECARTYPE.P" | "VAR" | "VARIANCE" | "VARP" | "VAR.P" => {
                let n = self.numbers(args, d)?;
                let population = name.ends_with('P');
                if n.is_empty() || (!population && n.len() < 2) { return Err("#DIV/0!".into()); }
                let mean = n.iter().sum::<f64>() / n.len() as f64;
                let spread: f64 = n.iter().map(|x| (x - mean).powi(2)).sum::<f64>() / if population { n.len() } else { n.len() - 1 } as f64;
                Value::Num(if name.starts_with("STDEV") || name.starts_with("ECART") { spread.sqrt() } else { spread })
            }
            "RANK" | "RANG" => {
                arity(2, 3)?;
                let x = self.num(args, 0, d)?;
                let list = self.numbers(&args[1..2], d)?;
                let ascending = args.len() == 3 && self.num(args, 2, d)? != 0.0;
                if !list.contains(&x) { return Err("#N/A".into()); }
                Value::Num(1.0 + list.iter().filter(|v| if ascending { **v < x } else { **v > x }).count() as f64)
            }
            "CEILING" | "PLAFOND" | "FLOOR" | "PLANCHER" => {
                arity(2, 2)?;
                let (x, step) = (self.num(args, 0, d)?, self.num(args, 1, d)?);
                if step == 0.0 { return Ok(Value::Num(0.0)); }
                Value::Num(if name == "CEILING" || name == "PLAFOND" { (x / step).ceil() * step } else { (x / step).floor() * step })
            }
            "FACT" => {
                arity(1, 1)?;
                let n = self.num(args, 0, d)?.trunc();
                if !(0.0..=170.0).contains(&n) { return Err("#NUM!".into()); }
                Value::Num((1..=n as u64).map(|k| k as f64).product())
            }
            "COMBIN" => {
                arity(2, 2)?;
                let (n, k) = (self.num(args, 0, d)?.trunc(), self.num(args, 1, d)?.trunc());
                if k < 0.0 || n < k { return Err("#NUM!".into()); }
                let k = k.min(n - k);
                Value::Num((0..k as u64).fold(1.0, |acc, i| acc * (n - i as f64) / (i as f64 + 1.0)).round())
            }
            "RAND" | "ALEA" => { arity(0, 0)?; Value::Num(random_unit()) }
            "RANDBETWEEN" | "ALEA.ENTRE.BORNES" => {
                arity(2, 2)?;
                let (lo, hi) = (self.num(args, 0, d)?.ceil(), self.num(args, 1, d)?.floor());
                if hi < lo { return Err("#NUM!".into()); }
                Value::Num(lo + (random_unit() * (hi - lo + 1.0)).floor())
            }
            "HLOOKUP" | "RECHERCHEH" => {
                arity(3, 4)?;
                let key = self.eval(&args[0], d)?;
                let table = self.grid(&args[1], d)?;
                let row = self.num(args, 2, d)?.trunc();
                let approximate = if args.len() == 4 { to_bool(&self.eval(&args[3], d)?)? } else { true };
                if row < 1.0 || row as usize > table.rows { return Err("#REF!".into()); }
                let hit = if approximate {
                    (0..table.columns).filter(|c| compare(table.at(0, *c), &key) != Ordering::Greater).last()
                } else {
                    (0..table.columns).find(|c| compare(table.at(0, *c), &key) == Ordering::Equal)
                };
                match hit { Some(c) => table.at(row as usize - 1, c).clone(), None => return Err("#N/A".into()) }
            }
            "SUMPRODUCT" | "SOMMEPROD" => {
                if args.is_empty() { return Err("#ARGS!".into()); }
                let mut grids = vec![];
                for a in args { grids.push(self.grid(a, d)?); }
                let len = grids[0].values.len();
                if grids.iter().any(|g| g.values.len() != len) { return Err("#VALUE!".into()); }
                Value::Num((0..len).map(|i| grids.iter().map(|g| match &g.values[i] { Value::Num(n) => *n, _ => 0.0 }).product::<f64>()).sum())
            }
            "ISEVEN" | "EST.PAIR" | "ISODD" | "EST.IMPAIR" => {
                arity(1, 1)?;
                let even = self.num(args, 0, d)?.trunc() % 2.0 == 0.0;
                Value::Bool(if name == "ISEVEN" || name == "EST.PAIR" { even } else { !even })
            }
            "SIN" | "COS" | "TAN" | "DEGREES" | "DEGRES" | "RADIANS" => {
                arity(1, 1)?;
                let x = self.num(args, 0, d)?;
                Value::Num(match name { "SIN" => x.sin(), "COS" => x.cos(), "TAN" => x.tan(), "RADIANS" => x.to_radians(), _ => x.to_degrees() })
            }
            "GCD" | "PGCD" | "LCM" | "PPCM" => {
                let values = self.numbers(args, d)?;
                if values.is_empty() || values.iter().any(|v| *v < 0.0) { return Err("#NUM!".into()); }
                let lcm = name == "LCM" || name == "PPCM";
                Value::Num(values.iter().fold(if lcm { 1u64 } else { 0u64 }, |acc, v| {
                    let v = *v as u64;
                    if lcm { if acc == 0 || v == 0 { 0 } else { acc / gcd(acc, v) * v } } else { gcd(acc, v) }
                }) as f64)
            }
            "CHOOSE" | "CHOISIR" => {
                if args.len() < 2 { return Err("#ARGS!".into()); }
                let i = self.num(args, 0, d)?.trunc();
                if i < 1.0 || i as usize >= args.len() { return Err("#VALUE!".into()); }
                self.eval(&args[i as usize], d)?
            }
            "IFNA" | "SI.NON.DISP" => {
                arity(2, 2)?;
                match self.eval(&args[0], d) {
                    Ok(v) => v,
                    Err(e) if e == "#N/A" => self.eval(&args[1], d)?,
                    Err(e) => return Err(e),
                }
            }
            "IFS" | "SI.CONDITIONS" => {
                if args.is_empty() || args.len() % 2 != 0 { return Err("#ARGS!".into()); }
                for pair in args.chunks(2) {
                    if to_bool(&self.eval(&pair[0], d)?)? { return self.eval(&pair[1], d); }
                }
                return Err("#N/A".into());
            }
            "NA" => return Err("#N/A".into()),
            "XOR" | "OUX" => {
                let mut flags = vec![];
                for (v, _) in self.flat(args, d)? { if v != Value::Empty { flags.push(to_bool(&v)?); } }
                Value::Bool(flags.iter().filter(|b| **b).count() % 2 == 1)
            }
            "FIND" | "TROUVE" | "SEARCH" | "CHERCHE" => {
                arity(2, 3)?;
                let sensitive = name == "FIND" || name == "TROUVE";
                let fold = |s: String| if sensitive { s } else { s.to_lowercase() };
                let needle: Vec<char> = fold(to_text(&self.eval(&args[0], d)?)).chars().collect();
                let hay: Vec<char> = fold(to_text(&self.eval(&args[1], d)?)).chars().collect();
                let start = if args.len() == 3 { self.num(args, 2, d)?.trunc() } else { 1.0 };
                if start < 1.0 || start as usize > hay.len() + 1 { return Err("#VALUE!".into()); }
                let from = start as usize - 1;
                let hit = (from..=hay.len().saturating_sub(needle.len())).find(|i| hay.len() >= needle.len() && hay[*i..*i + needle.len()] == needle[..]);
                match hit { Some(i) => Value::Num(i as f64 + 1.0), None => return Err("#VALUE!".into()) }
            }
            "SUBSTITUTE" | "SUBSTITUE" => {
                arity(3, 4)?;
                let text = to_text(&self.eval(&args[0], d)?);
                let (old, new) = (to_text(&self.eval(&args[1], d)?), to_text(&self.eval(&args[2], d)?));
                if old.is_empty() { return Ok(Value::Text(text)); }
                if args.len() == 4 {
                    let nth = self.num(args, 3, d)?.trunc();
                    if nth < 1.0 { return Err("#VALUE!".into()); }
                    let mut out = String::new();
                    let (mut seen, mut rest) = (0, text.as_str());
                    while let Some(pos) = rest.find(&old) {
                        seen += 1;
                        out.push_str(&rest[..pos]);
                        out.push_str(if seen == nth as usize { &new } else { &old });
                        rest = &rest[pos + old.len()..];
                    }
                    out.push_str(rest);
                    Value::Text(out)
                } else {
                    Value::Text(text.replace(&old, &new))
                }
            }
            "REPT" => {
                arity(2, 2)?;
                let (text, n) = (to_text(&self.eval(&args[0], d)?), self.num(args, 1, d)?.trunc());
                if !(0.0..=10000.0).contains(&n) { return Err("#VALUE!".into()); }
                Value::Text(text.repeat(n as usize))
            }
            "EXACT" => { arity(2, 2)?; Value::Bool(to_text(&self.eval(&args[0], d)?) == to_text(&self.eval(&args[1], d)?)) }
            "PROPER" | "NOMPROPRE" => {
                arity(1, 1)?;
                let mut up = true;
                Value::Text(to_text(&self.eval(&args[0], d)?).chars().map(|c| {
                    let out = if up { c.to_uppercase().next().unwrap_or(c) } else { c.to_lowercase().next().unwrap_or(c) };
                    up = !c.is_alphabetic();
                    out
                }).collect())
            }
            "TEXTJOIN" | "JOINDRE.TEXTE" => {
                if args.len() < 3 { return Err("#ARGS!".into()); }
                let delimiter = to_text(&self.eval(&args[0], d)?);
                let skip_empty = to_bool(&self.eval(&args[1], d)?)?;
                let parts: Vec<String> = self.flat(&args[2..], d)?.iter().map(|(v, _)| to_text(v)).filter(|t| !skip_empty || !t.is_empty()).collect();
                Value::Text(parts.join(&delimiter))
            }
            "DATE" => {
                arity(3, 3)?;
                let (y, m, day) = (self.num(args, 0, d)?.trunc() as i64, self.num(args, 1, d)?.trunc() as i64, self.num(args, 2, d)?.trunc() as i64);
                let months = y * 12 + (m - 1);
                Value::Num((days_from_civil(months.div_euclid(12), months.rem_euclid(12) + 1, 1) + day - 1 + 25569) as f64)
            }
            "TODAY" | "AUJOURDHUI" => { arity(0, 0)?; Value::Num(now_serial().floor()) }
            "NOW" | "MAINTENANT" => { arity(0, 0)?; Value::Num(now_serial()) }
            "YEAR" | "ANNEE" | "MONTH" | "MOIS" | "DAY" | "JOUR" => {
                arity(1, 1)?;
                let (y, m, day) = civil_from_days(self.num(args, 0, d)?.floor() as i64 - 25569);
                Value::Num(match name { "YEAR" | "ANNEE" => y, "MONTH" | "MOIS" => m, _ => day } as f64)
            }
            "WEEKDAY" | "JOURSEM" => {
                arity(1, 2)?;
                let days = self.num(args, 0, d)?.floor() as i64 - 25569;
                let sunday_first = (days + 4).rem_euclid(7);
                let monday_first = args.len() == 2 && self.num(args, 1, d)?.trunc() == 2.0;
                Value::Num(if monday_first { (sunday_first + 6) % 7 + 1 } else { sunday_first + 1 } as f64)
            }
            _ => return Err("#NAME?".into()),
        })
    }
}
