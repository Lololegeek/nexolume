# Nexolume architecture

## Responsibilities

`apps/Desktop` owns the XAML views, navigation, selection, input drafts and the Avalonia / Skia rendering. The visible data is a local projection of the engine. Drafts become confirmed content only once a Rust command has accepted them. FFI calls and file writes run asynchronously.

`src/OfficeBridge` is the only P/Invoke caller. `OfficeEngine` owns a SafeHandle. `DocumentSession` serialises operations per document, applies deltas and keeps the path and save state. Content and history belong to the engine. A revision mismatch reloads the snapshot and explicitly rejects the stale change; it is never silently replayed.

## Rust workspace

| Crate | Responsibility |
|---|---|
| document-core | Documents, paragraphs, cells, slides, objects and identifiers |
| command-system | JSON command envelopes and schema |
| history-engine | Shared inverse-operation stacks, transactions and typing grouping |
| change-tracking | Deltas typed by discriminant, targeted data |
| writer-engine | Paragraph and style edits / validation |
| sheets-engine | Expression parser, dependencies, evaluation, cycles, propagation |
| slides-engine | Slides, objects, moves and geometry validation |
| file-formats | Versioned native JSON, validation on open and recalculation |
| office-ffi | Handle and buffer registries, C ABI boundary, session routing |

Inverses target the paragraph, cell or object concerned. Deleting a slide keeps that slide for restoration, without copying the whole presentation. Grouped typing keeps a paragraph's initial and last values instead of every intermediate keystroke. A new change empties Redo. A failed transaction applies the inverses already produced, without changing the revision.

## Rendering

Writer reuses TextBox for the caret, selection and IME. Its RichTextPresenter overrides Avalonia's CreateTextLayout extension point: character properties are passed to the same TextLayout that draws, measures, places the caret and selects text. Drafts follow the rich ranges between Rust validations; only styles accepted by the engine become confirmed data. The spell-check overlays use the rectangles of that TextLayout. Sheets draws the visible cells in a single control: no instantiation of millions of controls. Slides draws a 960 × 540 logical surface, transformed by a Viewbox. A move is previewed locally and confirmed on release.

The engine computes and stores positions and properties. C# uses the current mouse coordinates to propose a command. There is no local HTTP server and no separate engine process.

## Evolution

Formats are isolated to accommodate importers and exporters. Replacing the Writer rendering with a pagination engine does not require moving history into C#. Paragraph properties remain distinct from character ranges (start/length in UTF-16, explicit non-overlapping styles). Native files that need these ranges, without page settings, use schema 3 so that older versions cannot silently drop the styles; the internal model normalises older files and flat files stay in schema 2. ParagraphChanged deltas carry the text, the paragraph style and its rich ranges.


## Writer paginated output

`WriterPrintLayout` shapes the snapshot's paragraphs with Avalonia TextLayout, then distributes its whole TextLines across pages. It keeps rich ranges, alignment, list markers and explicit breaks. The snapshot owns its layouts and releases them when the preview closes. Typing stays in the TextBoxes of the extendable surface.

`PrintPageControl` draws these lines for the preview, the Skia PDF and Windows pages. Scale factors are part of the Avalonia transforms, so the backend does not reset them. The PDF converts 96 logical pixels to 72 points, writes a temporary file, then replaces its destination. Windows printing renders each page as a 192 dpi BGRA bitmap, then passes the pages to the driver's GDI DC; it is not vector output. The document path, revisions and history are not modified by an output.

`PageLayout` belongs to the Rust document. DOCX imports the settings of the final section; changing them touches that section's pgSz/pgMar and preserves its other properties. ODT uses styles.xml and a global master page. Settings and breaks require storage schema 4, while the internal schema stays 2.
