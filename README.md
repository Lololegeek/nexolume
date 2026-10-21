# Nexolume

Offline desktop office suite for Windows x64: C# / .NET 10, Avalonia 11.3.22 and Rust engines. Writer, Sheets and Slides share a single application and a native history. The interface keeps the familiar landmarks of a ribbon word processor, with a slate-and-blue identity specific to Nexolume.

> The application interface is currently in French.

## Run

The portable distribution `artifacts/Nexolume-win-x64/Nexolume.exe` bundles .NET and the Rust DLL. Extract the **entire** ZIP before launching the executable; no installation or network connection is needed to work.

To build from source, install the .NET 10 SDK and stable Rust for Windows MSVC, with the Visual Studio C++ tools required for Rust linking. No C++ code is part of the project. The initial package restore needs Internet access.

```powershell
.\scripts\build.ps1
.\scripts\run.ps1 -NoBuild
.\scripts\test.ps1
# Build, test and produce the self-contained distribution:
.\scripts\publish.ps1
```

## Available features

- **Home**: create the three document types, open local files and search recent documents with their paths and modification dates.
- **Shell**: File menu, vector icons, consistent error banner and close dialog, tabbed documents, title editable with Enter, modification indicator, confirmation before closing, light and dark themes, errors shown in the status bar.
- **Writer**: UTF-8 typing, multiple paragraphs, bold / italic / underline, font, size, colour and highlight **on a selection within a paragraph**. Alignment and heading styles remain paragraph properties. Enter splits the paragraph, Backspace at the start merges with the previous one, Shift+Enter inserts a plain line break. The clipboard keeps plain text here. Zoom from 50 to 150% and a visual guide ruler.
- **Sheets**: grid drawn over the visible area, keyboard and mouse navigation, text and numbers, `+ - * /` formulas, parentheses, unary signs, references and `SUM(A1:A5)`. Entered value and result are kept separate. Dependencies, recalculation, cycles and errors are handled in Rust.
- **Slides**: slides with thumbnails, text boxes, rectangles and ellipses, mouse move and resize, editing of text, coordinates and colour in the side panel. A drag is a single history entry.
- **Common**: Rust Undo/Redo, atomic transactions, versioned deltas, native save and load. Writing uses a temporary file, a disk flush and a replacement with a `.bak` backup for an existing file.
- **Writer formats**: open, edit and save as `.docx` (Word) and `.odt` (LibreOffice/OpenDocument), in addition to `.oswriter`.

Versioned JSON native formats (schemas 2 to 4; legacy schema 1 files are still read): `.oswriter`, `.ossheet`, `.osslides`. These extensions come from the original specification and remain recognised under the Nexolume name.

## Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+N | New text document |
| Ctrl+O | Open |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z | Undo / Redo |
| Ctrl+W | Close the active tab |
| Sheets: arrows, Page Up/Down | Navigate |
| Sheets: Enter or F2 | Edit the selected cell |
| Sheets: Delete / Esc | Clear the cell / cancel the entry |
| Sheets: wheel / Shift+wheel | Vertical / horizontal scroll |

## Validation and limits

`test.ps1` runs the Rust tests, the .NET assertions against the real DLL, the Avalonia typing and mouse scenarios in a headless environment with Skia rendering, then a hidden **native Windows** startup. Screenshots are saved in `artifacts/validation`. No control of the user's desktop is required. The hidden native startup checks that the three editors, the commands and saving are created and then closed; it does not replace a manual exploration of every Windows dialog.

Reduced prototype: no selection across several paragraphs yet, no pagination in the editing surface, no image insertion, charts or plugins. Writer uses an extendable editing surface and a paginated preview shared with the PDF and printing; advanced objects in imported DOCX files are preserved on save but their display remains simplified. The graduated ruler is a visual guide, without editable tab stops. Sheets offers one sheet per document, range selection and atomic operations limited to 1,000 cells. Formula numbers use the decimal point and the functions `SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT` (French aliases `SOMME`, `MOYENNE`, `NB` available). The dependency graph is rebuilt on every edit; calculations and deltas stay targeted at dependents. No benchmark on large workbooks is claimed.

Native files are limited to 16 MiB, DOCX/ODT documents to 10 MiB, and a cell to 8,192 bytes. SUM ranges are limited to 10,000 cells and expression / dependency depths are bounded. History is kept in memory only and is not retained after closing.

Preference files and the error log are in `%LOCALAPPDATA%/Nexolume`. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/COMMANDS.md](docs/COMMANDS.md), [docs/FFI.md](docs/FFI.md) and [docs/ROADMAP.md](docs/ROADMAP.md).

## UI pass

The three spaces share consistent fields, ribbons and selection states. Sheets also offers direct address access, scroll bars and the sum of the cells above. Slides has a contextual inspector, readable numeric values and a fill palette.

The UI tests cover 24 additional renderings: four surfaces, two themes and three sizes (1000, 1440 and 1920 px), plus recent-document search, direct navigation, the quick sum, slide switching and the Cancel / Don't save choices of the close dialog.

The productivity pass adds a navigable outline, statistics, a focus mode with return via Esc and paragraph organisation in Writer. Sheets has a tabbed ribbon, mouse or Shift+arrow selections, range navigation (`A1:C6`), tabular copy-paste, formats on the selection, selection totals, zoom and formula display. Pasting keeps formulas as they are, without relative re-anchoring of references. Slides offers three editable layouts, collapsible panels, and text size, bold and colour that are actually saved. Home filters and sorts recent documents.

## Additional office tools

- Writer: bulleted and numbered lists, indents, colours, highlight, justification, line spacing and spacing after, format clearing, search with case and whole-word options, previous/next navigation and targeted or global replacements that can be undone. The ribbon shows the size in points, offers a compact gallery in a small window and can be hidden. Character properties can be applied to a selection; without a selection, the command keeps the scope of the active paragraph.
- Sheets: bold and general, number, euro currency and percent formats. The computed value stays independent of presentation. Aggregates ignore empty cells and text in references.
- Slides: duplication with new identifiers, slide and object order, centring on the slide, keyboard movement and full-screen slideshow. F5 presents from the start, Shift+F5 from the current slide; arrows to navigate, Esc to exit. Ctrl+D duplicates the selected object.
- Writer: Ctrl+B / Ctrl+I / Ctrl+U for formatting, Ctrl+F / Ctrl+H for search and replace.
- Writer, **Review** tab: capitalisation at the start of sentences, correction of double capitals, common French mistakes and personal replacements (up to 100). Apostrophes, `...`, `(c)`, `(r)`, `(tm)` and arrows are converted while typing. All-caps acronyms are preserved. The options are independent and saved in `%LOCALAPPDATA%/Nexolume/writing.json`.
- `*`, `-` or `1.` followed by Space create a list. Enter continues the list; Enter on an empty item ends it. Backspace immediately restores the last text correction. Case commands act on the selection or the active paragraph; Shift+F3 cycles between capitalising each word, all caps and all lowercase. Changes can be undone with Ctrl+Z.

Automatic correction relies on rules and replacements. The spell check below complements it; contextual grammar correction is not yet available. Pasted text, multi-character compositions and opened documents are not rewritten automatically. Recognised addresses and paths are excluded from word corrections. Ambiguous words such as `a`/`à` or `ou`/`où` are left to the author's decision. The UI tests cover real typing, restoration, options, personal replacements, lists, case changes, saving and the Review ribbon in both themes at 1000, 1440 and 1920 px.

## Writer rich text

Select a passage within a paragraph to change only its characters: bold, italic, underline, font family, size, colour and highlight. The ribbon shows the scope of the action. **Clear format** resets the selection; without a selection, the command resets the paragraph. Alignment, lists, indents and spacing always apply to the paragraph.

The caret, selection, glyph measurement and line wrapping use the same Avalonia layout engine. Rich portions survive typing, corrections, search/replace, Enter/Backspace, duplication, undo and `.oswriter`, DOCX and ODT saves. Older files remain readable. A native file containing mixed styles and no page settings uses schema 3: older versions of Nexolume explicitly refuse it rather than lose those styles. Files without mixed styles or page settings stay in schema 2.

Limits: 20,000 style portions per paragraph, selection within a single paragraph and plain-text clipboard. Without a selection, formatting still changes the active paragraph rather than a future typing style. Pagination in the editing surface, images and editable tables remain to be developed. ODT font declarations and styles on a selection are verified by actually reopening the files with Microsoft Word, in addition to the Open XML validator and the editing tests.

## Writer spell check and search

**Review → Spelling**, or **F7**, opens the proofreading panel. The check runs locally in the background after a short typing pause, using the installed Windows dictionaries (`fr-FR` or `en-US`). Flagged words are underlined in red and suggestions are picked in the panel. A correction goes through the Rust engine and can be undone with Ctrl+Z. A suggestion computed for text that has since changed is ignored.

A word can be ignored until the document is closed, added to Nexolume's personal dictionary or removed with **My dictionary**. These preferences are kept per language in `writing.json`; the global Windows dictionary is not modified. The underline can be turned off. If the language is missing, the panel explains it and indicates which Windows language features to install. The list is limited to 200 flags per paragraph and does not claim to check grammar.

Search offers **Match case**, **Whole words only**, previous/next occurrences with wrap-around, replacement of the matching selection and global replacement in a single undo. Word boundaries take Unicode characters into account, including combining marks. Navigation shows at most 10,000 results; global replacement handles up to 100,000 occurrences and 1,000 paragraphs, without applying any partial result if a limit is exceeded.

The tests cover the real Windows French spell checker, its suggestions and UTF-16 positions, missing languages, the panel, undo, the personal dictionary, language switching, F7, focus mode, stale results and renderings in both themes at three sizes. Search is verified with accented words, emoji, several paragraphs and more than 10,000 occurrences.

Reference for the native integration: [Microsoft Spell Checker API](https://learn.microsoft.com/windows/win32/api/spellcheck/).

The inspection of Word and its limits are recorded in [docs/WORD_REFERENCE.md](docs/WORD_REFERENCE.md). The DOCX/ODT formats and their limits are described in [docs/OFFICE_FORMATS.md](docs/OFFICE_FORMATS.md). All tools presented by Nexolume go through the Rust engine to modify a document.

## Paper, preview and PDF

In Writer, **Layout → Paper and margins** sets A4, A5, Letter or a custom size, the orientation and the four margins in centimetres. The setting can be undone and redone with the document history. **Insert → Page before paragraph** enables or removes an explicit break. Enter in that paragraph keeps the break on the original paragraph and does not repeat it on the next one.

**Layout → Preview · PDF · Print**, the File menu or **Ctrl+P** opens the computed pages, with navigation, zoom and PDF export of all pages, the displayed page or a range. Long paragraphs are distributed by whole lines; the PDF contains selectable text, rich styles, lists and page breaks. Export keeps the path and save state of the editable document. Windows printing offers the system dialog, pages and copies; paper/driver output is not validated in this environment, where the Spooler service is stopped and unreachable.

Native files with page settings or explicit breaks use schema 4. DOCX/ODT keep paper, margins and page breaks. For multi-section DOCX files, the settings modify the last section; the other sections remain in the original package. The preview uses a global format. Word objects absent from the editor are not drawn in the PDF or when printing.

Background outputs, without opening the document in a window:

```powershell
.\Nexolume.exe --export-pdf "C:\Documents\Text.oswriter" "C:\Documents\Text.pdf"
.\Nexolume.exe --print "C:\Documents\Text.oswriter" "Microsoft Print to PDF" "C:\Documents\Text-system.pdf"
```

The commands exit with code 0 on success and 1 on error (local application log). The second example requires the print service and the named driver.

Validation: `scripts/test.ps1 -Configuration Release`, `scripts/check-word.ps1 -NoBuild -Configuration Release`, then `python scripts/check-pdf.py`. The independent PDF check uses PyMuPDF (`python -m pip install --target artifacts/validation/pdf-tools pymupdf`) and compares the full text, typographic points, dimensions, exported pages and landscape orientation. These verification tools are not required by the portable application.
