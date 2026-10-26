# Jarvis Download-Tracking auf Netlify einrichten

## 1. Projekt bereitstellen

1. Entpacke die gesamte ZIP-Datei in einen Projektordner. `netlify.toml`, `package.json`, `public/` und `netlify/functions/` müssen erhalten bleiben.
2. Übernimm die Dateien in das Git-Repository, das mit deiner bestehenden Netlify-Webseite verbunden ist, oder deploye den gesamten Projektordner mit der Netlify CLI. Nur den `public/`-Ordner per Drag-and-drop hochzuladen reicht nicht aus, weil Netlify dann die serverseitigen Functions nicht bereitstellt.
3. Wenn du die Netlify CLI verwenden möchtest, führe im Projektordner aus:

   ```sh
   npm install
   npx netlify login
   npx netlify link
   npx netlify deploy --prod
   ```

   Prüfe bei `netlify link`, dass das vorhandene Projekt `my-jarvisai` ausgewählt ist.

## 2. Admin-Dashboard aufrufen

- Öffentliche Webseite: `https://my-jarvisai.netlify.app/`
- Dashboard: `https://my-jarvisai.netlify.app/jv-admin-7x9/`

Es gibt bewusst **keinen Login und kein Passwort**. Der Adminpfad ist nicht im normalen Seitenmenü verlinkt. Die Dashboard-Seite, die Statistik-API und der CSV-Export senden `noindex`-Hinweise (`X-Robots-Tag` und Meta-Tags), damit Suchmaschinen diese URLs nicht in ihren Index aufnehmen sollen. Das ist keine Garantie: Falls jemand die URL veröffentlicht oder von einer indexierten Seite verlinkt, kann ein Crawler sie trotzdem entdecken. `noindex` hält die Seite nicht geheim und verhindert keinen direkten Zugriff.

**Wichtig:** Weil kein Zugangsschutz vorhanden ist, können alle Personen, die den Pfad oder die API-Adressen kennen, Dashboard, Statistiken und CSV-Export öffnen. Das ist die bewusst gewählte Variante ohne Login.

## 3. Dauerhafte Speicherung und Download-Tracking

Die Netlify Functions sind direkt auf `/api/download`, `/api/admin/stats`, `/api/admin/export` und `/jv-admin-7x9/` geroutet. Netlify Blobs verwendet den Store `jarvis-download-events-v1`; er ist nicht Teil der statischen Dateien und bleibt bei normalen Deployments erhalten.

Alle vier Downloadbuttons zeigen direkt auf `https://github.com/FelixFGF/Jarvis/releases/latest/download/Jarvis-Setup.exe`. Beim Klick sendet das Frontend parallel einen nicht blockierenden POST an `/api/download`; die Function speichert ein einzelnes Ereignis mit UUID, Zeitstempel und dem auf der Webseite angezeigten Versionslabel in Netlify Blobs. Die Navigation zum GitHub-Download wird nicht abgefangen oder verzögert. Bei deaktiviertem JavaScript oder blockierter Tracking-Anfrage funktioniert der direkte Download weiterhin, aber das Ereignis kann ungezählt bleiben. Wiederholungen mit derselben Event-ID werden nicht doppelt gespeichert. Bekannte Crawler-User-Agents werden nicht gezählt. Wenn die Speicherung fehlschlägt, startet der Download dennoch.

Es werden keine erfundenen historischen Daten eingesetzt. Bei einem leeren Store zeigt das Dashboard überall 0 und einen leeren Verlauf.

Gezählt wird ein Download-Aufruf beziehungsweise eine Weiterleitung. Das ist kein Nachweis dafür, dass die Datei vollständig geladen oder installiert wurde. Direkte API-Aufrufe und automatisierte Requests können die Werte beeinflussen; ohne Schutz sind die Daten nicht manipulationssicher.

Tagesgrenzen und Anzeigezeit verwenden `Europe/Berlin`. „Heute“ und „Gestern“ sind Kalendertage; „letzte 7/30 Tage“ sind rollierende Zeitfenster von 7 × 24 beziehungsweise 30 × 24 Stunden. Diagramme und Versionsverteilung beruhen nur auf gespeicherten Ereignissen. CSV wird über `/api/admin/export` geladen.

## 4. Jarvis-Version aktualisieren

Die Website zeigt aktuell Version `1.1.1` an. Beim Wechsel auf eine neue Version aktualisiere die Versionsangaben der Webseite (`data-jarvis-version` und `version=...` an den Downloadlinks in `public/index.html`) gemeinsam. Optional kann `JARVIS_PUBLIC_VERSION` im Netlify-Dashboard als Environment Variable gesetzt werden; ohne diese Variable wird die im Function-Code hinterlegte Fallback-Version verwendet.

## 5. Kosten, Limits und Grenzen

Die Implementierung nutzt Netlify Functions und Netlify Blobs ohne zusätzlichen Datenbankanbieter. Bitte kontrolliere im Netlify-Dashboard unter **Usage & billing** die für dein Team geltenden Kontingente und Verbrauchswerte: verfügbare Credits und Preisregeln können je nach Konto/Tarif abweichen. Ich kann aus dem ZIP nicht den aktuellen Tarif oder die Restkontingente deines Netlify-Kontos auslesen.

Die Lösung eignet sich für eine kleine Website mit moderater Ereigniszahl. Für sehr hohe Downloadzahlen kann das Lesen aller Ereignisse zur Laufzeit langsamer werden. Außerdem ist das Dashboard ausdrücklich öffentlich zugänglich, solange kein Zugangsschutz eingerichtet wird.

## 6. Lokal testen

Voraussetzung: Node.js 20 oder neuer.

```sh
npm install
npm test
npm run check
```

Die automatisierten Tests verwenden einen In-Memory-Test-Doppel für Netlify Blobs. Die tatsächliche Blobs-Persistenz und die Routing-Konfiguration müssen nach dem Deploy auf Netlify geprüft werden.

## 7. Fehlerdiagnose

- **Dashboard öffnet sich nicht:** Im Netlify Deploy-Log kontrollieren, ob die Functions erkannt und gebaut wurden.
- **Dashboard zeigt einen Ladefehler:** Logs der Function `admin-stats` prüfen und kontrollieren, ob Netlify Blobs verfügbar ist.
- **Download funktioniert, aber eine Zählung fehlt:** Logs der Function `download` prüfen. Das Tracking ist so ausgelegt, dass eine Speicherausnahme den Installer nicht blockiert.
- **Deploy schlägt fehl:** Prüfe die Build-Logs und ob `@netlify/blobs` installiert wurde.

Netlify-Dokumentation:
- Blobs: https://docs.netlify.com/build/data-and-storage/netlify-blobs/
- Preise und Credits: https://docs.netlify.com/manage/accounts-and-billing/billing/billing-for-credit-based-plans/credit-based-pricing-plans/
- Functions: https://docs.netlify.com/build/functions/overview/
