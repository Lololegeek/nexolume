// Browser services used by .NET (module "nexolume-interop", Platform/BrowserInterop.cs).
// Everything stays in the page: no network request is made here and nothing is reported anywhere.

const settingsPrefix = 'nexolume.';
let unsaved = false;

addEventListener('beforeunload', (event) => {
  if (!unsaved) return;
  event.preventDefault();
  event.returnValue = '';
});

export function readSetting(key) {
  try { return localStorage.getItem(settingsPrefix + key); } catch { return null; }
}

export function writeSetting(key, value) {
  try { localStorage.setItem(settingsPrefix + key, value); return true; } catch { return false; }
}

export function openUrl(url) {
  // .NET only passes http(s)/mailto URLs. noopener keeps the document page out of reach of the link.
  window.open(url, '_blank', 'noopener,noreferrer');
}

export function setUnsavedChanges(value) {
  unsaved = !!value;
  document.title = (unsaved ? '• ' : '') + 'Nexolume';
}

export function hasSavePicker() {
  return typeof window.showSaveFilePicker === 'function';
}

// Memory views are only valid during the call: copy before anything asynchronous.
function blobOf(view, type) {
  return new Blob([view.slice()], { type });
}

export function download(fileName, mime, view) {
  const url = URL.createObjectURL(blobOf(view, mime));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

let printFrame = null;
let printUrl = null;
export function printPdf(title, view) {
  const blob = blobOf(view, 'application/pdf');
  if (printUrl) URL.revokeObjectURL(printUrl);
  printUrl = URL.createObjectURL(blob);
  printFrame?.remove();
  printFrame = document.createElement('iframe');
  printFrame.title = title;
  printFrame.setAttribute('aria-hidden', 'true');
  Object.assign(printFrame.style, { position: 'fixed', right: '0', bottom: '0', width: '1px', height: '1px', border: '0', opacity: '0' });
  const url = printUrl;
  printFrame.addEventListener('load', () => {
    try {
      printFrame.contentWindow.focus();
      printFrame.contentWindow.print();
    } catch {
      // Browsers whose PDF viewer cannot print from a frame (Safari): show the PDF, printable from its viewer.
      window.open(url, '_blank', 'noopener');
    }
  }, { once: true });
  printFrame.src = url;
  document.body.appendChild(printFrame);
}

export function setFullScreen(value) {
  try {
    if (value && !document.fullscreenElement) document.documentElement.requestFullscreen?.().catch(() => {});
    else if (!value && document.fullscreenElement) document.exitFullscreen?.().catch(() => {});
  } catch { /* full screen refused: the slideshow still fills the page */ }
}

export function logError(message) {
  console.error('[Nexolume]', message);
}

export function appReady() {
  document.getElementById('splash')?.remove();
  document.documentElement.dataset.ready = 'true';
  // Keyboard shortcuts work right away, before the first click.
  const container = document.querySelector('.avalonia-container');
  if (container && !container.contains(document.activeElement)) container.focus({ preventScroll: true });
}

export function appFailed(message) {
  const splash = document.getElementById('splash');
  if (!splash) return;
  splash.classList.add('failed');
  splash.querySelector('.message').textContent = 'Nexolume n’a pas pu démarrer : ' + message;
}
