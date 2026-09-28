$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BuildAI.Core/APS/ApsPublicationClient.cs'))
$start = $source.IndexOf('        private async Task<JObject> WaitForDerivativeReadyAsync(')
$end = $source.IndexOf('        private static RequiredViewableReadiness EvaluateRequiredViewableReadiness(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Polling method boundaries missing.' }
# Execute the production polling method with a virtual clock and scripted transport.
# No APS calls, user profile writes, or 45-minute real-time waits are needed.
$method = $source.Substring($start, $end - $start).Replace('DateTime.UtcNow', 'Now').Replace('Task.Delay(', 'Delay(')
$harness = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class PollingRetryTest {
    DateTime Now = new DateTime(2026,9,7);
    Queue<object> Responses = new Queue<object>();
    List<double> Delays = new List<double>();
    List<string> Routes = new List<string>();
    List<string> Logs = new List<string>();
    ApsTokenResponse ExpectedToken = new ApsTokenResponse();
    CancellationTokenSource CancelOnSend;
    CancellationTokenSource CancelOnDelay;
    bool AlwaysFail;
    const int MissingViewableConfirmationSeconds = 30;
    class ApsTokenResponse {}
    class ApsHttpResult { public int StatusCode; public string Body; }
    class JObject {
        public string this[string key] { get { return key == "status" ? "success" : "100%"; } }
        public static JObject Parse(string body) { return new JObject(); }
    }
    class RequiredViewableReadiness {
        public string Summary = "ready", AvailableViewNames = "view", PublishSetHint = "view";
        public bool HasTerminalFailure = false, IsReady = true, ViewableMissing = false;
        public bool Svf2Declared = false, Svf2Ready = false;
    }
    class ApsPublishSetException : Exception { public ApsPublishSetException(string s):base(s){} }
    static class PluginLog { public static void Info(string s, object data) {} }
    sealed class Progress : IProgress<string> {
        readonly List<string> logs;
        public Progress(List<string> logs) { this.logs=logs; }
        public void Report(string s) { logs.Add(s); }
    }
    static string Esc(string s) { return Uri.EscapeDataString(s); }
    static string Safe(string s) { return s; }
    static bool IsCompleteProgress(string s) { return s == "100%"; }
    static string BuildMissingViewableMessage(string s, RequiredViewableReadiness r) { return s; }
    static RequiredViewableReadiness EvaluateRequiredViewableReadiness(JObject j, string v) { return new RequiredViewableReadiness(); }
    Task Delay(TimeSpan delay, CancellationToken ct) {
        Delays.Add(delay.TotalSeconds);
        if(CancelOnDelay != null) CancelOnDelay.Cancel();
        ct.ThrowIfCancellationRequested();
        Now += delay;
        return Task.CompletedTask;
    }
    Task<ApsHttpResult> SendWithStatusAsync(HttpMethod method, string url, object body, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics, string operation) {
        if(method != HttpMethod.Get || body != null || !Object.ReferenceEquals(token, ExpectedToken)) throw new Exception("Request identity changed");
        Routes.Add(url);
        if(CancelOnSend != null) CancelOnSend.Cancel();
        object next = AlwaysFail ? new HttpRequestException("offline") : Responses.Dequeue();
        if(next is Exception) return Task.FromException<ApsHttpResult>((Exception)next);
        return Task.FromResult(new ApsHttpResult { StatusCode=(int)next, Body="{}" });
    }
$method
    Task<JObject> Run(CancellationToken ct) { return WaitForDerivativeReadyAsync("same-published-version", "view", ExpectedToken, ct, new Progress(Logs)); }
    static void Check(bool condition, string message) { if(!condition) throw new Exception(message); }
    public static async Task Test() {
        var p = new PollingRetryTest();
        p.Responses = new Queue<object>(new object[] {new HttpRequestException("offline"), new TaskCanceledException("timeout"), 202, new HttpRequestException("offline"), 200});
        await p.Run(CancellationToken.None);
        Check(p.Delays.SequenceEqual(new double[]{5,10,5,5}), "Backoff/reset mismatch");
        Check(p.Routes.Count==5 && p.Routes.Distinct().Count()==1, "Derivative route changed");
        Check(p.Logs.Count(s=>s.StartsWith("APS TRANSIENT TRANSPORT FAILURE"))==3, "Missing transient diagnostics");
        p = new PollingRetryTest { AlwaysFail=true };
        try { await p.Run(CancellationToken.None); throw new Exception("Missing timeout"); } catch(TimeoutException) {}
        Check(p.Now == new DateTime(2026,9,7).AddMinutes(45), "Budget exceeded");
        Check(p.Delays.All(d=>d>=5 && d<=30) && p.Delays.Max()==30, "Backoff outside 5-30 seconds");
        foreach(var ex in new Exception[]{new HttpRequestException(), new TaskCanceledException(), new OperationCanceledException()}) {
            using(var cts = new CancellationTokenSource()) {
                p = new PollingRetryTest { CancelOnSend=cts };
                p.Responses.Enqueue(ex);
                try { await p.Run(cts.Token); throw new Exception("Caller cancellation swallowed"); } catch(Exception actual) { Check(Object.ReferenceEquals(actual,ex), "Wrong cancellation exception"); }
                Check(p.Delays.Count==0 && p.Routes.Count==1, "Caller cancellation retried");
            }
        }
        p = new PollingRetryTest(); p.Responses.Enqueue(new OperationCanceledException());
        try { await p.Run(CancellationToken.None); throw new Exception("Generic cancellation swallowed"); } catch(OperationCanceledException) {}
        Check(p.Delays.Count==0, "Generic cancellation retried");
        using(var cts = new CancellationTokenSource()) {
            p = new PollingRetryTest { AlwaysFail=true, CancelOnDelay=cts };
            try { await p.Run(cts.Token); throw new Exception("Delay cancellation swallowed"); } catch(OperationCanceledException) {}
            Check(p.Routes.Count==1, "Request sent after delay cancellation");
        }
        using(var cts = new CancellationTokenSource()) {
            cts.Cancel(); p = new PollingRetryTest();
            try { await p.Run(cts.Token); throw new Exception("Initial cancellation swallowed"); } catch(OperationCanceledException) {}
            Check(p.Routes.Count==0, "Request sent after initial cancellation");
        }
    }
}
"@
Add-Type -TypeDefinition $harness
[PollingRetryTest]::Test().GetAwaiter().GetResult()
Write-Host '[OK] Polling transport retry, backoff reset/cap, 45-minute budget, request identity, diagnostics and cancellation verified with a virtual clock.'
