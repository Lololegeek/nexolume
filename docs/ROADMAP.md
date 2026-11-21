# Prototype status

## Automatically validated

- Compilation of the Rust workspace and the .NET solution.
- UTF-8 round trip through P/Invoke, 100 document cycles and 100 native instance / buffer cycles.
- Commands, revisions, transactions with rollback, central history and deltas with no snapshot after a mutation.
- Writer: Avalonia typing, paragraph and selection styles, lists, outline, statistics, focus mode, autocorrect, Windows spell check, personal dictionary, advanced search/replace, Undo/Redo, writing and reopening, paper/margin settings, explicit breaks, paginated preview and PDF export.
- Sheets: Avalonia typing, 10 + 20 = 30, source change and recalculation, Undo/Redo, saving and reopening.
- Slides: shapes (rectangle, rounded, ellipse, triangle, diamond, line), italic/underlined/aligned text, rotation, opacity, outline, background, transition, speaker notes, slideshow with fade/slide, Undo/Redo and reopening.
- Shell: tabs, Writer style titles, zoom, themes, closing a saved document.
- Real startup through the Windows Avalonia backend, three editors and save / close in hidden mode.

The Skia rendering screenshots are produced by `tests/DotNet`. The hidden Windows test is separate from these screenshots. The file-picker dialogs use StorageProvider and are not automated end to end on the user's desktop.

## Web version (Avalonia Browser + .NET WASM + Rust WASM)

Details and limits are in [WEB.md](WEB.md).

- **Validated automatically:**
  - Rust native tests for `office-wasm`.
  - Node tests of the published `office_wasm_bg.wasm`: create, edit, undo, DOCX, errors, handles.
  - Release build with `scripts/build-web.ps1`.
- **Validated by Playwright (Edge 157):**
  - startup without errors
  - DOCX open and save as DOCX
  - Sheets editing with recalculation and undo
  - beforeunload warning
  - offline reload
  - no request to another origin
- **Not validated automatically:** Writer typing in the browser. Under Playwright, Avalonia does not lay out its canvas (see WEB.md). It was checked by hand in a Chromium browser.
- **Not tested:** Firefox, Safari, mobile.

## Next steps

1. Writer: ~~rich selection across several paragraphs~~ (done: Ctrl+A, Shift+arrows, mouse, copy/cut/paste, formatting, deletion in a single undo step), clipboard formats and a typing style at the caret.
2. Writer: pagination in the editable surface, editing of sections and headers/footers. The preview and the PDF already have line-based pagination.
3. Writer: dynamic table of contents and richer table formatting. Editable tables/images, footnotes, comments, tracked edits and reusable personal styles are implemented.
4. Writer: validation of the print driver (Spooler stopped and unreachable in this environment), crash recovery and grammar correction.
5. Sheets: several sheets and a persistent dependency graph for large volumes, with measurements. Direct in-cell editing, relative formula fill, arithmetic series, CSV exchanges and transpose are implemented.
6. Slides: per-object animations and PPTX/ODP formats. Images, presentation themes, six canvas alignments, PDF/Full HD PNG exports and slideshow blackout/direct navigation are implemented.

DOCX/ODT are supported for Writer with the limits documented in OFFICE_FORMATS.md. Advanced DOCX objects are preserved in the package, but not all are displayed or editable. XLSX (Sheets) and PPTX (Slides) open and save with package preservation, see OFFICE_FORMATS.md. PDF import is not supported. PDF export is available for Writer and Slides; Slides also exports Full HD PNG images. The portable prototype has no installer and no system file associations.

## Office tools validation

The automated scenarios also cover Unicode splitting and merging of paragraphs from the keyboard, lists, colours and line spacing, search/replace followed by undo, cell formats and aggregates, duplication with stable identifiers after Undo/Redo, layers, centring, slide order and slideshow navigation/exit. Older schema 1 files are tested on opening and migrated to schema 2.
