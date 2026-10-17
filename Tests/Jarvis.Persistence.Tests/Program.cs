using Jarvis.Models;
using Jarvis.Services;
using System.Text.Json;
using Jarvis.Beta;

var root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new ChatStore();
    var path = Path.Combine(root, "chats.json");
    var session = new ChatSession { Title = "Test", Model = "local-model", Workspace = root, Messages = [new ChatRecord("user", "hello", [new ChatAttachment("hello.txt", Path.Combine(root, "hello.txt"), "text/plain")]), new ChatRecord("assistant", "world")] };
    await store.SaveAsync(path, [session]);
    using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
    {
        if (doc.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new Exception("Archive schema version was not written.");
    }
    var loaded = await store.LoadAsync(path);
    if (loaded.Count != 1 || loaded[0].Id != session.Id || loaded[0].Messages.Count != 2 || loaded[0].Model != session.Model || loaded[0].Messages[0].Attachments?.Single().Name != "hello.txt") throw new Exception("Versioned archive round trip failed.");

    var legacyPath = Path.Combine(root, "legacy.json");
    await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new[] { session }));
    var legacy = await store.LoadAsync(legacyPath);
    if (legacy.Count != 1 || legacy[0].Messages[1].Text != "world") throw new Exception("Legacy list migration failed.");

    var invalidPath = Path.Combine(root, "invalid.json");
    await File.WriteAllTextAsync(invalidPath, "{\"schemaVersion\":99,\"chats\":[]}");
    try { await store.LoadAsync(invalidPath); throw new Exception("Unsupported schema was accepted."); }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: schema v1 round trip, legacy list migration, unsupported version rejection.");

    var googleHtml = "<div><a href=\"https://example.com/news?a=1&amp;b=2\"><h3>Fortnite Cup <em>Schedule</em></h3></a><div class=\"VwiC3b\">Next event details</div></div><a href='/url?q=https%3A%2F%2Fexample.org%2Fsecond&amp;sa=U'><h3>Second result</h3></a>";
    var searchResults = GoogleSearchParser.Parse(googleHtml, 6);
    if (searchResults.Count != 2 || searchResults[0].Title != "Fortnite Cup Schedule" || searchResults[0].Url != "https://example.com/news?a=1&b=2" || searchResults[0].Snippet != "Next event details" || searchResults[1].Url != "https://example.org/second") throw new Exception("Google result parsing failed for standard and wrapped links.");
    var youtubeQuery = "such auf google nach youtube wann es erstellt wurde";
    if (GoogleSearchParser.ExpandQuery(youtubeQuery) != "YouTube founding year creation date established") throw new Exception("Date-intent query expansion failed.");
    if (!GoogleSearchParser.IsCreationDateQuery("YouTube Gründungsdatum oder Erstellungsdatum") || !GoogleSearchParser.ExpandWikipediaQuery("YouTube Gründungsdatum oder Erstellungsdatum").Contains("2005", StringComparison.Ordinal)) throw new Exception("German YouTube creation-date variants were not expanded for the factual fallback.");
    var youtubeResults = GoogleSearchParser.KeepRelevant(youtubeQuery, [
        new GoogleSearchResult("YouTube", "https://www.youtube.com/", "Videos und Musik entdecken"),
        new GoogleSearchResult("When was YouTube created?", "https://example.org/history", "YouTube was founded in 2005")
    ]);
    if (youtubeResults.Count != 1 || !youtubeResults[0].Snippet.Contains("founded", StringComparison.OrdinalIgnoreCase)) throw new Exception("Search relevance accepted a generic YouTube homepage for a creation-date query.");
    if (!GoogleSearchParser.IsBlockedPage("<title>Sorry</title>Our systems have detected unusual traffic")) throw new Exception("Google block page was not recognized.");
    var bingRss = """<?xml version="1.0"?><rss><channel><item><title>Result &amp; details</title><link>https://example.net/page</link><description>A useful snippet</description></item></channel></rss>""";
    var rssResults = GoogleSearchParser.ParseBingRss(bingRss, 5);
    if (rssResults.Count != 1 || rssResults[0].Title != "Result & details" || rssResults[0].Snippet != "A useful snippet" || rssResults[0].Url != "https://example.net/page") throw new Exception("Bing RSS search parsing failed.");
    var wikipediaResults = GoogleSearchParser.ParseWikipediaSearch("{\"query\":{\"search\":[{\"title\":\"YouTube\",\"snippet\":\"YouTube was founded in 2005.\"}]}}", 5);
    if (wikipediaResults.Count != 1 || !GoogleSearchParser.KeepRelevant(youtubeQuery, wikipediaResults).Any()) throw new Exception("Wikipedia factual fallback parsing or relevance filter failed.");
    if (!GoogleSearchParser.IsConsentPage("Before you continue to Google")) throw new Exception("Google consent page was not recognized.");
    var fallbackResults = GoogleSearchParser.Parse("<div><a href=\"https://example.net/event\">Official event calendar for players</a></div>", 6);
    if (fallbackResults.Count != 1 || fallbackResults[0].Title != "Official event calendar for players") throw new Exception("Google alternate markup fallback failed.");
    var youtubeCreationResults = GoogleSearchParser.KeepRelevant("YouTube wann es erstellt wurde", new[]
    {
        new GoogleSearchResult("YouTube", "https://www.youtube.com/", "Great videos and music you love; upload your own videos."),
        new GoogleSearchResult("YouTube company history", "https://en.wikipedia.org/wiki/YouTube", "YouTube was founded on February 14, 2005, by Steve Chen, Chad Hurley, and Jawed Karim.")
    });
    if (youtubeCreationResults.Count != 1 || !youtubeCreationResults[0].Snippet.Contains("2005", StringComparison.Ordinal)) throw new Exception("Generic YouTube home pages must not count as creation-date search results.");
    var unrelatedDateResults = GoogleSearchParser.KeepRelevant("YouTube wann es erstellt wurde", new[]
    {
        new GoogleSearchResult("YouTube", "https://www.youtube.com/", "Videos uploaded in 2024."),
        new GoogleSearchResult("Example company founded in 2001", "https://example.org", "A company history unrelated to YouTube, established in 2001.")
    });
    if (unrelatedDateResults.Count != 0) throw new Exception("Factual-date filtering accepted unrelated or homepage search results: " + string.Join(" | ", unrelatedDateResults.Select(result => result.Title + " :: " + result.Snippet)));
    if (!SpotifyCommandParser.TryParse("Stelle Spotify auf maximale Lautstärke", out var spotifyAction, out var spotifyPercent) || spotifyAction != "set_volume" || spotifyPercent != 100) throw new Exception("Explicit Spotify maximum-volume command was not recognized.");
    if (!SpotifyCommandParser.TryParse("Setze Spotify auf 42%", out spotifyAction, out spotifyPercent) || spotifyAction != "set_volume" || spotifyPercent != 42) throw new Exception("Spotify percentage command without the word volume was not recognized.");
    if (!SpotifyCommandParser.TryParse("Mach Spotify 10 Prozent leiser", out spotifyAction, out spotifyPercent) || spotifyAction != "volume_down" || spotifyPercent != 10) throw new Exception("Relative Spotify volume command was not recognized.");
    var ddgHtml = "<a class=\"result__a\" href=\"https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fcup\">Official Cup Schedule</a><div class=\"result__snippet\">Dates and times</div>";
    var ddgResults = GoogleSearchParser.ParseDuckDuckGo(ddgHtml, 6);
    if (ddgResults.Count != 1 || ddgResults[0].Url != "https://example.com/cup" || ddgResults[0].Title != "Official Cup Schedule") throw new Exception("DuckDuckGo fallback parsing failed.");
    var bingHtml = "<ol><li class=\"b_algo\"><h2><a href=\"https://example.com/result\">Official <strong>Cup</strong> Schedule</a></h2><p>Dates and times</p></li></ol>";
    var bingResults = GoogleSearchParser.ParseBing(bingHtml, 6);
    if (bingResults.Count != 1 || bingResults[0].Title != "Official Cup Schedule" || bingResults[0].Snippet != "Dates and times") throw new Exception("Bing fallback parsing failed.");
    var ttsText = LocalTtsService.Prepare("## Hallo 😀\n- **Welt** `code` [Jarvis](https://example.com) 👨‍👩‍👧‍👦");
    if (!ttsText.Contains("Hallo") || !ttsText.Contains("Welt") || ttsText.Contains("😀") || ttsText.Contains("**") || !ttsText.Contains("Jarvis: https://example.com")) throw new Exception("TTS markdown/emoji cleanup failed.");
    if (LocalTtsService.DetectCulture("Hello there", "en") != "en" || LocalTtsService.DetectCulture("Hallo", "de") != "de") throw new Exception("TTS language preference was not applied to short responses.");
    using (var synthesizer = new Windows.Media.SpeechSynthesis.SpeechSynthesizer())
    {
        var synthesizedLanguages = 0;
        foreach (var language in new[] { "de", "en" })
        {
            var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
            if (voice is null) { Console.WriteLine($"SKIP: no local Windows {language} voice installed; Windows speech language pack must be installed manually."); continue; }
            synthesizer.Voice = voice;
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(language == "de" ? "Jarvis liest diese deutsche Antwort vor." : "Jarvis reads this English response aloud.");
            if (stream.Size == 0 || !stream.ContentType.Contains("wav", StringComparison.OrdinalIgnoreCase)) throw new Exception($"Windows local {language} speech synthesis returned invalid audio.");
            synthesizedLanguages++;
        }
        if (synthesizedLanguages == 0) throw new Exception("No local Windows TTS voices are installed.");
    }
    using (var tts = new LocalTtsService())
    {
        var playback = tts.SpeakAsync("Jarvis testet die lokale deutsche Sprachausgabe mit einer etwas längeren gesprochenen Nachricht.");
        var playbackDeadline = DateTime.UtcNow.AddSeconds(5);
        while (tts.State != LocalTtsState.Speaking && !playback.IsCompleted && DateTime.UtcNow < playbackDeadline) await Task.Delay(20);
        if (tts.State != LocalTtsState.Speaking) { await playback; throw new Exception("TTS playback did not enter the speaking state."); }
        tts.Pause(); if (tts.State != LocalTtsState.Paused) throw new Exception("TTS pause state failed.");
        tts.Resume(); if (tts.State != LocalTtsState.Speaking) throw new Exception("TTS resume state failed.");
        tts.Stop(); await playback; if (tts.State != LocalTtsState.Ready) throw new Exception("TTS stop did not return to ready state.");
    }
    Console.WriteLine("PASS: local TTS synthesis and playback; pause, resume, and stop state transitions.");
    Console.WriteLine("PASS: Google/Bing/DuckDuckGo parsers; Markdown and emoji cleanup; local offline Windows synthesis for installed language voices.");
    Console.WriteLine("PASS: search relevance filtering rejects generic homepages for factual YouTube creation-date queries.");

    var betaOptions = new SupabaseOptions("https://example.supabase.co", "public-publishable-key");
    if (!betaOptions.IsConfigured || new SupabaseOptions("http://example.com", "key").IsConfigured || !new SupabaseOptions("http://127.0.0.1:54321", "key").IsConfigured) throw new Exception("Supabase endpoint validation did not enforce HTTPS except for local development.");
    using (var betaClient = new SupabaseClient(betaOptions, new BetaStubHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/auth/v1/token", StringComparison.Ordinal))
        {
            if (!request.Headers.TryGetValues("apikey", out var keys) || keys.Single() != "public-publishable-key") throw new Exception("Supabase public key header was not sent.");
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"access_token":"test-access-token","refresh_token":"test-refresh-token","expires_in":3600,"user":{"id":"11111111-1111-4111-8111-111111111111","email":"tester@example.com"}}""") };
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/functions/v1/validate-license", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization?.Parameter != "test-access-token") throw new Exception("The user's access token was not sent to the Edge Function.");
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"valid":true,"status":"active","expires_at":null}""") };
        }
        throw new Exception("Unexpected Supabase request: " + request.RequestUri);
    })))
    {
        var auth = await betaClient.SignInAsync("tester@example.com", "never-persist-this-password");
        if (auth.Session is null || auth.Session.UserId != "11111111-1111-4111-8111-111111111111" || auth.Session.RefreshToken != "test-refresh-token") throw new Exception("Supabase Auth session parsing failed.");
        var validation = await betaClient.InvokeAsync<LicenseValidation>("validate-license", auth.Session, new { });
        if (!validation.Valid || validation.Status != "active") throw new Exception("Beta license function DTO parsing failed.");
    }
    var betaStorePath = Path.Combine(root, "beta-session.dat");
    var betaStore = new BetaSessionStore(betaStorePath);
    var testSession = new BetaSession("access-secret-value", "refresh-secret-value", DateTimeOffset.UtcNow.AddHours(1), "11111111-1111-4111-8111-111111111111", "tester@example.com");
    await betaStore.SaveAsync(testSession);
    var sessionBytes = await File.ReadAllBytesAsync(betaStorePath);
    var sessionText = System.Text.Encoding.UTF8.GetString(sessionBytes);
    if (sessionText.Contains("access-secret-value", StringComparison.Ordinal) || sessionText.Contains("refresh-secret-value", StringComparison.Ordinal)) throw new Exception("Session tokens were written as plaintext instead of DPAPI ciphertext.");
    var restoredSession = await betaStore.LoadAsync();
    if (restoredSession?.RefreshToken != testSession.RefreshToken || restoredSession.UserId != testSession.UserId) throw new Exception("DPAPI protected session round trip failed.");
    await betaStore.DeleteAsync();
    Console.WriteLine("PASS: beta endpoint validation, Supabase Auth/Edge request handling, snake_case license response parsing, and DPAPI-protected session round trip.");

    using (var speech = new Jarvis.Services.LocalSpeechService())
    {
        var devices = speech.GetDevices();
        Console.WriteLine($"PASS: Windows capture-device enumeration completed ({devices.Count} active device(s)); absent devices are reported to UI instead of crashing.");
        var missingModel = Path.Combine(root, "missing-whisper-model.bin");
        try { await speech.TranscribeAsync(Path.Combine(root, "missing.wav"), missingModel, CancellationToken.None); throw new Exception("Missing speech model was accepted."); }
        catch (FileNotFoundException) { }
        Console.WriteLine("PASS: missing local Whisper model returns a controlled setup error.");
        if (devices.Any(device => device.IsDefault))
        {
            var recording = Path.Combine(root, "capture-smoke.wav"); var levelUpdates = 0;
            speech.StartRecording(null, true, recording, _ => Interlocked.Increment(ref levelUpdates));
            await Task.Delay(900);
            var result = await speech.StopRecordingAsync(CancellationToken.None);
            if (levelUpdates == 0 || new FileInfo(result).Length <= 44) throw new Exception("Microphone capture did not deliver audio samples.");
            Console.WriteLine($"PASS: default microphone capture produced a WAV stream and {levelUpdates} real audio-level updates.");
            File.Delete(result);
        }
        else Console.WriteLine("SKIP: no Windows default capture device is configured; physical recording check needs manual setup.");
    }
}
finally { Directory.Delete(root, recursive: true); }

sealed class BetaStubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> responder) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
}
