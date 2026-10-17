import { getStore } from '@netlify/blobs';

export function securityHeaders(extra = {}) {
  return {
    'Cache-Control': 'no-store, private, max-age=0',
    'Pragma': 'no-cache',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'X-Frame-Options': 'DENY',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
    'X-Robots-Tag': 'noindex, nofollow, noarchive, nosnippet',
    ...extra
  };
}

export function jsonResponse(data, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: securityHeaders({ 'Content-Type': 'application/json; charset=utf-8', ...extraHeaders })
  });
}

export function htmlResponse(html, status = 200) {
  return new Response(html, {
    status,
    headers: securityHeaders({ 'Content-Type': 'text/html; charset=utf-8' })
  });
}

export async function readAllEvents() {
  const store = getStore({ name: 'jarvis-download-events-v1', consistency: 'strong' });
  const { blobs } = await store.list({ prefix: 'events/' });
  const records = [];
  const batchSize = 16;
  for (let index = 0; index < blobs.length; index += batchSize) {
    const batch = blobs.slice(index, index + batchSize);
    const values = await Promise.all(batch.map(async ({ key }) => {
      try { return await store.get(key, { type: 'json', consistency: 'strong' }); }
      catch { return null; }
    }));
    for (const value of values) {
      if (value && typeof value.eventId === 'string' && typeof value.timestamp === 'string' && typeof value.version === 'string') records.push(value);
    }
  }
  return records;
}
