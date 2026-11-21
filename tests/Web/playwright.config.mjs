// Browser tests of the published static site (scripts/build-web.ps1 -> artifacts/web).
// NEXOLUME_WEB_DIR overrides the folder; NEXOLUME_BROWSERS=chromium,firefox,webkit picks engines;
// NEXOLUME_CHANNEL=msedge|chrome runs the chromium project in an installed browser instead of
// Playwright's own download (npx playwright install chromium).
import { defineConfig, devices } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const repo = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const site = process.env.NEXOLUME_WEB_DIR ?? resolve(repo, 'artifacts/web');
const port = Number(process.env.NEXOLUME_WEB_PORT ?? 8181);
const wanted = (process.env.NEXOLUME_BROWSERS ?? 'chromium').split(',').map((name) => name.trim());
const all = {
  chromium: devices['Desktop Chrome'],
  firefox: devices['Desktop Firefox'],
  webkit: devices['Desktop Safari'],
};

export default defineConfig({
  testDir: './e2e',
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${port}/`,
    viewport: { width: 1400, height: 900 },
    acceptDownloads: true,
    trace: 'retain-on-failure',
  },
  projects: wanted.map((name) => ({
    name,
    // The device presets carry their own 1280x720 viewport: keep the tests' 1400x900 layout.
    use: { ...all[name], viewport: { width: 1400, height: 900 }, ...(name === 'chromium' && process.env.NEXOLUME_CHANNEL ? { channel: process.env.NEXOLUME_CHANNEL } : {}) },
  })),
  webServer: {
    command: `node "${resolve(repo, 'scripts/serve-web.mjs')}" "${site}" ${port}`,
    url: `http://localhost:${port}/`,
    reuseExistingServer: false,
    timeout: 30_000,
  },
});
