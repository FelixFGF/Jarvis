# Jarvis AI – Download-Tracking und Admin-Dashboard

Diese Erweiterung ergänzt die bestehende statische Jarvis-Webseite um Downloadzählung über Netlify Functions, dauerhafte Ereignisspeicherung via Netlify Blobs, ein Dashboard ohne Login sowie CSV-Export.

## Routen

- `/api/download` – erfasst einen Download-Klick per Hintergrund-POST mit eindeutiger Ereignis-ID; der eigentliche Button-Link geht direkt zu GitHub `latest`.
- `/jv-admin-7x9/` – nicht verlinktes, mit `noindex` markiertes Dashboard.
- `/api/admin/stats` – Statistikdaten als JSON.
- `/api/admin/export` – alle gespeicherten Ereignisse als CSV.

**Es gibt absichtlich keine Anmeldung und keinen Zugangsschutz.** Wer den Dashboard-Pfad oder die API-URLs kennt, kann die Statistiken abrufen. `noindex` ist lediglich eine Bitte an Suchmaschinen und keine Zugriffssperre.

## Entwicklung und Tests

Voraussetzung: Node.js 20 oder neuer.

```sh
npm install
npm test
npm run check
```

Tests für Netlify Blobs nutzen einen In-Memory-Mock. Die echte Persistenz muss nach dem Deployment auf Netlify überprüft werden.

## Deployment

Lies `DEPLOYMENT-DE.md`. Stelle das vollständige Projekt bereit; nur `public/` reicht nicht, da die serverseitigen Functions mitdeployt werden müssen. Netlify Blobs speichert die Ereignisse unabhängig von der statischen Website.
