$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BuildAI.Core/APS/ApsPublicationClient.cs'))
$start = $source.IndexOf('        private async Task<ApsModelItemContext> ResolveRevitModelUrnAsync(')
$end = $source.IndexOf('        private async Task<ApsModelItemContext> GetLatestVersionAsync(', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Direct Revit URN resolver boundaries missing.' }
$method = $source.Substring($start, $end - $start)
$code = @"
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
public class AccCloudUrnTest {
 class ApsCloudModelIdentity { public string CloudModelUrn, ProjectId = "b.project"; }
 class ApsTokenResponse {}
 class ApsModelItemContext { public string ItemId, ItemName; }
 readonly Queue<string> Responses = new Queue<string>();
 readonly List<string> Urls = new List<string>();
 static string Esc(string value) => Uri.EscapeDataString(value);
 Task<string> SendAsync(HttpMethod method, string url, string body, ApsTokenResponse token, CancellationToken ct, IProgress<string> log, string operation) {
  ct.ThrowIfCancellationRequested();
  if (method != HttpMethod.Get || body != null) throw new Exception("Resolver must be read-only");
  Urls.Add(url);
  if (Responses.Count == 0) throw new Exception("Unexpected additional request");
  return Task.FromResult(Responses.Dequeue());
 }
$method
 static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
 static string Item(string urn, string type = "items") => new JObject { ["data"] = new JObject { ["type"] = type, ["id"] = urn, ["attributes"] = new JObject { ["displayName"] = "Model.rvt" } } }.ToString();
 static string Version(string urn, string item, string relationType = "items") => new JObject { ["data"] = new JObject { ["type"] = "versions", ["id"] = urn, ["relationships"] = new JObject { ["item"] = new JObject { ["data"] = new JObject { ["type"] = relationType, ["id"] = item } } } } }.ToString();
 static void Reject(string urn, params string[] responses) {
  var test = new AccCloudUrnTest(); foreach (var response in responses) test.Responses.Enqueue(response);
  try { test.ResolveRevitModelUrnAsync(new ApsCloudModelIdentity { CloudModelUrn = urn }, new ApsTokenResponse(), default, null).GetAwaiter().GetResult(); }
  catch (InvalidOperationException) { return; }
  throw new Exception("Invalid identity was accepted: " + urn);
 }
 public static void Run() {
  foreach (var region in new[] { "wipprod", "wips5jku" }) {
   var item = "urn:adsk." + region + ":dm.lineage:exact-item";
   var test = new AccCloudUrnTest(); test.Responses.Enqueue(Item(item));
   var result = test.ResolveRevitModelUrnAsync(new ApsCloudModelIdentity { CloudModelUrn = item }, new ApsTokenResponse(), default, null).GetAwaiter().GetResult();
   Check(result.ItemId == item && result.ItemName == "Model.rvt", "Wrong direct item");
   Check(test.Urls.Count == 1 && test.Urls[0].EndsWith("/items/" + Esc(item)), "Direct URN unexpectedly scans folders");
   var version = "urn:adsk." + region + ":fs.file:vf.other-id?version=12";
   test = new AccCloudUrnTest(); test.Responses.Enqueue(Version(version, item)); test.Responses.Enqueue(Item(item));
   result = test.ResolveRevitModelUrnAsync(new ApsCloudModelIdentity { CloudModelUrn = version }, new ApsTokenResponse(), default, null).GetAwaiter().GetResult();
   Check(result.ItemId == item && test.Urls.Count == 2, "Version relationship not resolved");
   Check(test.Urls[0].EndsWith("/versions/" + Esc(version)) && test.Urls[1].EndsWith("/items/" + Esc(item)), "URN routing/escaping failed");
   Reject(item, Item(item + "-wrong"));
   Reject(item, Item(item, "folders"));
   Reject(version, Version(version + "-wrong", item));
   Reject(version, Version(version, item, "folders"));
   Reject(version, "{ 'data': { 'type':'versions', 'id':'" + version + "' } }");
  }
  Reject("Model.rvt"); Reject("urn:adsk.wipprod:fs.folder:co.folder");
  Reject("urn:adsk.wipprod:dm.lineage:id", "{ 'data': null }");
 }
}
"@
$references = @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
$references += [Newtonsoft.Json.Linq.JObject].Assembly.Location
Add-Type -TypeDefinition $code -ReferencedAssemblies ($references | Sort-Object -Unique) -CompilerOptions '/nowarn:1701'
[AccCloudUrnTest]::Run()
Write-Host '[OK] Production URN resolver: direct items, version relationships, regional URNs, mismatches and missing data.'
