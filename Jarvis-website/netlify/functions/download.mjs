import { randomUUID } from 'node:crypto';
import { getStore } from '@netlify/blobs';
import { securityHeaders } from './lib/security.mjs';

const DEFAULT_VERSION = process.env.JARVIS_PUBLIC_VERSION || '1.0.7';
const VERSION_PATTERN = /^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/;
const UUID_V4_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export default async (request) => {
  if (request.method !== 'POST') {
    return new Response('Method not allowed', { status: 405, headers: securityHeaders({ Allow: 'POST' }) });
  }

  const url = new URL(request.url);
  const suppliedVersion = url.searchParams.get('version');
  const version = DEFAULT_VERSION;
  // The publicly supplied query parameter must match the server-configured release label.
  // This prevents callers from polluting version charts with arbitrary version strings.
  if (!VERSION_PATTERN.test(version) || version.length > 64 || (suppliedVersion && suppliedVersion !== version)) {
    return new Response('Invalid version', { status: 400, headers: securityHeaders() });
  }
  const providedId = url.searchParams.get('eventId') || '';
  const eventId = UUID_V4_PATTERN.test(providedId) ? providedId.toLowerCase() : randomUUID();
  const record = { eventId, timestamp: new Date().toISOString(), version, eventType: 'download_click' };
  const userAgent = request.headers.get('user-agent') || '';
  const likelyBot = /googlebot|bingbot|duckduckbot|baiduspider|yandexbot|slurp|crawler|spider|facebookexternalhit|twitterbot|slackbot|discordbot|telegrambot|whatsapp|applebot|ahrefsbot|semrushbot/i.test(userAgent);

  try {
    if (!likelyBot) {
      const store = getStore({ name: 'jarvis-download-events-v1', consistency: 'strong' });
      // A request retry with the same eventId cannot create a second record.
      await store.setJSON(`events/${eventId}`, record, { onlyIfNew: true });
    }
  } catch (error) {
    // Tracking errors must not affect the direct GitHub download in the browser.
    console.error('[jarvis-download-tracking] Persistence failed; direct download continues.', error?.name || 'Error');
  }

  return new Response(null, {
    status: 204,
    headers: securityHeaders({
      'Cache-Control': 'no-store, private, max-age=0'
    })
  });
};

export const config = { path: "/api/download", method: ["POST"] };
