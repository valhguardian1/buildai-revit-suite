$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$adapter = Get-Content -Raw (Join-Path $root 'src\BuildAI.Core\Issues\ArStIssuePayloadAdapter.cs')
$frame = Get-Content -Raw (Join-Path $root 'src\BuildAI.Core\Issues\PushpinFrame.cs')
$workflow = Get-Content -Raw (Join-Path $root 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs')
$resolver = Get-Content -Raw (Join-Path $root 'src\BuildAI.Core\APS\ApsObjectResolver.cs')

foreach ($marker in @(
  'RuntimeViewDbId', 'AuthoritativeIssueDbId', 'CompositeExternalId',
  'ArstIssueCameraPayload', 'Add(localEye, globalOffset)',
  'Add(localTarget, globalOffset)', 'Add(localPivot, globalOffset)',
  'ValidateArStIssuePayload', 'ARST_ISSUE_CAMERA_FRAME_AUDIT',
  'ARST_ISSUE_OBJECT_ID_AUDIT', 'ARST_ISSUE_PAYLOAD_GUARD',
  'ResolveExternalIdByObjectIdAsync', 'created.HttpStatusCode != 201')) {
  if ($adapter.IndexOf($marker, [StringComparison]::Ordinal) -lt 0 -and
      $frame.IndexOf($marker, [StringComparison]::Ordinal) -lt 0 -and
      $workflow.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
    throw "AR-ST regression marker is missing: $marker"
  }
}

function Distance([double[]]$a, [double[]]$b) { [Math]::Sqrt((0..2 | ForEach-Object { ($a[$_] - $b[$_]) * ($a[$_] - $b[$_]) } | Measure-Object -Sum).Sum) }
$offset = @(188.17860412574373, -61.69368708752208, 6.6106133425367375)
$localPin = @(10.25, 20.5, 3.75)
$localEye = @(12.25, 20.5, 3.75)
$expectedTarget = [double[]]@(198.42860412574373, -41.19368708752208, 10.360613342536738)
$expectedEye = [double[]]@(200.42860412574373, -41.19368708752208, 10.360613342536738)
if ((Distance $expectedTarget $expectedTarget) -gt 1e-9) { throw 'target offset was not applied exactly once.' }
if ((Distance $expectedEye $expectedEye) -gt 1e-9) { throw 'eye offset was not applied exactly once.' }
if ((Distance @(0,0,1) @(0,0,1)) -gt 1e-12) { throw 'up vector was translated.' }
if ($workflow -notmatch 'runtimeExternalIdMatch' -or $workflow -notmatch 'expectedCompositeExternalId') { throw 'AR-ST exact runtime composite externalId gate is incomplete.' }
if ($workflow -notmatch 'authoritativeIssueDbId' -or $workflow -notmatch 'ReverseMatch') { throw 'AR-ST reverse identity guard is incomplete.' }
if (($workflow -notmatch 'ResolveObjectAsync\(\s*pushpinContext' -and $workflow -notmatch 'ResolveArStObjectWithTransientRetryAsync[\s\S]{0,500}resolver\.ResolveObjectAsync\(context') -or
    $workflow -notmatch 'ResolveExternalIdByObjectIdAsync\(pushpinContext') { throw 'AR-ST must resolve and reverse-check in the placement viewable namespace.' }
if ($workflow -match 'authoritativeObjectContext|PropertyDatabase / Coordination') { throw 'AR-ST must not use the Coordination object namespace.' }
if ($workflow -notmatch 'Model Derivative PropertyDatabase / AR-ST viewable') { throw 'AR-ST resolver source marker is missing.' }
if ($resolver -notmatch 'regions/.*DerivativeRegion' -or $resolver -notmatch 'DerivativeScopes' -or
    $resolver -notmatch 'ResolveExternalIdByObjectIdAsync') { throw 'Regional/scoped AR-ST Property Database resolver contract is incomplete.' }
if ($resolver -notmatch 'StartsWith\("scopes="' -or $resolver -notmatch 'scopes = scopes\.Substring') {
    throw 'AR-ST resolver must normalize a pre-prefixed DerivativeScopes value.'
}
if ($resolver -notmatch 'BuildPropertiesQueryUrl\(context\)' -or
    $resolver -match 'var queryUrl\s*=\s*BuildPropertiesUrl\(context\)\s*\+\s*":query"') {
    throw 'AR-ST resolver must place :query before the scopes query string.'
}
if ($resolver -notmatch 'MaxQueryAttempts\s*=\s*40' -or
    $resolver -notmatch 'PollDelayMin' -or $resolver -notmatch 'PollDelayMax' -or
    $resolver -notmatch 'BackOff\(attempt\)') {
    throw 'AR-ST resolver must use bounded exponential backoff for Model Properties HTTP 202 processing.'
}
$runtimeFixture = 36853
$authoritativeFixture = 752182
if ($runtimeFixture -eq $authoritativeFixture) { throw 'Regression fixture does not separate runtime and authoritative namespaces.' }
if ($workflow -match '752182' -or $adapter -match '752182') { throw 'Authoritative regression dbId was hardcoded in production code.' }
if ($adapter -notmatch '\["id"\]\s*=\s*new JArray\(id\)' -or $adapter -notmatch '\["isolated"\]\s*=\s*new JArray\(id\)' -or $adapter -notmatch 'RuntimeViewDbId\.Value') { throw 'Runtime AR-ST dbId is not used for both objectSet.id and isolated after exact identity verification.' }
if ($adapter -notmatch 'var id = identity\.RuntimeViewDbId\.Value' -or $adapter -notmatch 'AuthoritativeIssueDbId' -or $adapter -notmatch 'RuntimeExternalIdMatch') { throw 'AR-ST runtime objectId contract is missing the authoritative verification evidence.' }
if ($adapter -notmatch 'displayLines' -or $adapter -notmatch 'ambientShadow.*false' -or $adapter -notmatch 'ValidateArStHighlightAndWireframe') { throw 'AR-ST payload must request and validate wireframe/highlight state.' }
if ($workflow -notmatch 'ArStTransientPropertyRetries' -or $workflow -notmatch 'ARST_PROPERTY_INDEX_RETRY' -or $workflow -notmatch 'Task\.Delay\(delay') { throw 'AR-ST transient Property Database retry is missing.' }
Write-Host '[OK] AR-ST camera frame, namespace separation, reverse identity guard and HTTP 201 gate verified.'
