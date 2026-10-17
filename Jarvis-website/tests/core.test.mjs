import test from 'node:test';
import assert from 'node:assert/strict';
import { berlinDateKey, summarizeEvents, eventsToCsv } from '../netlify/functions/lib/core.mjs';


test('Berlin date keys account for Europe/Berlin daylight-saving offset', () => {
  assert.equal(berlinDateKey('2026-10-15T22:30:00Z'), '2026-10-16');
  assert.equal(berlinDateKey('2026-10-15T20:30:00Z'), '2026-10-15');
});

test('empty stats are all zero and preserve the full 30-day chart shape', () => {
  const stats = summarizeEvents([], Date.parse('2026-10-15T10:00:00Z'));
  assert.deepEqual(stats.totals, { all: 0, today: 0, yesterday: 0, last7Days: 0, last30Days: 0 });
  assert.equal(stats.chart.length, 30);
  assert.ok(stats.chart.every((point) => point.count === 0));
  assert.deepEqual(stats.versionCounts, []);
  assert.deepEqual(stats.recent, []);
});

test('stats count calendar today/yesterday and rolling windows without inventing events', () => {
  const now = Date.parse('2026-10-15T10:00:00Z');
  const events = [
    { eventId: 'a', timestamp: '2026-10-15T09:00:00Z', version: '1.0.6' },
    { eventId: 'b', timestamp: '2026-10-14T12:00:00Z', version: '1.0.5' },
    { eventId: 'c', timestamp: '2026-10-01T09:00:00Z', version: '1.0.6' },
    { eventId: 'bad', timestamp: 'not-a-date', version: '1.0.6' }
  ];
  const stats = summarizeEvents(events, now);
  assert.deepEqual(stats.totals, { all: 3, today: 1, yesterday: 1, last7Days: 2, last30Days: 3 });
  assert.deepEqual(stats.versionCounts, [{ version: '1.0.6', count: 2 }, { version: '1.0.5', count: 1 }]);
  assert.equal(stats.recent[0].eventId, 'a');
});

test('CSV is semicolon-delimited and escapes formula-like fields and quotes', () => {
  const csv = eventsToCsv([{ eventId: 'id"x', timestamp: '2026-10-15T10:00:00Z', version: '=1+1' }]);
  assert.ok(csv.startsWith('\uFEFF"Zeitpunkt (UTC)";'));
  assert.ok(csv.includes("'=1+1"));
  assert.ok(csv.includes('id""x'));
});
