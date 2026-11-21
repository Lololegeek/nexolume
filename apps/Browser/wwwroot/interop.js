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

// Device fonts (Local Font Access API, Chrome/Edge). The files are read from the device, never sent anywhere.
const deviceFonts = new Map(); // family -> FontData[]

export function localFontsSupported() {
  return typeof window.queryLocalFonts === 'function';
}

// "granted", "prompt", "denied" or "unsupported".
export async function localFontsPermission() {
  if (!localFontsSupported()) return 'unsupported';
  try { return (await navigator.permissions.query({ name: 'local-fonts' })).state; } catch { return 'prompt'; }
}

// Lists the families as JSON. Shows the browser's permission prompt the first time (needs a recent click).
export async function listLocalFonts() {
  if (!localFontsSupported()) return '';
  try {
    const faces = await window.queryLocalFonts();
    deviceFonts.clear();
    for (const face of faces) {
      if (!face.family) continue;
      if (!deviceFonts.has(face.family)) deviceFonts.set(face.family, []);
      deviceFonts.get(face.family).push(face);
    }
    return JSON.stringify([...deviceFonts.keys()].sort((a, b) => a.localeCompare(b)));
  } catch {
    return '';
  }
}

function base64Of(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).slice(String(reader.result).indexOf(',') + 1));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });
}

// Returns the faces of a family as a JSON array of base64 font files ("" when unknown).
export async function loadLocalFontFamily(family) {
  const faces = deviceFonts.get(family);
  if (!faces) return '';
  const files = [];
  for (const face of faces.slice(0, 24)) {
    try { files.push(await base64Of(await face.blob())); } catch { /* unreadable face: skip it */ }
  }
  return JSON.stringify(files);
}

// Fonts shipped with the app (wwwroot/fonts): same JSON format, fetched from this site only.
export async function loadBundledFonts(files) {
  const result = [];
  for (const file of JSON.parse(files)) {
    try {
      const response = await fetch(new URL('fonts/' + file, import.meta.url));
      if (response.ok) result.push(await base64Of(await response.blob()));
    } catch { /* missing file: the family keeps the fallback font */ }
  }
  return JSON.stringify(result);
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
