"""Inspect the actual exported PDFs with an independent PDF parser (PyMuPDF)."""
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts" / "validation"
sys.path.insert(0, str(ARTIFACTS / "pdf-tools"))
import pymupdf

source = json.loads((ARTIFACTS / "pagination.oswriter").read_text(encoding="utf-8-sig"))
expected = json.loads((ARTIFACTS / "pagination-expected.json").read_text(encoding="utf-8"))
compact = lambda value: "".join(value.split())
text = "".join(p["text"] for p in source["content"]["paragraphs"])
pdf = pymupdf.open(ARTIFACTS / "pagination.pdf")
assert len(pdf) == expected["pages"] > 4
assert compact("".join(p.get_text() for p in pdf)) == compact(text), "PDF text is missing, repeated or clipped"
for page in pdf:
    assert abs(page.rect.width - expected["width_points"]) < 1
    assert abs(page.rect.height - expected["height_points"]) < 1
    for word in page.get_text("words"):
        assert 0 <= word[0] < word[2] <= page.rect.width + .1
        assert 0 <= word[1] < word[3] <= page.rect.height + .1
first = pdf[0].get_text("dict")["blocks"][0]["lines"][0]["spans"][0]
assert abs(first["size"] - 22.5) < .1, "Selected font size must convert from DIP to PDF points"
assert first["color"] == 0x285CB3 and "BoldItalic" in first["font"]
last = pymupdf.open(ARTIFACTS / "pagination-last.pdf")
assert len(last) == 1 and compact(last[0].get_text()) == compact(source["content"]["paragraphs"][-1]["text"])
subset = pymupdf.open(ARTIFACTS / "pagination-range.pdf")
assert len(subset) == 2 and compact("".join(p.get_text() for p in subset)) == compact(pdf[1].get_text() + pdf[2].get_text())
landscape = pymupdf.open(ARTIFACTS / "pagination-landscape.pdf")
assert all(p.rect.width > p.rect.height for p in landscape)
assert compact("".join(p.get_text() for p in landscape)) == compact(text)
pdf[0].get_pixmap(matrix=pymupdf.Matrix(1.4, 1.4)).save(ARTIFACTS / "pagination-rendered.png")
report = f"PASS independent PDF parsing: {len(pdf)} A5 pages; complete Unicode text; selected 22.5 pt blue bold italic; page range; landscape; page bounds."
(ARTIFACTS / "pdf-validation.txt").write_text(report, encoding="utf-8")
print(report)
