# Word reference and Nexolume adaptations

## Observed elements

Requested target: `C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE`.

- REA `inspect_pe_resources`: PE32+, SHA-256 `a02ec8e28e1210a13f1c8d1874c9f10d6ed987a18365cbaae72d8403ba2d906d`, 223 resources and 453 entries examined. Evidence: `ev_89f0d36764be20dc8a2daba54e59cbd6032226d842fbb9e7c44f8ad3457af4ae`.
- This inventory is static. It establishes the identity of the executable and its resources, not the layout of commands or how they run.
- A dedicated, invisible Word COM instance was created, queried and closed without saving. Observed version: 16.0, build 16.0.20430. Local result: `artifacts/reference/word-automation.json`.
- Blank document observed: Aptos 12 pt, page 595.3 × 841.9 points, margins of about 70.85 points, 8 points of spacing after. The observed line spacing was 13.9 points.
- The labels Paste, Cut, Copy, Bold, Italic, Underline, Font colour, Highlight, Bullets, Numbering, Indents, Find and Replace were exposed by this installation through CommandBars. Some other Office identifier queries were not exposed: that is not proof that these functions are absent from Word.

## Nexolume choices

The ribbon groups follow the landmarks of the Word capture provided by the user. Their precise layout is a Nexolume choice, not a measurement taken from the binary. The slate / blue identity is kept; the ribbon adapts to small windows by grouping the style gallery into a menu.

The document now uses Aptos 12 pt by default, with an explicit conversion between displayed points and Avalonia logical pixels. Rendering margins are close to 2.5 cm. The ribbon can be hidden, commands are spaced out and fields have understandable names.

The added features are independent implementations in the Rust engines and the Avalonia interface. Writer styles still apply to the whole paragraph. Images, tables, annotations, change tracking, pagination and Microsoft Office formats remain future work.
