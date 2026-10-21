use std::collections::BTreeSet;
#[derive(Clone, Debug)]
pub enum Expr {
    Number(f64),
    Text(String),
    Bool(bool),
    Reference(String),
    /// Top-left and bottom-right cell of a normalised rectangle.
    Range(String, String),
    /// Operators: + - * / ^ & = < > and the two-letter forms `l` (<=), `g` (>=), `n` (<>).
    Binary(char, Box<Expr>, Box<Expr>),
    Negate(Box<Expr>),
    Percent(Box<Expr>),
    Function(String, Vec<Expr>),
}
pub fn address(input: &str) -> Result<(u32, u32), String> {
    let s = input.as_bytes();
    let mut i = 0;
    let mut col = 0u32;
    while i < s.len() && s[i].is_ascii_uppercase() {
        col = col
            .checked_mul(26)
            .and_then(|c| c.checked_add((s[i] - b'A' + 1) as u32))
            .ok_or("#REF!")?;
        i += 1;
    }
    if i == 0 || i == s.len() || s[i] == b'0' {
        return Err("#REF!".into());
    }
    let row = input[i..].parse::<u32>().map_err(|_| "#REF!")?;
    if col > 16384 || row == 0 || row > 1048576 {
        return Err("#REF!".into());
    }
    Ok((col, row))
}
pub fn name(mut col: u32, row: u32) -> String {
    let mut s = String::new();
    while col > 0 {
        col -= 1;
        s.insert(0, (b'A' + (col % 26) as u8) as char);
        col /= 26;
    }
    format!("{s}{row}")
}
/// Cells covered by a range, column by column.
pub fn range_cells(a: &str, b: &str) -> Vec<String> {
    let (Ok(a), Ok(b)) = (address(a), address(b)) else {
        return vec![];
    };
    let mut out = vec![];
    for col in a.0.min(b.0)..=a.0.max(b.0) {
        for row in a.1.min(b.1)..=a.1.max(b.1) {
            out.push(name(col, row));
        }
    }
    out
}
pub fn parse(input: &str) -> Result<Expr, String> {
    if input.len() > 8192 {
        return Err("#LIMIT!".into());
    }
    if input
        .bytes()
        .filter(|c| b"+-*/^&(,<>=".contains(c))
        .count()
        > 256
    {
        return Err("#LIMIT!".into());
    }
    let mut p = Parser {
        source: input.as_bytes(),
        index: 0,
        depth: 0,
    };
    let expr = p.comparison()?;
    p.space();
    if p.index != p.source.len() {
        return Err("#PARSE!".into());
    }
    Ok(expr)
}
impl Expr {
    pub fn references(&self, out: &mut BTreeSet<String>) {
        match self {
            Self::Reference(s) => {
                out.insert(s.clone());
            }
            Self::Range(a, b) => out.extend(range_cells(a, b)),
            Self::Binary(_, a, b) => {
                a.references(out);
                b.references(out);
            }
            Self::Negate(e) | Self::Percent(e) => e.references(out),
            Self::Function(_, es) => {
                for e in es {
                    e.references(out)
                }
            }
            _ => {}
        }
    }
}
struct Parser<'a> {
    source: &'a [u8],
    index: usize,
    depth: usize,
}
impl Parser<'_> {
    fn space(&mut self) {
        while self
            .source
            .get(self.index)
            .is_some_and(u8::is_ascii_whitespace)
        {
            self.index += 1;
        }
    }
    fn peek(&mut self) -> Option<u8> {
        self.space();
        self.source.get(self.index).copied()
    }
    fn consume(&mut self, c: u8) -> bool {
        if self.peek() == Some(c) {
            self.index += 1;
            true
        } else {
            false
        }
    }
    fn comparison(&mut self) -> Result<Expr, String> {
        self.depth += 1;
        if self.depth > 128 {
            return Err("#DEPTH!".into());
        }
        let mut lhs = self.concat()?;
        loop {
            self.space();
            let rest = &self.source[self.index..];
            let (op, len) = if rest.starts_with(b"<=") {
                ('l', 2)
            } else if rest.starts_with(b">=") {
                ('g', 2)
            } else if rest.starts_with(b"<>") {
                ('n', 2)
            } else if rest.starts_with(b"<") {
                ('<', 1)
            } else if rest.starts_with(b">") {
                ('>', 1)
            } else if rest.starts_with(b"=") {
                ('=', 1)
            } else {
                break;
            };
            self.index += len;
            lhs = Expr::Binary(op, Box::new(lhs), Box::new(self.concat()?));
        }
        self.depth -= 1;
        Ok(lhs)
    }
    fn concat(&mut self) -> Result<Expr, String> {
        let mut lhs = self.expression()?;
        while self.consume(b'&') {
            lhs = Expr::Binary('&', Box::new(lhs), Box::new(self.expression()?));
        }
        Ok(lhs)
    }
    fn expression(&mut self) -> Result<Expr, String> {
        let mut lhs = self.term()?;
        loop {
            if self.consume(b'+') {
                lhs = Expr::Binary('+', Box::new(lhs), Box::new(self.term()?));
            } else if self.consume(b'-') {
                lhs = Expr::Binary('-', Box::new(lhs), Box::new(self.term()?));
            } else {
                break;
            }
        }
        Ok(lhs)
    }
    fn term(&mut self) -> Result<Expr, String> {
        let mut lhs = self.power()?;
        loop {
            if self.consume(b'*') {
                lhs = Expr::Binary('*', Box::new(lhs), Box::new(self.power()?));
            } else if self.consume(b'/') {
                lhs = Expr::Binary('/', Box::new(lhs), Box::new(self.power()?));
            } else {
                break;
            }
        }
        Ok(lhs)
    }
    fn power(&mut self) -> Result<Expr, String> {
        let mut lhs = self.factor()?;
        while self.consume(b'^') {
            lhs = Expr::Binary('^', Box::new(lhs), Box::new(self.factor()?));
        }
        Ok(lhs)
    }
    fn factor(&mut self) -> Result<Expr, String> {
        self.depth += 1;
        if self.depth > 128 {
            return Err("#DEPTH!".into());
        }
        let r = self.unary();
        self.depth -= 1;
        r
    }
    fn unary(&mut self) -> Result<Expr, String> {
        if self.consume(b'-') {
            return Ok(Expr::Negate(Box::new(self.factor()?)));
        }
        if self.consume(b'+') {
            return self.factor();
        }
        let mut e = self.atom()?;
        while self.consume(b'%') {
            e = Expr::Percent(Box::new(e));
        }
        Ok(e)
    }
    /// A cell reference such as `A1`, `$A$1` (the dollar signs are dropped).
    fn reference(&mut self) -> Result<String, String> {
        self.space();
        let mut cell = String::new();
        let mut letters = 0;
        for pass in 0..2 {
            self.consume(b'$');
            let start = self.index;
            while self.source.get(self.index).is_some_and(|c| {
                if pass == 0 {
                    c.is_ascii_alphabetic()
                } else {
                    c.is_ascii_digit()
                }
            }) {
                self.index += 1;
            }
            if pass == 0 {
                letters = self.index - start;
            }
            cell.push_str(&String::from_utf8_lossy(&self.source[start..self.index]).to_ascii_uppercase());
        }
        if letters == 0 {
            return Err("#PARSE!".into());
        }
        address(&cell)?;
        Ok(cell)
    }
    fn atom(&mut self) -> Result<Expr, String> {
        if self.consume(b'(') {
            let e = self.comparison()?;
            if !self.consume(b')') {
                return Err("#PARSE!".into());
            }
            return Ok(e);
        }
        if self.consume(b'"') {
            let mut text = vec![];
            loop {
                match self.source.get(self.index) {
                    None => return Err("#PARSE!".into()),
                    Some(b'"') if self.source.get(self.index + 1) == Some(&b'"') => {
                        text.push(b'"');
                        self.index += 2;
                    }
                    Some(b'"') => {
                        self.index += 1;
                        break;
                    }
                    Some(c) => {
                        text.push(*c);
                        self.index += 1;
                    }
                }
            }
            return String::from_utf8(text)
                .map(Expr::Text)
                .map_err(|_| "#PARSE!".into());
        }
        self.space();
        let start = self.index;
        if self.source.get(start).is_some_and(u8::is_ascii_alphabetic) {
            let mut end = start;
            while self
                .source
                .get(end)
                .is_some_and(|c| c.is_ascii_alphanumeric() || *c == b'.' || *c == b'_')
            {
                end += 1;
            }
            let ident = String::from_utf8_lossy(&self.source[start..end]).to_ascii_uppercase();
            if self.source.get(end) == Some(&b'(') {
                self.index = end + 1;
                let mut args = vec![];
                if !self.consume(b')') {
                    args.push(self.comparison()?);
                    while self.consume(b',') || self.consume(b';') {
                        args.push(self.comparison()?);
                    }
                    if !self.consume(b')') {
                        return Err("#PARSE!".into());
                    }
                }
                return Ok(Expr::Function(ident, args));
            }
            let literal = match ident.as_str() {
                "TRUE" | "VRAI" => Some(true),
                "FALSE" | "FAUX" => Some(false),
                _ => None,
            };
            if let Some(b) = literal {
                self.index = end;
                return Ok(Expr::Bool(b));
            }
        }
        if self
            .source
            .get(start)
            .is_some_and(|c| c.is_ascii_alphabetic() || *c == b'$')
        {
            let first = self.reference()?;
            if self.consume(b':') {
                let second = self.reference()?;
                let (a, b) = (address(&first)?, address(&second)?);
                let (minc, maxc) = (a.0.min(b.0), a.0.max(b.0));
                let (minr, maxr) = (a.1.min(b.1), a.1.max(b.1));
                if (maxc - minc + 1) as u64 * (maxr - minr + 1) as u64 > 10000 {
                    return Err("#LIMIT!".into());
                }
                return Ok(Expr::Range(name(minc, minr), name(maxc, maxr)));
            }
            return Ok(Expr::Reference(first));
        }
        while self
            .source
            .get(self.index)
            .is_some_and(|c| c.is_ascii_digit() || *c == b'.')
        {
            self.index += 1;
        }
        if start == self.index {
            return Err("#PARSE!".into());
        }
        let number = std::str::from_utf8(&self.source[start..self.index])
            .unwrap()
            .parse::<f64>()
            .map_err(|_| "#PARSE!")?;
        if !number.is_finite() {
            return Err("#NUM!".into());
        }
        Ok(Expr::Number(number))
    }
}
