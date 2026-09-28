$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = [IO.File]::ReadAllText((Join-Path $root 'src/Plugin2.VolumeEstimator/Publishing/PublicationFlow.cs'))
$start = $source.IndexOf('        private static int _accPublicationInProgress;')
$end = $source.IndexOf('        public static async Task PublishAsync(' + "`n" + '            string filePath,')
if ($end -lt 0) { $end = $source.IndexOf('        public static async Task PublishAsync(' + "`r`n" + '            string filePath,') }
if ($start -lt 0 -or $end -le $start) { throw 'ACC workflow boundaries missing.' }
$workflow = $source.Substring($start, $end - $start)
$notify = $source.Substring($source.IndexOf('        private static void Notify('))
# Execute production workflow and gate with scripted APS dependencies: no Revit,
# network, credentials or user-profile writes. Keep readiness failures observable.
$code = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.APS;
using BuildAI.Core.Configuration;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using Plugin2.VolumeEstimator.Estimation;
namespace Plugin2.VolumeEstimator.Publishing {
public static class PublicationFlow {
$workflow
$notify
namespace BuildAI.Core.Configuration { public class BuildAiOptions { public string BaseUrl = "https://test.invalid"; public static BuildAiOptions Load() => new BuildAiOptions(); } }
namespace BuildAI.Core.Security { public class WindowsCredentialStore {} }
namespace BuildAI.Core.Logging { public static class PluginLog { public static void Error(string s, Exception e) {} public static void Warn(string s, object o) {} } }
namespace Newtonsoft.Json { public static class JsonConvert { public static string SerializeObject(object o) => "descriptor"; } }
namespace Plugin2.VolumeEstimator.Revit {
public static class PluginContext { public static BuildAiOptions Options = new BuildAiOptions(); }
public static class AccViewPreparation { public const string ViewName = "BuildAI Publication"; }
}
namespace BuildAI.Core.Issues {
public class ApsCloudModelIdentity {}
public class ApsPushpinContext { public bool Is3D = true; public string DerivativeUrn = "urn", ViewableGeometryGuid = "geometry"; }
public static class IssueCreationFileLog { public static void WriteTo(string p, string s) {} public static void WriteFatalTo(string p, string s) {} }
public class IssueIntegrationClient : IDisposable {
 public IssueIntegrationClient(string url, WindowsCredentialStore store) {}
 public Task<object> GetApsTokenAsync(CancellationToken ct, IProgress<string> p) => Task.FromResult<object>(new object());
 public void Dispose() {}
}
}
namespace Plugin2.VolumeEstimator.Estimation {
public class EstimationResult { public string PublicationStatus, PublicationError; public ApsPushpinContext AccView; }
}
namespace BuildAI.Core.APS {
public class ApsPublicationClient : IDisposable {
 public static TaskCompletionSource<ApsPushpinContext> Completion;
 public Task<ApsPushpinContext> PublishAndResolveAsync(ApsCloudModelIdentity id, object token, string view, CancellationToken ct, IProgress<string> p, bool checkPreviousPublishSet = true) {
  if (view != "BuildAI Publication" || checkPreviousPublishSet) throw new Exception("Wrong view or stale-set preflight enabled");
  return Completion.Task;
 }
 public void Dispose() {}
}
}
public static class AccPublicationTest {
 static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
 public static void Run() {
  RunCase(new ApsPushpinContext(), null, false, true);
  RunCase(null, new Exception("APS forbidden"), false, false);
  RunCase(new ApsPushpinContext { ViewableGeometryGuid = "" }, null, false, false);
  RunCase(new ApsPushpinContext { Is3D = false }, null, false, false);
  RunCase(new ApsPushpinContext(), null, true, true);
 }
 static void RunCase(ApsPushpinContext response, Exception failure, bool brokenUi, bool success) {
  Check(Plugin2.VolumeEstimator.Publishing.PublicationFlow.TryBeginAccPublication(), "Gate leaked from earlier operation");
  ApsPublicationClient.Completion = new TaskCompletionSource<ApsPushpinContext>();
  var result = new EstimationResult();
  int callbacks = 0;
  var task = Plugin2.VolumeEstimator.Publishing.PublicationFlow.PublishAsync(new ApsCloudModelIdentity(), "model", result, r => { callbacks++; if (brokenUi) throw new Exception("UI closed"); }, "log");
  Check(result.PublicationStatus == "publishing-acc" && !task.IsCompleted, "Premature ready state");
  Check(!Plugin2.VolumeEstimator.Publishing.PublicationFlow.TryBeginAccPublication(), "Duplicate publication admitted");
  if (failure != null) ApsPublicationClient.Completion.SetException(failure); else ApsPublicationClient.Completion.SetResult(response);
  task.GetAwaiter().GetResult();
  Check(result.PublicationStatus == (success ? "published-acc" : "failed"), "Incorrect final status");
  Check(success ? ReferenceEquals(response, result.AccView) : !string.IsNullOrWhiteSpace(result.PublicationError), "Missing descriptor/error");
  Check(callbacks == 2, "UI did not receive both states");
  Check(Plugin2.VolumeEstimator.Publishing.PublicationFlow.TryBeginAccPublication(), "Gate not released");
  Plugin2.VolumeEstimator.Publishing.PublicationFlow.EndAccPublication();
 }
}
"@
Add-Type -TypeDefinition $code
[AccPublicationTest]::Run()
Write-Host '[OK] ACC publication success, rejection, invalid view, concurrent launch and closed UI verified.'
