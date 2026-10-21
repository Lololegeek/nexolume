# Commands and deltas, protocol 1

```json
{"protocol":1,"document":"opaque-document-id","expected_revision":3,"command":"SetCell","params":{"address":"A1","input":"=10+20"}}
```

The protocol, command name, parameter types and editor-specific rules are validated in Rust. Mutation commands require the current revision. `CreateDocument`, `OpenDocument`, `Snapshot` and `CloseDocument` do not require an expected revision.

| Command | Parameters |
|---|---|
| Ping | none |
| CreateDocument | kind: writer / sheets / slides, title |
| OpenDocument | json: file content |
| Snapshot / CloseDocument | document in the envelope |
| SaveDocument | document and expected_revision; returns the serialisation, C# writes the file |
| RenameDocument | title |
| Undo / Redo | no parameters |
| Batch | commands: array of {command, params}; one revision and one history entry |
| SetParagraph | id, text and/or style, optional runs, image and/or table; optional group for typing |
| InsertParagraph | id, index, text, optional style |
| DeleteParagraph | id |
| SetCell | address, input; an empty input clears the cell, a `=` prefix marks a formula |
| AddSlide | id, optional index; full slide reserved notably for restoration |
| DeleteSlide | id |
| AddObject | slide, object, optional index |
| DeleteObject | slide, id |
| UpdateObject / MoveObject / ResizeObject / UpdateObjectText | slide, id and the properties to replace |
| UpdateSlide | id, then background (#RRGGBB), notes, transition (none, fade, slide) |

Writer style: bold, italic, underline, font_family, font_size (6–96), alignment (left / center / right). Slides object: id, kind (text / rectangle / rounded / ellipse / triangle / diamond / line), text, x, y, width, height, fill (#RRGGBB), italic, underline, align, line, line_width, rotation (°), opacity (0.05–1). The order in the array is the drawing order.

```json
{"protocol":1,"ok":true,"document":"id","revision":4,"canUndo":true,"canRedo":false,"changes":[{"kind":"CellChanged","data":{"address":"A1","cell":{"input":"=10+20","display":"30"}}}]}
```

Changes are ordered. The available deltas are DocumentRenamed, PageLayoutChanged, ParagraphChanged / Inserted / Removed, CellChanged / Recalculated, SlideAdded / Removed and ObjectAdded / Removed / Moved / Updated. A full snapshot is reserved for creation, opening or resynchronisation.

A business error returns `ok:false`, `error:{code,message}`, the unchanged revision and `changes:[]`. Business codes: InvalidCommand, RevisionMismatch. Transport errors use a separate C ABI code. Formula errors (#PARSE!, #REF!, #DIV/0!, #CYCLE!, #VALUE!, #NUM!, #DEPTH!, #LIMIT!) are displayable cell results; the entered formula is kept.

## Office extensions (protocol 1, storage schema 2)

Writer: `SplitParagraph` (id, new_id, start, end in UTF-16 units), `MergeParagraph` (id), `ReplaceAll` (non-empty find, replacement). Compound operations return several targeted deltas and undo atomically. Styles add color, highlight, list, indent, line_spacing and spacing_after.

Rich Writer: `FormatText` (id, start, end, partial character style), `ReplaceText` (id, start, end, text), `ReplaceRanges` (id, edits: non-overlapping start/end/text, at most 100,000). Bounds are in UTF-16 and must respect Unicode characters. The allowed style fields are bold, italic, underline, font_family, font_size, color and highlight. `SetParagraph` and `InsertParagraph` also accept `runs`: sorted ranges `{start,length,style}` with a complete character style. An empty array removes explicit ranges. Adjacent identical styles are merged; validation limits each paragraph to 20,000 ranges. Replacements, splits and merges adapt positions and keep styles. Undo restores the text and all its ranges.

Sheets: `SortRange` (range, column = offset within the range, descending, header) reorders the rows of a range by displayed values (numbers then text, blanks last); its inverse is `RestoreCells` (list of address/cell). About 50 functions added (French names, as typed in the app): SOMME.SI.ENS, NB.SI.ENS, MOYENNE.SI.ENS, SI.CONDITIONS, SI.NON.DISP, CHOISIR, RECHERCHEH, SOMMEPROD, RANG, ECARTYPE, VAR, PLAFOND, PLANCHER, FACT, COMBIN, PGCD, PPCM, ALEA, TROUVE, CHERCHE, SUBSTITUE, REPT, NOMPROPRE, JOINDRE.TEXTE, DATE, AUJOURDHUI, ANNEE, MOIS, JOUR, JOURSEM, SIN, COS, TAN. Writer page: `header`, `footer` ({page}, {pages} tokens), `header_align`, `footer_align`.
Sheets: `SetCellStyle` (address, style: bold, format), `RestoreCell` for inverse operations. Formats: general, number, currency, percent. Formulas: SUM / SOMME, AVERAGE / MOYENNE, MIN, MAX, COUNT / NB. Formats change the display, never the input or the evaluation of references.

Slides: `DuplicateSlide` (id), `MoveSlide` (id, index), `DuplicateObject` (slide, id, optional new_id), `ReorderObject` (slide, id, index), `AlignObject` (slide, id, alignment: left / center / right / top / middle / bottom). Additional deltas: SlideReordered, ObjectReordered. Undo/Redo rebuilds the inverse operations actually applied so that identifiers are preserved on redo.


## Writer page layout (protocol 1, storage schema 4)

`SetPageLayout` accepts `layout: {width,height,top,right,bottom,left}` in logical pixels at 96 dpi. `layout:null` restores the default A4 with 96-pixel margins. All six values are finite, dimensions between 200 and 2,400, margins zero or positive, and the text area at least 144 × 144. The `PageLayoutChanged` delta carries `layout`; Undo/Redo restores its previous value. Paragraph styles add `page_break_before` (false by default).

Storage schema 4 protects page data against silent removal by older versions. Flat formats without this data stay in schema 2, or 3 when rich ranges are present. Older native files containing a DOCX source recover their paper format from the original package.

Writer tables: `InsertParagraph` and `SetParagraph` accept `table`: `{rows, header}` where `rows` is a rectangular grid of strings (1 to 200 rows, 1 to 20 columns, 5,000 characters per cell) and `header` makes the first row a header. A table is its own paragraph: its text is empty, it has no image and it can be neither split nor merged. `null` removes the table. A document containing a table is saved in native schema 5.
