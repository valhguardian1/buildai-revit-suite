$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BuildAI.Core/APS/ApsPublicationClient.cs'))
$start = $source.IndexOf('                var now = DateTime.UtcNow;', $source.IndexOf('AEC MODEL DATA WAIT START'))
$end = $source.IndexOf('                // Poll gently', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'AEC decision boundaries missing' }
$decision = $source.Substring($start, $end-$start).Replace('DateTime.UtcNow','clock')
$harness = @"
using System;
public static class AecBudgetTest {
 static readonly TimeSpan AecModelDataTotalBudget = TimeSpan.FromMinutes(25);
 static readonly TimeSpan AecModelDataStallBudget = TimeSpan.FromMinutes(5);
 static bool IsCompleteProgress(string progress) { return progress == "100%"; }
 sealed class Log { public int Count; public void Report(string value) { if(value.StartsWith("AEC MODEL DATA INCOMPLETE ROOT STALL TOLERATED")) Count++; } }
 static void Run(bool success) {
  var clock = new DateTime(2026,9,7);
  var started = clock;
  var deadlineUtc = clock + AecModelDataTotalBudget;
  var lastProgressChangeUtc = clock;
  var rootStatus = success ? "success" : "inprogress";
  var rootProgress = success ? "100%" : "99%";
  var incompleteRootStallReported = false;
  var diagnostics = new Log();
  var lastFailure = "HTTP 404";
  while(true) {
$decision
   clock = clock.AddSeconds(15);
   if(clock > deadlineUtc.AddSeconds(15)) throw new Exception("Unbounded wait");
  }
  var expected = success ? 5 : 25;
  if((clock-started).TotalMinutes != expected) throw new Exception("Incorrect budget");
  if(diagnostics.Count != (success ? 0 : 1)) throw new Exception("Stall must be logged once");
 }
 public static void Test() { Run(false); Run(true); }
}
"@
Add-Type -TypeDefinition $harness
[AecBudgetTest]::Test()
Write-Host '[OK] Production AEC wait decisions: 99% stall waits 25 minutes, logs once, completed root stops at five minutes.'
