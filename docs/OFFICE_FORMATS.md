# Word and LibreOffice formats

Nexolume Writer opens, edits and saves `.docx` (Word) and `.odt` (OpenDocument Text) documents. These formats are available in the Open and Save As dialogs. The native `.oswriter` format remains available to keep Nexolume's own data.

DOCX import reads the default styles, paragraph styles and their parent styles, then the properties of each text run, including inherited character styles. Theme fonts, point sizes, alignments, lists, colours, highlighting and spacing are taken into account. Text deleted by change tracking and field instructions are not added to the visible text.

An imported DOCX keeps its original package. Saving without changes returns the same file byte for byte. After editing, Nexolume modifies the affected paragraphs and copies over the other parts of the package: images, relationships, styles, headers, footers and other resources. Tables and section properties stay in the document XML. Text changes preserve the formatting runs around them; replaced text takes the format of the run where the replacement starts. A round trip through the native `.oswriter` format also keeps this original package.

The Writer display remains simplified: it shows cell text as paragraphs and does not yet draw tables, images, headers, footers or Word pagination. The supported mixed styles are displayed and editable on a selection within a paragraph: bold, italic, underline, font, size, colour and highlight. Advanced properties that are not represented stay preserved in the DOCX package. Moving a paragraph between a table cell and the document body is refused on export to avoid an incorrect reorganisation. Text changes inside a field or a revision do not amount to a full editor for fields or change tracking.

New DOCX files export bulleted/numbered lists, correct point sizes, highlighting, line spacing and paragraph properties. Keeping the original package applies to DOCX saves: converting to ODT keeps the text and the supported mixed character styles, without advanced objects. ODT remains a simplified import/export and does not yet benefit from full package preservation.

Legacy binary `.doc` files and Office presentations/workbooks are not yet supported. Imported `.docx` and `.odt` files are limited to 10 MiB by the application; the engine also limits the decompressed XML size of each part it reads.

The test suite checks inherited styles, units, Unicode edits in the middle of runs, paragraph insertions/moves and preservation of the other ZIP parts. The integration test project uses the schema validator from Microsoft's Open XML SDK; that SDK is not a dependency of the distributed application. `scripts/check-word.ps1` creates a document with a hidden Microsoft Word, modifies it through the real Rust DLL and reopens it in Word to check text, mixed formatting, table, image, header/footer and landscape orientation. It also checks the bullets, highlighting and size of a new DOCX, the addition of a list to an existing DOCX, and the sizes/fonts/decorations of a selected passage in an imported DOCX, a new DOCX and an ODT. Microsoft Word must be installed; the check files are created in `artifacts/validation`.

Reference for structure and styles: [Microsoft Learn — WordprocessingML paragraph styles](https://learn.microsoft.com/en-us/office/open-xml/word/how-to-create-and-add-a-paragraph-style-to-a-word-processing-document).


### Paper and paginated output

Dimensions, orientation, the four margins and `pageBreakBefore` are imported/exported in DOCX. Page changes preserve unmodified attributes (notably header/footer distances and gutter), section references and the other ZIP parts. The format of the last section drives the global rendering; earlier sections are not edited by this setting.

The generated ODT contains a page definition in styles.xml and `fo:break-before` paragraph breaks. DOCX/ODT settings were checked by reopening them in Word. ODT templates with several master pages are reduced to a single global setting.

The PDF is an output of the content Nexolume can display: selectable Unicode text, character styles, paragraphs/lists and line-based pagination. DOCX objects not rendered in the editor are not included. Breaks and formats may therefore differ from Word on advanced documents; the original DOCX package remains the format that preserves objects.

Writer tables: a simple DOCX body table (rectangular, with no merged cells, no image or object in cells, at most 200 rows and 20 columns) is imported as an editable table paragraph, with its header row if marked `w:tblHeader`. Complex tables stay flattened into cell paragraphs, as before. An unmodified simple table keeps its original XML; editing it rewrites it with simple borders and loses its original Word formatting. DOCX and ODT export write tables with borders and a bold header; ODT reads back simple tables with no merging or nesting. Printing and PDF draw the grid and break pages between two rows.
