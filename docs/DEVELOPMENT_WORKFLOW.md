# Git and agent workflow

## Starting a task

Check `git status --short` and preserve existing user changes. Start from the agreed base and create a branch:

```powershell
git switch -c task/short-description
```

The initial `main` commit is a snapshot of the imported source, not evidence of a historical release. Local Git history is not an off-machine backup. Configure a remote only when the user specifies or authorizes one.

## Developer and reviewer

The developer implements the request. For each implementation task, delegate a bounded independent review to a read-only sub-agent, ideally while the developer validates or documents the change. Supply the request, base commit, changed files and available test evidence.

The reviewer reports actionable findings with file locations, impact and suggested validation. Check correctness, unintended behavior changes, compatibility with Revit 2023-2026, packaging when relevant, and the protected-file policy. Do not edit files or launch builds. Explicitly report when no material findings were found and distinguish inspection from runtime testing.

The developer resolves findings, obtains a follow-up review of material fixes, and runs the canonical validation:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File ./harness/Invoke-BuildHarness.ps1 -Mode Verify
```

Read `.harness/build-summary.json` first. Read the referenced detailed log only on failure. Before committing, inspect `git diff`, `git diff --cached` and `git status --short`; stage named files. Commit the reviewed task, then integrate into `main` after validation. Keep unrelated user changes separate.

## Additional agents and worktrees

Start with one developer and one reviewer. Add implementation agents only for independent subtasks with named file ownership. A read-only reviewer can share the developer's folder. For agents making overlapping edits, use a separate branch and worktree:

```powershell
git worktree add .worktrees/task-name -b task/task-name
```

Each worktree needs its own prerequisites and generated output; ignored `.harness` tools are not copied by Git. Coordinate any tools that write to shared machine locations. Never run two builds or release workflows against the same directory. Integrate changes and run the final Verify in the integrating developer's working directory.

## Releases

Use the existing `harness/Invoke-BuildHarness.ps1 -Mode Release` workflow. Only after it succeeds, review and commit its version changes and tag that exact commit as `v<version>`. Never tag an unverified snapshot as a released version. Keep MSI files in `artifacts/release` outside Git. Publishing commits, tags or MSI files requires the user's authorization.
