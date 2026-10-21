use super::*;
fn set(c: &mut BTreeMap<String, Cell>, address: &str, input: &str) {
    apply(
        c,
        &Command {
            command: "SetCell".into(),
            params: json!({"address":address,"input":input}),
        },
    )
    .unwrap();
}
#[test]
fn aggregate_formulas_and_empty_cells() {
    let mut c = BTreeMap::new();
    set(&mut c, "A1", "10");
    set(&mut c, "A2", "20");
    set(&mut c, "A3", "texte");
    for (address, input, expected) in [
        ("B1", "=AVERAGE(A1:A5)", "15"),
        ("B2", "=MIN(A1:A5)", "10"),
        ("B3", "=MAX(A1:A5)", "20"),
        ("B4", "=COUNT(A1:A5)", "2"),
        ("B5", "=SOMME(A1;A2)", "30"),
    ] {
        set(&mut c, address, input);
        assert_eq!(c[address].display, expected);
    }
    set(&mut c, "A1", "40");
    assert_eq!(c["B1"].display, "30");
    assert_eq!(c["B3"].display, "40");
}
#[test]
fn formats_preserve_values_and_undo() {
    let mut c = BTreeMap::new();
    set(&mut c, "A1", "0.5");
    let (inverse, _) = apply(
        &mut c,
        &Command {
            command: "SetCellStyle".into(),
            params: json!({"address":"A1","style":{"bold":true,"format":"percent"}}),
        },
    )
    .unwrap();
    assert_eq!(c["A1"].display, "50,00 %");
    set(&mut c, "B1", "=A1*2");
    assert_eq!(c["B1"].display, "1");
    apply(&mut c, &inverse).unwrap();
    assert_eq!(c["A1"].display, "0.5");
    assert!(!c["A1"].style.bold);
    set(&mut c, "A1", "");
    assert_eq!(c["B1"].display, "0");
}
#[test]
fn excel_functions_operators_and_text() {
    let mut c = BTreeMap::new();
    for (a, v) in [("A1", "10"), ("A2", "20"), ("A3", "30"), ("B1", "pomme"), ("B2", "poire"), ("B3", "pomme")] {
        set(&mut c, a, v);
    }
    for (input, expected) in [
        ("=2^3^2", "64"),
        ("=-2^2", "4"),
        ("=50%+1", "1.5"),
        ("=\"a\"&\"b\"&1", "ab1"),
        ("=A1<A2", "VRAI"),
        ("=A1<>10", "FAUX"),
        ("=SI(A1>5;\"grand\";\"petit\")", "grand"),
        ("=IF(A1>50,1)", "FAUX"),
        ("=ROUND(2.567,2)", "2.57"),
        ("=ARRONDI(-2.5;0)", "-3"),
        ("=ABS(-4)+SQRT(16)+MOD(-7;3)", "10"),
        ("=POWER(2,10)", "1024"),
        ("=COUNTA(A1:B3)", "6"),
        ("=COUNTIF(B1:B3;\"pomme\")", "2"),
        ("=NB.SI(A1:A3;\">15\")", "2"),
        ("=SUMIF(A1:A3;\">=20\")", "50"),
        ("=SOMME.SI(B1:B3;\"po*\";A1:A3)", "60"),
        ("=AVERAGEIF(A1:A3;\"<>20\")", "20"),
        ("=CONCAT(B1;\" \";B2)", "pomme poire"),
        ("=UPPER(B1)&LEN(B2)", "POMME5"),
        ("=LEFT(B1;2)&RIGHT(B1;2)&MID(B1;2;3)", "pomeomm"),
        ("=AND(A1>0;A2>0)", "VRAI"),
        ("=OR(A1>100;NOT(A2>100))", "VRAI"),
        ("=IFERROR(1/0;\"oups\")", "oups"),
        ("=MEDIAN(A1:A3)", "20"),
        ("=LARGE(A1:A3;2)", "20"),
        ("=VLOOKUP(\"poire\";B1:B3;1;FALSE)", "poire"),
        ("=INDEX(A1:A3;2)", "20"),
        ("=MATCH(30;A1:A3;0)", "3"),
        ("=$A$1+A$2+$A3", "60"),
        ("=0.1+0.2", "0.3"),
        ("=FOO(1)", "#NAME?"),
        ("=A1+B1", "#VALUE!"),
        ("=SUM(A1:A3;5)", "65"),
    ] {
        set(&mut c, "D1", input);
        let expected = if input == "=50%+1" { "1.5" } else { expected };
        assert_eq!(c["D1"].display, expected, "{input}");
    }
    set(&mut c, "A1", "40");
    set(&mut c, "E1", "=SUM($A$1:A3)");
    assert_eq!(c["E1"].display, "90");
    set(&mut c, "A3", "1");
    assert_eq!(c["E1"].display, "61");
}
#[test]
fn extended_function_library() {
    let mut c = BTreeMap::new();
    for (a, v) in [("A1", "10"), ("A2", "20"), ("A3", "30"), ("B1", "pomme"), ("B2", "poire"), ("B3", "pomme"), ("C1", "x"), ("C2", "y"), ("C3", "x")] {
        set(&mut c, a, v);
    }
    for (input, expected) in [
        ("=SOMME.SI.ENS(A1:A3;B1:B3;\"pomme\";C1:C3;\"x\")", "40"),
        ("=NB.SI.ENS(B1:B3;\"pomme\";A1:A3;\">15\")", "1"),
        ("=MOYENNE.SI.ENS(A1:A3;B1:B3;\"pomme\")", "20"),
        ("=ECARTYPE.P(A1:A3)", "8.16496580927726"),
        ("=VAR(A1:A3)", "100"),
        ("=RANG(20;A1:A3)", "2"),
        ("=PLAFOND(12;5)", "15"),
        ("=PLANCHER(12;5)", "10"),
        ("=FACT(5)", "120"),
        ("=COMBIN(5;2)", "10"),
        ("=RECHERCHEH(20;A2:A3;1;FAUX)", "20"),
        ("=SOMMEPROD(A1:A3;A1:A3)", "1400"),
        ("=PGCD(12;18)", "6"),
        ("=PPCM(4;6)", "12"),
        ("=CHOISIR(2;\"a\";\"b\")", "b"),
        ("=SI.CONDITIONS(A1>50;\"x\";A1>5;\"y\")", "y"),
        ("=SI.NON.DISP(NA();\"z\")", "z"),
        ("=TROUVE(\"o\";B1)", "2"),
        ("=CHERCHE(\"M\";B1)", "3"),
        ("=SUBSTITUE(B1;\"m\";\"M\")", "poMMe"),
        ("=SUBSTITUE(B1;\"m\";\"M\";2)", "pomMe"),
        ("=REPT(\"ab\";3)", "ababab"),
        ("=NOMPROPRE(\"bonjour le monde\")", "Bonjour Le Monde"),
        ("=JOINDRE.TEXTE(\"-\";VRAI;B1:B3)", "pomme-poire-pomme"),
        ("=ANNEE(DATE(2024;2;29))", "2024"),
        ("=MOIS(DATE(2024;14;1))", "2"),
        ("=JOUR(DATE(2024;3;0))", "29"),
        ("=JOURSEM(DATE(2024;1;1);2)", "1"),
        ("=EST.PAIR(4)", "VRAI"),
    ] {
        set(&mut c, "D1", input);
        assert_eq!(c["D1"].display, expected, "{input}");
    }
}
#[test]
fn sorting_a_range_is_undoable() {
    let mut c = BTreeMap::new();
    for (a, v) in [("A1", "Nom"), ("B1", "Age"), ("A2", "zoé"), ("B2", "30"), ("A3", "alex"), ("B3", "9"), ("A4", "marc"), ("B4", "100"), ("C2", "=B2*2")] {
        set(&mut c, a, v);
    }
    let sort = |c: &mut BTreeMap<String, Cell>, column: u64, descending: bool| {
        apply(c, &Command { command: "SortRange".into(), params: json!({"range":"A1:C4","column":column,"descending":descending,"header":true}) }).unwrap()
    };
    let (undo, deltas) = sort(&mut c, 1, false);
    assert_eq!(c["A1"].display, "Nom");
    assert_eq!((c["A2"].display.as_str(), c["A3"].display.as_str(), c["A4"].display.as_str()), ("alex", "zoé", "marc"));
    assert!(deltas.iter().any(|d| d.kind == "CellChanged"));
    apply(&mut c, &undo).unwrap();
    assert_eq!((c["A2"].display.as_str(), c["A3"].display.as_str(), c["A4"].display.as_str()), ("zoé", "alex", "marc"));
    assert_eq!(c["C2"].display, "60");
    sort(&mut c, 0, true);
    assert_eq!(c["A2"].display, "zoé");
    assert!(apply(&mut c, &Command { command: "SortRange".into(), params: json!({"range":"A1:C4","column":5}) }).is_err());
}
#[test]
fn rich_styles_and_extra_number_formats() {
    let mut c = BTreeMap::new();
    set(&mut c, "A1", "1234567.891");
    for (format, expected) in [("integer", "1234568"), ("thousands", "1\u{a0}234\u{a0}567,89"), ("scientific", "1,23E6")] {
        apply(
            &mut c,
            &Command {
                command: "SetCellStyle".into(),
                params: json!({"address":"A1","style":{"format":format,"italic":true,"underline":true,"align":"center","color":"#C00000","fill":"#FFF2CC"}}),
            },
        )
        .unwrap();
        assert_eq!(c["A1"].display, expected);
    }
    assert!(c["A1"].style.italic && c["A1"].style.underline && c["A1"].style.align == "center");
    let bad = Command {
        command: "SetCellStyle".into(),
        params: json!({"address":"A1","style":{"fill":"red"}}),
    };
    assert!(apply(&mut c, &bad).is_err());
}
