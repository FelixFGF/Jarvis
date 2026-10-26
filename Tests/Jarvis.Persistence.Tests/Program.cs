using Jarvis.Models;
using Jarvis.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Jarvis;
using Jarvis.Beta;

if (args.Contains("--live-search", StringComparer.Ordinal))
{
    foreach (var query in new[] { "official Fabric documentation", "Minecraft Fabric 1.21.1 hotbar color bug", "google wie man einen minecraft mod bug fixt für crash bei hotbar colors", "Minecraft shader screenshots" })
    {
        using var service = new WebResearchService(providerTimeout: TimeSpan.FromSeconds(15));
        var effectiveQuery = WebResearchIntentParser.TryParse(query, out var routedQuery) ? routedQuery : query;
        var result = await service.SearchAsync(effectiveQuery, 8, CancellationToken.None);
        Console.WriteLine($"WEB query={query} final={result.Provider} retained={result.Results.Count}");
        foreach (var attempt in result.Attempts) Console.WriteLine($"  {attempt.Provider}: outcome={attempt.Outcome} http={attempt.HttpStatus?.ToString() ?? "n/a"} bytes={attempt.RawBytes} raw={attempt.RawCount} parsed={attempt.ParsedCount} retained={attempt.RetainedCount} durationMs={attempt.DurationMs} rejected={attempt.RejectionReason}");
    }
    foreach (var query in new[] { "Minecraft logo", "official Minecraft logo", "Minecraft shader screenshots", "Complementary Reimagined shaders", "Minecraft landscape" })
    {
        using var service = new ImageSearchService();
        var result = await service.SearchDetailedAsync(query, 8, CancellationToken.None);
        Console.WriteLine($"IMAGE query={query} outcome={result.Outcome} retained={result.Results.Count}");
        foreach (var attempt in result.Attempts) Console.WriteLine($"  {attempt.Provider}: outcome={attempt.Outcome} http={attempt.HttpStatus?.ToString() ?? "n/a"} raw={attempt.RawCount} parsed={attempt.ParsedCount} retained={attempt.RetainedCount}");
    }
    return;
}

var root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    SpeechAudioChecks.Run(root);
    var invalidModel = Path.Combine(root, "invalid-model.bin");
    await File.WriteAllBytesAsync(invalidModel, "not-a-model"u8.ToArray());
    try { LocalSpeechService.ValidateDownloadedModel(invalidModel, "tiny"); throw new Exception("Invalid Whisper download was accepted."); }
    catch (InvalidDataException) { }
    var validModel = Path.Combine(root, "valid-model.bin");
    await using (var stream = File.Create(validModel)) { await stream.WriteAsync(new byte[] { 0x6C, 0x6D, 0x67, 0x67 }); stream.SetLength(20L * 1024 * 1024); }
    LocalSpeechService.ValidateDownloadedModel(validModel, "tiny");
    Console.WriteLine("PASS: Whisper downloads require plausible size and GGML header before installation.");
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

    var presentationNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
    var xamlNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
    var appXaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "App.xaml"));
    var comboStyle = appXaml.Descendants(presentationNs + "Style").Single(element => (string?)element.Attribute("TargetType") == "ComboBox");
    var comboTemplate = comboStyle.Descendants(presentationNs + "ControlTemplate").First();
    var dropdownToggle = comboTemplate.Descendants(presentationNs + "ToggleButton").Single(element => (string?)element.Attribute(xamlNs + "Name") == "DropDownToggle");
    var dropdownPopup = comboTemplate.Descendants(presentationNs + "Popup").Single(element => (string?)element.Attribute(xamlNs + "Name") == "PART_Popup");
    if (!((string?)dropdownToggle.Attribute("IsChecked"))!.Contains("IsDropDownOpen", StringComparison.Ordinal) ||
        !((string?)dropdownToggle.Attribute("IsChecked"))!.Contains("Mode=TwoWay", StringComparison.Ordinal) ||
        !((string?)dropdownPopup.Attribute("IsOpen"))!.Contains("IsDropDownOpen", StringComparison.Ordinal) ||
        !((string?)dropdownPopup.Attribute("IsOpen"))!.Contains("Mode=TwoWay", StringComparison.Ordinal) ||
        !dropdownPopup.Descendants(presentationNs + "ItemsPresenter").Any(element => (string?)element.Attribute(xamlNs + "Name") == "ItemsPresenter"))
        throw new Exception("The custom ComboBox template does not wire its toggle, popup, and item host for dropdown interaction.");
    var selectedPresenter = dropdownToggle.Descendants(presentationNs + "ContentPresenter").Single();
    if (!((string?)selectedPresenter.Attribute("Content"))!.Contains("SelectedItem", StringComparison.Ordinal)
        || !((string?)selectedPresenter.Attribute("Content"))!.Contains("ComboBoxSelectionConverter", StringComparison.Ordinal))
        throw new Exception("The closed ComboBox still reuses a selected item container and can retain stale overlapping labels.");
    var mainWindowXaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
    var fallbackSteps = mainWindowXaml.Descendants(presentationNs + "ListBox").Single(element => (string?)element.Attribute(xamlNs + "Name") == "FallbackSteps");
    if (!fallbackSteps.Descendants(presentationNs + "Expander").Any() || !fallbackSteps.Descendants(presentationNs + "ComboBox").Any(element => (string?)element.Attribute("ToolTip") == "{Binding Model}"))
        throw new Exception("The fallback editor is not a responsive step-card list with accessible long model names and advanced settings.");
    var chatModelBox = mainWindowXaml.Descendants(presentationNs + "ComboBox").Single(element => (string?)element.Attribute(xamlNs + "Name") == "ChatModelBox");
    if ((string?)chatModelBox.Attribute("SelectedValuePath") != "Id" || (string?)chatModelBox.Attribute("SelectionChanged") != "ChatModel_SelectionChanged")
        throw new Exception("The chat model selector is missing its model ID selection binding or persistence handler.");
    var settingsModelBox = mainWindowXaml.Descendants(presentationNs + "ComboBox").Single(element => (string?)element.Attribute(xamlNs + "Name") == "ModelBox");
    if ((string?)settingsModelBox.Attribute("SelectedValuePath") != "Id" || (string?)settingsModelBox.Attribute("SelectionChanged") != "SettingsModel_SelectionChanged" ||
        mainWindowXaml.Descendants(presentationNs + "TextBox").Any(element => (string?)element.Attribute(xamlNs + "Name") == "ModelBox"))
        throw new Exception("The provider settings model selector is not a model-ID ComboBox with its settings persistence handler.");
    if (!mainWindowXaml.Descendants(presentationNs + "Button").Any(element => (string?)element.Attribute("Click") == "ModelPrompt_Click" && (string?)element.Attribute("Content") == "Manuell…"))
        throw new Exception("The provider settings model selector no longer offers manual model-ID entry.");
    var mainWindowSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs"));
    var mainWindowMarkup = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
    if (!mainWindowMarkup.Contains("x:Name=\"ChatPermissionModeBox\"", StringComparison.Ordinal)
        || !mainWindowMarkup.Contains("SelectionChanged=\"ChatPermissionMode_Changed\"", StringComparison.Ordinal)
        || !mainWindowMarkup.Contains("x:Name=\"ChatPermissionModeBox\" Width=\"210\" MinHeight=\"30\" SelectedValuePath=\"Id\"", StringComparison.Ordinal)
        || !mainWindowMarkup.Contains("x:Name=\"ToolPermissionModeBox\" SelectedValuePath=\"Id\"", StringComparison.Ordinal)
        || mainWindowMarkup.Contains("ChatPermissionModeBox.Items.OfType&lt;ComboBoxItem&gt;", StringComparison.Ordinal)
        || !mainWindowSource.Contains("ApplyPermissionModeAsync", StringComparison.Ordinal)
        || !mainWindowSource.Contains("ChatPermissionModeBox.ItemsSource = options", StringComparison.Ordinal)
        || !mainWindowSource.Contains("ToolPermissionModeBox.ItemsSource = options", StringComparison.Ordinal)
        || !mainWindowSource.Contains("_config.ToolPermissionMode = mode", StringComparison.Ordinal)
        || !mainWindowSource.Contains("RequestApprovalInlineAsync", StringComparison.Ordinal))
        throw new Exception("The chat permission selector is not synchronized with the persisted execution policy.");
    if (!mainWindowSource.Contains("if (item.State == \"Ringing\") { await ResolveAlarmAsync(item.Id, false); return; }", StringComparison.Ordinal)
        || !mainWindowSource.Contains("if (item.State == \"Ringing\") { await ResolveAlarmAsync(item.Id, true); return; }", StringComparison.Ordinal)
        || !mainWindowSource.Contains("ActiveAlarmCount()", StringComparison.Ordinal))
        throw new Exception("Stop and snooze do not share the active alarm ID playback lifecycle.");
    if (!mainWindowMarkup.Contains("x:Name=\"WakeWordButtonLabel\" Text=\"Hey Jarvis — Off\"", StringComparison.Ordinal)
        || !mainWindowSource.Contains("\"Hey Jarvis — On\"", StringComparison.Ordinal)
        || !mainWindowSource.Contains("\"Hey Jarvis — Off\"", StringComparison.Ordinal)
        || !mainWindowSource.Contains("WakeWordButtonLabel.Text", StringComparison.Ordinal)
        || !mainWindowSource.Contains("_config.WakeWordEnabled", StringComparison.Ordinal))
        throw new Exception("The localized Hey Jarvis control is not derived from the actual wake-word state.");
    var alarmNotificationSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "AlarmNotificationWindow.cs"));
    if (!alarmNotificationSource.Contains("reminder.Title", StringComparison.Ordinal)
        || !alarmNotificationSource.Contains("reminder.Description", StringComparison.Ordinal)
        || !alarmNotificationSource.Contains("Scheduled", StringComparison.Ordinal)
        || !alarmNotificationSource.Contains("StopRequested", StringComparison.Ordinal)
        || !alarmNotificationSource.Contains("SnoozeRequested", StringComparison.Ordinal)
        || !mainWindowSource.Contains("_alarmWindows.TryGetValue(reminder.Id", StringComparison.Ordinal))
        throw new Exception("Alarm notification does not display real reminder data or deduplicate its lifecycle actions.");
    if (!mainWindowMarkup.Contains("x:Name=\"SourcesButton\"", StringComparison.Ordinal)
        || !mainWindowMarkup.Contains("x:Name=\"SourcesPopup\"", StringComparison.Ordinal)
        || !mainWindowMarkup.Contains("x:Name=\"AttachmentPreviewPanel\"", StringComparison.Ordinal)
        || !mainWindowSource.Contains("private void ShowActivity", StringComparison.Ordinal)
        || !mainWindowSource.Contains("private void BeginToolActivity", StringComparison.Ordinal)
        || !mainWindowSource.Contains("private void CompleteToolActivity", StringComparison.Ordinal)
        || !mainWindowSource.Contains("Tool activity", StringComparison.Ordinal)
        || !mainWindowSource.Contains("catch (OperationCanceledException) when (request.IsCancellationRequested)", StringComparison.Ordinal)
        || !mainWindowSource.Contains("the user did not cancel the task", StringComparison.Ordinal)
        || !mainWindowSource.Contains("name == \"ask_questions\"", StringComparison.Ordinal)
        || mainWindowSource.Contains("_activityExpander = new Expander", StringComparison.Ordinal)
        || !mainWindowSource.Contains("AppendPendingSource(new ChatSource(result.Title", StringComparison.Ordinal)
        || !mainWindowSource.Contains("AppendPendingSource(new ChatSource(info.Name", StringComparison.Ordinal))
        throw new Exception("The compact activity, source, or attachment preview UI is missing.");
    var questionUiSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MainWindow.InteractiveQuestions.cs"));
    if (!questionUiSource.Contains("TaskCompletionSource<string>", StringComparison.Ordinal)
        || !questionUiSource.Contains("RadioButton", StringComparison.Ordinal)
        || !questionUiSource.Contains("CheckBox", StringComparison.Ordinal)
        || !questionUiSource.Contains("AcceptsReturn", StringComparison.Ordinal)
        || !questionUiSource.Contains("completion.TrySetResult", StringComparison.Ordinal))
        throw new Exception("The native non-blocking clarification form is not wired for all supported control types and duplicate-safe completion.");
    var projectDocument = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Jarvis.csproj"));
    string ProjectValue(string name) => projectDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value ?? "";
    if (ProjectValue("Version") != "1.1.1" || ProjectValue("AssemblyVersion") != "1.1.1.0" || ProjectValue("FileVersion") != "1.1.1.0")
        throw new Exception("Jarvis version metadata changed from 1.1.1 / 1.1.1.0.");
    foreach (var metadataFile in new[] { "Jarvis.Beta.Shared.csproj", "Jarvis.Setup.csproj" })
    {
        var metadata = XDocument.Load(Path.Combine(AppContext.BaseDirectory, metadataFile));
        string Value(string name) => metadata.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value ?? "";
        if (Value("Version") != "1.1.1" || Value("AssemblyVersion") != "1.1.1.0" || Value("FileVersion") != "1.1.1.0")
            throw new Exception($"{metadataFile} version metadata is inconsistent with Jarvis 1.1.1.");
    }
    var installerSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Installer.Setup.MainWindow.xaml.cs"));
    if (!installerSource.Contains("LanguageBox.SelectedValue = \"en\"", StringComparison.Ordinal))
        throw new Exception("The installer does not default new installations to English.");
    Console.WriteLine("PASS: version lock, English installer default, compact activity/source UI and actual-state Hey Jarvis labels.");
    if (!mainWindowSource.Contains("search_images", StringComparison.Ordinal) || !mainWindowSource.Contains("TryCreateImageGallery", StringComparison.Ordinal))
        throw new Exception("Image search is not registered with a real chat gallery renderer.");
    var settingsSelectionStart = mainWindowSource.IndexOf("private async void SettingsModel_SelectionChanged", StringComparison.Ordinal);
    if (settingsSelectionStart < 0) throw new Exception("The settings model selection handler could not be located.");
    var settingsSelectionEnd = mainWindowSource.IndexOf("private async void ChatModel_LostFocus", settingsSelectionStart, StringComparison.Ordinal);
    if (settingsSelectionEnd < 0) throw new Exception("The settings model selection handler boundary could not be located.");
    var settingsSelectionHandler = mainWindowSource[settingsSelectionStart..settingsSelectionEnd];
    if (!settingsSelectionHandler.Contains("_config.Model = model", StringComparison.Ordinal) || !settingsSelectionHandler.Contains("SetChatModel(model)", StringComparison.Ordinal) ||
        !settingsSelectionHandler.Contains("PersistConfig()", StringComparison.Ordinal) || !settingsSelectionHandler.Contains("PersistChatState()", StringComparison.Ordinal) ||
        !mainWindowSource.Contains("ModelBox.ItemsSource = options", StringComparison.Ordinal))
        throw new Exception("The provider settings selector is not bound to the discovered model list or does not persist and synchronize selection.");
    Console.WriteLine("PASS: chat and provider settings model selectors use model IDs; settings has selection persistence and manual model entry.");

    var geminiRequestedUri = "";
    var geminiKeyHeader = "";
    var geminiHandler = new BetaStubHandler(request =>
    {
        geminiRequestedUri = request.RequestUri!.AbsoluteUri;
        geminiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : "";
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("""{"models":[{"name":"models/gemini-2.5-flash","displayName":"Gemini Flash","supportedGenerationMethods":["generateContent"]},{"name":"models/text-embedding-004","supportedGenerationMethods":["embedContent"]},{"name":"models/image-only","supportedGenerationMethods":["predict"]}]}""")
        };
    });
    using (var geminiHttp = new HttpClient(geminiHandler))
    using (var catalog = new ApiModelCatalogService(geminiHttp))
    {
        var models = await catalog.ListModelsAsync("https://generativelanguage.googleapis.com/v1beta/openai/", "fixture-gemini-key");
        if (models.Count != 1 || models[0].Id != "gemini-2.5-flash" || models[0].DisplayName != "Gemini Flash") throw new Exception("Gemini listing did not filter for models that support generateContent.");
        if (!geminiRequestedUri.Equals("https://generativelanguage.googleapis.com/v1beta/models", StringComparison.Ordinal) || geminiKeyHeader != "fixture-gemini-key") throw new Exception("Gemini native model-list endpoint or x-goog-api-key header was incorrect.");
        if (geminiRequestedUri.Contains("fixture-gemini-key", StringComparison.Ordinal)) throw new Exception("Gemini API key leaked into the request URL.");
    }
    if (ApiModelCatalogService.ResolveChatCompletionsUri("https://generativelanguage.googleapis.com/v1beta/models") != new Uri("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")) throw new Exception("Gemini chat requests did not resolve to Google's OpenAI-compatible generation endpoint.");

    var openAiRequestedUri = "";
    var openAiAuth = "";
    var openAiHandler = new BetaStubHandler(request =>
    {
        openAiRequestedUri = request.RequestUri!.AbsoluteUri;
        openAiAuth = request.Headers.Authorization?.ToString() ?? "";
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("""{"data":[{"id":"gpt-fixture"},{"id":"https://evil.example/model"},{"id":""}]}""")
        };
    });
    using (var openAiHttp = new HttpClient(openAiHandler))
    using (var catalog = new ApiModelCatalogService(openAiHttp))
    {
        var models = await catalog.ListModelsAsync("https://api.example.test/v1/", "fixture-openai-key");
        if (models.Count != 1 || models[0].Id != "gpt-fixture") throw new Exception("OpenAI-compatible IDs were not extracted or URL-like IDs were not excluded.");
        if (openAiRequestedUri != "https://api.example.test/v1/models" || openAiAuth != "Bearer fixture-openai-key") throw new Exception("OpenAI-compatible model endpoint or authorization header was incorrect.");
    }
    if (ApiModelCatalogService.ResolveOpenAiModelsUri("https://api.example.test/v1/models/").AbsoluteUri != "https://api.example.test/v1/models") throw new Exception("An existing models path was duplicated.");
    if (ApiModelCatalogService.ResolveOpenAiModelsUri("https://api.example.test/v1/chat/completions").AbsoluteUri != "https://api.example.test/v1/models") throw new Exception("The chat-completions path was not normalized for model listing.");
    if (ApiModelCatalogService.ResolveChatCompletionsUri("https://api.example.test/v1/chat/completions/").AbsoluteUri != "https://api.example.test/v1/chat/completions") throw new Exception("An existing chat-completions path was duplicated.");
    if (ApiModelCatalogService.IsValidModelId("https://api.example.test/v1") || ApiModelCatalogService.IsValidModelId("/v1/models") || !ApiModelCatalogService.IsValidModelId("gemini-2.5-flash")) throw new Exception("Model identifier validation accepted an endpoint URL or rejected a valid ID.");
    try { ApiModelCatalogService.ResolveOpenAiModelsUri("not a url"); throw new Exception("An invalid provider URL was accepted."); }
    catch (ModelCatalogException ex) when (ex.Failure == ModelCatalogFailure.InvalidEndpoint) { }
    try
    {
        using var malformedHttp = new HttpClient(new BetaStubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("not-json") }));
        using var catalog = new ApiModelCatalogService(malformedHttp);
        await catalog.ListModelsAsync("https://api.example.test/v1", null);
        throw new Exception("Malformed model-list JSON was accepted.");
    }
    catch (ModelCatalogException ex) when (ex.Failure == ModelCatalogFailure.InvalidResponse) { }
    using (var emptyHttp = new HttpClient(new BetaStubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"data":[]}""") })))
    using (var catalog = new ApiModelCatalogService(emptyHttp))
        if ((await catalog.ListModelsAsync("https://api.example.test/v1", null)).Count != 0) throw new Exception("An empty model list was not preserved as empty.");
    try
    {
        using var unauthorizedHttp = new HttpClient(new BetaStubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)));
        using var catalog = new ApiModelCatalogService(unauthorizedHttp);
        await catalog.ListModelsAsync("https://api.example.test/v1", "never-log-this-key");
        throw new Exception("Unauthorized model catalog access was accepted.");
    }
    catch (ModelCatalogException ex) when (ex.Failure == ModelCatalogFailure.Authentication && !ex.Message.Contains("never-log-this-key", StringComparison.Ordinal)) { }
    try
    {
        using var notFoundHttp = new HttpClient(new BetaStubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));
        using var catalog = new ApiModelCatalogService(notFoundHttp);
        await catalog.ListModelsAsync("https://api.example.test/wrong-path", null);
        throw new Exception("A missing model endpoint was accepted.");
    }
    catch (ModelCatalogException ex) when (ex.Failure == ModelCatalogFailure.NotFound && ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    try
    {
        using var unexpectedHttp = new HttpClient(new BetaStubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"models":[]}""") }));
        using var catalog = new ApiModelCatalogService(unexpectedHttp);
        await catalog.ListModelsAsync("https://api.example.test/v1", null);
        throw new Exception("A non OpenAI-compatible response shape was accepted for a generic provider.");
    }
    catch (ModelCatalogException ex) when (ex.Failure == ModelCatalogFailure.InvalidResponse) { }
    var chatPayload = ApiModelCatalogService.CreateChatCompletionPayload("gemini-2.5-flash", Array.Empty<object>(), Array.Empty<object>(), "auto", 0.4);
    if (JsonDocument.Parse(JsonSerializer.Serialize(chatPayload)).RootElement.GetProperty("model").GetString() != "gemini-2.5-flash") throw new Exception("The selected provider model ID was not carried into the generation request.");
    var providerConfig = new AppConfig { Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-2.5-flash", ApiKeyProtected = "dpapi-ciphertext-placeholder" };
    var restoredProviderConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(providerConfig));
    if (restoredProviderConfig?.Endpoint != providerConfig.Endpoint || restoredProviderConfig.Model != providerConfig.Model || restoredProviderConfig.ApiKeyProtected != providerConfig.ApiKeyProtected) throw new Exception("Provider URL, model ID, or protected API key setting did not survive configuration persistence.");
    Console.WriteLine("PASS: Gemini/OpenAI model discovery, supported-method filtering, endpoint normalization, errors, URL/model separation, request model selection, and provider settings persistence.");

    const string geminiQuotaBody = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded for quota metric; retry after daily quota reset."}}""";
    if (ProviderRateLimitRecovery.Classify(HttpStatusCode.TooManyRequests, geminiQuotaBody) != ProviderFailureKind.QuotaExhausted ||
        ProviderRateLimitRecovery.ShouldRetry(HttpStatusCode.TooManyRequests, geminiQuotaBody, 0))
        throw new Exception("A Gemini 429 exhausted quota was not distinguished from a temporary rate limit, or would be retried blindly.");
    const string transient429 = """{"error":{"type":"rate_limit_error","message":"Please retry in 2 seconds"}}""";
    if (ProviderRateLimitRecovery.Classify(HttpStatusCode.TooManyRequests, transient429) != ProviderFailureKind.TemporaryRateLimit ||
        !ProviderRateLimitRecovery.ShouldRetry(HttpStatusCode.TooManyRequests, transient429, 0) ||
        ProviderRateLimitRecovery.ShouldRetry(HttpStatusCode.TooManyRequests, transient429, 3))
        throw new Exception("Temporary provider rate limits did not receive bounded retries.");
    if (ProviderRateLimitRecovery.ParseRetryAfter("7", DateTimeOffset.UnixEpoch) != TimeSpan.FromSeconds(7) ||
        ProviderRateLimitRecovery.GetRetryDelay(0, null) != TimeSpan.FromSeconds(1) ||
        ProviderRateLimitRecovery.GetRetryDelay(2, null) != TimeSpan.FromSeconds(4) ||
        ProviderRateLimitRecovery.GetRetryDelay(9, TimeSpan.FromMinutes(5)) != TimeSpan.FromSeconds(60))
        throw new Exception("Retry-After parsing or bounded exponential backoff was incorrect.");
    if (ProviderRateLimitRecovery.Classify(HttpStatusCode.ServiceUnavailable, "upstream unavailable") != ProviderFailureKind.TemporaryProviderFailure
        || ProviderRateLimitRecovery.Classify(HttpStatusCode.Unauthorized, "invalid API key") != ProviderFailureKind.Other
        || !ProviderRateLimitRecovery.IsTransientTransportFailure(new HttpRequestException("offline"))
        || ProviderRateLimitRecovery.IsTransientTransportFailure(new TaskCanceledException("user cancelled"), requestCancelled: true)
        || ProviderRateLimitRecovery.IsTransientTransportFailure(new InvalidOperationException("bad model")))
        throw new Exception("Transient transport/server failures were not separated from cancellation and permanent configuration errors.");
    if (!ProviderRateLimitRecovery.IsConfirmedOpenAiCompatibleFallback("http://localhost:11434/v1", "qwen3-vl:8b", true) ||
        ProviderRateLimitRecovery.IsConfirmedOpenAiCompatibleFallback("http://localhost:11434/v1", "qwen3-vl:8b", false) ||
        ProviderRateLimitRecovery.IsConfirmedOpenAiCompatibleFallback("not-a-url", "qwen3-vl:8b", true) ||
        ProviderRateLimitRecovery.IsConfirmedOpenAiCompatibleFallback("https://user:secret@example.test/v1?key=secret", "qwen3-vl:8b", true) ||
        OpenAiCompatibleChatTransport.SafeEndpointLabel("https://user:secret@example.test/v1?key=secret") != "example.test")
        throw new Exception("Computer Use fallback validation did not require a valid endpoint/model and explicit vision/tool capability confirmation.");
    var recoveryMessages = new List<object> { new { role = "tool", content = "completed action; result uncertain" } };
    var oldMessage = recoveryMessages[0];
    ComputerUseRecoveryContext.AppendFreshObservation(recoveryMessages,
        [new ComputerUseObservationImage("data:image/png;base64,ZmFrZQ==", "monitor-2, physical x=-1920")],
        "After provider failure, recover from the latest screen.");
    var recoveryJson = JsonDocument.Parse(JsonSerializer.Serialize(recoveryMessages));
    if (!ReferenceEquals(recoveryMessages[0], oldMessage) || recoveryMessages.Count != 2 ||
        recoveryJson.RootElement[1].GetProperty("content").EnumerateArray().All(item => !item.ToString().Contains("data:image/png", StringComparison.Ordinal)))
        throw new Exception("Provider switching did not retain prior action results and append a fresh screen observation.");
    var fallbackConfig = new AppConfig
    {
        ComputerUseFallbackEndpoint = "http://localhost:11434/v1",
        ComputerUseFallbackModel = "qwen3-vl:8b",
        ComputerUseFallbackCapabilitiesConfirmed = true,
        ComputerUseFallbackApiKeyProtected = "DPAPI-ciphertext-only"
    };
    var restoredFallback = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(fallbackConfig));
    if (restoredFallback?.ComputerUseFallbackEndpoint != fallbackConfig.ComputerUseFallbackEndpoint ||
        restoredFallback.ComputerUseFallbackModel != fallbackConfig.ComputerUseFallbackModel ||
        !restoredFallback.ComputerUseFallbackCapabilitiesConfirmed ||
        restoredFallback.ComputerUseFallbackApiKeyProtected != "DPAPI-ciphertext-only")
        throw new Exception("Fallback endpoint, model, capability confirmation, or protected credential did not persist.");
    var fallbackRequestUri = "";
    var fallbackRequestAuthorization = "";
    var fallbackRequestModel = "";
    var fallbackPayload = new Dictionary<string, object>
    {
        ["model"] = fallbackConfig.ComputerUseFallbackModel,
        ["messages"] = new object[] { new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = "data:image/png;base64,ZmFrZQ==" } } } } },
        ["tools"] = Array.Empty<object>()
    };
    using (var fallbackHttp = new HttpClient(new BetaStubHandler(request =>
    {
        fallbackRequestUri = request.RequestUri!.AbsoluteUri;
        fallbackRequestAuthorization = request.Headers.Authorization?.ToString() ?? "";
        using var requestBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        fallbackRequestModel = requestBody.RootElement.GetProperty("model").GetString() ?? "";
        if (!requestBody.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().Any(item => item.TryGetProperty("type", out var type) && type.GetString() == "image_url"))
            throw new Exception("The fallback request did not include the fresh screen observation.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"recovered\"}}]}") };
    })))
    using (var fallbackResponse = await OpenAiCompatibleChatTransport.SendAsync(fallbackHttp,
        fallbackConfig.ComputerUseFallbackEndpoint, "fixture-ollama-key",
        JsonSerializer.Serialize(fallbackPayload),
        CancellationToken.None))
    {
        if (!fallbackResponse.IsSuccessStatusCode || fallbackRequestUri != "http://localhost:11434/v1/chat/completions" ||
            fallbackRequestAuthorization != "Bearer fixture-ollama-key" || fallbackRequestModel != "qwen3-vl:8b")
            throw new Exception("Configured Ollama fallback did not receive the correct endpoint, API key, model ID, or fresh image context.");
    }
    Console.WriteLine("PASS: Gemini quota exhaustion is not retried; temporary 429 uses bounded backoff/Retry-After; confirmed Ollama fallback is validated; action history is retained and fresh screen context is appended.");

    var googleHtml = "<div><a href=\"https://example.com/news?a=1&amp;b=2\"><h3>Fortnite Cup <em>Schedule</em></h3></a><div class=\"VwiC3b\">Next event details</div></div><a href='/url?q=https%3A%2F%2Fexample.org%2Fsecond&amp;sa=U'><h3>Second result</h3></a>";
    if (!ImageSearchIntentParser.TryParse("zeig mir das minecraft logo", out var germanLogoQuery) || germanLogoQuery != "Minecraft logo"
        || !ImageSearchIntentParser.TryParse("Show me the Minecraft logo", out var englishLogoQuery) || englishLogoQuery != "Minecraft logo"
        || !ImageSearchIntentParser.TryParse("Bilder von Complementary Reimagined shaders", out var germanImageQuery) || germanImageQuery != "Complementary Reimagined shaders"
        || ImageSearchIntentParser.NormalizeTopic("mir das minecraft logo") != "Minecraft logo"
        || ImageSearchIntentParser.TryParse("Was bedeutet das Logo in dieser App?", out _))
        throw new Exception("Image-search intent did not remove conversational wrappers safely or produced a false positive.");
    using (var toolArguments = JsonDocument.Parse(ImageSearchIntentParser.CreateToolArguments(germanLogoQuery, 8)))
        if (toolArguments.RootElement.GetProperty("query").GetString() != "Minecraft logo" || toolArguments.RootElement.GetProperty("limit").GetInt32() != 8)
            throw new Exception("The actual deterministic image-search tool arguments lost the normalized query.");

    if (!WebResearchIntentParser.TryParse("Google how to fix this Minecraft bug", out var explicitGoogleQuery) || !explicitGoogleQuery.Contains("Minecraft bug", StringComparison.Ordinal)
        || WebResearchIntentParser.TryParse("Google is changing its logo again", out _)
        || WebResearchIntentParser.TryParse("What do you think about Google?", out _))
        throw new Exception("Web research routing still uses the word Google as an unconditional trigger.");
    var contextualResearch = WebResearchIntentParser.ComposeContextualQuery(
        ["Wie fixe ich einen Bug in meiner Mod für Hopper Colors Minecraft 1.18.11?"],
        "Das Game crasht, Fabric 9-9. Bitte finde mögliche Lösungen.");
    if (!contextualResearch.Contains("Hopper Colors", StringComparison.OrdinalIgnoreCase)
        || !contextualResearch.Contains("Minecraft 1.18.11", StringComparison.OrdinalIgnoreCase)
        || !contextualResearch.Contains("Fabric 9-9", StringComparison.OrdinalIgnoreCase))
        throw new Exception("A follow-up research instruction discarded the Minecraft crash conversation context.");

    using (var questionJson = JsonDocument.Parse("""{"form_id":"minecraft_crash","title":"Crash details","questions":[{"id":"version","label":"Minecraft version","type":"single_choice","required":true,"allow_other":true,"options":[{"value":"1.18.1","label":"1.18.1"},{"value":"1.18.2","label":"1.18.2"}]},{"id":"report","label":"Do you have a crash report?","type":"yes_no","required":false},{"id":"log","label":"Paste the error","type":"multiline","required":false}]}"""))
    {
        var form = InteractiveQuestionSchema.Parse(questionJson.RootElement);
        if (form.Questions.Count != 3 || form.Questions[0].Type != InteractiveQuestionType.SingleChoice || !form.Questions[0].AllowOther || form.Questions[1].Type != InteractiveQuestionType.YesNo)
            throw new Exception("The native clarification schema lost question types or Other support.");
    }
    try
    {
        using var invalidQuestionJson = JsonDocument.Parse("""{"form_id":"bad","title":"Bad","questions":[{"id":"same","label":"A","type":"text","required":true},{"id":"same","label":"B","type":"text","required":false}]}""");
        InteractiveQuestionSchema.Parse(invalidQuestionJson.RootElement);
        throw new Exception("Duplicate structured-question IDs were accepted.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: image/web intent routing, exact serialized image arguments, contextual bug research, and validated interactive-question schemas.");

    var imageApiHandler = new BetaStubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.Host == "api.openverse.org"
            ? """{"results":[{"title":"Minecraft shader screenshot","thumbnail":"https://images.openverse.org/thumb.jpg","foreign_landing_url":"https://example.org/minecraft-shader","creator":"Example Author","license":"cc-by","license_version":"4.0"}]}"""
            : """{"query":{"pages":[{"title":"File:Jarvis example.jpg","imageinfo":[{"thumburl":"https://upload.wikimedia.org/example-thumb.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Jarvis_example.jpg","extmetadata":{"Artist":{"value":"Example Author"},"LicenseShortName":{"value":"CC BY-SA 4.0"}}}]}]}}""")
    });
    using (var imageHttp = new HttpClient(imageApiHandler))
    using (var imageSearch = new ImageSearchService(imageHttp))
    {
        var images = await imageSearch.SearchAsync("Minecraft shader", 4, CancellationToken.None);
        if (images.Count != 1 || images[0].Title != "Minecraft shader screenshot" || images[0].Author != "Example Author" || images[0].License != "cc-by 4.0" || images[0].Provider != "Openverse")
            throw new Exception("Openverse image search did not preserve real thumbnail, source, provider, author, and license metadata.");
    }
    using (var modrinthHttp = new HttpClient(new BetaStubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.Host == "api.modrinth.com"
            ? "{\"hits\":[{\"title\":\"Complementary Reimagined\",\"slug\":\"complementary-reimagined\",\"project_type\":\"shader\",\"author\":\"Author\",\"license\":\"ARR\",\"gallery\":[\"https://cdn.modrinth.com/data/example/images/view-a.png\",\"https://cdn.modrinth.com/data/example/images/view-b.png\",\"https://cdn.modrinth.com/data/example/images/view-c.png\"]}]}"
            : "{\"results\":[]}")
    })))
    using (var modrinthSearch = new ImageSearchService(modrinthHttp))
    {
        var result = await modrinthSearch.SearchDetailedAsync("Complementary Reimagined shaders", 4, CancellationToken.None);
        var modrinthAttempt = result.Attempts.Single(attempt => attempt.Provider == "Modrinth");
        if (!result.IsSuccess || result.Results.Count != 3 || result.Results.Any(image => image.Provider != "Modrinth" || image.FullImageUrl != image.ThumbnailUrl) || result.Results.Select(image => image.SourceUrl).Distinct().Count() != 1 || modrinthAttempt.RawCount != 1 || modrinthAttempt.ParsedCount != 3 || modrinthAttempt.RetainedCount != 3) throw new Exception("Structured Modrinth shader-gallery search collapsed multiple project images or lost full-image metadata.");
    }
    using (var emptyHttp = new HttpClient(new BetaStubHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host == "api.openverse.org" ? "{\"results\":[]}" : "{}") })))
    using (var emptySearch = new ImageSearchService(emptyHttp))
    {
        var empty = await emptySearch.SearchDetailedAsync("nothing", 4, CancellationToken.None);
        if (empty.Outcome != ImageSearchOutcome.Empty || empty.Results.Count != 0 || empty.Attempts.Count != 2 || empty.Attempts[0].RawCount != 0) throw new Exception("Empty image search did not retain distinct provider diagnostics.");
    }
    using (var fallbackHttp = new HttpClient(new BetaStubHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host == "api.openverse.org" ? "{\"results\":[]}" : "{\"query\":{\"pages\":[{\"title\":\"File:Minecraft landscape.png\",\"imageinfo\":[{\"url\":\"https://upload.wikimedia.org/minecraft-landscape-full.png\",\"thumburl\":\"https://upload.wikimedia.org/minecraft-landscape.png\",\"descriptionurl\":\"https://commons.wikimedia.org/wiki/File:Minecraft_landscape.png\",\"extmetadata\":{\"Artist\":{\"value\":\"Author\"},\"LicenseShortName\":{\"value\":\"CC BY\"}}}] }]}}" ) })))
    using (var fallbackSearch = new ImageSearchService(fallbackHttp))
    {
        var fallback = await fallbackSearch.SearchDetailedAsync("Minecraft landscape", 4, CancellationToken.None);
        if (!fallback.IsSuccess || fallback.Results.Single().Provider != "Wikimedia Commons" || fallback.Attempts.Count != 2 || fallback.Attempts[0].Outcome != ImageSearchOutcome.Empty) throw new Exception("Wikimedia fallback did not run after an empty Openverse response.");
    }
    using (var malformedHttp = new HttpClient(new BetaStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad-json") })))
    using (var malformedSearch = new ImageSearchService(malformedHttp))
    {
        var malformed = await malformedSearch.SearchDetailedAsync("Minecraft landscape", 4, CancellationToken.None);
        if (malformed.IsSuccess || malformed.Attempts.Any(attempt => attempt.Outcome != ImageSearchOutcome.ParseFailed)) throw new Exception("Malformed provider responses were not distinguished from empty results.");
    }
    try
    {
        using var failingHttp = new HttpClient(new BetaStubHandler(_ => throw new HttpRequestException("offline")));
        using var failingSearch = new ImageSearchService(failingHttp);
        await failingSearch.SearchAsync("offline", 4, CancellationToken.None);
        throw new Exception("Image-search network error was silently accepted.");
    }
    catch (ImageSearchUnavailableException) { }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        using var cancelledSearch = new ImageSearchService(new HttpClient(new BetaStubHandler(_ => throw new Exception("A cancelled image search reached the provider."))));
        try { await cancelledSearch.SearchDetailedAsync("Minecraft landscape", 4, cancelled.Token); throw new Exception("Image-search cancellation was ignored."); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }
    using (var downloadHttp = new HttpClient(new BetaStubHandler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([137, 80, 78, 71]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    })))
    using (var downloader = new ImageDownloadService(downloadHttp))
    {
        var downloaded = await downloader.FetchAsync("https://images.example.org/test.png", CancellationToken.None);
        if (downloaded.Extension != ".png" || downloaded.MediaType != "image/png" || downloaded.Bytes.Length != 4) throw new Exception("Full-image download did not preserve validated image bytes and media metadata.");
    }
    using (var invalidDownloadHttp = new HttpClient(new BetaStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not an image") })))
    using (var downloader = new ImageDownloadService(invalidDownloadHttp))
    {
        try { await downloader.FetchAsync("https://images.example.org/not-image", CancellationToken.None); throw new Exception("A non-image response was accepted as a gallery download."); }
        catch (InvalidDataException) { }
    }
    using (var cancelledDownloadHttp = new HttpClient(new AsyncStubHandler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(2), token); return new HttpResponseMessage(HttpStatusCode.OK); })))
    using (var downloader = new ImageDownloadService(cancelledDownloadHttp))
    using (var cancelledDownload = new CancellationTokenSource(20))
    {
        try { await downloader.FetchAsync("https://images.example.org/cancelled.png", cancelledDownload.Token); throw new Exception("A cancelled gallery download continued to completion."); }
        catch (OperationCanceledException) when (cancelledDownload.IsCancellationRequested) { }
    }
    Console.WriteLine("PASS: image-search routing, multi-image retention, full-image metadata/download validation, empty results, provider fallback and unavailable-provider reporting.");
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
    var githubResults = GoogleSearchParser.ParseGitHubSearch("{\"items\":[{\"full_name\":\"FabricMC/fabric-docs\",\"html_url\":\"https://github.com/FabricMC/fabric-docs\",\"homepage\":\"https://docs.fabricmc.net/\",\"description\":\"Official Fabric documentation for Minecraft mod development\"}]}", 5);
    if (githubResults.Count != 1 || githubResults[0].Url != "https://docs.fabricmc.net/" || !GoogleSearchParser.LooksTechnical("Minecraft Fabric docs")) throw new Exception("Structured GitHub technical search parsing failed.");
    var modrinthResults = GoogleSearchParser.ParseModrinthSearch("{\"hits\":[{\"title\":\"Hotbar Colors Fix\",\"slug\":\"hotbar-colors-fix\",\"project_type\":\"mod\",\"description\":\"Fixes a Fabric hotbar colour rendering crash\",\"versions\":[\"1.21.1\"]}]}", 5);
    if (modrinthResults.Count != 1 || modrinthResults[0].Url != "https://modrinth.com/mod/hotbar-colors-fix" || !modrinthResults[0].Snippet.Contains("1.21.1", StringComparison.Ordinal)) throw new Exception("Structured Modrinth research parsing failed.");
    var redditResults = GoogleSearchParser.ParseRedditSearch("{\"data\":{\"children\":[{\"data\":{\"title\":\"Fabric hotbar colour crash fix\",\"permalink\":\"/r/fabricmc/comments/example/fix/\",\"selftext\":\"Resolved by updating the rendering mod.\"}}]}}", 5);
    if (redditResults.Count != 1 || redditResults[0].Url != "https://www.reddit.com/r/fabricmc/comments/example/fix/" || !redditResults[0].Snippet.Contains("Resolved", StringComparison.Ordinal)) throw new Exception("Structured Reddit research parsing failed.");
    var fabricQuery = "Wie erstelle ich eine Fabric-Mod für Minecraft 1.21.1 mit Java 21?";
    var fabricFiltered = GoogleSearchParser.KeepRelevant(fabricQuery, new[]
    {
        new GoogleSearchResult("Fabric Documentation - Creating Your First Mod", "https://docs.fabricmc.net/develop/getting-started/creating-a-project", "Create a Fabric mod for Minecraft 1.21.1 using Java 21."),
        new GoogleSearchResult("TCP and UDP port numbers", "https://en.wikipedia.org/wiki/List_of_TCP_and_UDP_port_numbers", "Minecraft servers and Java APIs use network ports."),
        new GoogleSearchResult("Duplicate", "https://docs.fabricmc.net/develop/getting-started/creating-a-project#top", "Fabric mod Minecraft 1.21.1 Java 21")
    });
    if (fabricFiltered.Count != 1 || fabricFiltered[0].Domain != "docs.fabricmc.net" || fabricFiltered[0].Relevance < .7) throw new Exception("Technical relevance filtering accepted an unrelated or duplicate result.");
    var hotbarFiltered = GoogleSearchParser.KeepRelevant("Minecraft Fabric 1.21.1 hotbar color bug", new[]
    {
        new GoogleSearchResult("Hotbar colors render incorrectly with Fabric", "https://github.com/example/mod/issues/42", "Minecraft 1.21.1 visual bug affects the hotbar colour overlay."),
        new GoogleSearchResult("Minecraft server port list", "https://example.org/ports", "Fabric servers and network port colors."),
        new GoogleSearchResult("Fabric documentation", "https://docs.fabricmc.net/", "Official Fabric developer documentation.")
    });
    if (hotbarFiltered.Count != 1 || !hotbarFiltered[0].Url.Contains("issues/42", StringComparison.Ordinal)) throw new Exception("Hotbar relevance filtering rejected a legitimate singular/plural result or accepted generic Fabric pages.");
    var fabricRss = """<?xml version="1.0"?><rss><channel><item><title>Fabric Documentation - Creating Your First Mod</title><link>https://docs.fabricmc.net/develop/getting-started/creating-a-project</link><description>Create a Fabric mod for Minecraft 1.21.1 using Java 21.</description></item><item><title>Fabric Example Mod</title><link>https://github.com/FabricMC/fabric-example-mod</link><description>Official Fabric Minecraft mod project for Java.</description></item></channel></rss>""";
    using (var researchHttp = new HttpClient(new AsyncStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fabricRss) }))))
    using (var research = new WebResearchService(researchHttp, TimeSpan.FromSeconds(1)))
    {
        var result = await research.SearchAsync(fabricQuery, 5, CancellationToken.None);
        var successful = result.Attempts.Single(attempt => attempt.Outcome == SearchProviderOutcome.RelevantResults);
        if (!result.IsSuccess || result.Provider != "Bing RSS" || result.Results.Count != 2 || successful.Outcome != SearchProviderOutcome.RelevantResults || successful.ParsedCount != 2 || successful.RetainedCount != 2 || successful.HttpStatus != 200) throw new Exception("Structured research did not return multiple relevant technical sources with diagnostics.");
    }
    using (var emptyResearchHttp = new HttpClient(new AsyncStubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host.Contains("bing") ? "<rss><channel></channel></rss>" : "<html>unexpected markup</html>") }))))
    using (var emptyResearch = new WebResearchService(emptyResearchHttp, TimeSpan.FromSeconds(1)))
    {
        var result = await emptyResearch.SearchAsync(fabricQuery, 5, CancellationToken.None);
        if (result.IsSuccess || result.Attempts.Count != 5 || result.Attempts.Any(attempt => attempt.Outcome == SearchProviderOutcome.RelevantResults)) throw new Exception("Empty or unexpected search responses were reported as successful.");
    }
    using (var timeoutHttp = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
    {
        if (request.RequestUri!.Host.Contains("bing")) { await Task.Delay(TimeSpan.FromSeconds(5), cancellation); throw new Exception("unreachable"); }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Host.Contains("duckduckgo") ? "<a class='result__a' href='https://docs.fabricmc.net/develop/getting-started/creating-a-project'>Fabric Minecraft Java mod guide</a><div class='result__snippet'>Fabric mod Minecraft Java project</div>" : "") };
    })))
    using (var timeoutResearch = new WebResearchService(timeoutHttp, TimeSpan.FromMilliseconds(30)))
    {
        var result = await timeoutResearch.SearchAsync("Fabric Minecraft Java mod", 5, CancellationToken.None);
        if (!result.IsSuccess || !result.Attempts.Any(attempt => attempt.Outcome == SearchProviderOutcome.TimedOut) || result.Provider != "DuckDuckGo HTML") throw new Exception("Provider timeout cancelled the parent search instead of using a bounded fallback.");
    }
    using (var cancellationHttp = new HttpClient(new AsyncStubHandler(async (_, cancellation) => { await Task.Delay(TimeSpan.FromSeconds(5), cancellation); return new HttpResponseMessage(HttpStatusCode.OK); })))
    using (var cancellationResearch = new WebResearchService(cancellationHttp, TimeSpan.FromSeconds(10)))
    using (var cancellation = new CancellationTokenSource(20))
    {
        try { await cancellationResearch.SearchAsync(fabricQuery, 5, cancellation.Token); throw new Exception("Explicit search cancellation was ignored."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    Console.WriteLine("PASS: web research relevance, URL deduplication, empty/malformed responses, provider timeout fallback, and user cancellation.");
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

    if (!PublicWebPageReader.ValidateUrl("https://example.com/docs").IsAbsoluteUri
        || !PublicWebPageReader.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8"))
        || !PublicWebPageReader.IsPublicAddress(System.Net.IPAddress.Parse("2001:4860:4860::8888"))
        || PublicWebPageReader.IsPublicAddress(System.Net.IPAddress.Parse("10.0.0.1"))
        || PublicWebPageReader.IsPublicAddress(System.Net.IPAddress.Parse("fc00::1"))
        || PublicWebPageReader.ToReadableText("<h1>Official Docs</h1><script>secret()</script><p>Use &amp; verify.</p>").Contains("secret()", StringComparison.Ordinal)
        || !PublicWebPageReader.ToReadableText("<h1>Official Docs</h1><script>secret()</script><p>Use &amp; verify.</p>").Contains("Use & verify.", StringComparison.Ordinal))
        throw new Exception("Public web-page validation or HTML text extraction failed.");
    foreach (var unsafeUrl in new[] { "http://localhost/", "http://127.0.0.1/", "https://10.0.0.2/docs", "https://example.com:8443/docs", "https://example.com/?api_key=private" })
    {
        try { PublicWebPageReader.ValidateUrl(unsafeUrl); throw new Exception($"Unsafe web-page URL was accepted: {unsafeUrl}"); }
        catch (InvalidDataException) { }
    }
    Console.WriteLine("PASS: direct page retrieval blocks local/private hosts, credential URLs, and nonstandard ports; HTML extraction removes active content.");

    if (!DefaultSystemPrompt.Text.Contains("WINDOWS COMMAND EXECUTION", StringComparison.Ordinal)
        || !DefaultSystemPrompt.Text.Contains("WINDOWS COMPUTER USE", StringComparison.Ordinal)
        || !DefaultSystemPrompt.Text.Contains("Ordinary web research uses Jarvis's independent web search", StringComparison.Ordinal)
        || !DefaultSystemPrompt.Text.Contains("there is no manual mode button", StringComparison.Ordinal)
        || !DefaultSystemPrompt.Text.Contains("plan_task", StringComparison.Ordinal)
        || !DefaultSystemPrompt.IsLegacyDefault(DefaultSystemPrompt.LegacyText))
        throw new Exception("The new provider-independent agent prompt or legacy prompt migration is incomplete.");
    Console.WriteLine("PASS: provider-independent English agent prompt covers planning, execution evidence, shell approval, Computer Use, safety, and bounded recovery.");

    if (ComputerUseSessionState.Stopped.IsRunning()
        || !ComputerUseSessionState.Starting.IsRunning()
        || !ComputerUseSessionState.AwaitingApproval.IsRunning()
        || !ComputerUseSessionState.Active.IsRunning()
        || !ComputerUseSessionState.Paused.IsRunning()
        || !ComputerUseSessionState.Stopping.IsRunning()
        || ComputerUseSessionState.Finished.IsRunning()
        || ComputerUseSessionState.Failed.IsRunning())
        throw new Exception("Computer Use overlay session lifecycle states are inconsistent.");
    Console.WriteLine("PASS: Computer Use state lifecycle distinguishes pending, active, stopping, and terminal states.");

    var recoverableAttempt = AgentPlanOutcome.ToolAttemptStatus(succeeded: false, outcomeVerified: false);
    var sentButUnverified = AgentPlanOutcome.ToolAttemptStatus(succeeded: true, outcomeVerified: false);
    var verifiedFallback = AgentPlanOutcome.ToolAttemptStatus(succeeded: true, outcomeVerified: true);
    if (recoverableAttempt != "Pending" || sentButUnverified != "Needs confirmation" || verifiedFallback != "Completed")
        throw new Exception("Tool-attempt status incorrectly conflates method failure, unverified input, and verified outcome.");
    if (!AgentPlanOutcome.CanConfirmComputerOutcome("Needs confirmation", observationIsFresh: true, foregroundMatches: true)
        || AgentPlanOutcome.CanConfirmComputerOutcome("Needs confirmation", observationIsFresh: false, foregroundMatches: true)
        || AgentPlanOutcome.CanConfirmComputerOutcome("Needs confirmation", observationIsFresh: true, foregroundMatches: false)
        || AgentPlanOutcome.CanConfirmComputerOutcome("Pending", observationIsFresh: true, foregroundMatches: true))
        throw new Exception("Computer Use plan completion was allowed without a fresh observation of the same foreground target.");
    var fallbackPlan = new[] { recoverableAttempt, sentButUnverified };
    if (AgentPlanOutcome.ResolvePlanStatus(fallbackPlan) != "Incomplete"
        || AgentPlanOutcome.ResolvePlanStatus([verifiedFallback]) != "Completed"
        || AgentPlanOutcome.ResolvePlanStatus(["Completed", "Failed"]) != "Partially completed"
        || AgentPlanOutcome.ResolvePlanStatus(["Needs confirmation"]) != "Needs confirmation")
        throw new Exception("Plan outcome aggregation did not preserve recovery, partial completion, and uncertainty.");
    if (AgentPlanOutcome.ResolveCancellation("Completed", userCancelled: true) != "Completed"
        || AgentPlanOutcome.ResolveCancellation("Incomplete", userCancelled: true, responseAlreadyDelivered: true) != "Completed"
        || AgentPlanOutcome.ResolveCancellation("In progress", userCancelled: true) != "Cancelled"
        || AgentPlanOutcome.ResolveCancellation("In progress", userCancelled: false) != "In progress")
        throw new Exception("A late cancellation overwrote a completed task or an actual cancellation was ignored.");
    Console.WriteLine("PASS: failed tool attempts remain recoverable, unverified computer input needs confirmation, and verified fallback success wins over late cancellation.");

    var actions = new ComputerUseActionLedger();
    var clickAction = actions.Begin("task-share-stop", "plan-share-stop", 2, "computer_click", "Stop screen share", "screen-before");
    actions.MarkExecuting(clickAction.ActionId);
    actions.MarkDispatchStarting(clickAction.ActionId);
    actions.MarkDispatched(clickAction.ActionId);
    if (!actions.HasUnverifiedAction || actions.Resolve(clickAction.ActionId, "screen-before", "verified", "Clicked", "window-1", "plan-share-stop", 2))
        throw new Exception("A dispatched click or its stale source screenshot was treated as verified.");
    try { actions.Begin("task-share-stop", "plan-share-stop", 2, "computer_press_key", "fallback shortcut", "screen-before"); throw new Exception("A second action was allowed before observing the first."); }
    catch (InvalidOperationException) { }
    if (!actions.AttachObservation(clickAction.ActionId, "screen-after-click", "window-1", "post-click screenshot"))
        throw new Exception("A fresh post-click observation was not associated with the action.");
    if (actions.Resolve(clickAction.ActionId, "screen-after-click", "verified", "Share stopped", "window-2", "plan-share-stop", 2)
        || actions.Current?.Status != ComputerUseActionStatus.Verifying)
        throw new Exception("Verification ignored a foreground-window change after the screenshot.");
    if (!actions.Resolve(clickAction.ActionId, "screen-after-click", "not_achieved", "Discord still shows active screen sharing", "window-1", "plan-share-stop", 2)
        || actions.Current?.Status != ComputerUseActionStatus.Failed
        || AgentPlanOutcome.CanCompleteComputerStep("Needs confirmation", hasLinkedAction: true, linkedActionVerified: false, exactFreshObservationMatches: true))
        throw new Exception("An unchanged screen-share state was incorrectly marked complete.");

    var fallbackAction = actions.Begin("task-share-stop", "plan-share-stop", 2, "computer_press_key", "verified Discord shortcut", "screen-after-click");
    actions.MarkExecuting(fallbackAction.ActionId);
    actions.MarkDispatchStarting(fallbackAction.ActionId);
    actions.MarkDispatched(fallbackAction.ActionId);
    actions.AttachObservation(fallbackAction.ActionId, "screen-after-shortcut", "window-1", "fresh screenshot after shortcut");
    if (!actions.Resolve(fallbackAction.ActionId, "screen-after-shortcut", "verified", "Discord no longer shows an active share", "window-1", "plan-share-stop", 2)
        || actions.Current?.Status != ComputerUseActionStatus.Succeeded
        || !actions.IsVerifiedForPlanStep(fallbackAction.ActionId, "screen-after-shortcut", "plan-share-stop", 2)
        || !AgentPlanOutcome.CanCompleteComputerStep("Needs confirmation", hasLinkedAction: true, linkedActionVerified: true, exactFreshObservationMatches: false))
        throw new Exception("A verified fallback did not complete the linked plan step.");
    actions.MarkCancelled(fallbackAction.ActionId, inputMayHaveBeenDispatched: true);
    if (actions.Current?.Status != ComputerUseActionStatus.Succeeded)
        throw new Exception("Late cancellation overwrote an already verified action result.");

    var uncertainAction = actions.Begin("task-uncertain", null, null, "computer_click", "ambiguous control", "screen-uncertain-before");
    actions.MarkExecuting(uncertainAction.ActionId);
    actions.MarkDispatchStarting(uncertainAction.ActionId);
    if (actions.Current?.Status != ComputerUseActionStatus.AwaitingObservation || !actions.HasUnverifiedAction)
        throw new Exception("A possible partially dispatched action was not preserved as uncertain when observation was unavailable.");
    actions.MarkCancelled(uncertainAction.ActionId, inputMayHaveBeenDispatched: true);
    if (actions.Current?.Status != ComputerUseActionStatus.AwaitingObservation)
        throw new Exception("Cancellation erased the need to inspect a possibly dispatched action.");
    actions.AttachObservation(uncertainAction.ActionId, "screen-uncertain-after", "window-1", "fresh controlled screenshot");
    if (!actions.Resolve(uncertainAction.ActionId, "screen-uncertain-after", "uncertain", "An unexpected confirmation dialog obscures the share state", "window-1")
        || actions.Current?.Status != ComputerUseActionStatus.NeedsUserInput)
        throw new Exception("An unexpected dialog or uncertain state was not surfaced as requiring user input.");
    try { actions.Begin("task-retry", null, null, "computer_click", "same ambiguous control", "screen-uncertain-after"); throw new Exception("An uncertain possibly non-idempotent action was blindly repeated."); }
    catch (InvalidOperationException) { }
    if (!actions.AttachObservation(uncertainAction.ActionId, "screen-after-dialog-change", "window-1", "new observation after dialog was resolved")
        || !actions.Resolve(uncertainAction.ActionId, "screen-after-dialog-change", "not_achieved", "Fresh state shows the goal was not reached", "window-1")
        || actions.HasUnverifiedAction)
        throw new Exception("A fresh re-observation could not resolve uncertainty before a safe fallback.");
    if (!AgentPlanOutcome.CanCompleteComputerStep("Needs confirmation", hasLinkedAction: false, linkedActionVerified: false, exactFreshObservationMatches: true)
        || AgentPlanOutcome.CanCompleteComputerStep("Needs confirmation", hasLinkedAction: false, linkedActionVerified: false, exactFreshObservationMatches: false))
        throw new Exception("Observation-only plan steps did not require the exact fresh observation.");
    var actionsBeforeAlreadySatisfied = actions.History.Count;
    if (!AgentPlanOutcome.CanCompleteComputerStep("Needs confirmation", hasLinkedAction: false, linkedActionVerified: false, exactFreshObservationMatches: true)
        || actions.History.Count != actionsBeforeAlreadySatisfied)
        throw new Exception("An already-satisfied observed state should complete without sending an unnecessary input action.");
    var beforeAnyAction = new ComputerUseActionLedger();
    if (beforeAnyAction.HasUnverifiedAction || beforeAnyAction.History.Count != 0)
        throw new Exception("A provider failure before tool execution must not fabricate a dispatched desktop action.");
    Console.WriteLine("PASS: action ledger requires a fresh post-action observation, blocks stale/overlapping inputs, preserves uncertain dispatch and cancellation, and reconciles failed-click/verified-shortcut Discord scenarios without claiming unverified success.");

    var scopedActions = new ComputerUseActionLedger();
    scopedActions.BeginTask("old-request");
    var staleFailure = scopedActions.Begin("old-request", null, null, "computer_click", "Untitled - Notepad", "old-screen");
    scopedActions.MarkExecuting(staleFailure.ActionId);
    scopedActions.MarkFailed(staleFailure.ActionId, "The requested result was not achieved.");
    if (scopedActions.Current?.Status != ComputerUseActionStatus.Failed) throw new Exception("The failed fixture action was not recorded.");
    scopedActions.BeginTask("new-request");
    if (scopedActions.Current is not null || scopedActions.HasUnverifiedAction)
        throw new Exception("A stale desktop failure leaked into a newer chat request.");
    Console.WriteLine("PASS: a fresh request scope cannot inherit or repeat a previous Notepad action outcome.");

    var map = new ScreenshotCoordinateMap(new System.Drawing.Rectangle(-1920, 0, 3840, 2160), 1600, 900);
    var mappedCenter = map.ToDesktop(800, 450);
    var mappedTopLeft = map.ToDesktop(0, 0);
    var mappedBottomRight = map.ToDesktop(1599, 899);
    if (mappedCenter.X is < 0 or > 2 || mappedCenter.Y is < 1078 or > 1082
        || mappedTopLeft.X != -1919 || mappedTopLeft.Y != 1
        || mappedBottomRight.X != 1918 || mappedBottomRight.Y != 2158)
        throw new Exception("Screenshot pixels did not map to physical desktop coordinates including a negative monitor origin.");
    try { map.ToDesktop(1600, 900); throw new Exception("An out-of-image coordinate was accepted."); }
    catch (ArgumentOutOfRangeException) { }
    Console.WriteLine("PASS: screenshot coordinates map proportionally to physical desktop pixels across negative monitor origins and reject out-of-image points.");

    var nativeMap = new ScreenshotCoordinateMap(new System.Drawing.Rectangle(0, 0, 1920, 1080), 1920, 1080);
    if (nativeMap.ToDesktop(1200, 700) != (1200, 700)) throw new Exception("Native-size screenshot mapping changed an exact pixel coordinate.");
    var halfMap = new ScreenshotCoordinateMap(new System.Drawing.Rectangle(0, 0, 1920, 1080), 960, 540);
    if (halfMap.ToDesktop(400, 250) != (801, 501)) throw new Exception("Half-size screenshot coordinates did not map back through exact image dimensions.");
    var croppedMap = new ScreenshotCoordinateMap(new System.Drawing.Rectangle(-740, 180, 640, 400), 320, 200);
    if (croppedMap.ToDesktop(10, 10) != (-719, 201)) throw new Exception("A cropped image with a nonzero/negative desktop origin mapped incorrectly.");
    foreach (var dpi in new[] { 125, 150, 200 })
    {
        var scale = dpi / 100d;
        var physicalBounds = new System.Drawing.Rectangle(-((int)(1600 * scale)), 0, (int)(1600 * scale), (int)(900 * scale));
        var dpiMap = new ScreenshotCoordinateMap(physicalBounds, 800, 450);
        var point = dpiMap.ToDesktop(400, 225);
        if (Math.Abs(point.X - (physicalBounds.X + physicalBounds.Width / 2)) > Math.Ceiling(scale) || Math.Abs(point.Y - (physicalBounds.Y + physicalBounds.Height / 2)) > Math.Ceiling(scale))
            throw new Exception($"Screenshot mapping applied an incorrect extra scale at {dpi}% DPI.");
    }
    var targetMap = new ScreenshotCoordinateMap(new System.Drawing.Rectangle(-1920, -100, 3840, 2160), 1600, 900);
    var visualTarget = new VisualMouseTarget("shot-current", new System.Drawing.Rectangle(700, 400, 120, 60), 760, 430, .93);
    var mappedTarget = ComputerMouseTargeting.Map(visualTarget, "shot-current", targetMap,
        new System.Drawing.Rectangle(-500, 300, 1500, 1000));
    if (!mappedTarget.DesktopBounds.Contains(mappedTarget.IntendedDesktopPoint)
        || mappedTarget.PositionTolerancePixels < 1 || !ComputerMouseTargeting.CursorMatchesTarget(mappedTarget.IntendedDesktopPoint, mappedTarget.IntendedDesktopPoint, mappedTarget.DesktopBounds, mappedTarget.PositionTolerancePixels)
        || ComputerMouseTargeting.CursorMatchesTarget(mappedTarget.IntendedDesktopPoint, new System.Drawing.Point(mappedTarget.IntendedDesktopPoint.X + 20, mappedTarget.IntendedDesktopPoint.Y), mappedTarget.DesktopBounds, mappedTarget.PositionTolerancePixels))
        throw new Exception("Visual target mapping or measured cursor-position validation is incorrect.");
    foreach (var invalid in new[]
    {
        (Target: visualTarget with { ObservationId = "stale" }, Id: "shot-current", Bounds: (System.Drawing.Rectangle?)null),
        (Target: visualTarget with { Confidence = .2 }, Id: "shot-current", Bounds: (System.Drawing.Rectangle?)null),
        (Target: visualTarget with { ImageX = 699 }, Id: "shot-current", Bounds: (System.Drawing.Rectangle?)null),
        (Target: visualTarget, Id: "shot-current", Bounds: (System.Drawing.Rectangle?)new System.Drawing.Rectangle(0, 0, 300, 200))
    })
    {
        try { ComputerMouseTargeting.Map(invalid.Target, invalid.Id, targetMap, invalid.Bounds); throw new Exception("Invalid/stale/foreign-window visual target was accepted."); }
        catch (InvalidOperationException) { }
    }
    try { ComputerMouseTargeting.Map(visualTarget with { ImageBounds = new System.Drawing.Rectangle(1590, 100, 30, 30), ImageX = 1595, ImageY = 110 }, "shot-current", targetMap); throw new Exception("Out-of-bounds visual target was accepted."); }
    catch (ArgumentOutOfRangeException) { }
    if (ComputerMouseTargeting.CursorMatchesTarget(mappedTarget.IntendedDesktopPoint, new System.Drawing.Point(1, 1), mappedTarget.DesktopBounds, mappedTarget.PositionTolerancePixels))
        throw new Exception("A cursor read-back outside the target was accepted.");
    var fakeMouse = new FakeMouseInput(mappedTarget.IntendedDesktopPoint);
    var clickPosition = VerifiedMouseClick.Execute(fakeMouse, mappedTarget.IntendedDesktopPoint, mappedTarget.DesktopBounds, mappedTarget.PositionTolerancePixels);
    if (fakeMouse.ClickCount != 1 || clickPosition != mappedTarget.IntendedDesktopPoint || fakeMouse.ReadCount != 3)
        throw new Exception("The verified mouse executor did not position, read back twice, then dispatch exactly one click.");
    var displacedMouse = new FakeMouseInput(new System.Drawing.Point(mappedTarget.IntendedDesktopPoint.X + 25, mappedTarget.IntendedDesktopPoint.Y));
    try { VerifiedMouseClick.Execute(displacedMouse, mappedTarget.IntendedDesktopPoint, mappedTarget.DesktopBounds, mappedTarget.PositionTolerancePixels); throw new Exception("A click was dispatched after cursor-position mismatch."); }
    catch (InvalidOperationException) { if (displacedMouse.ClickCount != 0) throw new Exception("The executor clicked despite an unreconciled cursor position."); }
    Console.WriteLine("PASS: visual targeting rejects stale IDs, low confidence, out-of-target points, out-of-window boxes and cursor discrepancies; native, resized, cropped, negative-origin and 125/150/200% DPI maps are exact.");

    var monitorLeft = new MonitorGeometry("DISPLAY2", new System.Drawing.Rectangle(-1280, 0, 1280, 1024), new System.Drawing.Rectangle(-1280, 0, 1280, 984), false, 144, 144);
    var monitorPrimary = new MonitorGeometry("DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1040), true, 96, 96);
    var monitorPortrait = new MonitorGeometry("DISPLAY3", new System.Drawing.Rectangle(1920, -900, 900, 1600), new System.Drawing.Rectangle(1920, -900, 900, 1560), false, 192, 192);
    var discovered = new[] { monitorLeft, monitorPrimary, monitorPortrait };
    if (DesktopGeometry.FindMonitorForWindow(new System.Drawing.Rectangle(-1100, 40, 900, 700), discovered)?.Id != "DISPLAY2"
        || DesktopGeometry.FindMonitorForWindow(new System.Drawing.Rectangle(2100, -700, 500, 900), discovered)?.Id != "DISPLAY3"
        || DesktopGeometry.FindMonitorForWindow(new System.Drawing.Rectangle(400, 200, 800, 600), discovered)?.Id != "DISPLAY1"
        || DesktopGeometry.FindMonitorForWindow(new System.Drawing.Rectangle(4000, 3000, 400, 400), discovered) is not null
        || monitorPortrait.Orientation != "Portrait" || monitorLeft.ScalePercent != 150 || monitorPrimary.ScalePercent != 100
        || !DesktopGeometry.CoversMonitor(monitorPrimary.Bounds, monitorPrimary.Bounds))
        throw new Exception("Multi-monitor association, mixed DPI, portrait geometry, or fullscreen bounds were calculated incorrectly.");
    Console.WriteLine("PASS: desktop geometry maps windows across negative coordinates, mixed DPI, portrait monitors, and avoids falsely assigning off-screen windows.");

    var privateArgs = FirefoxLaunch.BuildPrivateWindowArguments("https://www.ebay.com");
    if (!privateArgs.SequenceEqual(new[] { "-private-window", "https://www.ebay.com/" })
        || !FirefoxLaunch.BuildPrivateWindowArguments(null).SequenceEqual(new[] { "-private-window" }))
        throw new Exception("Firefox private-window launch arguments did not include the supported mode and normalized URL.");
    foreach (var unsafeUrl in new[] { "javascript:alert(1)", "file:///C:/secret.txt", "https://user:pass@example.com" })
    {
        try { FirefoxLaunch.BuildPrivateWindowArguments(unsafeUrl); throw new Exception($"Firefox launcher accepted an unsafe URL: {unsafeUrl}"); }
        catch (ArgumentException) { }
    }
    if (!FirefoxLaunch.IsMozillaFirefoxProduct("Firefox", "Mozilla Corporation")
        || FirefoxLaunch.IsMozillaFirefoxProduct("Not Firefox", "Unknown Publisher"))
        throw new Exception("Firefox resolver did not validate the executable's version resource publisher fields.");
    Console.WriteLine("PASS: Firefox private-window arguments are separated from shell parsing; unsafe URLs and non-Mozilla executables are rejected.");

    var desktopToolSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs"));
    if (!desktopToolSource.Contains("name\":\"inspect_desktop\"", StringComparison.Ordinal)
        || !desktopToolSource.Contains("name\":\"open_private_browser\"", StringComparison.Ordinal)
        || !desktopToolSource.Contains("CaptureMonitor(monitor.Id", StringComparison.Ordinal)
        || !desktopToolSource.Contains("_lastMonitorObservationMaps.TryGetValue", StringComparison.Ordinal)
        || !desktopToolSource.Contains("private_window_verified = false", StringComparison.Ordinal)
        || !desktopToolSource.Contains("!existingFirefoxWindows.Contains(window.Handle)", StringComparison.Ordinal)
        || !desktopToolSource.Contains("TryGetWindowBounds(observedForeground) != _lastComputerObservationForegroundBounds", StringComparison.Ordinal))
        throw new Exception("Desktop discovery, monitor-scoped screenshot mapping, or honest Firefox private-state verification is not wired into the app.");
    Console.WriteLine("PASS: app tool schemas wire structured desktop inspection, per-monitor image mapping, and Firefox private-window launch without claiming unverified success.");
    if (!WindowsComputerUseService.IsExactAccessibleIdentifierMatch("Save Settings", "Save-Settings")
        || WindowsComputerUseService.IsExactAccessibleIdentifierMatch("Save", "Save Settings")
        || !WindowsComputerUseService.IsExactAccessibleIdentifierMatch("save_button", "Save Button"))
        throw new Exception("UI Automation identifiers are not normalized and matched exactly.");
    Console.WriteLine("PASS: UI Automation identifiers use normalized exact matching and do not allow ambiguous partial matches.");

    var computerUseSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs"));
    var clickSchemaStart = computerUseSource.IndexOf("name\":\"computer_click", StringComparison.Ordinal);
    if (clickSchemaStart < 0) throw new Exception("The structured computer_click schema could not be found.");
    var clickSchemaEnd = computerUseSource.IndexOf("computer_type_text", clickSchemaStart, StringComparison.Ordinal);
    var clickSchema = computerUseSource[clickSchemaStart..clickSchemaEnd];
    if (!clickSchema.Contains("observation_id", StringComparison.Ordinal) || !clickSchema.Contains("target_bounds", StringComparison.Ordinal)
        || !clickSchema.Contains("confidence", StringComparison.Ordinal) || !clickSchema.Contains("required", StringComparison.Ordinal))
        throw new Exception("Visual click requests are not bound to a screenshot ID, identified target rectangle, and confidence value.");
    var mouseInputSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Services", "WindowsComputerUseService.cs"));
    var targetingSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Services", "ComputerMouseTargeting.cs"));
    if (!mouseInputSource.Contains("SetCursorPos(position.X, position.Y)", StringComparison.Ordinal)
        || !mouseInputSource.Contains("VerifiedMouseClick.Execute", StringComparison.Ordinal)
        || !targetingSource.Contains("CursorMatchesTarget", StringComparison.Ordinal))
        throw new Exception("Native mouse clicks do not verify actual cursor placement before dispatch.");
    var manifestPath = Path.Combine(AppContext.BaseDirectory, "app.manifest");
    if (!File.Exists(manifestPath)) throw new Exception("The Per-Monitor-V2 application manifest was not copied for test inspection.");
    var manifestText = await File.ReadAllTextAsync(manifestPath);
    var appStartupSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
    var projectSource = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Jarvis.csproj"));
    if (!manifestText.Contains("asInvoker", StringComparison.Ordinal)
        || !appStartupSource.Contains("Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2)", StringComparison.Ordinal)
        || !projectSource.Contains("ApplicationHighDpiMode>PerMonitorV2", StringComparison.Ordinal))
        throw new Exception("Per-Monitor-V2 awareness is not established before WPF startup with normal user privileges.");
    var sessionStart = computerUseSource.IndexOf("private async Task<bool> EnsureComputerUseSessionAsync", StringComparison.Ordinal);
    var sessionStop = computerUseSource.IndexOf("private void StopComputerUseSession", sessionStart, StringComparison.Ordinal);
    var sessionBlock = computerUseSource[sessionStart..sessionStop];
    var approvalPosition = sessionBlock.IndexOf("var approval = await RequestApprovalInlineAsync", StringComparison.Ordinal);
    var overlayPosition = sessionBlock.IndexOf("new ComputerUseOverlayWindow", StringComparison.Ordinal);
    if (approvalPosition < 0 || overlayPosition < approvalPosition
        || sessionBlock[..approvalPosition].Contains("_computerUseOverlay.Show", StringComparison.Ordinal))
        throw new Exception("The active Computer Use overlay is created or shown before session approval.");
    if (!sessionBlock.Contains("_computerUse.GetTargetMonitorBounds()", StringComparison.Ordinal)
        || !computerUseSource.Contains("_lastComputerObservationMap", StringComparison.Ordinal))
        throw new Exception("Monitor targeting or screenshot coordinate mapping is not wired into Computer Use.");
    var focusStart = computerUseSource.IndexOf("private async Task<string> FocusComputerUseWindowAsync", StringComparison.Ordinal);
    var focusEnd = computerUseSource.IndexOf("private async Task<string> ExecuteComputerUseToolAsync", focusStart, StringComparison.Ordinal);
    var focusBlock = computerUseSource[focusStart..focusEnd];
    if (focusBlock.Contains("ShowWindow(window.Handle, SwRestore)", StringComparison.Ordinal)
        || !focusBlock.Contains("IsIconic(window.Handle)", StringComparison.Ordinal))
        throw new Exception("Focusing an application still restores its window or does not protect minimized window state.");
    if (!computerUseSource.Contains("currentForeground != observedForeground", StringComparison.Ordinal)
        || !computerUseSource.Contains("_lastComputerObservationForegroundHandle = foregroundAfter", StringComparison.Ordinal))
        throw new Exception("Keyboard/mouse actions are not guarded by the foreground application captured with the current screenshot.");
    if (!computerUseSource.Contains("if (computerActionExecutedInBatch)", StringComparison.Ordinal)
        || !computerUseSource.Contains("This tool call was not executed. A desktop action already ran from this model response", StringComparison.Ordinal)
        || !computerUseSource.Contains("action_id = actionRecord.ActionId", StringComparison.Ordinal)
        || !computerUseSource.Contains("post-action observation_id", StringComparison.Ordinal)
        || !computerUseSource.Contains("_computerUseActionLedger.IsVerifiedForPlanStep", StringComparison.Ordinal))
        throw new Exception("The live model tool loop does not enforce the single-action observation barrier or exact verification linkage.");
    Console.WriteLine("PASS: session approval precedes overlay creation, with monitor-scoped overlay and screenshot coordinate mapping wired.");
    Console.WriteLine("PASS: focus does not restore/maximize windows, and desktop input requires the observed foreground window to remain active.");

    var mainWindowMarkupPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");
    if (File.Exists(mainWindowMarkupPath))
    {
        var markup = await File.ReadAllTextAsync(mainWindowMarkupPath);
        if (markup.Contains("x:Name=\"ComputerUseButton\"", StringComparison.Ordinal)
            || markup.Contains("ComputerUseButton_Click", StringComparison.Ordinal)
            || markup.Contains("Computer Use ist nur aktiv, wenn du es unten im Chat einschaltest", StringComparison.Ordinal))
            throw new Exception("The manual Computer Use activation workflow remains visible in the main window.");
        Console.WriteLine("PASS: main window markup no longer exposes the manual Computer Use activation control.");
    }
    var mainWindowCodePath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs");
    if (File.Exists(mainWindowCodePath))
    {
        var mainWindowCode = await File.ReadAllTextAsync(mainWindowCodePath);
        if (!mainWindowCode.Contains("computer_click", StringComparison.Ordinal)
            || !mainWindowCode.Contains("read_web_page", StringComparison.Ordinal)
            || !mainWindowCode.Contains("new WebResearchService()", StringComparison.Ordinal)
            || !mainWindowCode.Contains("PublicWebPageReader().ReadAsync", StringComparison.Ordinal))
            throw new Exception("Autonomous Computer Use or independent web research is not wired into the main agent request flow.");
        Console.WriteLine("PASS: Computer Use and independent Google/fallback/page-reading tools are wired into the agent request flow.");
    }

    if (!WindowsComputerUseService.ParseKeyChord("CTRL+SHIFT+TAB").SequenceEqual(new ushort[] { 0x11, 0x10, 0x09 })
        || WindowsComputerUseService.ParseKeyChord("ENTER").Single() != 0x0D)
        throw new Exception("Supported Computer Use key chords were not mapped correctly.");
    foreach (var unsafeKey in new[] { "WIN+R", "CTRL+ALT+DELETE", "CTRL+F12", "NOT_A_KEY" })
    {
        try { WindowsComputerUseService.ParseKeyChord(unsafeKey); throw new Exception($"Unsafe or unsupported key chord was accepted: {unsafeKey}"); }
        catch (ArgumentException) { }
    }
    using (var cancelledComputerUse = new CancellationTokenSource())
    {
        cancelledComputerUse.Cancel();
        try { new WindowsComputerUseService().TypeText("must not be sent", cancelledComputerUse.Token); throw new Exception("Computer Use ignored a cancelled input session."); }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("PASS: Computer Use validates shortcuts, rejects Windows/system key sequences, and stops input when cancelled.");

    var commandRoot = Path.Combine(root, "approved-command-folder");
    Directory.CreateDirectory(commandRoot);
    using (var commands = new CommandExecutionService())
    {
        try { CommandExecutionService.ValidateWorkingDirectory(root, [commandRoot]); throw new Exception("A command working directory outside the approved folder was accepted."); }
        catch (UnauthorizedAccessException) { }
        try { CommandExecutionService.ValidateWorkingDirectory(Path.Combine(root, "missing-command-folder"), [commandRoot]); throw new Exception("A missing command working directory was accepted."); }
        catch (DirectoryNotFoundException) { }
        if (CommandExecutionService.ValidateWorkingDirectory(commandRoot, [commandRoot]) != Path.GetFullPath(commandRoot)) throw new Exception("An approved command working directory was rejected.");

        async Task<CommandJobSnapshot> WaitForJob(string id, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var state = commands.Get(id);
            while (state.State == "running" && DateTime.UtcNow < deadline) { await Task.Delay(50); state = commands.Get(id); }
            return state;
        }

        var powershellJob = await commands.StartAsync("powershell",
            "[Console]::Out.WriteLine('jarvis-stdout'); [Console]::Error.WriteLine('jarvis-stderr'); [Console]::Out.WriteLine('access_token=verysecretvalue'); exit 7",
            commandRoot, 10, [commandRoot], CancellationToken.None);
        var powershellResult = await WaitForJob(powershellJob.JobId, 10);
        if (powershellResult.State != "failed" || powershellResult.ExitCode != 7
            || !powershellResult.StandardOutput.Contains("jarvis-stdout", StringComparison.Ordinal)
            || !powershellResult.StandardError.Contains("jarvis-stderr", StringComparison.Ordinal)
            || powershellResult.StandardOutput.Contains("verysecretvalue", StringComparison.Ordinal))
            throw new Exception("PowerShell command result, exit code, or secret redaction was incorrect.");

        var cmdJob = await commands.StartAsync("cmd", "echo Jarvis CMD smoke", commandRoot, 10, [commandRoot], CancellationToken.None);
        var cmdResult = await WaitForJob(cmdJob.JobId, 10);
        if (cmdResult.State != "completed" || cmdResult.ExitCode != 0 || !cmdResult.StandardOutput.Contains("Jarvis CMD smoke", StringComparison.OrdinalIgnoreCase))
            throw new Exception("CMD did not return its real successful process output.");

        var timeoutJob = await commands.StartAsync("powershell", "Start-Sleep -Seconds 5", commandRoot, 1, [commandRoot], CancellationToken.None);
        var timeoutResult = await WaitForJob(timeoutJob.JobId, 8);
        if (timeoutResult.State != "timed_out") throw new Exception("Command timeout did not terminate and report the process.");

        var cancelJob = await commands.StartAsync("powershell", "Start-Sleep -Seconds 15", commandRoot, 30, [commandRoot], CancellationToken.None);
        var cancelled = commands.Cancel(cancelJob.JobId);
        var cancelResult = await WaitForJob(cancelJob.JobId, 8);
        if (cancelled.State != "cancelled" || cancelResult.State != "cancelled") throw new Exception("Command cancellation did not stop the tracked job.");
    }
    Console.WriteLine("PASS: real PowerShell and CMD jobs, stdout/stderr, nonzero exit code, allowed-folder validation, secret redaction, timeout, and process-tree cancellation.");

    const string testPublishableKey = "sb_publishable_test-key";
    var betaOptions = new SupabaseOptions("https://example.supabase.co", testPublishableKey);
    if (!betaOptions.IsConfigured || new SupabaseOptions("http://example.com", testPublishableKey).IsConfigured || !new SupabaseOptions("http://127.0.0.1:54321", testPublishableKey).IsConfigured) throw new Exception("Supabase endpoint validation did not enforce HTTPS except for local development.");
    using (var betaClient = new SupabaseClient(betaOptions, new BetaStubHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/auth/v1/token", StringComparison.Ordinal))
        {
            if (!request.Headers.TryGetValues("apikey", out var keys) || keys.Single() != testPublishableKey) throw new Exception("Supabase public key header was not sent.");
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

    if (SpeechRecognitionProviderRouter.NormalizeProviderId("windows") != "windows"
        || SpeechRecognitionProviderRouter.NormalizeProviderId("WHISPER") != "whisper"
        || SpeechRecognitionProviderRouter.NormalizeProviderId("ElevenLabs") != "elevenlabs"
        || SpeechRecognitionProviderRouter.NormalizeProviderId("unexpected") != "whisper")
        throw new Exception("Speech-provider selection does not preserve supported values or safely migrate unknown values to Whisper.");
    var speechConfig = new AppConfig { SpeechProvider = "windows", ElevenLabsSttModelId = "scribe_v2" };
    var restoredSpeechConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(speechConfig));
    if (restoredSpeechConfig?.SpeechProvider != "windows" || restoredSpeechConfig.ElevenLabsSttModelId != "scribe_v2") throw new Exception("Speech-provider selection or ElevenLabs STT model was not persisted.");

    var fakeWhisper = new FakeSpeechProvider("whisper", true, (_, _) => Task.FromResult("recognized text"));
    var unavailableWindows = new FakeSpeechProvider("windows", false, (_, _) => throw new InvalidOperationException("unavailable"));
    var speechRouter = new SpeechRecognitionProviderRouter([fakeWhisper, unavailableWindows]);
    var speechRequest = new SpeechTranscriptionRequest("fixture.wav", "fixture.bin", "de-DE");
    if (speechRouter.Get("windows").GetAvailability(speechRequest).Available) throw new Exception("Unavailable native speech components were advertised as ready.");
    if (await speechRouter.Get("whisper").TranscribeAsync(speechRequest, CancellationToken.None) != "recognized text") throw new Exception("Speech transcription result was not returned by the selected provider.");
    var cancelledSpeech = new FakeSpeechProvider("windows", true, async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(5), token); return "unexpected"; });
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
    {
        try { await new SpeechRecognitionProviderRouter([fakeWhisper, cancelledSpeech]).Get("windows").TranscribeAsync(speechRequest, cancellation.Token); throw new Exception("Speech cancellation was ignored."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    var failingSpeech = new FakeSpeechProvider("windows", true, (_, _) => Task.FromException<string>(new InvalidDataException("recognition failed")));
    try { await new SpeechRecognitionProviderRouter([fakeWhisper, failingSpeech]).Get("windows").TranscribeAsync(speechRequest, CancellationToken.None); throw new Exception("Speech provider errors were swallowed."); }
    catch (InvalidDataException ex) when (ex.Message == "recognition failed") { }
    Console.WriteLine("PASS: speech provider selection, settings persistence, unavailable components, transcription results, cancellation and provider errors are handled explicitly.");

    var sttAudio = Path.Combine(root, "elevenlabs-stt.wav");
    await File.WriteAllBytesAsync(sttAudio, [82, 73, 70, 70, 36, 0, 0, 0, 87, 65, 86, 69]);
    string sttUri = "", sttKey = "", sttBody = "";
    using (var sttHttp = new HttpClient(new BetaStubHandler(request =>
    {
        sttUri = request.RequestUri?.AbsoluteUri ?? "";
        sttKey = request.Headers.TryGetValues("xi-api-key", out var keys) ? keys.Single() : "";
        sttBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"language_code\":\"de\",\"text\":\"Hey Jarvis, was geht?\"}") };
    })))
    using (var stt = new ElevenLabsSttService(sttHttp))
    {
        var transcript = await stt.TranscribeAsync(sttAudio, "scribe_v2", "fixture-secret-key", CancellationToken.None);
        if (transcript != "Hey Jarvis, was geht?" || sttUri != "https://api.elevenlabs.io/v1/speech-to-text" || sttKey != "fixture-secret-key"
            || !sttBody.Contains("name=file", StringComparison.Ordinal) || !sttBody.Contains("name=model_id", StringComparison.Ordinal) || !sttBody.Contains("scribe_v2", StringComparison.Ordinal)
            || sttBody.Contains("fixture-secret-key", StringComparison.Ordinal))
            throw new Exception("ElevenLabs STT request construction, authentication header, multipart audio, model, or transcription parsing is incorrect.");
    }
    using (var unauthorizedStt = new ElevenLabsSttService(new HttpClient(new BetaStubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))))
    {
        try { await unauthorizedStt.TranscribeAsync(sttAudio, "scribe_v2", "rejected", CancellationToken.None); throw new Exception("ElevenLabs STT authentication failure was accepted."); }
        catch (ElevenLabsException ex) when (ex.Kind == ElevenLabsErrorKind.Authentication) { }
    }
    using (var malformedStt = new ElevenLabsSttService(new HttpClient(new BetaStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad") }))))
    {
        try { await malformedStt.TranscribeAsync(sttAudio, "scribe_v2", "fixture", CancellationToken.None); throw new Exception("Malformed ElevenLabs STT JSON was accepted."); }
        catch (ElevenLabsException ex) when (ex.Kind == ElevenLabsErrorKind.Parsing) { }
    }
    using (var cancelledStt = new ElevenLabsSttService(new HttpClient(new AsyncStubHandler(async (_, token) => { await Task.Delay(2000, token); return new HttpResponseMessage(HttpStatusCode.OK); }))))
    using (var sttCancellation = new CancellationTokenSource(20))
    {
        try { await cancelledStt.TranscribeAsync(sttAudio, "scribe_v2", "fixture", sttCancellation.Token); throw new Exception("ElevenLabs STT cancellation was ignored."); }
        catch (OperationCanceledException) when (sttCancellation.IsCancellationRequested) { }
    }
    if (ElevenLabsSttService.IsValidModelId("eleven_multilingual_v2") || !ElevenLabsSttService.IsValidModelId("scribe_v2"))
        throw new Exception("ElevenLabs TTS and STT model identifiers were not kept distinct.");
    Console.WriteLine("PASS: ElevenLabs STT endpoint, multipart request, Scribe model validation, response parsing, authentication errors, and cancellation are deterministic.");

    var nativeSpeech = new WindowsNativeSpeechRecognitionProvider();
    var nativeAvailability = nativeSpeech.GetAvailability(speechRequest);
    Console.WriteLine(nativeAvailability.Available
        ? $"PASS: Windows native speech provider found installed recognizer language(s): {string.Join(", ", nativeAvailability.Languages)}."
        : $"SKIP: Windows native speech integration test unavailable on this machine: {nativeAvailability.Message}");

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

sealed class AsyncStubHandler(Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> responder) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
}

sealed class FakeMouseInput(System.Drawing.Point actualPosition) : IMouseInputController
{
    public int ClickCount { get; private set; }
    public int ReadCount { get; private set; }
    public bool SetCursorPosition(System.Drawing.Point position) => true;
    public System.Drawing.Point GetCursorPosition() { ReadCount++; return actualPosition; }
    public void SendLeftClick() => ClickCount++;
}

sealed class FakeSpeechProvider(string id, bool available, Func<SpeechTranscriptionRequest, CancellationToken, Task<string>> transcribe) : ISpeechRecognitionProvider
{
    public string Id => id;
    public SpeechProviderAvailability GetAvailability(SpeechTranscriptionRequest request) => new(available, available ? "ready" : "unavailable", []);
    public Task PrepareAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken) => available ? Task.CompletedTask : Task.FromException(new InvalidOperationException("unavailable"));
    public Task<string> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken) => transcribe(request, cancellationToken);
}
