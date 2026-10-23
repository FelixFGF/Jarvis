using System.Collections.ObjectModel;

namespace Jarvis;

[Flags]
public enum RequestCapabilities { Text = 1, Tools = 2, Vision = 4, StructuredOutput = 8, ComputerUse = 16 }
[Flags]
public enum FallbackFailures { None = 0, Timeout = 1, Network = 2, RateLimit = 4, Server = 8, ModelUnavailable = 16 }
public sealed class ModelFallbackPlan
{
    public string Name { get; set; } = "Fallback";
    public bool Enabled { get; set; }
    public ObservableCollection<ModelFallbackStep> Steps { get; set; } = [];
}
public sealed class ModelFallbackStep : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private FallbackFailures _onFailure = FallbackFailures.Timeout | FallbackFailures.Network | FallbackFailures.RateLimit | FallbackFailures.Server;
    // References existing DPAPI-protected provider configuration; no secrets in plans.
    public string Provider { get; set; } = "primary";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public ObservableCollection<string> AvailableModels { get; } = [];
    public RequestCapabilities ConfirmedCapabilities { get; set; } = RequestCapabilities.Text;
    public FallbackFailures OnFailure
    {
        get => _onFailure;
        set
        {
            if (_onFailure == value) return;
            _onFailure = value;
            // The simple switch and advanced checkboxes edit the same persisted flags.
            foreach (var property in new[] { nameof(OnFailure), nameof(AcceptFallback), nameof(Timeout), nameof(Network), nameof(RateLimit), nameof(Server), nameof(ModelUnavailable) })
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
        }
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool AcceptFallback
    {
        get => OnFailure != FallbackFailures.None;
        set { if (!value) OnFailure = FallbackFailures.None; else if (OnFailure == FallbackFailures.None) OnFailure = FallbackFailures.Timeout | FallbackFailures.Network | FallbackFailures.RateLimit | FallbackFailures.Server; }
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool Tools { get => (ConfirmedCapabilities & RequestCapabilities.Tools) != 0; set => ConfirmedCapabilities = value ? ConfirmedCapabilities | RequestCapabilities.Tools : ConfirmedCapabilities & ~RequestCapabilities.Tools; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Vision { get => (ConfirmedCapabilities & RequestCapabilities.Vision) != 0; set => ConfirmedCapabilities = value ? ConfirmedCapabilities | RequestCapabilities.Vision : ConfirmedCapabilities & ~RequestCapabilities.Vision; }
    [System.Text.Json.Serialization.JsonIgnore] public bool StructuredOutput { get => (ConfirmedCapabilities & RequestCapabilities.StructuredOutput) != 0; set => ConfirmedCapabilities = value ? ConfirmedCapabilities | RequestCapabilities.StructuredOutput : ConfirmedCapabilities & ~RequestCapabilities.StructuredOutput; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ComputerUse { get => (ConfirmedCapabilities & RequestCapabilities.ComputerUse) != 0; set => ConfirmedCapabilities = value ? ConfirmedCapabilities | RequestCapabilities.ComputerUse : ConfirmedCapabilities & ~RequestCapabilities.ComputerUse; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Timeout { get => (OnFailure & FallbackFailures.Timeout) != 0; set => OnFailure = value ? OnFailure | FallbackFailures.Timeout : OnFailure & ~FallbackFailures.Timeout; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Network { get => (OnFailure & FallbackFailures.Network) != 0; set => OnFailure = value ? OnFailure | FallbackFailures.Network : OnFailure & ~FallbackFailures.Network; }
    [System.Text.Json.Serialization.JsonIgnore] public bool RateLimit { get => (OnFailure & FallbackFailures.RateLimit) != 0; set => OnFailure = value ? OnFailure | FallbackFailures.RateLimit : OnFailure & ~FallbackFailures.RateLimit; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Server { get => (OnFailure & FallbackFailures.Server) != 0; set => OnFailure = value ? OnFailure | FallbackFailures.Server : OnFailure & ~FallbackFailures.Server; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ModelUnavailable { get => (OnFailure & FallbackFailures.ModelUnavailable) != 0; set => OnFailure = value ? OnFailure | FallbackFailures.ModelUnavailable : OnFailure & ~FallbackFailures.ModelUnavailable; }
    public int TimeoutSeconds { get; set; } = 120;
}
