namespace Jarvis.Services;

/// <summary>
/// Tracks a bounded sequence of desktop-state signatures after native input.
/// It lets the caller capture after the UI has either changed and stabilized or
/// remained stable for a short grace period, without using an unbounded sleep.
/// </summary>
public sealed class ComputerUseTransitionTracker(string initialSignature)
{
    private readonly string _initial = initialSignature ?? "";
    private string _last = initialSignature ?? "";
    private int _stableSamples;

    public bool Changed { get; private set; }
    public int StableSamples => _stableSamples;

    public bool Observe(string signature)
    {
        signature ??= "";
        Changed |= !string.Equals(signature, _initial, StringComparison.Ordinal);
        _stableSamples = string.Equals(signature, _last, StringComparison.Ordinal) ? _stableSamples + 1 : 1;
        _last = signature;
        return Changed ? _stableSamples >= 3 : _stableSamples >= 5;
    }
}
