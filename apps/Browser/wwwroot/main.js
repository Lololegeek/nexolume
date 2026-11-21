// Starts the .NET WebAssembly runtime; Program.Main then loads the Rust engine and Avalonia.
import { dotnet } from './_framework/dotnet.js';

function fail(message) {
  const splash = document.getElementById('splash');
  if (!splash) return;
  splash.classList.add('failed');
  splash.querySelector('.message').textContent = message;
}

if (typeof WebAssembly !== 'object') {
  fail('Ce navigateur ne prend pas en charge WebAssembly. Utilisez une version récente de Chrome, Edge, Firefox ou Safari.');
} else {
  // Avalonia cancels pointerdown (preventDefault), so a click never gives its container the DOM
  // focus and keys typed afterwards go to <body>, where Avalonia does not listen. Give the
  // container the focus first; Avalonia then moves it to its hidden text input if the click
  // lands in a text field.
  document.addEventListener('pointerdown', (event) => {
    const container = event.target instanceof Element ? event.target.closest('.avalonia-container') : null;
    if (container && !container.contains(document.activeElement)) container.focus({ preventScroll: true });
  }, true);
  try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const config = runtime.getConfig();
    // Read-only diagnostics used by the browser tests (see Platform/BrowserDiagnostics.cs).
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    globalThis.nexolume = Object.freeze({
      state: () => JSON.parse(exports.Nexolume.Desktop.Platform.BrowserDiagnostics.State()),
    });
    await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
  } catch (error) {
    console.error('[Nexolume]', error);
    fail('Nexolume n’a pas pu démarrer : ' + (error?.message ?? error));
  }
}

// Offline support: the service worker caches the published files after the first visit.
if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
  navigator.serviceWorker.register('./sw.js').catch((error) => console.warn('[Nexolume] Mode hors ligne indisponible :', error));
}
