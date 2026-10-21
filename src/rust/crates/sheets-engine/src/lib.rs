mod calculation;
#[cfg(test)]
mod office_tests;
pub mod parser;
use change_tracking::Delta;
use command_system::Command;
use document_core::{Cell, CellStyle};
use serde_json::json;
use std::collections::{BTreeMap, BTreeSet, HashMap, VecDeque};
pub fn apply(
    cells: &mut BTreeMap<String, Cell>,
    command: &Command,
) -> Result<(Command, Vec<Delta>), String> {
    if ["SortRange", "RestoreCells"].contains(&command.command.as_str()) {
        return apply_block(cells, command);
    }
    if !["SetCell", "SetCellStyle", "RestoreCell"].contains(&command.command.as_str()) {
        return Err("Unsupported Sheets command".into());
    }
    let address = command.params["address"]
        .as_str()
        .ok_or("Missing address")?
        .to_ascii_uppercase();
    parser::address(&address)?;
    let old = cells.get(&address).cloned();
    let mut cell = old.clone().unwrap_or(Cell {
        input: String::new(),
        display: String::new(),
        style: CellStyle::default(),
    });
    match command.command.as_str() {
        "RestoreCell" => {
            if command.params["cell"].is_null() {
                cells.remove(&address);
            } else {
                cell = serde_json::from_value(command.params["cell"].clone())
                    .map_err(|e| e.to_string())?;
                validate_cell(&cell)?;
                cells.insert(address.clone(), cell);
            }
        }
        "SetCellStyle" => {
            cell.style = serde_json::from_value(command.params["style"].clone())
                .map_err(|e| e.to_string())?;
            validate_cell(&cell)?;
            cells.insert(address.clone(), cell);
        }
        _ => {
            cell.input = command.params["input"]
                .as_str()
                .ok_or("Missing input")?
                .into();
            validate_cell(&cell)?;
            if cell.input.is_empty() && cell.style == CellStyle::default() {
                cells.remove(&address);
            } else {
                cells.insert(address.clone(), cell);
            }
        }
    }
    let deltas = recalculate(cells, Some(&address));
    Ok((
        Command {
            command: "RestoreCell".into(),
            params: json!({"address":address,"cell":old}),
        },
        deltas,
    ))
}
/// Orders cells by their displayed value: numbers, then text without case, empty cells always last.
fn sort_order(x: Option<&Cell>, y: Option<&Cell>, descending: bool) -> std::cmp::Ordering {
    use std::cmp::Ordering::*;
    let key = |c: Option<&Cell>| c.map(|c| c.display.trim().to_string()).filter(|s| !s.is_empty());
    let number = |s: &str| s.replace(',', ".").replace('\u{a0}', "").parse::<f64>().ok();
    match (key(x), key(y)) {
        (None, None) => Equal,
        (None, _) => Greater,
        (_, None) => Less,
        (Some(a), Some(b)) => {
            let order = match (number(&a), number(&b)) {
                (Some(p), Some(q)) => p.partial_cmp(&q).unwrap_or(Equal),
                (Some(_), None) => Less,
                (None, Some(_)) => Greater,
                (None, None) => a.to_lowercase().cmp(&b.to_lowercase()),
            };
            if descending { order.reverse() } else { order }
        }
    }
}
/// Commands that rewrite several cells at once; the inverse is always `RestoreCells`.
fn apply_block(
    cells: &mut BTreeMap<String, Cell>,
    command: &Command,
) -> Result<(Command, Vec<Delta>), String> {
    let mut after: Vec<(String, Option<Cell>)> = vec![];
    if command.command == "RestoreCells" {
        for item in command.params["cells"].as_array().ok_or("Missing cells")? {
            let address = item["address"].as_str().ok_or("Missing address")?.to_ascii_uppercase();
            parser::address(&address)?;
            let cell = if item["cell"].is_null() {
                None
            } else {
                let cell: Cell = serde_json::from_value(item["cell"].clone()).map_err(|e| e.to_string())?;
                validate_cell(&cell)?;
                Some(cell)
            };
            after.push((address, cell));
        }
    } else {
        let range = command.params["range"].as_str().ok_or("Missing range")?.to_ascii_uppercase();
        let (from, to) = range.split_once(':').ok_or("Invalid range")?;
        let (a, b) = (parser::address(from)?, parser::address(to)?);
        if b.0 < a.0 || b.1 < a.1 {
            return Err("Invalid range".into());
        }
        let columns = (b.0 - a.0 + 1) as usize;
        if columns * (b.1 - a.1 + 1) as usize > 100_000 {
            return Err("Range too large".into());
        }
        let key = command.params["column"].as_u64().unwrap_or(0) as usize;
        if key >= columns {
            return Err("Sort column outside the range".into());
        }
        let descending = command.params["descending"].as_bool().unwrap_or(false);
        let first = a.1 + command.params["header"].as_bool().unwrap_or(false) as u32;
        if first < b.1 {
            let mut rows: Vec<Vec<Option<Cell>>> = (first..=b.1)
                .map(|r| (a.0..=b.0).map(|c| cells.get(&parser::name(c, r)).cloned()).collect())
                .collect();
            rows.sort_by(|x, y| sort_order(x[key].as_ref(), y[key].as_ref(), descending));
            for (i, row) in rows.into_iter().enumerate() {
                for (j, cell) in row.into_iter().enumerate() {
                    after.push((parser::name(a.0 + j as u32, first + i as u32), cell));
                }
            }
        }
    }
    let before: Vec<_> = after
        .iter()
        .map(|(address, _)| json!({"address":address,"cell":cells.get(address)}))
        .collect();
    for (address, cell) in &after {
        match cell {
            Some(cell) if !(cell.input.is_empty() && cell.style == CellStyle::default()) => {
                cells.insert(address.clone(), cell.clone());
            }
            _ => {
                cells.remove(address);
            }
        }
    }
    let touched: BTreeSet<&String> = after.iter().map(|(address, _)| address).collect();
    let mut deltas: Vec<Delta> = recalculate(cells, None)
        .into_iter()
        .filter(|d| !d.data["address"].as_str().is_some_and(|a| touched.contains(&a.to_string())))
        .collect();
    for address in touched {
        deltas.push(Delta::new("CellChanged", json!({"address":address,"cell":cells.get(address)})));
    }
    Ok((
        Command {
            command: "RestoreCells".into(),
            params: json!({"cells":before}),
        },
        deltas,
    ))
}
pub fn validate_cell(cell: &Cell) -> Result<(), String> {
    let colour = |c: &str| {
        c == "none" || (c.len() == 7 && c.starts_with('#') && c[1..].bytes().all(|b| b.is_ascii_hexdigit()))
    };
    if cell.input.len() > 8192
        || ![
            "general", "number", "currency", "percent", "integer", "thousands", "scientific",
        ]
        .contains(&cell.style.format.as_str())
        || !["general", "left", "center", "right"].contains(&cell.style.align.as_str())
        || !colour(&cell.style.color)
        || !colour(&cell.style.fill)
    {
        return Err("Invalid cell value or style".into());
    }
    Ok(())
}
fn format_display(display: String, style: &CellStyle) -> String {
    let Ok(value) = display.parse::<f64>() else {
        return display;
    };
    if !value.is_finite() {
        return "#NUM!".into();
    }
    match style.format.as_str() {
        "number" => format!("{value:.2}").replace('.', ","),
        "integer" => format!("{value:.0}"),
        "thousands" => {
            let text = format!("{:.2}", value.abs());
            let (whole, decimals) = text.split_once('.').unwrap_or((&text, "00"));
            let mut grouped = String::new();
            for (i, digit) in whole.chars().enumerate() {
                if i > 0 && (whole.len() - i) % 3 == 0 {
                    grouped.push('\u{a0}');
                }
                grouped.push(digit);
            }
            format!("{}{grouped},{decimals}", if value < 0.0 { "-" } else { "" })
        }
        "scientific" => format!("{value:.2e}").replace('.', ",").replace('e', "E"),
        "currency" => format!("{value:.2} €").replace('.', ","),
        "percent" => {
            let percent = value * 100.0;
            if !percent.is_finite() {
                return "#NUM!".into();
            }
            format!("{percent:.2} %").replace('.', ",")
        }
        _ => display,
    }
}

pub fn recalculate(cells: &mut BTreeMap<String, Cell>, changed: Option<&str>) -> Vec<Delta> {
    let expressions: HashMap<_, _> = cells
        .iter()
        .filter_map(|(name, c)| {
            c.input
                .strip_prefix('=')
                .map(|s| (name.clone(), parser::parse(s)))
        })
        .collect();
    let mut reverse: HashMap<String, BTreeSet<String>> = HashMap::new();
    for (cell, expr) in &expressions {
        if let Ok(e) = expr {
            let mut refs = BTreeSet::new();
            e.references(&mut refs);
            for r in refs {
                reverse.entry(r).or_default().insert(cell.clone());
            }
        }
    }
    let mut affected = BTreeSet::new();
    let mut queue = VecDeque::new();
    if let Some(name) = changed {
        queue.push_back(name.to_string());
    } else {
        queue.extend(cells.keys().cloned());
    }
    while let Some(name) = queue.pop_front() {
        if !affected.insert(name.clone()) {
            continue;
        }
        if let Some(deps) = reverse.get(&name) {
            queue.extend(deps.iter().cloned());
        }
    }
    let mut calculator = calculation::Calculator::new(cells, &expressions);
    let mut updates = vec![];
    for name in affected {
        if let Some(c) = cells.get(&name) {
            let display = if c.input.starts_with('=') {
                match calculator.cell(&name) {
                    Ok(v) => v.display(),
                    Err(e) => e,
                }
            } else {
                c.input.clone()
            };
            let display = format_display(display, &c.style);
            if Some(name.as_str()) == changed || display != c.display {
                updates.push((name, Some(display)));
            }
        } else {
            updates.push((name, None));
        }
    }
    let mut deltas = vec![];
    for (name, display) in updates {
        let kind = if Some(name.as_str()) == changed {
            "CellChanged"
        } else {
            "CellRecalculated"
        };
        if let Some(display) = display {
            cells.get_mut(&name).unwrap().display = display;
            deltas.push(Delta::new(
                kind,
                json!({"address":name,"cell":cells[&name]}),
            ));
        } else {
            deltas.push(Delta::new(kind, json!({"address":name,"cell":null})));
        }
    }
    deltas
}
#[cfg(test)]
mod tests {
    use super::*;
    fn set(c: &mut BTreeMap<String, Cell>, a: &str, input: &str) -> Vec<Delta> {
        apply(
            c,
            &Command {
                command: "SetCell".into(),
                params: json!({"address":a,"input":input}),
            },
        )
        .unwrap()
        .1
    }
    #[test]
    fn arithmetic_dependencies_and_errors() {
        let mut c = BTreeMap::new();
        set(&mut c, "A1", "10");
        set(&mut c, "A2", "20");
        set(&mut c, "A3", "=A1+A2");
        assert_eq!(c["A3"].display, "30");
        let d = set(&mut c, "A1", "15");
        assert_eq!(c["A3"].display, "35");
        assert_eq!(d.len(), 2);
        set(&mut c, "B1", "=SUM(A1:A3)*2-(4/2)");
        assert_eq!(c["B1"].display, "138");
        set(&mut c, "C1", "=1/0");
        assert_eq!(c["C1"].display, "#DIV/0!");
        set(&mut c, "C2", "=A1+");
        assert_eq!(c["C2"].display, "#PARSE!");
        set(&mut c, "C3", "=2+3*4");
        assert_eq!(c["C3"].display, "14");
        set(&mut c, "C4", "=-(2+3)*4");
        assert_eq!(c["C4"].display, "-20");
    }
    #[test]
    fn cycles_recover_and_clear_recalculates() {
        let mut c = BTreeMap::new();
        set(&mut c, "A1", "=B1");
        set(&mut c, "B1", "=A1");
        assert_eq!(c["A1"].display, "#CYCLE!");
        assert_eq!(c["B1"].display, "#CYCLE!");
        set(&mut c, "B1", "7");
        assert_eq!(c["A1"].display, "7");
        set(&mut c, "B1", "");
        assert_eq!(c["A1"].display, "0");
    }
    #[test]
    fn parser_limits() {
        assert!(parser::parse("SUM(A1:XFD1048576)").is_err());
        assert!(parser::address("XFE1").is_err());
        assert!(parser::parse(&"(".repeat(300)).is_err());
    }
}
