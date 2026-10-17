import { jsonResponse, readAllEvents } from './lib/security.mjs';
import { summarizeEvents } from './lib/core.mjs';

export default async (request) => {
  if (request.method !== 'GET') return jsonResponse({ error: 'Method not allowed' }, 405, { Allow: 'GET' });
  try {
    const events = await readAllEvents();
    return jsonResponse(summarizeEvents(events));
  } catch (error) {
    console.error('[jarvis-admin-stats] Statistics unavailable.', error?.name || 'Error');
    return jsonResponse({ error: 'Statistiken sind vorübergehend nicht verfügbar.' }, 503);
  }
};

export const config = { path: "/api/admin/stats", method: ["GET"] };
