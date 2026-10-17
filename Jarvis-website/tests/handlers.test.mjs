import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

process.env.JARVIS_PUBLIC_VERSION = '1.0.6';

const { default: download } = await import('../netlify/functions/download.mjs');
const { default: adminUi } = await import('../netlify/functions/admin-ui.mjs');
const { default: adminStats } = await import('../netlify/functions/admin-stats.mjs');
const { default: adminExport } = await import('../netlify/functions/admin-export.mjs');

const origin = 'https://my-jarvisai.netlify.app';
const eventId = 'aa3fcb5d-573e-48c2-bc6d-2f7341332a11';
function request(path, options = {}) { return new Request(`${origin}${path}`, options); }

await test('all visible download buttons link directly to the requested GitHub latest asset', async () => {
  const html = await readFile(new URL('../public/index.html', import.meta.url), 'utf8');
  const downloadLinks = [...html.matchAll(/<a\b[^>]*class="[^"]*jarvis-download[^"]*"[^>]*>/g)].map(([tag]) => tag);
  assert.equal(downloadLinks.length, 4);
  for (const tag of downloadLinks) {
    assert.match(tag, /href="https:\/\/github\.com\/FelixFGF\/Jarvis\/releases\/latest\/download\/Jarvis-Setup\.exe"/);
    assert.doesNotMatch(tag, /href="\/api\/download/);
  }
  const script = await readFile(new URL('../public/script.js', import.meta.url), 'utf8');
  assert.match(script, /navigator\.sendBeacon/);
  assert.doesNotMatch(script, /event\.preventDefault\(\)/);
});

await test('public admin dashboard and handlers using an in-memory Netlify Blobs API stub', async (t) => {
  await t.test('hidden admin route serves the dashboard directly without a login form', async () => {
    const response = await adminUi(request('/jv-admin-7x9/'));
    const html = await response.text();
    assert.equal(response.status, 200);
    assert.match(html, /Download-Statistik/);
    assert.match(html, /id="total"/);
    assert.doesNotMatch(html, /login-form|Admin-Passwort|Abmelden/);
    assert.match(html, /name="robots" content="noindex/);
    assert.match(response.headers.get('x-robots-tag'), /noindex/);
  });

  await t.test('stats and CSV are intentionally available without a session', async () => {
    const stats = await adminStats(request('/api/admin/stats'));
    assert.equal(stats.status, 200);
    assert.match(stats.headers.get('x-robots-tag'), /noindex/);
    const data = await stats.json();
    assert.equal(data.totals.all, 0);
    assert.equal(data.totals.today, 0);

    const csv = await adminExport(request('/api/admin/export'));
    assert.equal(csv.status, 200);
    assert.match(csv.headers.get('content-type'), /text\/csv/);
    assert.match(await csv.text(), /Jarvis-Version/);
  });

  await t.test('download click beacon records once for an idempotency UUID and does not redirect', async () => {
    const path = `/api/download?version=1.0.6&eventId=${eventId}`;
    const makeRequest = () => request(path, { method: 'POST', headers: { 'user-agent': 'Mozilla/5.0 Chrome/130.0' } });
    const first = await download(makeRequest());
    const retry = await download(makeRequest());
    assert.equal(first.status, 204);
    assert.equal(first.headers.get('location'), null);
    assert.equal(retry.status, 204);
    const statsResponse = await adminStats(request('/api/admin/stats'));
    const data = await statsResponse.json();
    assert.equal(data.totals.all, 1);
    assert.equal(data.recent[0].eventId, eventId);
    assert.equal(data.versionCounts[0].version, '1.0.6');
  });

  await t.test('invalid version returns 400; known crawler click beacon is not counted and GET is disabled', async () => {
    const invalid = await download(request('/api/download?version=%3D1%2B1', { method: 'POST' }));
    const forgedVersion = await download(request('/api/download?version=9.9.9&eventId=cc3fcb5d-573e-48c2-bc6d-2f7341332a12', { method: 'POST' }));
    assert.equal(invalid.status, 400);
    assert.equal(forgedVersion.status, 400);
    const bot = await download(request('/api/download?version=1.0.6&eventId=bb3fcb5d-573e-48c2-bc6d-2f7341332a12', { method: 'POST', headers: { 'user-agent': 'Googlebot/2.1' } }));
    assert.equal(bot.status, 204);
    const oldMethod = await download(request('/api/download?version=1.0.6'));
    assert.equal(oldMethod.status, 405);
  });
});
