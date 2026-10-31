using System.Net.Http;
using System.Text.Json.Nodes;

namespace Jarvis.Services;

public sealed record FallbackResult(HttpResponseMessage Response, string Model, string Provider);
public static class ModelFallbackOrchestrator
{
    public static void Validate(ModelFallbackPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 80 || plan.Steps.Count is < 1 or > 8)
            throw new ArgumentException("A plan needs a name and 1–8 steps.");
        foreach (var step in plan.Steps)
        {
            if (!step.Enabled) continue;
            if (step.Provider is not ("primary" or "fallback") || !ApiModelCatalogService.IsValidModelId(step.Model) ||
                step.TimeoutSeconds is < 5 or > 600)
                throw new ArgumentException("Invalid provider, model or timeout (5–600 seconds).");
        }
        if (!plan.Steps.Any(step => step.Enabled)) throw new ArgumentException("A plan needs at least one enabled step.");
    }
    public static RequestCapabilities Required(string payload, bool computerUse)
    {
        var root = JsonNode.Parse(payload)!;
        var required = RequestCapabilities.Text;
        if (root["tools"] is JsonArray tools && tools.Count > 0) required |= RequestCapabilities.Tools;
        if (root["response_format"] is not null) required |= RequestCapabilities.StructuredOutput;
        if (root["messages"] is JsonArray messages)
            foreach (var message in messages)
                if (message?["content"] is JsonArray parts && parts.Any(p => p?["type"]?.ToString() == "image_url"))
                    required |= RequestCapabilities.Vision;
        if (computerUse) required |= RequestCapabilities.ComputerUse;
        return required;
    }
    public static async Task<FallbackResult> ExecuteAsync(ModelFallbackPlan plan, RequestCapabilities required,
        Func<bool> unresolvedAction, Func<ModelFallbackStep, CancellationToken, Task<HttpResponseMessage>> send,
        Action<string, string> status, CancellationToken token,
        Func<ModelFallbackStep, bool>? isCurrentProvider = null,
        Func<ModelFallbackStep, TimeSpan>? timeoutSelector = null)
    {
        Validate(plan);
        if (!plan.Enabled) throw new InvalidOperationException("Disabled fallback plan cannot route requests.");
        FallbackFailures failure = FallbackFailures.None;
        var attempt = 0;
        foreach (var step in plan.Steps)
        {
            token.ThrowIfCancellationRequested();
            if (!step.Enabled) { status(step.Model, "disabled"); continue; }
            if ((step.ConfirmedCapabilities & required) != required) { status(step.Model, "incompatible"); continue; }
            if (failure != FallbackFailures.None && (step.OnFailure & failure) == 0) { status(step.Model, "condition_not_matched"); continue; }
            // Observation requests may continue on the current provider. Switching while an
            // input outcome is unknown must wait for reconciliation, never replay the input.
            if (unresolvedAction() && isCurrentProvider?.Invoke(step) != true)
                throw new InvalidOperationException("An action must be reconciled before switching providers.");
            var delay = TimeSpan.FromMilliseconds(Math.Min(4000, 500 * Math.Pow(2, attempt++)));
            status(step.Model, failure == FallbackFailures.None ? "selected" : failure.ToString());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutSelector?.Invoke(step) ?? TimeSpan.FromSeconds(step.TimeoutSeconds));
            try
            {
                var response = await send(step, deadline.Token);
                if (response.IsSuccessStatusCode) return new(response, step.Model, step.Provider);
                failure = (int)response.StatusCode switch
                {
                    408 => FallbackFailures.Timeout, 429 => FallbackFailures.RateLimit,
                    500 or 502 or 503 or 504 => FallbackFailures.Server,
                    404 => FallbackFailures.ModelUnavailable, _ => FallbackFailures.None
                };
                // Auth and permanent errors are returned immediately, never blindly retried.
                if (failure == FallbackFailures.None) return new(response, step.Model, step.Provider);
                var retryAfter = response.Headers.RetryAfter;
                var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                if (requestedDelay > delay) delay = requestedDelay.Value;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { failure = FallbackFailures.Timeout; }
            catch (HttpRequestException) { failure = FallbackFailures.Network; }
            // A very long server cooldown is a blocker, not permission to retry early.
            if (delay > TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("The provider requested a long cooldown. Retry later.");
            await Task.Delay(delay, token);
        }
        throw new InvalidOperationException("All compatible fallback steps failed or were skipped.");
    }
}
