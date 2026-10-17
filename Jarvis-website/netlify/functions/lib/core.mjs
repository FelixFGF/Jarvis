export const TIME_ZONE = 'Europe/Berlin';

export function berlinDateKey(value) {
  const date = value instanceof Date ? value : new Date(value);
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit'
  }).formatToParts(date);
  const get = (name) => parts.find((part) => part.type === name)?.value ?? '00';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

function shiftDateKey(key, deltaDays) {
  const [year, month, day] = key.split('-').map(Number);
  const shifted = new Date(Date.UTC(year, month - 1, day + deltaDays));
  return `${shifted.getUTCFullYear()}-${String(shifted.getUTCMonth() + 1).padStart(2, '0')}-${String(shifted.getUTCDate()).padStart(2, '0')}`;
}

export function summarizeEvents(inputEvents, nowMs = Date.now()) {
  const now = new Date(nowMs);
  const todayKey = berlinDateKey(now);
  const yesterdayKey = shiftDateKey(todayKey, -1);
  const events = inputEvents
    .filter((event) => event && typeof event.timestamp === 'string' && Number.isFinite(Date.parse(event.timestamp)) && typeof event.version === 'string')
    .map((event) => ({
      eventId: String(event.eventId ?? ''),
      timestamp: new Date(event.timestamp).toISOString(),
      version: event.version,
      date: berlinDateKey(event.timestamp)
    }))
    .sort((a, b) => Date.parse(b.timestamp) - Date.parse(a.timestamp));

  const last7Cutoff = nowMs - 7 * 24 * 60 * 60 * 1000;
  const last30Cutoff = nowMs - 30 * 24 * 60 * 60 * 1000;
  const versionCounts = {};
  for (const event of events) versionCounts[event.version] = (versionCounts[event.version] ?? 0) + 1;

  const chart = [];
  for (let offset = 29; offset >= 0; offset -= 1) {
    const date = shiftDateKey(todayKey, -offset);
    chart.push({ date, count: events.filter((event) => event.date === date).length });
  }

  return {
    totals: {
      all: events.length,
      today: events.filter((event) => event.date === todayKey).length,
      yesterday: events.filter((event) => event.date === yesterdayKey).length,
      last7Days: events.filter((event) => Date.parse(event.timestamp) >= last7Cutoff && Date.parse(event.timestamp) <= nowMs).length,
      last30Days: events.filter((event) => Date.parse(event.timestamp) >= last30Cutoff && Date.parse(event.timestamp) <= nowMs).length
    },
    timeZone: TIME_ZONE,
    today: todayKey,
    chart,
    versionCounts: Object.entries(versionCounts).sort((a, b) => b[1] - a[1]).map(([version, count]) => ({ version, count })),
    recent: events.slice(0, 100),
    eventCount: events.length
  };
}

export function csvEscape(value) {
  let text = String(value ?? '');
  // Neutralize spreadsheet formulas if any untrusted value is ever introduced.
  if (/^[\s]*[=+@-]/.test(text)) text = `'${text}`;
  return `"${text.replaceAll('"', '""')}"`;
}

export function eventsToCsv(inputEvents) {
  const events = inputEvents
    .filter((event) => event && typeof event.timestamp === 'string' && Number.isFinite(Date.parse(event.timestamp)) && typeof event.version === 'string')
    .sort((a, b) => Date.parse(b.timestamp) - Date.parse(a.timestamp));
  const rows = [['Zeitpunkt (UTC)', 'Zeitpunkt (Europe/Berlin)', 'Jarvis-Version', 'Ereignis-ID']];
  const localFormatter = new Intl.DateTimeFormat('de-DE', {
    timeZone: TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
  });
  for (const event of events) {
    rows.push([
      new Date(event.timestamp).toISOString(),
      localFormatter.format(new Date(event.timestamp)),
      event.version,
      event.eventId ?? ''
    ]);
  }
  return `\uFEFF${rows.map((row) => row.map(csvEscape).join(';')).join('\r\n')}\r\n`;
}
