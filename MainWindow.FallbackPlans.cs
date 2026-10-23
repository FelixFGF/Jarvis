using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Jarvis.Services;

namespace Jarvis;

public partial class MainWindow
{
    private void FallbackDetails_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Expander expander && expander.Header is string header)
            expander.Header = UiLocalization.Translate(header, _config.UiLanguage);
        UiLocalization.Apply((DependencyObject)sender, _config.UiLanguage);
    }
    private void ValidateFallbackPlan_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is not ModelFallbackPlan plan) return;
        plan.Name = FallbackPlanName.Text.Trim();
        try
        {
            ModelFallbackOrchestrator.Validate(plan);
            FallbackSummary.Text = string.Join("\n", plan.Steps.Select((step, index) =>
                $"{index + 1}. {step.Model} · {UiLocalization.Translate(step.Provider == "primary" ? "Hauptanbieter" : "Ersatzanbieter", _config.UiLanguage)} – "
                + (step.Enabled ? UiLocalization.Translate(index == 0 ? "zuerst verwenden" : "bei passendem Fehler übernehmen", _config.UiLanguage) : UiLocalization.Translate("Deaktiviert", _config.UiLanguage))))
                + "\n" + UiLocalization.Translate("Nach dem letzten Schritt ist kein weiterer Fallback verfügbar.", _config.UiLanguage);
            FallbackEditorStatus.Text = UiLocalization.Translate(plan.Steps.Any(step => !step.Tools)
                ? "Plan ist gültig. Werkzeugfähigkeiten fehlen bei mindestens einem Modell; entsprechende Aufgaben überspringen diesen Schritt."
                : "Plan ist gültig. Prüfe die Verfügbarkeit über den Verbindungstest.", _config.UiLanguage);
        }
        catch (ArgumentException) { FallbackEditorStatus.Text = UiLocalization.Translate("Prüfe Name, Anbieter, Modell und Zeitlimit (5–600 Sekunden).", _config.UiLanguage); }
    }
    private async void FallbackModels_Opened(object sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ModelFallbackStep step } box || box.Tag is true) return;
        box.Tag = true;
        try
        {
            using var service = new ApiModelCatalogService();
            var models = await service.ListModelsAsync(step.Provider == "primary" ? _config.Endpoint : _config.ComputerUseFallbackEndpoint,
                step.Provider == "primary" ? GetConfiguredApiKey() : GetComputerUseFallbackApiKey());
            step.AvailableModels.Clear();
            foreach (var model in models) step.AvailableModels.Add(model.Id);
        }
        catch (Exception) { FallbackEditorStatus.Text = UiLocalization.Translate("Verbindungstest fehlgeschlagen. Prüfe die gespeicherte Anbieterkonfiguration.", _config.UiLanguage); }
        finally { box.Tag = null; }
    }
    private System.Collections.ObjectModel.ObservableCollection<ModelFallbackPlan>? _fallbackDrafts;
    private static T CopyPlanData<T>(T source) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(source))!;
    private static string FallbackReason(string reason) => reason switch {
        "selected" => "Plan: ausgewählt", "incompatible" => "Plan: Fähigkeiten fehlen",
        "condition_not_matched" => "Plan: Bedingung nicht erfüllt",
        "Timeout" => "Plan: Zeitüberschreitung", "Network" => "Plan: Netzwerkfehler",
        "RateLimit" => "Plan: Ratenlimit", "Server" => "Plan: Serverfehler",
        "ModelUnavailable" => "Plan: Modell nicht verfügbar", _ => "Plan: fehlgeschlagen"
    };
    private void RefreshFallbackPlans()
    {
        _fallbackDrafts ??= CopyPlanData(_config.FallbackPlans);
        FallbackPlanBox.ItemsSource = _fallbackDrafts;
        FallbackPlanBox.Items.Refresh();
    }
    private void FallbackPlan_Selected(object sender, SelectionChangedEventArgs e)
    {
        var plan = FallbackPlanBox.SelectedItem as ModelFallbackPlan;
        FallbackSteps.ItemsSource = plan?.Steps;
        FallbackPlanName.Text = plan?.Name ?? "";
        FallbackPlanEnabled.IsChecked = plan?.Enabled == true;
        if (plan is not null) ValidateFallbackPlan_Click(sender, e);
        else if (FallbackSummary is not null) FallbackSummary.Text = "";
    }
    private void NewFallbackPlan_Click(object sender, RoutedEventArgs e)
    {
        var plan = new ModelFallbackPlan { Name = UiLocalization.Translate("Neuer Plan", _config.UiLanguage) };
        plan.Steps.Add(new ModelFallbackStep { Model = _config.Model });
        _fallbackDrafts!.Add(plan);
        RefreshFallbackPlans(); FallbackPlanBox.SelectedItem = plan;
    }
    private void DuplicateFallbackPlan_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is not ModelFallbackPlan selected) return;
        var plan = System.Text.Json.JsonSerializer.Deserialize<ModelFallbackPlan>(System.Text.Json.JsonSerializer.Serialize(selected))!;
        plan.Enabled = false; plan.Name += " (2)";
        _fallbackDrafts!.Add(plan); RefreshFallbackPlans(); FallbackPlanBox.SelectedItem = plan;
    }
    private void DeleteFallbackPlan_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is ModelFallbackPlan plan) _fallbackDrafts!.Remove(plan);
        RefreshFallbackPlans();
    }
    private void AddFallbackStep_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is ModelFallbackPlan plan && plan.Steps.Count < 8) plan.Steps.Add(new ModelFallbackStep());
    }
    private void RemoveFallbackStep_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is ModelFallbackPlan plan && FallbackSteps.SelectedItem is ModelFallbackStep step) plan.Steps.Remove(step);
    }
    private void MoveFallbackStep_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is not ModelFallbackPlan plan || FallbackSteps.SelectedItem is not ModelFallbackStep step) return;
        var index = plan.Steps.IndexOf(step); var target = index + (((System.Windows.Controls.Button)sender).Tag?.ToString() == "up" ? -1 : 1);
        if (target >= 0 && target < plan.Steps.Count) plan.Steps.Move(index, target);
    }
    private async void SaveFallbackPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FallbackPlanBox.SelectedItem is ModelFallbackPlan plan)
            {
                plan.Name = FallbackPlanName.Text.Trim();
                ModelFallbackOrchestrator.Validate(plan);
                if (FallbackPlanEnabled.IsChecked == true)
                    foreach (var other in _fallbackDrafts!) other.Enabled = false;
                plan.Enabled = FallbackPlanEnabled.IsChecked == true;
            }
            foreach (var entry in _fallbackDrafts!) ModelFallbackOrchestrator.Validate(entry);
            var previous = _config.FallbackPlans;
            _config.FallbackPlans = CopyPlanData(_fallbackDrafts!);
            try { await PersistConfig(); }
            catch { _config.FallbackPlans = previous; throw; }
            RefreshFallbackPlans();
            FallbackEditorStatus.Text = UiLocalization.Translate("Plan gespeichert.", _config.UiLanguage);
        }
        catch (Exception)
        {
            FallbackEditorStatus.Text = UiLocalization.Translate("Prüfe Name, Anbieter, Modell und Zeitlimit (5–600 Sekunden).", _config.UiLanguage);
        }
    }
    private async void TestFallbackStep_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackSteps.SelectedItem is not ModelFallbackStep step) return;
        try
        {
            using var service = new ApiModelCatalogService();
            var models = await service.ListModelsAsync(step.Provider == "primary" ? _config.Endpoint : _config.ComputerUseFallbackEndpoint,
                step.Provider == "primary" ? GetConfiguredApiKey() : GetComputerUseFallbackApiKey());
            FallbackEditorStatus.Text = UiLocalization.Translate(models.Any(m => m.Id == step.Model)
                ? "Modell verfügbar. Fähigkeiten müssen separat bestätigt werden."
                : "Das Modell wurde vom Anbieter nicht aufgelistet.", _config.UiLanguage);
        }
        catch (Exception) { FallbackEditorStatus.Text = UiLocalization.Translate("Verbindungstest fehlgeschlagen. Prüfe die gespeicherte Anbieterkonfiguration.", _config.UiLanguage); }
    }

    private void RemoveFallbackCard_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is ModelFallbackPlan plan && sender is System.Windows.Controls.Button { DataContext: ModelFallbackStep step })
            plan.Steps.Remove(step);
    }

    private void MoveFallbackCard_Click(object sender, RoutedEventArgs e)
    {
        if (FallbackPlanBox.SelectedItem is not ModelFallbackPlan plan || sender is not System.Windows.Controls.Button { DataContext: ModelFallbackStep step } button) return;
        var index = plan.Steps.IndexOf(step); var target = index + (button.Tag?.ToString() == "up" ? -1 : 1);
        if (target >= 0 && target < plan.Steps.Count) plan.Steps.Move(index, target);
    }

    private async void TestFallbackCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ModelFallbackStep step }) return;
        await TestFallbackStepAsync(step);
    }

    private async Task TestFallbackStepAsync(ModelFallbackStep step)
    {
        try
        {
            using var service = new ApiModelCatalogService();
            var models = await service.ListModelsAsync(step.Provider == "primary" ? _config.Endpoint : _config.ComputerUseFallbackEndpoint,
                step.Provider == "primary" ? GetConfiguredApiKey() : GetComputerUseFallbackApiKey());
            FallbackEditorStatus.Text = UiLocalization.Translate(models.Any(m => m.Id == step.Model)
                ? "Modell verfügbar. Fähigkeiten müssen separat bestätigt werden."
                : "Das Modell wurde vom Anbieter nicht aufgelistet.", _config.UiLanguage);
        }
        catch (Exception) { FallbackEditorStatus.Text = UiLocalization.Translate("Verbindungstest fehlgeschlagen. Prüfe die gespeicherte Anbieterkonfiguration.", _config.UiLanguage); }
    }
}
