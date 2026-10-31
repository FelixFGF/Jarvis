using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MessageBox = System.Windows.MessageBox;
using Forms = System.Windows.Forms;

namespace Jarvis.Setup;

public partial class MainWindow : Window
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient PullHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ObservableCollection<BackendChoice> _backends = [];
    private readonly ObservableCollection<ModelChoice> _models = [];
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _installCancellation;
    private long _payloadBytes;
    private string _language = "en";
    private bool _isUpdate;
    private int _wizardStep;
    private string _installedVersion = "unknown";

    public MainWindow()
    {
        InitializeComponent();
        Opacity = 0;
        BackendBox.ItemsSource = _backends;
        ModelBox.ItemsSource = _models;
        InstallPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Jarvis.Services.JarvisStoragePaths.ProductDirectoryName);
        LanguageBox.SelectedValue = "en";
        ApplyLanguage();
        Loaded += async (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
            StartupSpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.25)) { RepeatBehavior = RepeatBehavior.Forever });
            StartupStatusText.Text = _language == "de"
                ? "Prüfe, ob Jarvis bereits auf diesem PC installiert ist …"
                : "Checking whether Jarvis is already installed on this PC…";
            var existingInstall = FindExistingInstallPath();
            if (existingInstall is not null)
            {
                _isUpdate = true;
                InstallPathBox.Text = existingInstall;
                var installedFileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(existingInstall, "Jarvis.exe"));
                _installedVersion = installedFileVersion.ProductVersion ?? installedFileVersion.FileVersion ?? "unknown";
                StartupStatusText.Text = _language == "de"
                    ? $"Jarvis {_installedVersion} gefunden. Update auf 1.1.3 wird vorbereitet …"
                    : $"Jarvis {_installedVersion} found. Preparing update to 1.1.3…";
                StatusText.Text = "Vorhandene Jarvis-Installation erkannt. Die Programmdateien werden aktualisiert; lokale Einstellungen, Chats und Logs bleiben erhalten.";
            }
            else StartupStatusText.Text = _language == "de"
                ? "Keine vorhandene Jarvis-Installation gefunden. Setup wird vorbereitet …"
                : "No existing Jarvis installation found. Preparing setup…";
            HardwareText.Text = HardwareSummary();
            _payloadBytes = GetPayloadSize();
            DiskText.Text = DiskSummary(InstallPathBox.Text, _payloadBytes);
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
            DataPathDetail.Text = _language == "de"
                ? $"Programmdateien liegen im gewählten Ordner. Jarvis-Modelle und Zwischendateien liegen unter „{Path.Combine(InstallPathBox.Text, "JarvisData")}“. Einstellungen, Chats und Logs liegen unter „{appDataPath}“. Ollama verwaltet seine eigenen Modelle."
                : $"Program files use the selected folder. Jarvis models and cache files are stored in “{Path.Combine(InstallPathBox.Text, "JarvisData")}”. Settings, chats and logs are stored in “{appDataPath}”. Ollama manages its own models.";
            // Updating an existing app does not need model-server discovery. Avoid
            // waiting on offline or slow local AI endpoints before showing the updater.
            if (!_isUpdate)
            {
                StartupStatusText.Text = _language == "de"
                    ? "Prüfe lokale KI-Backends für die neue Installation …"
                    : "Checking local AI backends for the new installation…";
                await RefreshBackendsAsync();
            }
            StartupProgress.Visibility = Visibility.Collapsed;
            if (_isUpdate)
            {
                UpdateVersionText.Text = _language == "de"
                    ? $"Installierte Version {_installedVersion}  →  Neue Version 1.1.3"
                    : $"Installed version {_installedVersion}  →  New version 1.1.3";
                UpdateVersionText.Visibility = Visibility.Visible;
                StatusText.Text = _language == "de"
                    ? "Vorhandene Installation erkannt. Jarvis wird auf Version 1.1.3 aktualisiert; Einstellungen, Chats, Modelle und Logs bleiben erhalten."
                    : "Existing installation found. Jarvis will update to version 1.1.3; settings, chats, models and logs will be preserved.";
            }
            SetWizardStep(_isUpdate ? 2 : 0);
            StartupSpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            StartupOverlay.Visibility = Visibility.Collapsed;
        };
        InstallPathBox.TextChanged += (_, _) => DiskText.Text = DiskSummary(InstallPathBox.Text, _payloadBytes);
    }

    private void SetWizardStep(int step)
    {
        _wizardStep = Math.Clamp(step, 0, 2);
        BackendCard.Visibility = !_isUpdate && _wizardStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelCard.Visibility = !_isUpdate && _wizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        InstallCard.Visibility = _isUpdate || _wizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = !_isUpdate && _wizardStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = !_isUpdate && _wizardStep < 2 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = _wizardStep != 0 || IsBackendStepValid();
        InstallButton.Visibility = _isUpdate || _wizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Content = _isUpdate
            ? (_language == "de" ? "Jarvis aktualisieren" : "Update Jarvis")
            : (_language == "de" ? "Jarvis installieren" : "Install Jarvis");
        WizardStepText.Text = _isUpdate
            ? (_language == "de" ? "Updater · Version 1.1.3" : "Updater · Version 1.1.3")
            : _language == "de"
                ? $"Schritt {_wizardStep + 1} von 3 · {new[] { "KI-Backend", "Modell auswählen", "Installationsordner" }[_wizardStep]}"
                : $"Step {_wizardStep + 1} of 3 · {new[] { "AI backend", "Choose a model", "Installation folder" }[_wizardStep]}";
        WizardTitle.Text = _isUpdate
            ? (_language == "de" ? "Jarvis auf Version 1.1.3 aktualisieren" : "Update Jarvis to version 1.1.3")
            : (_language == "de" ? "Jarvis auf diesem PC einrichten" : "Set up Jarvis on this PC");
        if (_isUpdate)
        {
            UpdateVersionText.Text = _language == "de"
                ? $"Installierte Version {_installedVersion}  →  Neue Version 1.1.3"
                : $"Installed version {_installedVersion}  →  New version 1.1.3";
            UpdateVersionText.Visibility = Visibility.Visible;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardStep == 0 && !IsBackendStepValid())
        {
            StatusText.Text = _language == "de" ? "Wähle ein Backend und gib eine gültige Serveradresse ein, bevor du fortfährst." : "Choose a backend and enter a valid server address before continuing.";
            return;
        }
        SetWizardStep(_wizardStep + 1);
    }

    private bool IsBackendStepValid()
    {
        if (BackendBox.SelectedItem is not BackendChoice backend) return false;
        if (backend.IsOllama) return backend.IsCompatible || IsValidOpenAiEndpoint(BionicEndpointBox.Text);
        return IsValidOpenAiEndpoint(BionicEndpointBox.Text);
    }

    private static bool IsValidOpenAiEndpoint(string? value) =>
        TryNormalizeOpenAiEndpoint(value, out _);

    private void BackendEndpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (NextButton is not null && _wizardStep == 0) NextButton.IsEnabled = IsBackendStepValid();
    }

    private void BackendApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (NextButton is not null && _wizardStep == 0) NextButton.IsEnabled = IsBackendStepValid();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => SetWizardStep(_wizardStep - 1);

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is not string language || language == _language) return;
        _language = language;
        ApplyLanguage();
        SetWizardStep(_wizardStep);
        UpdateModels();
        RefreshBackendLanguage();
    }

    private void ApplyLanguage()
    {
        if (LanguageBox is null) return;
        Jarvis.Services.UiLocalization.Apply(this, _language);
    }

    private void RefreshBackendLanguage()
    {
        var selectedEndpoint = (BackendBox.SelectedItem as BackendChoice)?.Endpoint;
        for (var i = 0; i < _backends.Count; i++)
        {
            var item = _backends[i];
            var name = item.IsCustom
                ? (_language == "de" ? "Eigener OpenAI-kompatibler Dienst" : "Custom OpenAI-compatible service")
                : item.IsBionic && (item.Name.Contains("Adresse eingeben", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("enter endpoint", StringComparison.OrdinalIgnoreCase))
                    ? (_language == "de" ? "BionicGPT · Adresse eingeben" : "BionicGPT · enter endpoint")
                    : item.IsOllama && !item.IsCompatible
                        ? (_language == "de" ? "Ollama · nicht erreichbar / Adresse eingeben" : "Ollama · unavailable / enter endpoint")
                        : item.Name;
            if (name != item.Name) _backends[i] = item with { Name = name };
        }
        if (selectedEndpoint is not null)
            BackendBox.SelectedItem = _backends.FirstOrDefault(item => item.Endpoint.Equals(selectedEndpoint, StringComparison.OrdinalIgnoreCase));
        var detected = _backends.Where(b => b.IsCompatible || (b.IsBionic && b.Name.Contains("API-Schlüssel", StringComparison.OrdinalIgnoreCase))).ToList();
        if (detected.Count == 0)
        {
            BackendStatus.Text = _language == "de" ? "Kein unterstützter Dienst erreichbar. Du kannst Ollama installieren oder BionicGPT manuell verbinden." : "No supported service is reachable. Install Ollama or connect BionicGPT manually.";
            BackendDetail.Text = _language == "de" ? "Wähle BionicGPT, gib den OpenAI-kompatiblen Endpunkt und API-Schlüssel ein und lade die Modellliste." : "Choose BionicGPT, enter its OpenAI-compatible endpoint and API key, then load its model list.";
        }
        else if (detected.All(b => b.IsBionic && !b.IsCompatible))
        {
            BackendStatus.Text = _language == "de" ? "BionicGPT wurde gefunden, verlangt aber einen API-Schlüssel." : "BionicGPT was found and requires an API key.";
            BackendDetail.Text = _language == "de" ? "Gib den BionicGPT-API-Schlüssel ein und lade die authentifizierte Modellliste." : "Enter your BionicGPT API key and load its authenticated model list.";
        }
        else
        {
            BackendStatus.Text = detected.Count == 1
                ? (_language == "de" ? $"{detected[0].Name} wurde gefunden und ist erreichbar." : $"{detected[0].Name} was found and is reachable.")
                : (_language == "de" ? $"{detected.Count} lokale Backend-Endpunkte wurden gefunden." : $"{detected.Count} local AI backends were found.");
            BackendDetail.Text = _language == "de" ? "Modelle werden vom jeweiligen Server gelesen. Jarvis übernimmt den gewählten kompatiblen Endpunkt und das verfügbare Modell." : "Models are read from each server. Jarvis uses the selected compatible endpoint and available model.";
        }
    }

    private async Task RefreshBackendsAsync()
    {
        RefreshButton.IsEnabled = false;
        BackendStatus.Text = _language == "de" ? "Prüfe lokale KI-Backends …" : "Checking local AI backends…";
        BackendDetail.Text = _language == "de" ? "Es werden nur lokale Endpunkte abgefragt. Es wird nichts an einen Cloud-Dienst gesendet." : "Only local endpoints are checked. Nothing is sent to a cloud service.";
        _backends.Clear();
        var checks = new[]
        {
            CheckOllamaAsync(Http), CheckCompatibleAsync("LM Studio", "http://localhost:1234/v1", Http),
            CheckCompatibleAsync("BionicGPT", "http://localhost:1235/v1", Http), CheckCompatibleAsync("BionicGPT (Port 3000)", "http://localhost:3000/v1", Http),
            CheckCompatibleAsync("BionicGPT Model API", "http://localhost:8080/v1", Http)
        };
        foreach (var result in await Task.WhenAll(checks))
            if (result is not null && !_backends.Any(b => b.Endpoint.Equals(result.Endpoint, StringComparison.OrdinalIgnoreCase))) _backends.Add(result);
        var detectedEndpointCount = _backends.Count;
        if (!_backends.Any(backend => backend.IsBionic)) _backends.Add(new BackendChoice(_language == "de" ? "BionicGPT · Adresse eingeben" : "BionicGPT · enter endpoint", "http://localhost:1235/v1", [], null, false));
        // Keep both choices available even on a clean PC; an unreachable choice is clearly marked and is not reported as detected.
        if (!_backends.Any(backend => backend.IsOllama)) _backends.Add(new BackendChoice("Ollama · nicht erreichbar / Adresse eingeben", "http://localhost:11434/v1", [], "http://localhost:11434", false));
        _backends.Add(new BackendChoice(_language == "de" ? "Eigener OpenAI-kompatibler Dienst" : "Custom OpenAI-compatible service", "https://api.openai.com/v1", [], null, false, true));
        if (_backends.Count == 0)
        {
            OllamaButton.Visibility = Visibility.Visible;
            var installed = GetInstalledBackendNames();
            BackendStatus.Text = installed.Count > 0 ? (_language == "de" ? $"Installiert erkannt, aber kein Dienst erreichbar: {string.Join(", ", installed)}." : $"Installed, but no service is reachable: {string.Join(", ", installed)}.") : (_language == "de" ? "Kein unterstütztes KI-Backend installiert und erreichbar." : "No supported AI backend is installed and reachable.");
            BackendDetail.Text = _language == "de" ? "Installiere optional Ollama über den offiziellen Installer oder verbinde BionicGPT über localhost:1235. Der API-Schlüssel wird für dein Windows-Konto geschützt gespeichert." : "Optionally install Ollama using its official installer, or connect BionicGPT at localhost:1235. The API key is protected for your Windows account.";
        }
        else if (detectedEndpointCount == 0)
        {
            BackendStatus.Text = _language == "de" ? "Kein unterstützter Dienst erreichbar. Du kannst Ollama installieren oder BionicGPT manuell verbinden." : "No supported service is reachable. Install Ollama or connect BionicGPT manually.";
            BackendDetail.Text = _language == "de" ? "Wähle BionicGPT in der Liste, gib den OpenAI-kompatiblen Endpunkt und API-Schlüssel ein und lade die Modellliste. Ollama kannst du über die Schaltfläche installieren." : "Choose BionicGPT, enter its OpenAI-compatible endpoint and API key, then load its model list. You can install Ollama using the button.";
            BackendBox.SelectedIndex = _backends.Count - 1;
        }
        else
        {
            OllamaButton.Visibility = Visibility.Visible;
            BackendBox.SelectedIndex = 0;
            if (_backends.All(b => !b.IsCompatible))
            {
                BackendStatus.Text = _language == "de" ? "BionicGPT wurde gefunden, verlangt aber einen API-Schlüssel." : "BionicGPT was found and requires an API key.";
                BackendDetail.Text = _language == "de" ? "Gib den BionicGPT-API-Schlüssel ein und lade die authentifizierte Modellliste. Alternativ kannst du Ollama als Backend verwenden." : "Enter your BionicGPT API key and load its authenticated model list. You can also use Ollama.";
            }
            else
            {
                BackendStatus.Text = _backends.Count == 1 ? (_language == "de" ? $"{_backends[0].Name} wurde gefunden und ist erreichbar." : $"{_backends[0].Name} was found and is reachable.") : (_language == "de" ? $"{_backends.Count} lokale Backend-Endpunkte wurden gefunden." : $"{_backends.Count} local AI backends were found.");
                BackendDetail.Text = _language == "de" ? "Modelle werden vom jeweiligen Server gelesen. Jarvis übernimmt den gewählten kompatiblen Endpunkt und das verfügbare Modell." : "Models are read from each server. Jarvis uses the selected compatible endpoint and available model.";
            }
        }
        RefreshButton.IsEnabled = true;
        var ollamaInstalled = GetInstalledBackendNames().Contains("Ollama", StringComparer.OrdinalIgnoreCase) || FindOllamaExecutable() is not null;
        OllamaButton.Visibility = _backends.Any(backend => backend.IsOllama && backend.IsCompatible) ? Visibility.Collapsed : Visibility.Visible;
        OllamaButton.Content = ollamaInstalled ? (_language == "de" ? "Ollama starten" : "Start Ollama") : (_language == "de" ? "Ollama installieren" : "Install Ollama");
        UpdateModels();
        ApplyLanguage();
    }

    private static async Task<BackendChoice?> CheckOllamaAsync(HttpClient client)
    {
        const string host = "http://localhost:11434";
        try
        {
            using var response = await client.GetAsync(host + "/api/tags");
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            var ids = JsonNode.Parse(json)?["models"]?.AsArray().Select(x => (string?)x?["name"]).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() ?? [];
            return new BackendChoice("Ollama", host + "/v1", ids, host, true);
        }
        catch { return null; }
    }

    private static string? FindOllamaExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama app.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return pathDirs.Select(dir => Path.Combine(dir.Trim('"'), "ollama.exe")).FirstOrDefault(File.Exists);
    }

    private static async Task<BackendChoice?> CheckCompatibleAsync(string name, string endpoint, HttpClient client)
    {
        try
        {
            using var response = await client.GetAsync(endpoint.TrimEnd('/') + "/models");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return name.StartsWith("BionicGPT", StringComparison.Ordinal) ? new BackendChoice("BionicGPT (API-Schlüssel erforderlich)", endpoint, [], null, false) : null;
            var ids = new List<string>();
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                ids = ExtractOpenAiModelIds(JsonNode.Parse(json));
            }
            if (name.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(endpoint);
                var catalog = await ReadLmStudioCatalogAsync(uri, client);
                ids.AddRange(catalog);
            }
            ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return response.IsSuccessStatusCode || ids.Count > 0 ? new BackendChoice(name, endpoint, ids, null, true) : null;
        }
        catch { return null; }
    }

    private static async Task<List<string>> ReadLmStudioCatalogAsync(Uri endpoint, HttpClient client)
    {
        if (!endpoint.IsLoopback) return [];
        foreach (var (path, collection, idField) in new[] { ("/api/v1/models", "models", "key"), ("/api/v0/models", "data", "id") })
        {
            try
            {
                using var response = await client.GetAsync(new UriBuilder(endpoint) { Path = path, Query = "", Fragment = "" }.Uri);
                if (!response.IsSuccessStatusCode) continue;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())?[collection]?.AsArray();
                if (json is null) continue;
                var ids = json.Where(item =>
                    !item!.AsObject().TryGetPropertyValue("type", out var type) ||
                    type?.GetValue<string>() is "llm" or "decision" or "llm (gguf)" or "llm (mlx)" or "vlm")
                    .Select(item => (string?)item?[idField]).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToList();
                if (ids.Count > 0) return ids;
            }
            catch { }
        }
        return [];
    }

    private static List<string> ExtractOpenAiModelIds(JsonNode? json)
    {
        var entries = json?["data"] as JsonArray ?? json?["models"] as JsonArray;
        if (entries is null && json is JsonArray direct) entries = direct;
        return entries?.Select(item => (string?)item?["id"] ?? (string?)item?["key"] ?? (string?)item?["name"])
            .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    internal static async Task VerifyBackendDetectionCasesAsync()
    {
        static HttpClient Client(Func<Uri, HttpResponseMessage> route) => new(new FixtureHandler(route));
        static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound);
        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
        async Task<List<BackendChoice>> Detect(HttpClient client)
        {
            using (client)
            {
                var results = await Task.WhenAll(
                    CheckOllamaAsync(client), CheckCompatibleAsync("LM Studio", "http://localhost:1234/v1", client),
                    CheckCompatibleAsync("BionicGPT", "http://localhost:1235/v1", client));
                return results.Where(item => item is not null).Cast<BackendChoice>().ToList();
            }
        }
        var none = await Detect(Client(_ => Missing()));
        if (none.Count != 0) throw new InvalidDataException("No-backend fixture was misdetected.");
        var ollama = await Detect(Client(uri => uri.AbsolutePath == "/api/tags" ? Json("{\"models\":[{\"name\":\"qwen2.5:3b\"}]}") : Missing()));
        if (ollama.Count != 1 || !ollama[0].IsOllama || ollama[0].Models.Single() != "qwen2.5:3b") throw new InvalidDataException("Ollama-only fixture was not detected correctly.");
        var compatible = await Detect(Client(uri => uri.Port == 1234 && uri.AbsolutePath == "/api/v1/models" ? Json("{\"models\":[{\"key\":\"catalog-model\",\"type\":\"llm\"},{\"key\":\"embedding-only\",\"type\":\"embedding\"}]}") : uri.Port == 1234 ? Json("{\"data\":[{\"id\":\"fixture-model\"}]}") : Missing()));
        if (compatible.Count != 1 || compatible[0].IsOllama || !compatible[0].Models.Contains("fixture-model") || !compatible[0].Models.Contains("catalog-model") || compatible[0].Models.Contains("embedding-only")) throw new InvalidDataException("OpenAI-compatible backend model catalog was not detected correctly.");
        var both = await Detect(Client(uri => uri.AbsolutePath == "/api/tags" ? Json("{\"models\":[{\"name\":\"qwen2.5:3b\"}]}") : uri.Port == 1234 && uri.AbsolutePath == "/api/v1/models" ? Json("{\"models\":[{\"key\":\"catalog-model\",\"type\":\"llm\"}]}") : uri.Port == 1234 ? Json("{\"data\":[{\"id\":\"fixture-model\"}]}") : Missing()));
        if (both.Count != 2 || both.Count(item => item.IsOllama) != 1) throw new InvalidDataException("Both-backends fixture was not detected correctly.");
        var auth = await Detect(Client(uri => uri.Port == 1235 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Missing()));
        if (auth.Count != 1 || !auth[0].IsBionic || auth[0].IsCompatible) throw new InvalidDataException("Bionic authentication requirement was misreported.");
        var bionicModels = ExtractOpenAiModelIds(JsonNode.Parse("{\"models\":[{\"key\":\"bionic-id\"},{\"name\":\"alternate-id\"}]}"));
        if (bionicModels.Count != 2 || !bionicModels.Contains("bionic-id") || !bionicModels.Contains("alternate-id")) throw new InvalidDataException("Bionic-compatible model list variants were not parsed.");
        var secret = Encoding.UTF8.GetBytes("jarvis-installer-fixture");
        var protectedSecret = System.Security.Cryptography.ProtectedData.Protect(secret, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var restoredSecret = System.Security.Cryptography.ProtectedData.Unprotect(protectedSecret, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try { if (!secret.SequenceEqual(restoredSecret)) throw new InvalidDataException("Bionic API-key protection roundtrip failed."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); System.Security.Cryptography.CryptographicOperations.ZeroMemory(restoredSecret); }
    }

    private sealed class FixtureHandler(Func<Uri, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(route(request.RequestUri ?? throw new InvalidOperationException("Fixture request has no URI.")));
        }
    }

    private void UpdateModels()
    {
        _models.Clear();
        if (BackendBox.SelectedItem is not BackendChoice backend) { BionicAuthPanel.Visibility = Visibility.Collapsed; LoadBionicModelsButton.Visibility = Visibility.Collapsed; ModelInfo.Text = _language == "de" ? "Kein Backend ausgewählt." : "No backend selected."; ApplyLanguage(); return; }
        BionicAuthPanel.Visibility = backend.IsBionic || backend.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        LoadBionicModelsButton.Visibility = (backend.IsBionic || backend.IsCustom) && (!backend.IsCompatible || backend.Models.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        LoadBionicModelsButton.Content = _language == "de" ? "Modelle laden" : "Load models";
        if (backend.IsBionic || backend.IsCustom)
        {
            BionicEndpointBox.Text = backend.Endpoint;
            BackendEndpointHelp.Text = backend.IsBionic
                ? (_language == "de" ? "BionicGPT OpenAI-kompatibler Endpunkt (Standard: Port 1235). Ein API-Key ist optional, falls dein Server keine Authentifizierung verlangt." : "BionicGPT OpenAI-compatible endpoint (default: port 1235). An API key is optional if your server does not require authentication.")
                : (_language == "de" ? "Gib die Domain oder API-Basis-URL deines OpenAI-kompatiblen Anbieters ein. Ein API-Key ist optional und nur bei erforderlicher Authentifizierung nötig." : "Enter the domain or API base URL of your OpenAI-compatible provider. An API key is optional and only needed when authentication is required.");
        }
        if (backend.IsOllama)
        {
            var ram = GetInstalledRamGiB();
            var recommendation = ram < 12 ? "qwen3:1.7b" : ram < 32 ? "qwen3:4b-instruct-2507-q4_K_M" : "qwen3:8b";
            var candidates = new[]
            {
                ("qwen3:0.6b", _language == "de" ? "ca. 0,5 GB · sehr ressourcenschonend" : "~0.5 GB · very lightweight"),
                ("qwen3:1.7b", _language == "de" ? "ca. 1,4 GB · kompakt" : "~1.4 GB · compact"),
                ("qwen3:4b-instruct-2507-q4_K_M", _language == "de" ? "ca. 2,5 GB · gute Instruktionsbefolgung" : "~2.5 GB · follows instructions well"),
                ("qwen3:8b", _language == "de" ? "ca. 5,2 GB · höhere Qualität" : "~5.2 GB · higher quality"),
                ("qwen3:14b", _language == "de" ? "ca. 9,3 GB · leistungsstarker PC" : "~9.3 GB · for powerful PCs")
            };
            foreach (var (id, size) in candidates.OrderByDescending(c => c.Item1.Equals(recommendation, StringComparison.OrdinalIgnoreCase)))
            {
                var installed = backend.Models.Contains(id, StringComparer.OrdinalIgnoreCase);
                var recommended = id == recommendation;
                var recommendationText = _language == "de" ? "RAM-Empfehlung" : "Suggested for RAM";
                var installedText = _language == "de" ? "Bereits vorhanden" : "Already installed";
                var downloadText = _language == "de" ? "Download" : "Download";
                _models.Add(new ModelChoice(id, installed ? $"{installedText} · {(recommended ? recommendationText : size)}" : $"{size} · {downloadText}", recommended));
            }
        }
        var existingDescription = backend.IsOllama ? (_language == "de" ? "Bereits vorhanden" : "Already installed") : backend.IsBionic ? "BionicGPT" : (_language == "de" ? "Auf diesem PC · bei Bedarf laden" : "On this PC · download if needed");
        foreach (var model in backend.Models.Where(id => !_models.Any(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))) _models.Add(new ModelChoice(model, existingDescription));
        if (_models.Count > 0)
        {
            var installedIndex = Enumerable.Range(0, _models.Count).FirstOrDefault(i => backend.Models.Contains(_models[i].Id, StringComparer.OrdinalIgnoreCase), -1);
            var recommendationIndex = Enumerable.Range(0, _models.Count).FirstOrDefault(i => _models[i].IsRecommended, -1);
            ModelBox.SelectedIndex = installedIndex >= 0 ? installedIndex : Math.Max(0, recommendationIndex);
        }
        ModelBox.IsEnabled = backend.IsOllama || _models.Count > 0;
        ModelInfo.Text = backend.IsOllama
            ? _language == "de" ? "Die RAM-Empfehlung ist vorausgewählt. Du kannst jedes Modell in der Liste selbst wählen; Downloads starten erst nach deiner Bestätigung. Größen sind Näherungswerte." : "A RAM-based model is suggested. You can choose any model; downloads start only after confirmation. Sizes are estimates."
            : backend.IsBionic && !backend.IsCompatible ? _language == "de" ? "Gib den BionicGPT-API-Schlüssel ein und lade die Modelle. Der Schlüssel wird lokal per Windows-DPAPI geschützt." : "Enter your BionicGPT API key and load its models. The key is protected locally with Windows DPAPI."
            : backend.IsBionic ? _language == "de" ? "Wähle direkt ein Modell aus BionicGPTs erreichbarer Modellliste." : "Choose a model from BionicGPT's available model list."
            : _models.Count == 0 ? _language == "de" ? "Der Server ist erreichbar, meldet aber keine geladenen Chatmodelle. Starte/lade ein Modell und wähle danach „Erneut prüfen“." : "The server is reachable but reports no loaded chat models. Load a model, then select “Check again.”" : _language == "de" ? "Modellliste stammt direkt vom erreichbaren Server." : "The model list comes directly from the reachable server.";
        ApplyLanguage();
    }

    private void BackendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackendBox.SelectedItem is BackendChoice backend && !backend.IsOllama) BionicEndpointBox.Text = backend.Endpoint;
        UpdateModels();
        if (NextButton is not null && _wizardStep == 0) NextButton.IsEnabled = IsBackendStepValid();
    }
    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is ModelChoice model) ModelInfo.Text = model.Label;
        ApplyLanguage();
    }
    private async void LoadBionicModels_Click(object sender, RoutedEventArgs e)
    {
        if (BackendBox.SelectedItem is not BackendChoice backend || (!backend.IsBionic && !backend.IsCustom)) return;
        var key = BionicApiKeyBox.Password.Trim();
        try
        {
            LoadBionicModelsButton.IsEnabled = false; ModelInfo.Text = _language == "de" ? "Lade Modellliste …" : "Loading model list…";
            var endpoint = NormalizeOpenAiEndpoint(BionicEndpointBox.Text);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var bionicClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await bionicClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync(); response.EnsureSuccessStatusCode();
            var ids = ExtractOpenAiModelIds(JsonNode.Parse(body));
            if (ids.Count == 0) throw new InvalidDataException(_language == "de" ? "Der Server antwortet, hat aber keine Modelle unter /v1/models gemeldet." : "The server responded but returned no models from /v1/models.");
            var updated = backend with { Endpoint = endpoint, Models = ids, IsCompatible = true };
            var index = BackendBox.SelectedIndex; _backends[index] = updated; BackendBox.SelectedItem = updated; UpdateModels();
            ModelInfo.Text = _language == "de" ? $"{ids.Count} Modell(e) geladen. Du kannst unten selbst wählen." : $"Loaded {ids.Count} model(s). You can choose one below.";
        }
        catch (Exception ex) { ModelInfo.Text = (_language == "de" ? "Modelle konnten nicht geladen werden: " : "Could not load models: ") + ex.Message; }
        finally { LoadBionicModelsButton.IsEnabled = true; ApplyLanguage(); if (_wizardStep == 0) NextButton.IsEnabled = IsBackendStepValid(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshBackendsAsync();

    private static string NormalizeOpenAiEndpoint(string text)
    {
        if (!TryNormalizeOpenAiEndpoint(text, out var endpoint))
            throw new InvalidDataException("Bitte eine gültige HTTP- oder HTTPS-Endpunktadresse ohne Zugangsdaten eingeben.");
        return endpoint;
    }

    private static bool TryNormalizeOpenAiEndpoint(string? text, out string endpoint)
    {
        endpoint = "";
        var input = text?.Trim() ?? "";
        if (input.Length == 0) return false;
        if (!input.Contains("://", StringComparison.Ordinal))
            input = input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || input.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                ? "http://" + input
                : "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
        endpoint = new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/');
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Jarvis-Installationsordner auswählen", UseDescriptionForTitle = true, ShowNewFolderButton = true, SelectedPath = InstallPathBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) InstallPathBox.Text = Jarvis.Services.JarvisStoragePaths.NormalizeSelectedInstallPath(dialog.SelectedPath);
    }

    private async void InstallOllama_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckOllamaAsync(Http) is not null)
        {
            StatusText.Text = _language == "de" ? "Ollama ist bereits erreichbar. Die Modellliste wurde aktualisiert." : "Ollama is already reachable. The model list has been refreshed.";
            await RefreshBackendsAsync(); return;
        }
        var existingOllama = FindOllamaExecutable();
        if (existingOllama is not null || GetInstalledBackendNames().Contains("Ollama", StringComparer.OrdinalIgnoreCase))
        {
            if (existingOllama is null)
            {
                MessageBox.Show(this, _language == "de" ? "Ollama ist installiert, aber der lokale Dienst antwortet nicht. Starte Ollama über das Startmenü und wähle danach ‚Erneut prüfen‘. Es wird keine zweite Installation gestartet." : "Ollama is installed, but its local service is not responding. Start Ollama from the Start menu and check again. A second installer will not be launched.", "Ollama", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(existingOllama) { UseShellExecute = true });
                StatusText.Text = _language == "de" ? "Ollama wurde gestartet. Warte auf die lokale API …" : "Ollama started. Waiting for its local API…";
                for (var attempt = 0; attempt < 15 && await CheckOllamaAsync(Http) is null; attempt++) await Task.Delay(1000, _closing.Token);
                await RefreshBackendsAsync();
            }
            catch (Exception ex) { StatusText.Text = (_language == "de" ? "Ollama konnte nicht gestartet werden: " : "Could not start Ollama: ") + ex.Message; }
            return;
        }
        if (MessageBox.Show(this, "Soll Ollama von der offiziellen Ollama-Downloadadresse heruntergeladen werden? Vor dem Start prüft Jarvis die gültige Codesignatur und den Herausgeber „Ollama Inc.“. Windows kann zusätzlich eine UAC-Abfrage anzeigen.", "Ollama installieren", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        OllamaButton.IsEnabled = RefreshButton.IsEnabled = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        try
        {
            var installer = Path.Combine(Path.GetTempPath(), "Jarvis-OllamaSetup.exe");
            try
            {
                StatusText.Text = "Lade Ollama von ollama.com …";
                using var response = await PullHttp.GetAsync("https://ollama.com/download/OllamaSetup.exe", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellation.Token))
                await using (var destination = new FileStream(installer, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072]; long received = 0; int count;
                    DownloadProgress.Visibility = Visibility.Visible;
                    while ((count = await source.ReadAsync(buffer, cancellation.Token)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellation.Token); received += count;
                        if (total > 0) { DownloadProgress.Value = received * 100d / total; ProgressLabel.Text = $"Ollama laden · {FormatBytes(received)} / {FormatBytes(total)}"; }
                        else ProgressLabel.Text = $"Ollama laden · {FormatBytes(received)}";
                    }
                }
                if (!VerifyTrustedPublisherSignature(installer, "Ollama Inc.")) throw new InvalidDataException("Die Ollama-Datei besitzt keine gültige Signatur von Ollama Inc. Sie wurde nicht gestartet.");
                StatusText.Text = "Ollama-Signatur bestätigt. Der offizielle Installationsassistent wird geöffnet …";
                using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }) ?? throw new InvalidOperationException("Ollama-Setup konnte nicht gestartet werden.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException($"Ollama-Setup wurde mit Exitcode {process.ExitCode} beendet.");
            }
            finally { try { if (File.Exists(installer)) File.Delete(installer); } catch { } }
            StatusText.Text = "Ollama-Setup ist beendet. Warte auf den lokalen Dienst …";
            var reachable = false;
            for (var attempt = 0; attempt < 20 && !reachable; attempt++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                reachable = await CheckOllamaAsync(Http) is not null;
                if (!reachable) await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            }
            if (!reachable) throw new InvalidOperationException("Der offizielle Installer wurde beendet, aber Ollamas lokale API ist noch nicht erreichbar. Starte Ollama aus dem Startmenü und wähle danach „Erneut prüfen“.");
            await RefreshBackendsAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Ollama-Installation abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Ollama konnte nicht installiert werden: " + ex.Message; MessageBox.Show(this, ex.Message, "Ollama-Installation fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { OllamaButton.IsEnabled = true; RefreshButton.IsEnabled = true; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installCancellation is not null) { _installCancellation.Cancel(); return; }
        string target;
        try { target = _isUpdate ? Path.GetFullPath(InstallPathBox.Text.Trim()) : Jarvis.Services.JarvisStoragePaths.NormalizeSelectedInstallPath(InstallPathBox.Text); InstallPathBox.Text = target; }
        catch (Exception ex) { MessageBox.Show(this, "Der Installationspfad ist ungültig: " + ex.Message, "Installationsordner", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var backend = _isUpdate ? null : BackendBox.SelectedItem as BackendChoice;
        if (backend is { IsBionic: true } or { IsCustom: true })
        {
            try { backend = backend with { Endpoint = NormalizeOpenAiEndpoint(BionicEndpointBox.Text), IsCompatible = true }; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, _language == "de" ? "Serveradresse ungültig" : "Invalid server address", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        var model = _isUpdate ? null : ModelBox.SelectedItem as ModelChoice;
        if (!string.IsNullOrWhiteSpace(CustomModelBox.Text))
            model = new ModelChoice(CustomModelBox.Text.Trim(), _language == "de" ? "Eigene Modell-ID" : "Custom model ID");
        if (backend?.IsOllama == true && model is not null && !backend.Models.Contains(model.Id, StringComparer.OrdinalIgnoreCase) && !ConfirmModelDownload(model)) return;
        if (!ValidateInstallTarget(target, out var message)) { MessageBox.Show(this, message, "Installationsordner", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(target)!);
            var needed = Math.Max(_payloadBytes + 30L * 1024 * 1024, 250L * 1024 * 1024);
            if (drive.AvailableFreeSpace < needed) { MessageBox.Show(this, $"Auf dem Laufwerk sind nur {FormatBytes(drive.AvailableFreeSpace)} frei. Jarvis benötigt für Programmdateien mindestens ungefähr {FormatBytes(needed)}.", "Nicht genügend Speicher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        catch (Exception ex) { MessageBox.Show(this, "Der freie Speicherplatz konnte nicht geprüft werden: " + ex.Message, "Speicherplatzprüfung", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        _installCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = _installCancellation.Token;
        InstallButton.Content = "Installation abbrechen";
        RefreshButton.IsEnabled = BackendBox.IsEnabled = ModelBox.IsEnabled = false;
        try
        {
            await EnsureVcRuntimeAsync(token);
            if (!_isUpdate && backend?.IsOllama == true && model is not null && !backend.Models.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
                await PullOllamaModelAsync(backend, model.Id, token);
            DownloadProgress.Visibility = Visibility.Visible;
            await ExtractPayloadAsync(target, token);
            CreateStorageLayout(target);
            CopyExistingResourceCaches(Path.Combine(target, "Data", "Models"));
            await ConfigureJarvisAsync(target, _isUpdate ? null : backend, _isUpdate ? null : model, token, BionicApiKeyBox.Password);
            CreateUninstaller(target);
            await CreateShortcutsAsync(target, token);
            StatusText.Text = _isUpdate
                ? (_language == "de" ? "Jarvis wurde auf Version 1.1.3 aktualisiert. Einstellungen, Chats, Modelle und Logs wurden beibehalten." : "Jarvis was updated to version 1.1.3. Settings, chats, models and logs were preserved.")
                : (_language == "de"
                    ? $"Jarvis wurde installiert. Modelle und Zwischendateien liegen unter „{Path.Combine(target, "JarvisData")}“. Einstellungen, Chats und Logs liegen unter „{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis")}“."
                    : $"Jarvis was installed. Models and cache files are stored in “{Path.Combine(target, "JarvisData")}”. Settings, chats and logs are stored in “{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis")}”.");
            ProgressLabel.Text = _language == "de" ? "Version 1.1.3 · abgeschlossen" : "Version 1.1.3 · complete";
            if (MessageBox.Show(this, _isUpdate ? "Update abgeschlossen. Jarvis jetzt starten?" : "Installation abgeschlossen. Jarvis jetzt starten?", "Jarvis ist bereit", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "Jarvis.exe")) { UseShellExecute = true, WorkingDirectory = target });
            Close();
        }
        catch (OperationCanceledException) { StatusText.Text = "Installation abgebrochen. Bereits installierte Programmdateien bleiben erhalten; Nutzerdaten wurden nicht verändert."; ProgressLabel.Text = "Abgebrochen"; }
        catch (Exception ex) { StatusText.Text = "Die Installation ist fehlgeschlagen: " + ex.Message; ProgressLabel.Text = "Fehler"; MessageBox.Show(this, ex.Message, "Jarvis konnte nicht installiert werden", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            _installCancellation.Dispose(); _installCancellation = null;
            InstallButton.Content = _isUpdate ? (_language == "de" ? "Jarvis aktualisieren" : "Update Jarvis") : (_language == "de" ? "Jarvis installieren" : "Install Jarvis");
            RefreshButton.IsEnabled = BackendBox.IsEnabled = ModelBox.IsEnabled = true;
        }
    }

    private async Task EnsureVcRuntimeAsync(CancellationToken token)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (Convert.ToInt32(key?.GetValue("Installed") ?? 0) == 1) return;
        }
        catch { }
        if (MessageBox.Show(this, "Für die lokale Whisper-Spracherkennung fehlt das Microsoft Visual C++ x64 Runtimepaket. Soll es von der offiziellen Microsoft-Downloadadresse geladen, auf Microsoft-Signatur geprüft und installiert werden? Windows fragt dafür gegebenenfalls nach Administratorbestätigung. Jarvis selbst benötigt keine Administratorrechte.", "Laufzeitkomponente fehlt", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
        {
            if (MessageBox.Show(this, "Jarvis kann trotzdem installiert werden, aber die lokale Whisper-Spracheingabe benötigt die C++-Laufzeit. Ohne sie fortfahren?", "Spracheingabe nicht verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                throw new OperationCanceledException("Die benötigte VC++-Laufzeit wurde nicht installiert.");
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "Jarvis-vc_redist.x64.exe");
        try
        {
            StatusText.Text = "Lade Microsoft Visual C++ Runtime über aka.ms/vs/17/release/vc_redist.x64.exe …";
            using var response = await PullHttp.GetAsync("https://aka.ms/vs/17/release/vc_redist.x64.exe", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long received = 0; int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), token); received += count;
                    if (total > 0) { DownloadProgress.Visibility = Visibility.Visible; DownloadProgress.Value = received * 100d / total; ProgressLabel.Text = $"Microsoft-Laufzeit laden · {FormatBytes(received)} / {FormatBytes(total)}"; }
                }
            }
            token.ThrowIfCancellationRequested();
            if (!VerifyTrustedPublisherSignature(temp, "Microsoft Corporation")) throw new InvalidDataException("Die heruntergeladene Laufzeit besitzt keine gültige Microsoft-Code-Signatur. Sie wurde nicht gestartet.");
            StatusText.Text = "Microsoft-Signatur bestätigt. Starte den VC++-Installer; bestätige die Windows-UAC-Abfrage …";
            using var process = Process.Start(new ProcessStartInfo(temp, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" }) ?? throw new InvalidOperationException("Microsoft VC++-Installer konnte nicht gestartet werden.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Microsoft VC++-Installer wurde mit Exitcode {process.ExitCode} beendet.");
            ProgressLabel.Text = "Microsoft Visual C++ Runtime installiert";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Die Windows-UAC-Abfrage für das Microsoft Runtimepaket wurde abgelehnt.");
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static bool VerifyTrustedPublisherSignature(string path, string organization)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = filePath };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
        var data = new WinTrustData { StructSize = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1, FileInfo = fileInfoPtr, StateAction = 0, ProviderFlags = 0x00000010 };
        try
        {
            var action = GenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return certificate.Subject.Split(',').Select(part => part.Trim()).Any(part => part.Equals("O=" + organization, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
        finally { Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr); Marshal.FreeHGlobal(fileInfoPtr); Marshal.FreeCoTaskMem(filePath); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfo { public uint StructSize; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustData { public uint StructSize; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)] private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, [In, MarshalAs(UnmanagedType.LPStruct)] ref WinTrustData data);

    private bool ConfirmModelDownload(ModelChoice model)
    {
        var size = ModelSizeLabel(model.Id);
        var root = Environment.GetEnvironmentVariable("OLLAMA_MODELS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        var drivePath = Path.GetPathRoot(Path.GetFullPath(root)) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        long free;
        try { free = new DriveInfo(drivePath).AvailableFreeSpace; }
        catch (Exception ex) { MessageBox.Show(this, "Das Ollama-Modelllaufwerk konnte nicht geprüft werden: " + ex.Message, "Speicherplatzprüfung", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (free < EstimatedModelBytes(model.Id) + 350L * 1024 * 1024) { MessageBox.Show(this, $"Im Modelllaufwerk sind nur {FormatBytes(free)} verfügbar. Für {model.Id} werden ungefähr {size} plus temporärer Speicher benötigt.\n\nOllama-Modellpfad: {root}", "Zu wenig Speicher", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        return MessageBox.Show(this, $"Soll Ollama das Modell {model.Id} herunterladen?\n\nDownload: ungefähr {size}\nAblage: {root}\nFreier Platz: {FormatBytes(free)}\n\nOllama lädt das Modell direkt von seiner Registry.", "Lokales Modell herunterladen", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async Task PullOllamaModelAsync(BackendChoice backend, string model, CancellationToken token)
    {
        StatusText.Text = _language == "de"
            ? $"Lokales Modell wird geladen: {model}. Ollama lädt es direkt herunter. Dieser Vorgang kann je nach Modellgröße einige Minuten dauern."
            : $"Loading local model: {model}. Ollama is downloading it directly. This may take several minutes depending on model size.";
        ProgressLabel.Text = _language == "de" ? $"Modell {model} wird geladen …" : $"Loading model {model}…";
        DownloadProgress.Visibility = Visibility.Visible;
        using var request = new HttpRequestMessage(HttpMethod.Post, backend.OllamaHost + "/api/pull") { Content = JsonContent.Create(new { name = model, stream = true }) };
        using var response = await PullHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonNode.Parse(line);
            var status = (string?)item?["status"] ?? "Lade Modell …";
            var total = (double?)item?["total"] ?? 0;
            var completed = (double?)item?["completed"] ?? 0;
            if (total > 0) { DownloadProgress.Value = Math.Clamp(completed / total * 100, 0, 100); ProgressLabel.Text = $"{status}: {DownloadProgress.Value:0}% · {FormatBytes((long)completed)} / {FormatBytes((long)total)}"; }
            else ProgressLabel.Text = status;
            if (item?["error"] is JsonNode error) throw new InvalidOperationException("Ollama meldet: " + error.ToString());
        }
        using var verify = await Http.GetAsync(backend.OllamaHost + "/api/tags", token);
        verify.EnsureSuccessStatusCode();
        var available = (await verify.Content.ReadFromJsonAsync<OllamaTags>(cancellationToken: token))?.Models?.Select(m => m.Name).ToArray() ?? [];
        if (!available.Contains(model, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"Ollama hat den Download beendet, aber {model} ist danach nicht in der Modellliste erschienen.");
        ProgressLabel.Text = $"Modell {model} heruntergeladen und bestätigt";
    }

    private async Task ExtractPayloadAsync(string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        var oldManifest = ReadManifest(target);
        var oldFiles = oldManifest?.Files ?? [];
        var assembly = typeof(MainWindow).Assembly;
        await using var payload = assembly.GetManifestResourceStream("Jarvis.Setup.payload.zip") ?? throw new InvalidOperationException("Das eingebettete Jarvis-Programmpaket fehlt. Erzeuge das Setup mit Build-Installer.ps1 neu.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
        _manifestShortcuts.Clear();
        if (oldManifest?.Shortcuts is not null) _manifestShortcuts.AddRange(oldManifest.Shortcuts);
        var written = new List<string>(entries.Length + oldFiles.Count + 3);
        var planned = entries.Select(entry => entry.FullName.Replace('/', Path.DirectorySeparatorChar)).ToList();
        foreach (var old in oldFiles) if (!planned.Contains(old, StringComparer.OrdinalIgnoreCase)) planned.Add(old);
        foreach (var required in new[] { "uninstall.ps1", "install-manifest.json" }) if (!planned.Contains(required, StringComparer.OrdinalIgnoreCase)) planned.Add(required);
        _manifestFiles = planned;
        await File.WriteAllTextAsync(Path.Combine(target, "install-manifest.json"), JsonSerializer.Serialize(new InstallManifest("Jarvis", planned, _manifestShortcuts), JsonOptions), new UTF8Encoding(false), token);
        long done = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Das Installationspaket enthält einen ungültigen Pfad.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
            await input.CopyToAsync(output, 131072, token);
            written.Add(Path.GetRelativePath(target, destination));
            done++;
            if (done % 4 == 0 || done == entries.Length) { DownloadProgress.Value = done * 100d / entries.Length; ProgressLabel.Text = $"Programmpaket installieren · {done} / {entries.Length} Dateien"; }
        }
        foreach (var old in oldFiles) if (!written.Contains(old, StringComparer.OrdinalIgnoreCase)) written.Add(old);
        _manifestFiles = written;
    }

    private List<string> _manifestFiles = [];
    private async Task ConfigureJarvisAsync(string installPath, BackendChoice? backend, ModelChoice? model, CancellationToken token, string bionicApiKey)
    {
        var configDirectory = Path.Combine(installPath, "Config");
        Directory.CreateDirectory(configDirectory);
        var settings = Path.Combine(configDirectory, "settings.json");
        var legacySettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "settings.json");
        if (!File.Exists(settings) && File.Exists(legacySettings)) File.Copy(legacySettings, settings, false);
        JsonObject config;
        if (File.Exists(settings))
        {
            var existing = await File.ReadAllTextAsync(settings, token);
            config = JsonNode.Parse(existing) as JsonObject ?? throw new InvalidDataException("Die vorhandene Einstellungsdatei ist kein JSON-Objekt. Sie wurde nicht geändert.");
            var backup = settings + ".installer-backup-" + Guid.NewGuid().ToString("N");
            File.Copy(settings, backup, overwrite: false);
        }
        else config = new JsonObject();
        config["ResourceDirectory"] = Path.Combine(installPath, "Data");
        if (backend is { IsCompatible: true })
        {
            config["Endpoint"] = backend.Endpoint;
        config["Model"] = model is not null && (backend.Models.Contains(model.Id, StringComparer.OrdinalIgnoreCase) || backend.IsOllama || backend.IsCustom || backend.IsBionic) ? model.Id : "";
            config["ApiKeyProtected"] = (backend.IsBionic || backend.IsCustom) && !string.IsNullOrWhiteSpace(bionicApiKey)
                ? Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(bionicApiKey), null, System.Security.Cryptography.DataProtectionScope.CurrentUser))
                : "";
        }
        if (string.IsNullOrWhiteSpace((string?)config["SystemPrompt"]) || Jarvis.Services.DefaultSystemPrompt.IsLegacyDefault((string?)config["SystemPrompt"]))
            config["SystemPrompt"] = Jarvis.Services.DefaultSystemPrompt.Text;
        var temp = settings + ".tmp";
        await File.WriteAllTextAsync(temp, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), token);
        File.Move(temp, settings, overwrite: true);
    }

    private static void CopyExistingResourceCaches(string destination)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "Models");
        if (!Directory.Exists(source)) return;
        CopyMissingFiles(source, destination);
    }

    private static void CreateStorageLayout(string target)
    {
        foreach (var path in new[] { "Data", @"Data\SVG", @"Data\Chats", @"Data\Logs", @"Data\Cache", @"Data\Backups", @"Data\Voice", @"Data\Models", "Config" })
            Directory.CreateDirectory(Path.Combine(target, path));
        File.WriteAllText(Path.Combine(target, Jarvis.Services.JarvisStoragePaths.RootMarkerFileName), target, new UTF8Encoding(false));
    }

    private static void CopyMissingFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyMissingFiles(directory, Path.Combine(destination, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
        }
    }

    private async Task CreateShortcutsAsync(string target, CancellationToken token)
    {
        var app = Path.Combine(target, "Jarvis.exe");
        if (StartMenuCheck.IsChecked == true)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Jarvis");
            Directory.CreateDirectory(folder);
            await CreateShortcutAsync(Path.Combine(folder, "Jarvis.lnk"), app, target, token);
            await CreateShortcutAsync(Path.Combine(folder, "Jarvis deinstallieren.lnk"), "powershell.exe", target, token, "-NoProfile -ExecutionPolicy Bypass -File " + Quote(Path.Combine(target, "uninstall.ps1")));
            _manifestShortcuts.Add(Path.Combine(folder, "Jarvis.lnk")); _manifestShortcuts.Add(Path.Combine(folder, "Jarvis deinstallieren.lnk"));
        }
        if (DesktopCheck.IsChecked == true)
        {
            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Jarvis.lnk");
            await CreateShortcutAsync(shortcut, app, target, token); _manifestShortcuts.Add(shortcut);
        }
        var uninstallKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Jarvis");
        uninstallKey.SetValue("DisplayName", "Jarvis");
        uninstallKey.SetValue("DisplayVersion", "1.1.3");
        uninstallKey.SetValue("Publisher", "Jarvis");
        uninstallKey.SetValue("InstallLocation", target);
        uninstallKey.SetValue("DisplayIcon", app);
        uninstallKey.SetValue("UninstallString", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Quote(Path.Combine(target, "uninstall.ps1")));
        uninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstallKey.Close();
        var manifest = new InstallManifest("Jarvis", _manifestFiles, _manifestShortcuts);
        await File.WriteAllTextAsync(Path.Combine(target, "install-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false), token);
    }

    private readonly List<string> _manifestShortcuts = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static async Task CreateShortcutAsync(string path, string target, string workingDirectory, CancellationToken token, string? arguments = null)
    {
        token.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows konnte keine Verknüpfungen erstellen.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = target; shortcut.WorkingDirectory = workingDirectory; shortcut.Description = arguments is null ? "Jarvis starten" : "Jarvis deinstallieren";
            if (arguments is not null) shortcut.Arguments = arguments;
            if (arguments is null && File.Exists(Path.Combine(workingDirectory, "Assets", "Jarvis.ico"))) shortcut.IconLocation = Path.Combine(workingDirectory, "Assets", "Jarvis.ico");
            shortcut.Save();
        }, token);
    }

    private void CreateUninstaller(string target)
    {
        var scriptPath = Path.Combine(target, "uninstall.ps1");
        var script = """
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path)).TrimEnd('\') + '\'
$manifestPath = Join-Path $root 'install-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { [Windows.Forms.MessageBox]::Show('Jarvis-Installationsnachweis fehlt. Es wurden keine Dateien entfernt.','Jarvis deinstallieren') | Out-Null; exit 2 }
Add-Type -AssemblyName System.Windows.Forms
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Product -ne 'Jarvis') { [Windows.Forms.MessageBox]::Show('Der Ordner gehört nicht zu Jarvis.','Jarvis deinstallieren') | Out-Null; exit 2 }
$dataRoot = [IO.Path]::GetFullPath((Join-Path $root 'Data'))
$settingsPath = Join-Path (Join-Path $root 'Config') 'settings.json'
$resourceRoot = $dataRoot
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    try {
        $config = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($config.ResourceDirectory -and [IO.Path]::IsPathFullyQualified([string]$config.ResourceDirectory)) { $resourceRoot = [IO.Path]::GetFullPath([string]$config.ResourceDirectory) }
    } catch { }
}
$choice = [Windows.Forms.MessageBox]::Show("__HOW__`n`nYES = __KEEP__`n`nNO = __DELETE__`n$dataRoot`n$resourceRoot`n`n__EXTERNAL__`n`nCANCEL = __CANCEL__",'__UNINSTALL_TITLE__',[Windows.Forms.MessageBoxButtons]::YesNoCancel,[Windows.Forms.MessageBoxIcon]::Question)
if ($choice -eq [Windows.Forms.DialogResult]::Cancel) { exit 0 }
$removeData = $choice -eq [Windows.Forms.DialogResult]::No
if ($removeData) {
    $confirm = [Windows.Forms.MessageBox]::Show("__CONFIRM__`n$dataRoot`n`n$resourceRoot`n`n__CONTINUE__",'__DELETE_TITLE__',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Warning)
    if ($confirm -ne [Windows.Forms.DialogResult]::Yes) { exit 0 }
    $installPath = $root.TrimEnd('\')
    $dataNormalized = $dataRoot.TrimEnd('\')
    if (-not $dataNormalized.StartsWith($installPath + '\',[StringComparison]::OrdinalIgnoreCase) -or $dataNormalized -eq $installPath) { throw 'The user-data path failed its safety check.' }
    $resourceRoot = [IO.Path]::GetFullPath($resourceRoot)
    $resourceNormalized = $resourceRoot.TrimEnd('\')
    $resourceDriveRoot = [IO.Path]::GetPathRoot($resourceRoot)
    if ($resourceNormalized -eq $resourceDriveRoot.TrimEnd('\') -or $resourceNormalized -eq $installPath -or -not $resourceNormalized.StartsWith($installPath + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The model-data path failed its safety check. No user data was deleted.' }
}
Set-Location -LiteralPath $env:TEMP
foreach ($shortcut in $manifest.Shortcuts) { if ($shortcut -and (Test-Path -LiteralPath $shortcut)) { Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue } }
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Jarvis' -Recurse -Force -ErrorAction SilentlyContinue
if ($removeData) {
    if (Test-Path -LiteralPath $dataRoot -PathType Container) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
    if (Test-Path -LiteralPath $resourceRoot -PathType Container) { Remove-Item -LiteralPath $resourceRoot -Recurse -Force }
}
foreach ($relative in $manifest.Files) { $path = [IO.Path]::GetFullPath((Join-Path $root $relative)); if ($path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $path -PathType Leaf)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue } }
$directories = Get-ChildItem -LiteralPath $root -Directory -Recurse -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending
foreach ($directory in $directories) { if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue } }
$cmd = '/c ping 127.0.0.1 -n 3 >nul & rmdir "' + $root.TrimEnd('\') + '"'
Start-Process -FilePath $env:ComSpec -ArgumentList $cmd -WindowStyle Hidden
""";
        script = script
            .Replace("__HOW__", _language == "de" ? "Wie soll Jarvis deinstalliert werden?" : "How should Jarvis be uninstalled?")
            .Replace("__KEEP__", _language == "de" ? "App entfernen, Chats, Einstellungen, Logs und Modelle BEHALTEN." : "Remove the app and KEEP chats, settings, logs and models.")
            .Replace("__DELETE__", _language == "de" ? "App ENTFERNEN und alle Jarvis-Daten LÖSCHEN:" : "Remove the app AND DELETE all Jarvis data:")
            .Replace("__EXTERNAL__", _language == "de" ? "Modelle, die Ollama oder ein anderes Backend verwaltet, bleiben unberührt." : "Models managed by Ollama or another backend will not be touched.")
            .Replace("__CANCEL__", _language == "de" ? "Abbrechen und nichts ändern." : "Cancel and make no changes.")
            .Replace("__UNINSTALL_TITLE__", _language == "de" ? "Jarvis deinstallieren" : "Uninstall Jarvis")
            .Replace("__CONFIRM__", _language == "de" ? "Diese Aktion löscht dauerhaft Chats, Einstellungen und Logs unter:" : "This permanently deletes chats, settings and logs at:")
            .Replace("__CONTINUE__", _language == "de" ? "sowie Jarvis-Modelle und Zwischendateien. Fortfahren?" : "and Jarvis models and cache files. Continue?")
            .Replace("__DELETE_TITLE__", _language == "de" ? "Jarvis-Daten löschen" : "Delete Jarvis data");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        if (!_manifestFiles.Contains("uninstall.ps1")) _manifestFiles.Add("uninstall.ps1");
        if (!_manifestFiles.Contains("install-manifest.json")) _manifestFiles.Add("install-manifest.json");
    }

    private static InstallManifest? ReadManifest(string target)
    {
        var path = Path.Combine(target, "install-manifest.json");
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), JsonOptions);
        if (manifest?.Product != "Jarvis" || manifest.Files.Any(file => Path.IsPathRooted(file) || file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == ".."))) throw new InvalidDataException("Im Installationsordner wurde ein ungültiger Installationsnachweis gefunden. Aus Sicherheitsgründen wurde nichts überschrieben.");
        return manifest;
    }

    private static bool ValidateInstallTarget(string target, out string message)
    {
        message = "";
        try
        {
            if (target.Length < 8 || target == Path.GetPathRoot(target)) { message = "Wähle einen eigenen Installationsordner, nicht das Laufwerksstammverzeichnis."; return false; }
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                try { if (ReadManifest(target) is null) { message = "Der gewählte Ordner ist nicht leer und enthält keine Jarvis-Installation. Wähle bitte einen neuen Ordner."; return false; } }
                catch (Exception ex) { message = ex.Message; return false; }
            }
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".jarvis-write-test"); File.WriteAllText(probe, "ok"); File.Delete(probe);
            return true;
        }
        catch (Exception ex) { message = "Der Ordner ist nicht beschreibbar: " + ex.Message; return false; }
    }

    private static string? FindExistingInstallPath()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var keyPath in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Jarvis", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Jarvis" })
            {
                try { using var key = hive.OpenSubKey(keyPath); if (key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path)) candidates.Add(path); } catch { }
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Jarvis"));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(full, "Jarvis.exe")) && ReadManifest(full) is not null) return full;
            }
            catch { }
        }
        return null;
    }

    private static string HardwareSummary()
    {
        ReadMemoryStatus(out var total, out var available);
        var ram = total / 1024d / 1024 / 1024;
        var cores = Environment.ProcessorCount;
        var os = RuntimeInformation.OSDescription;
        var architecture = RuntimeInformation.OSArchitecture;
        var cpu = "nicht auslesbar";
        try { cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string ?? cpu; } catch { }
        var gpu = ReadDisplayAdapters();
        return $"{os} · {architecture}\nCPU: {cpu} · {cores} logische Kerne\nRAM: {ram:0.#} GB installiert · {available / 1024d / 1024 / 1024:0.#} GB verfügbar\nGrafikadapter: {gpu}. Treiber-/Backend-Offload wird erst beim Laden verifiziert.";
    }

    private static string ReadDisplayAdapters()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var adapters = key?.GetSubKeyNames().Where(n => n.Length == 4 && int.TryParse(n, out _)).Select(n =>
            {
                using var subkey = key.OpenSubKey(n);
                var name = subkey?.GetValue("DriverDesc") as string;
                var raw = subkey?.GetValue("HardwareInformation.qwMemorySize") as byte[];
                var bytes = raw is { Length: >= 8 } ? BitConverter.ToUInt64(raw, 0) : 0UL;
                return string.IsNullOrWhiteSpace(name) ? null : bytes > 0 ? $"{name} ({bytes / 1024d / 1024 / 1024:0.#} GB gemeldet)" : name;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
            return adapters is { Length: > 0 } ? string.Join(", ", adapters) : "kein Adapter auslesbar";
        }
        catch { return "nicht auslesbar"; }
    }

    private static List<string> GetInstalledBackendNames()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var app = uninstall!.OpenSubKey(name);
                    var display = app?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    if (display.Contains("Ollama", StringComparison.OrdinalIgnoreCase)) found.Add("Ollama");
                    if (display.Contains("Bionic", StringComparison.OrdinalIgnoreCase)) found.Add("BionicGPT");
                }
            }
            catch { }
        }
        return found.OrderBy(name => name).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private class MemoryStatus { public uint Length = (uint)Marshal.SizeOf<MemoryStatus>(); public uint MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);
    private static double GetInstalledRamGiB() { ReadMemoryStatus(out var total, out _); return total > 0 ? total / 1024d / 1024 / 1024 : 8; }
    private static string ModelSizeLabel(string id) => id switch
    {
        "qwen3:0.6b" => "ca. 0,5 GB", "qwen3:1.7b" => "ca. 1,4 GB", "qwen3:4b-instruct-2507-q4_K_M" => "ca. 2,5 GB", "qwen3:8b" => "ca. 5,2 GB", "qwen3:14b" => "ca. 9,3 GB",
        "qwen2.5:0.5b" => "ca. 0,4 GB", "qwen2.5:1.5b" => "ca. 1,0 GB", "qwen2.5:3b" => "ca. 1,9 GB", "qwen2.5:7b" => "ca. 4,7 GB", _ => "Größe abhängig vom Backend"
    };
    private static long EstimatedModelBytes(string id) => id switch
    {
        "qwen3:0.6b" => 523L * 1024 * 1024, "qwen3:1.7b" => 1434L * 1024 * 1024, "qwen3:4b-instruct-2507-q4_K_M" => 2560L * 1024 * 1024, "qwen3:8b" => 5325L * 1024 * 1024, "qwen3:14b" => 9523L * 1024 * 1024,
        "qwen2.5:0.5b" => 400L * 1024 * 1024, "qwen2.5:1.5b" => 1024L * 1024 * 1024, "qwen2.5:3b" => 2L * 1024 * 1024 * 1024, "qwen2.5:7b" => 5L * 1024 * 1024 * 1024, _ => 1024L * 1024 * 1024
    };
    private static void ReadMemoryStatus(out ulong total, out ulong available)
    {
        try { var s = new MemoryStatus(); if (GlobalMemoryStatusEx(s)) { total = s.TotalPhys; available = s.AvailPhys; return; } } catch { }
        total = 8UL * 1024 * 1024 * 1024; available = 0;
    }
    private static long GetPayloadSize()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Jarvis.Setup.payload.zip");
        if (stream is null) return 200L * 1024 * 1024;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries.Sum(e => e.Length);
    }
    private static string DiskSummary(string path, long payload)
    {
        try { var root = Path.GetPathRoot(Path.GetFullPath(path)); if (string.IsNullOrWhiteSpace(root)) return "Installationslaufwerk konnte nicht ermittelt werden."; var disk = new DriveInfo(root); return $"Programm: ungefähr {FormatBytes(payload)} entpackt · verfügbar: {FormatBytes(disk.AvailableFreeSpace)}"; }
        catch { return "Speicherplatz wird nach Auswahl eines gültigen Ordners geprüft."; }
    }
    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024 / 1024:0.0} GB" : $"{value / 1024d / 1024:0} MB";
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_installCancellation is not null) { _installCancellation.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "Breche an der nächsten sicheren Stelle ab …"; return; }
        Close();
    }
    protected override void OnClosed(EventArgs e) { _closing.Cancel(); _installCancellation?.Cancel(); _closing.Dispose(); base.OnClosed(e); }

    private sealed record BackendChoice(string Name, string Endpoint, List<string> Models, string? OllamaHost, bool IsCompatible, bool IsCustom = false)
    {
        public bool IsOllama => OllamaHost is not null;
        public bool IsBionic => Name.StartsWith("BionicGPT", StringComparison.Ordinal);
        public string Label => Models.Count > 0 ? $"{Name} · {Models.Count} Modell(e) · {Endpoint}" : $"{Name} · {Endpoint}";
        public override string ToString() => Label;
    }
    private sealed record ModelChoice(string Id, string Description, bool IsRecommended = false) { public string Label => $"{Id} · {Description}"; public override string ToString() => Label; }
    private sealed record OllamaTags(List<OllamaModel>? Models);
    private sealed record OllamaModel(string Name);
    private sealed record InstallManifest(string Product, List<string> Files, List<string> Shortcuts);
}
