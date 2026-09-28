# Local Windows build harness

The harness keeps stable build policy in the repository instead of repeating it in prompts. Its machine-readable result is `.harness/build-summary.json`; detailed logs are retained only for failed runs.

## Verify a change

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Invoke-BuildHarness.ps1 -Mode Verify
```

This checks the protected SHA-256 baseline and runs `build.ps1`. The build executes all 39 `tools/Test-*.ps1` validations, compiles every plugin for Revit 2023-2026, builds the MSI, and verifies its payload.

## Create a release

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Invoke-BuildHarness.ps1 -Mode Release
```

Release mode first runs a full preflight without changing the version. Only after that succeeds does it increment the patch version and run the same complete build again. The only published artifact is `artifacts/release/BuildAI_RevitSuite_<version>_Setup.msi`.

On success, intermediate `bin`, `obj`, installer payload, output, and run logs are removed. On failure, the version is restored if needed and the detailed log remains at the location recorded in the summary.

## Protected behavior

Pushpin, viewer/camera, APS publication, and Issue workflow files plus their contract tests are hash-protected. A build-infrastructure repair must not alter them. For an explicitly requested behavior change, review it, run all tests, and then deliberately update the baseline:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Set-ProtectedBaseline.ps1 -Reason 'Describe the approved behavior change'
```

## Assess and apply patches

Assess first. Codex should normally read only `.harness/patch-summary.json`; the full patch and detailed log are escalation data for conflicts and failures:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Invoke-PatchWorkflow.ps1 -PatchPath .\change.patch -Mode Assess
```

The assessment validates paths, size, file types and whitespace; reports changed and protected files; routes fast tests from `patch-policy.json`; and detects clean, already-applied, or conflicting/superseded patches. Application always runs the routed tests and then the complete 39-test build. Failed patches are rolled back.

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Invoke-PatchWorkflow.ps1 -PatchPath .\change.patch -Mode Apply
```

A deliberately approved patch to protected behavior requires both an explicit switch and a reason. The baseline is updated only after every test and the complete build pass:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\harness\Invoke-PatchWorkflow.ps1 -PatchPath .\change.patch -Mode Apply -ApproveProtectedBehaviorChange -Reason 'Approved camera behavior change'
```
