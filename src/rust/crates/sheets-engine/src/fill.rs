use crate::parser;
use change_tracking::Delta;
use command_system::Command;
use document_core::Cell;
use serde_json::json;
use std::collections::BTreeMap;

/// Translate references while preserving string literals, function names and $ anchors.
fn translate(input: &str, dx: i64, dy: i64) -> Result<String, String> {
    if !input.starts_with('=') {
        return Ok(input.to_owned());
    }
    let bytes = input.as_bytes();
    let mut output = String::new();
    let mut i = 0;
    let mut quoted = false;
    while i < bytes.len() {
        if bytes[i] == b'"' {
            output.push('"');
            i += 1;
            if quoted && bytes.get(i) == Some(&b'"') {
                output.push('"');
                i += 1;
            } else {
                quoted = !quoted;
            }
            continue;
        }
        let boundary = i == 0
            || !matches!(bytes[i - 1], b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'_' | b'.' | b'$');
        if !quoted && boundary && (bytes[i].is_ascii_alphabetic() || bytes[i] == b'$') {
            let start = i;
            let mut end = i;
            let fixed_col = bytes[end] == b'$';
            end += fixed_col as usize;
            let letters = end;
            while bytes.get(end).is_some_and(u8::is_ascii_alphabetic) {
                end += 1;
            }
            let col_end = end;
            let fixed_row = bytes.get(end) == Some(&b'$');
            end += fixed_row as usize;
            let digits = end;
            while bytes.get(end).is_some_and(u8::is_ascii_digit) {
                end += 1;
            }
            let mut following = end;
            while bytes.get(following).is_some_and(u8::is_ascii_whitespace) {
                following += 1;
            }
            let suffix = bytes
                .get(end)
                .is_some_and(|b| b.is_ascii_alphanumeric() || b"_.$".contains(b));
            if letters < col_end && digits < end && !suffix && bytes.get(following) != Some(&b'(') {
                let reference = format!(
                    "{}{}",
                    input[letters..col_end].to_ascii_uppercase(),
                    &input[digits..end]
                );
                if let Ok((col, row)) = parser::address(&reference) {
                    let col = col as i64 + if fixed_col { 0 } else { dx };
                    let row = row as i64 + if fixed_row { 0 } else { dy };
                    if !(1..=16384).contains(&col) || !(1..=1048576).contains(&row) {
                        return Err(
                            "La recopie déplacerait une référence hors de la feuille.".into()
                        );
                    }
                    let target = parser::name(col as u32, row as u32);
                    let split = target.bytes().take_while(u8::is_ascii_alphabetic).count();
                    if fixed_col {
                        output.push('$');
                    }
                    output.push_str(&target[..split]);
                    if fixed_row {
                        output.push('$');
                    }
                    output.push_str(&target[split..]);
                    i = end;
                    continue;
                }
            }
            // An identifier is copied whole so its suffix cannot become a reference.
            i = end.max(start + 1);
            output.push_str(&input[start..i]);
            continue;
        }
        let c = input[i..].chars().next().unwrap();
        output.push(c);
        i += c.len_utf8();
    }
    Ok(output)
}

pub fn apply(
    cells: &mut BTreeMap<String, Cell>,
    command: &Command,
) -> Result<(Command, Vec<Delta>), String> {
    let range = command.params["range"]
        .as_str()
        .ok_or("Missing range")?
        .to_ascii_uppercase();
    let (a, b) = range.split_once(':').ok_or("Invalid range")?;
    let (a, b) = (parser::address(a)?, parser::address(b)?);
    if b.0 < a.0 || b.1 < a.1 || (b.0 - a.0 + 1) as u64 * (b.1 - a.1 + 1) as u64 > 10000 {
        return Err("La recopie est limitée à une plage de 10 000 cellules.".into());
    }
    let mode = command.params["direction"]
        .as_str()
        .ok_or("Missing direction")?;
    let down = match mode {
        "down" | "series_down" => true,
        "right" | "series_right" => false,
        _ => return Err("Invalid fill direction".into()),
    };
    let series = mode.starts_with("series_");
    let length = if down { b.1 - a.1 + 1 } else { b.0 - a.0 + 1 };
    if length < if series { 3 } else { 2 } {
        return Err(if series {
            "Sélectionnez les deux valeurs de départ et au moins une cellule à remplir."
        } else {
            "Sélectionnez la cellule source et les cellules à remplir."
        }
        .into());
    }
    let mut updates = vec![];
    for col in a.0..=b.0 {
        for row in a.1..=b.1 {
            let offset = if down { row - a.1 } else { col - a.0 };
            if offset < if series { 2 } else { 1 } {
                continue;
            }
            let origin = parser::name(if down { col } else { a.0 }, if down { a.1 } else { row });
            let mut cell = cells.get(&origin).cloned();
            if series {
                let second = parser::name(
                    if down { col } else { a.0 + 1 },
                    if down { a.1 + 1 } else { row },
                );
                let number = |key: &str| {
                    cells
                        .get(key)
                        .and_then(|c| c.input.trim().replace(',', ".").parse::<f64>().ok())
                        .filter(|n| n.is_finite())
                };
                let (first, next) = (
                    number(&origin)
                        .ok_or("La série nécessite deux nombres saisis, sans formule.")?,
                    number(&second)
                        .ok_or("La série nécessite deux nombres saisis, sans formule.")?,
                );
                let value = first + (next - first) * offset as f64;
                if !value.is_finite() {
                    return Err("La série dépasse la capacité numérique.".into());
                }
                cell.as_mut().unwrap().input =
                    format!("{value:.14e}").parse::<f64>().unwrap().to_string();
            } else if let Some(c) = &mut cell {
                c.input = translate(
                    &c.input,
                    if down { 0 } else { (col - a.0) as i64 },
                    if down { (row - a.1) as i64 } else { 0 },
                )?;
            }
            updates.push(json!({"address":parser::name(col, row),"cell":cell}));
        }
    }
    crate::apply_block(
        cells,
        &Command {
            command: "RestoreCells".into(),
            params: json!({"cells":updates}),
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fill_keeps_anchors_literals_identifiers_and_rejects_overflow_atomically() {
        for (input, dx, dy, expected) in [
            ("=A1+$B2+C$3+$D$4", 2, 3, "=C4+$B5+E$3+$D$4"),
            (
                "=SUM(A1:B2)+LOG10(100)+1E10",
                1,
                1,
                "=SUM(B2:C3)+LOG10(100)+1E10",
            ),
            (
                "=IF(A1=\"A1 \"\"B2\"\" été\";A2;0)",
                1,
                2,
                "=IF(B3=\"A1 \"\"B2\"\" été\";B4;0)",
            ),
            ("=$a1+b$2", 2, 1, "=$A2+D$2"),
            ("texte A1", 1, 1, "texte A1"),
        ] {
            assert_eq!(translate(input, dx, dy).unwrap(), expected);
        }
        assert!(translate("=XFD1", 1, 0).is_err());
        assert!(translate("=A1048576", 0, 1).is_err());
        let mut cells = BTreeMap::new();
        for (a, input) in [("A1", "1"), ("A2", "3"), ("B1", "5"), ("B2", "invalid")] {
            crate::apply(
                &mut cells,
                &Command {
                    command: "SetCell".into(),
                    params: json!({"address":a,"input":input}),
                },
            )
            .unwrap();
        }
        let original = cells.clone();
        assert!(apply(
            &mut cells,
            &Command {
                command: "FillRange".into(),
                params: json!({"range":"A1:B5","direction":"series_down"})
            }
        )
        .is_err());
        assert_eq!(
            cells, original,
            "invalid second seed must roll back every column"
        );
        let (inverse, _) = apply(
            &mut cells,
            &Command {
                command: "FillRange".into(),
                params: json!({"range":"A1:A5","direction":"series_down"}),
            },
        )
        .unwrap();
        assert_eq!(cells["A5"].input, "9");
        crate::apply(&mut cells, &inverse).unwrap();
        assert_eq!(cells, original);
    }
}
