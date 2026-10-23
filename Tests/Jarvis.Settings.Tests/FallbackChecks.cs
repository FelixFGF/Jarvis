using System.Net;
using System.Net.Http;
using Jarvis;
using Jarvis.Services;

internal static class FallbackChecks
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
        ModelFallbackPlan Plan() => new() { Name = "test", Enabled = true, Steps = [new() { Model = "first" }, new() { Model = "second", Provider = "fallback" }] };
        async Task<bool> Throws(Func<Task> action) { try { await action(); return false; } catch (InvalidOperationException) { return true; } }
        var seen = new List<string>();
        var plan = Plan();
        plan.Steps[1].AcceptFallback = false;
        Check(plan.Steps[1].OnFailure == FallbackFailures.None, "Simple fallback toggle disables incoming recovery");
        plan.Steps[1].AcceptFallback = true;
        Check(plan.Steps[1].Timeout && plan.Steps[1].Network && plan.Steps[1].RateLimit, "Simple toggle restores bounded default conditions");
        var saved = System.Text.Json.JsonSerializer.Deserialize<ModelFallbackPlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;
        Check(saved.Steps[1].Provider == "fallback" && saved.Steps[1].AcceptFallback, "Fallback editor round trip retains order/provider/conditions");
        saved.Steps[0].Enabled = false;
        Check(!saved.Steps[0].Enabled && saved.Steps[1].Enabled, "Fallback step enabled state persists independently");
        var result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, () => false,
            (s,t) => { seen.Add(s.Model); return Task.FromResult(new HttpResponseMessage(s.Model == "first" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)); }, (_,_)=>{}, default);
        Check(seen.SequenceEqual(new[]{"first","second"}) && result.Model == "second", "429 follows ordered plan"); result.Response.Dispose();
        plan.Steps[0].Enabled = false; seen.Clear();
        result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, () => false,
            (s,t) => { seen.Add(s.Model); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, (_,_)=>{}, default);
        Check(seen.SequenceEqual(new[]{"second"}), "Disabled fallback card is skipped"); result.Response.Dispose();
        plan.Steps[0].Enabled = true;
        seen.Clear();
        result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, () => false,
            (s,t) => { seen.Add(s.Model); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }, (_,_)=>{}, default);
        Check(seen.Count == 1 && result.Response.StatusCode == HttpStatusCode.Unauthorized, "Permanent authentication error stops"); result.Response.Dispose();
        seen.Clear(); plan.Steps[1].Vision = true;
        result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text | RequestCapabilities.Vision, () => false,
            (s,t) => { seen.Add(s.Model); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, (_,_)=>{}, default);
        Check(seen.SequenceEqual(new[]{"second"}), "Unsupported vision skipped"); result.Response.Dispose();
        seen.Clear();
        Check(await Throws(async () => await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, ()=>true,
            (s,t)=>{seen.Add(s.Model); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));}, (_,_)=>{}, default)), "Unreconciled action blocks routing");
        Check(seen.Count == 0, "Potential action never repeated");
        result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, () => true,
            (s,t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), (_,_)=>{}, default, s => s.Model == "first");
        Check(result.Model == "first", "Current provider can observe an unresolved action"); result.Response.Dispose();
        seen.Clear();
        result = await ModelFallbackOrchestrator.ExecuteAsync(plan, RequestCapabilities.Text, ()=>false,
            (s,t)=> { seen.Add(s.Model); if(s.Model=="first") throw new OperationCanceledException(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, (_,_)=>{}, default);
        Check(seen.Count==2, "Provider timeout routes once"); result.Response.Dispose();
        Check(await Throws(async()=> await ModelFallbackOrchestrator.ExecuteAsync(plan,RequestCapabilities.Text,()=>false,
            (s,t)=>throw new HttpRequestException("mock"),(_,_)=>{},default)), "Bounded total network failure");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled=false;
        try { await ModelFallbackOrchestrator.ExecuteAsync(plan,RequestCapabilities.Text,()=>false,(s,t)=>throw new Exception("must not send"),(_,_)=>{},cancel.Token); }
        catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"User cancellation not retried");
        plan.Enabled=false;
        Check(await Throws(async()=>await ModelFallbackOrchestrator.ExecuteAsync(plan,RequestCapabilities.Text,()=>false,(s,t)=>throw new Exception("must not send"),(_,_)=>{},default)), "Disabled plan never sends");
        Check(ModelFallbackOrchestrator.Required("""{"tools":[{}],"response_format":{},"messages":[{"content":[{"type":"image_url"}]}]}""",true)==(RequestCapabilities.Text|RequestCapabilities.Tools|RequestCapabilities.Vision|RequestCapabilities.StructuredOutput|RequestCapabilities.ComputerUse),"Request capability extraction");
        Console.WriteLine(checks+" fallback checks passed.");
    }
}
