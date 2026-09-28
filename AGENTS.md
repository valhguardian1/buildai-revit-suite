# BuildAI repository instructions

## Git and independent review

Use a task branch (`task/<short-name>`) for new work. Keep `main` as the accepted integration baseline. Commit only reviewed source, configuration and documentation; never commit credentials, local harness state, build output or installer payloads. Do not push or create a remote without user authorization.

For implementation tasks, delegate an independent read-only review to one sub-agent. This is standing authorization to use a reviewer. The developer owns edits, integration and the final validation; the reviewer checks the diff, protected behavior boundaries, regression risks and validation evidence. Give the reviewer a bounded scope and the intended behavior. Resolve material findings before reporting completion. If sub-agents are unavailable, disclose that independent review could not run.

Use additional agents only for substantial independent work. Assign disjoint files or separate Git worktrees and explicitly name each owner. Never run concurrent builds, patch workflows, baseline updates or releases in the same working directory. Only the integrating developer runs the final canonical Verify. A reviewer must not modify files or regenerate the protected baseline.

Follow `docs/DEVELOPMENT_WORKFLOW.md` for branches, review and release tags.

Use `harness/Invoke-BuildHarness.ps1 -Mode Verify` as the canonical local Windows validation command. It always runs the complete build, every repository validation test, and MSI verification for Revit 2023-2026.

Read `.harness/build-summary.json` first after every run. Open the detailed failed-run log named there only when the summary reports failure. Successful run logs are temporary and are removed.

Codex may autonomously repair build scripts, project files, dependency configuration, tests, packaging, and other build infrastructure. It must not change protected pushpin, camera/viewer, APS publication, or Issue behavior as part of a build repair. The SHA-256 baseline and functional tests enforce this boundary.

Never regenerate `harness/protected-baseline.json` merely to make a failed build pass. Update it only for an explicit user-requested functional change, using `harness/Set-ProtectedBaseline.ps1 -Reason '<request>'`, after reviewing the protected behavior change and passing all tests.

For a supplied `.patch` or `.diff`, run `harness/Invoke-PatchWorkflow.ps1 -PatchPath <file> -Mode Assess` first and read only `.harness/patch-summary.json`. Do not open the entire patch unless the summary reports a conflict, high risk, or an unclear target. Apply only patches assessed as `ready`. Protected behavior requires explicit approval plus `-ApproveProtectedBehaviorChange -Reason '<request>'`. The workflow runs routed fast tests before the mandatory full build and rolls target files back on failure.

Use `harness/Invoke-BuildHarness.ps1 -Mode Release` for releases. It performs a green preflight at the current version, increments the patch version, reruns the complete build at the new version, publishes only the MSI under `artifacts/release`, and removes intermediate build output after success. If the final build fails, it restores the prior version and keeps diagnostics.
