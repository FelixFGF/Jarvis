import { readAllEvents, securityHeaders } from './lib/security.mjs';
import { eventsToCsv } from './lib/core.mjs';

export default async (request) => {
  if (request.method !== 'GET') return new Response('Method not allowed', { status: 405, headers: securityHeaders({ Allow: 'GET' }) });
  try {
    const events = await readAllEvents();
    const csv = eventsToCsv(events);
    return new Response(csv, {
      status: 200,
      headers: securityHeaders({
        'Content-Type': 'text/csv; charset=utf-8',
        'Content-Disposition': 'attachment; filename="jarvis-download-statistik.csv"'
      })
    });
  } catch (error) {
    console.error('[jarvis-admin-export] CSV export unavailable.', error?.name || 'Error');
    return new Response('Export vorübergehend nicht verfügbar.', { status: 503, headers: securityHeaders({ 'Content-Type': 'text/plain; charset=utf-8' }) });
  }
};

export const config = { path: "/api/admin/export", method: ["GET"] };
