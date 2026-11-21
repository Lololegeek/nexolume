# Nexolume in the browser

The web version is the same application as the Windows one, not a rewrite:

- **Same code:** the Avalonia views, editors and themes in `apps/Desktop` are compiled a second time by `apps/Browser` for .NET WebAssembly (`browser-wasm`, Avalonia.Browser 11.3.22).
- **Same engine:** the Rust engine is compiled to `wasm32-unknown-unknown` by the `office-wasm` crate.
- **Static site:** no server-side code, no account, no telemetry. Documents never leave the browser unless the user downloads them.

## Layout

| Path | Role |
|---|---|
| `apps/Desktop` | Windows head (`WinExe`). It also owns all shared views, themes and editors. |
| `apps/Browser` | Browser head. It compiles the shared sources of `apps/Desktop` with `NEXOLUME_BROWSER`, plus `Platform/Browser*.cs`, and owns `wwwroot`. |
| `apps/Desktop/Platform/AppPlatform.cs` | `IAppPlatform`, the only host-specific surface the views use: files, settings, print, spelling, full screen, unsaved-changes guard. |
| `src/OfficeBridge/Abstractions` | `IOfficeTransport`: one JSON request in, one JSON reply out. |
| `src/OfficeBridge/Native` | P/Invoke transport to `office_ffi.dll` (Windows). |
| `src/OfficeBridge/Browser` | JavaScript-interop transport to `office-wasm` (browser). |
| `src/rust/crates/office-wasm` | wasm-bindgen exports of the same `office_ffi::engine::Engine`. |
| `apps/Browser/wwwroot` | `index.html`, `main.js` (boot), `interop.js` (files, print, full screen, beforeunload), `engine/nexolume-engine.js` (WASM loader), `sw.js` (offline cache), `manifest.webmanifest`. |

`MainView` is the whole application UI:

- On Windows, `MainWindow` hosts it.
- In the browser, it is the `ISingleViewApplicationLifetime` main view.
- Dialogs derive from `DialogWindow`. It is a real `Window` on desktop and an in-page overlay in the browser (`DialogWindow.Browser.cs`). Callers keep the same `ShowDialog` code.

Undo/redo, deltas, revisions, formula calculation and DOCX/ODT import-export all run in Rust in both builds. C# keeps only the projection and the drafts, as on desktop (see [ARCHITECTURE.md](ARCHITECTURE.md)).

## Engine bridge

See [FFI.md](FFI.md#webassembly-contract). In short:

- **Calls:** synchronous on the browser main thread, with one UTF-8 JSON string each way. wasm-bindgen copies and encodes the strings, so no pointer ever reaches .NET.
- **Handles:** engines are opaque `u32` handles in a thread-local map. `destroy` frees one; closing the page frees everything.
- **Transport errors:** reported as a JS exception `"Code: message"`, which `BrowserOfficeTransport` maps to `OfficeTransportException`. Business errors stay inside the JSON reply (`ok: false`), exactly as on native.
- **Panics:** a Rust panic aborts the WASM instance (`panic = "abort"`). The loader then marks the module dead. Every later call fails with `EngineCrashed` instead of using corrupted memory, and the user is told to reload. `lastPanic()` keeps the message for the log.

## Files

| Action | Chromium (Chrome, Edge) | Firefox, Safari |
|---|---|---|
| Open | File picker (`showOpenFilePicker`) | `<input type=file>` |
| Save | Writes back to the same file once permission is granted (File System Access API) | Downloads the file under its name |
| Save as | Native save dialog | Download |
| Export PDF / DOCX / ODT / XLSX / PPTX | Native save dialog | Download |

- **Size limits:** files are read fully into memory. Native files are limited to 16 MiB and DOCX/ODT/XLSX/PPTX to 10 MiB, the same limits as on desktop.
- **Unsaved changes:** a `beforeunload` warning is armed while a document is unsaved, and the tab title starts with `•`.
- **No local paths:** the browser has none, so the home page's recent-documents list stays empty. Files are reopened through the picker.

## Features that differ from Windows

- **Print:** Writer builds the same paginated PDF as on Windows, with the same `WriterPrintLayout` and Skia PDF. It then prints it through the browser's own print dialog, which also offers *Save as PDF*. When the browser's PDF viewer cannot print from a frame (Safari), the PDF opens in a new tab so it can be printed there. Windows prints through GDI instead.
- **Spell check:** **not available in the browser.** The desktop build uses the Windows spell-checking API, and no French dictionary is shipped with the site. The dialog says so explicitly instead of reporting "no errors".
- **Slideshow:** runs inside the page and requests full screen with the Fullscreen API. If the browser refuses, the slideshow still fills the page, and Esc leaves it.
- **Links:** only `http`, `https` and `mailto` links open, in a new tab with `noopener`.
- **Settings:** stored in `localStorage`, per site. The error log goes to the browser console.

## Offline and privacy

`sw.js` caches every published file listed in `precache.json`, and each file's SHA-256 is checked before it is cached.

- **After the first visit:** the app opens and works offline.
- **Updates:** a new build changes the cache version, and old caches are deleted.
- **Installable PWA:** `manifest.webmanifest` declares the app, plus file handlers for `.docx`, `.odt`, `.xlsx`, `.pptx`, `.oswriter`, `.ossheet` and `.osslides` (Chromium).

The service worker only works over HTTPS or on `localhost`. The page's Content-Security-Policy forbids connections to other origins. There is no analytics or telemetry code, no font or CDN request, and no backend. The Playwright suite checks that no request leaves the site's origin.

## Build

Prerequisites:

- .NET 10 SDK with the `wasm-tools` workload: `dotnet workload install wasm-tools`
- Rust stable with the WASM target: `rustup target add wasm32-unknown-unknown`
- wasm-bindgen 0.2.130: `cargo install wasm-bindgen-cli --version 0.2.130 --locked`. The version must match `Cargo.lock`.
- Optional: `wasm-opt` (binaryen). The script also finds the copy bundled with the .NET Emscripten pack.
- Node.js, only to serve locally and to run the tests

```powershell
.\scripts\build-web.ps1                         # Release site in artifacts/web
.\scripts\build-web.ps1 -Configuration Debug    # faster, untrimmed
.\scripts\build-web.ps1 -EngineOnly             # only rebuild the Rust engine into wwwroot/engine
.\scripts\build-web.ps1 -Serve -Port 8080       # build then serve on http://localhost:8080
```

The script runs these steps in order:

1. `cargo build -p office-wasm --target wasm32-unknown-unknown --profile wasm-release`
2. wasm-bindgen (`--target web`), then wasm-opt `-Oz`
3. Copy the engine into `apps/Browser/wwwroot/engine` (git-ignored)
4. `dotnet publish apps/Browser`
5. Assemble `artifacts/web`, then write `precache.json`

`apps/Browser` refuses to build when the engine is missing, so run the script once first.

To serve an existing build:

```bash
node scripts/serve-web.mjs artifacts/web 8080
```

## Deploy

`artifacts/web` is a plain static folder. It needs:

- `application/wasm` served for `.wasm` files
- HTTPS, for offline mode and the file pickers
- no caching of `index.html`, `sw.js`, `precache.json` and `engine/*`

| Host | Configuration |
|---|---|
| Vercel | Deploy `artifacts/web`. The included `vercel.json` sets the headers and cache rules. |
| Netlify / Cloudflare Pages | Deploy `artifacts/web`. The included `_headers` file is used. |
| GitHub Pages | Workflow `.github/workflows/web-pages.yml`, run on every push to `main` (or manually). It publishes the landing page `site/` at the root and the app under `app/`. `.nojekyll` and `404.html` are included. Paths are relative, so a project sub-path (`/user.github.io/repo/`) works. |
| nginx | `deploy/nginx.conf`: MIME types, gzip, immutable caching for `_framework`. |

## Tests

```bash
cd tests/Web && npm install
npm run test:engine          # Node: the published office_wasm_bg.wasm (create, edit, undo, DOCX, errors, handles)
npm run test:e2e             # Playwright: the published site in a real browser
```

- `NEXOLUME_CHANNEL=msedge` (or `chrome`) runs the e2e suite in an installed browser. Without it, Playwright uses its own Chromium (`npx playwright install chromium`).
- **Known e2e problem:** when Playwright launches Edge 157 or Chrome on this Windows machine (headless or headed, any viewport, with or without the service worker), Avalonia usually never sizes its canvas. It stays at 300×150, nothing is laid out, and no control can take the keyboard focus. WebGL2 is available, `requestAnimationFrame` runs, and the page reports no error; the cause has not been found. The tests that only go through shortcuts and the engine pass (startup, DOCX, beforeunload, offline, no foreign request; Sheets passed with Edge). **Writer typing fails.** The same scenario (Ctrl+N, focus in the first paragraph, typed text in the engine) was checked by hand on the Release build in the Chromium of the Claude desktop app, where the canvas renders normally. The test is kept as is, not skipped.
- `NEXOLUME_BROWSERS=chromium,firefox,webkit` adds the other engines.

The canvas UI has no DOM, so the e2e tests act like a user with the keyboard, mouse and file chooser. They read the result through a read-only hook, `globalThis.nexolume.state()`, which lists the open documents, their revision, dirty state and engine content.

The e2e suite covers:

- startup without errors
- Writer typing (UTF-8), undo/redo, save by download and reopen
- DOCX open and save back as DOCX
- Sheets: open a workbook, type in a cell, formula recalculated by the engine, undo
- the beforeunload warning
- offline reload through the service worker
- no request to another origin

Native Rust tests (`cargo test`) also run the `office-wasm` host logic.

## Known limitations

- **Tested browsers:** only Chromium-based browsers were tested: the Chromium of the Claude desktop app (by hand), and Edge 157 / Chrome launched by Playwright (partly, see the e2e note above), on Windows. Firefox and Safari should work because the APIs used have fallbacks, but they have **not** been tested.
- **Mobile:** touch devices and small screens are not adapted. The ribbon UI is designed for a mouse and keyboard.
- **First load:** the Release site is about 23 MB before compression; the .NET runtime and the trimmed assemblies make up most of it. Later visits load from cache.
- **Threads:** WebAssembly runs on one thread. A very large recalculation or import blocks the page while it runs, but no work is lost.
- **Spell check:** not available (see above).
- **Saving in Firefox and Safari:** every save is a new download. The browser may rename it (`name (1).docx`).
- **Text input without key events:** Avalonia Browser builds typed text from `keydown`. Text inserted without a key event (some IMEs without composition, dictation, some virtual keyboards, automation tools that use `insertText`) does not reach the document. Physical keyboards and composition-based IMEs work.
- **Focus:** a new or opened document takes the keyboard focus (Writer: first paragraph, Sheets: grid). Shortcuts (Ctrl+N/O/S/Z/Y/W) work as soon as the app has started.
- **Print output:** the page size and margins of the PDF are respected. The print dialog's own scaling options can still change the output.
