using Newtonsoft.Json.Linq;
using BuildAI.Core.ViewerProbe;
using System.Globalization;
using Plugin5.ClashFormaIntegration.Revit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core;
using BuildAI.Core.APS;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Issues
{
    public sealed class ClashIssueWorkflow
    {
        private static int _creationInProgress;
        private readonly IssueIntegrationClient _client;
        public Task<IReadOnlyList<ApsAssigneeResolution>> GetProjectAssigneesAsync(CancellationToken ct = default) => _client.GetProjectAssigneesAsync(ct);
        public bool IsBuildAiAvailable { get; private set; } = true;
        public ClashIssueWorkflow(IssueIntegrationClient client) { _client = client; }

        public async Task LoadExistingAsync(ClashReport report, string modelUid, CancellationToken ct = default)
        {
            try
            {
                var existing = await _client.GetExistingIssuesAsync(modelUid, "clash", ct).ConfigureAwait(false);
                IsBuildAiAvailable = true;
                ExistingIssueMergeService.Merge(report.Items, existing, CreateHistoricalRow,
                    message => PluginLog.Info(message));
            }
            catch (Exception ex) { IsBuildAiAvailable = false; PluginLog.Error("Failed to load existing Clash Issues", ex); }
        }

        public async Task CreateSelectedAsync(ClashReport report, IReadOnlyList<ClashItem> selectedRows, string modelUid, ApsCloudModelIdentity cloudModel, IProgress<IssueCreationProgress> progress, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _creationInProgress, 1) != 0)
                throw new InvalidOperationException("Another Clash Issue creation operation is already running.");
            try
            {
                var operationId = Guid.NewGuid().ToString("N");
                using (PluginLog.BeginOperation("Create Clash Issues", operationId))
                    await CreateSelectedCoreAsync(report, selectedRows, modelUid, cloudModel, progress, ct, operationId).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _creationInProgress, 0);
            }
        }

        private async Task CreateSelectedCoreAsync(ClashReport report, IReadOnlyList<ClashItem> selectedRows, string modelUid, ApsCloudModelIdentity cloudModel, IProgress<IssueCreationProgress> progress, CancellationToken ct, string operationId)
        {
            var generatedAtUtc = DateTime.UtcNow;
            var rows = (selectedRows ?? Array.Empty<ClashItem>())
                .Where(x => x != null && !x.HasApsIssue)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ResultKey) ? x.Id : x.ResultKey, StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException("No rows are selected for Issue creation. Select at least one result and try again.");
            var probeTotal = rows.Count;
            int ok = 0, failed = 0;
            int cameraPrimaryAccepted = 0, cameraNearFallbackAccepted = 0, cameraFarFallbackAccepted = 0, cameraRejected = 0, postAttempted = 0;
            int revitClashesConfirmed = rows.Count, pushpinExactAccepted = 0, pushpinCorrected = 0;
            int completed = 0;
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message=BuildInfo.Banner, Error=BuildInfo.LoadedAssemblyIdentity(typeof(ClashIssueWorkflow).Assembly, null) });
            IProgress<string> diagnosticProgress = new Progress<string>(text => progress?.Report(new IssueCreationProgress
            {
                Current = completed, Total = probeTotal, Succeeded = ok, Failed = failed,
                Message = "Diagnostics", Error = text
            }));
            var pendingStore = new PendingIssueBatchStore();
            var fingerprintStore = new IssueFingerprintStore();
            // A read-only BuildAI lookup validates the frozen model/source pair
            // before the first irreversible Autodesk Issue POST.
            await _client.GetExistingIssuesAsync(modelUid, "clash", ct).ConfigureAwait(false);
            // One-time cleanup for journals written before attempt counting existed:
            // anything queued more than a day ago and still unacknowledged is not
            // waiting out a transient outage, and would otherwise receive a full fresh
            // allowance of retries it has already exhausted in practice.
            var retiredStale = pendingStore.RetireStale(TimeSpan.FromDays(1));
            if (retiredStale > 0)
            {
                diagnosticProgress.Report(
                    "BUILDAI OUTBOX CLEANUP" + Environment.NewLine +
                    "Retired " + retiredStale + " batch(es) queued more than 24 hours ago and never acknowledged." + Environment.NewLine +
                    "They remain in the journal, flagged, and the Autodesk Issues they describe still exist.");
            }

            // Draining the outbox is best-effort and must never abort the run.
            // It previously threw straight through: a batch the server rejects for a
            // structural reason is rejected identically every time, so one unsyncable
            // Issue killed every subsequent run before any new work began - the plugin
            // was effectively disabled for that model. The new Issues the user asked
            // for do not depend on old ones reaching BuildAI.
            foreach (var pending in pendingStore.GetPending(modelUid, "clash"))
            {
                var attempt = pendingStore.AttemptsSoFar(pending) + 1;
                diagnosticProgress.Report("BUILDAI OUTBOX RETRY\nRetrying " + pending.Issues.Count +
                                          " already-created Autodesk Issue(s) before creating new ones." +
                                          "\nAttempt " + attempt + " of " + PendingIssueBatchStore.MaxDrainAttempts + ".");
                try
                {
                    await _client.SaveIssuesAsync(pending, ct, diagnosticProgress).ConfigureAwait(false);
                    foreach (var fingerprint in pendingStore.GetFingerprints(pending))
                        fingerprintStore.MarkCreated(fingerprint);
                    pendingStore.Acknowledge(pending);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var retired = pendingStore.RecordFailure(pending, ex.Message);
                    diagnosticProgress.Report(
                        "BUILDAI OUTBOX RETRY FAILED" + Environment.NewLine +
                        "Issues in batch: " + pending.Issues.Count + Environment.NewLine +
                        "Attempt: " + attempt + " of " + PendingIssueBatchStore.MaxDrainAttempts + Environment.NewLine +
                        "Reason: " + ex.Message + Environment.NewLine +
                        (retired
                            ? "This batch has used up its attempts and was RETIRED. It stays in the journal, flagged, " +
                              "and will no longer be retried. The Autodesk Issues themselves are unaffected and still exist."
                            : "It will be retried on the next run.") + Environment.NewLine +
                        "Issue creation continues; new Issues do not depend on this batch.");
                }
            }
            var project = await _client.GetCurrentApsProjectAsync(ct, diagnosticProgress).ConfigureAwait(false);
            var token = await _client.GetApsTokenAsync(ct, diagnosticProgress).ConfigureAwait(false);

            // Preflight the Issues API BEFORE publishing. Resolving the Issues
            // environment costs about a second, while publication plus translation
            // costs 15-25 minutes. Running it last meant an unreachable Issues
            // endpoint was only discovered after all that time had been spent.
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Checking Autodesk Issues access..." });
            await _client.ResolveIssueEnvironmentAsync(
                FirstNonEmptyId(project?.ContainerId, project?.ProjectId),
                cloudModel?.ProjectId, token, ct, diagnosticProgress).ConfigureAwait(false);

            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message=cloudModel != null && cloudModel.UseExistingPublication ? "Resolving latest published model..." : "Publishing current Revit model and resolving linked document..." });
            ApsPushpinContext pushpinContext;
            using (var publication = new ApsPublicationClient())
            {
                pushpinContext = cloudModel != null && cloudModel.UseExistingPublication
                    ? await publication.ResolveExistingAsync(cloudModel, token, BuildAiViewNames.Coordination, ct, diagnosticProgress).ConfigureAwait(false)
                    : await publication.PublishAndResolveAsync(cloudModel, token, BuildAiViewNames.Coordination, ct, diagnosticProgress).ConfigureAwait(false);
            }
            if (!pushpinContext.IsUsable)
                throw new InvalidOperationException("The published Autodesk model cannot be attached to Issues. Required view: BuildAI Coordination\n" + (pushpinContext.ValidationError ?? "Document URN, version, or 3D viewable GUID is missing."));
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Validating Autodesk region, URN and Issues environment..." });
            var issueEnvironment = await _client.ResolveRegionalIssueEnvironmentAsync(project, cloudModel?.ProjectId, pushpinContext, token, ct, diagnosticProgress).ConfigureAwait(false);
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Resolving current APS user..." });
            string currentUserId = null;
            try { currentUserId = await _client.ResolveCurrentUserIdAsync(issueEnvironment, token, ct, diagnosticProgress).ConfigureAwait(false); }
            catch (Exception ex) { diagnosticProgress.Report("APS USER RESOLUTION WARNING\n" + ex.Message + "\nIssues will be created without assignment if BIM Manager lookup also fails."); }
            var assigneeCache = new Dictionary<string, ApsAssigneeResolution>(StringComparer.OrdinalIgnoreCase);
            var fingerprints = new Dictionary<ClashItem, string>();
            foreach (var row in rows)
                fingerprints[row] = fingerprintStore.Build(
                    "clash", issueEnvironment.ProjectId, issueEnvironment.ContainerId,
                    pushpinContext.VersionUrn, row.ModelUidA, row.ModelUidB,
                    row.Kind.ToString(), row.ElementAId.ToString(), row.ElementBId.ToString(),
                    row.SourceA + "=>" + row.SourceB);
            var skipped = rows.Where(row => fingerprintStore.Contains(fingerprints[row])).ToList();
            foreach (var row in skipped)
            {
                row.IsSelectedForIssueCreation = false;
                row.CreationState = IssueCreationState.AlreadyCreated;
            }
            rows = rows.Except(skipped).ToList();
            probeTotal = rows.Count;
            diagnosticProgress.Report("DUPLICATE PROTECTION\nWill create: " + rows.Count + "\nSkipped as already created: " + skipped.Count + "\nFailed results remain retryable.");
            if (rows.Count == 0)
            {
                progress?.Report(new IssueCreationProgress { Current=0, Total=0, Succeeded=0, Failed=0, Message="No new Issues to create; all selected results were already confirmed locally.", IsCompleted=true });
                return;
            }
            var saved = new List<SavedIssueDto>();
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Loading the APS viewable once and resolving " + probeTotal + " Viewer objects..." });
            var probeRequests = rows.Select(row => BuildProbeRequest(row, pushpinContext)).ToList();
            var probeOutcomes = await ViewerCoordinateProbe.ResolveBatchAsync(probeRequests, token, ct, diagnosticProgress).ConfigureAwait(false);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                progress?.Report(new IssueCreationProgress { Current=i, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Preparing Viewer Coordinate Probe " + (i + 1) + "/" + probeTotal + " for " + row.ElementA + " / " + row.ElementB });
                try
                {
                    var caption = IssueCaptionBuilder.Clash(row.CategoryA, row.CategoryB, row.Level, operationId);
                    var title = caption.Title;
                    // GetRoleCandidates already returns discipline-specific roles first and
                    // ends with "BIM Coordinator", "BIM Manager" as the deliberate fallback.
                    // Prepending "BIM Manager" here inverted that order: because every real
                    // project has a BIM Manager, the first candidate always matched and the
                    // discipline mapping below it never ran. Every Issue went to the BIM
                    // Manager regardless of which element was at fault.
                    // A configured role wins over the derived discipline, but stays at the
                    // head of the SAME candidate list rather than replacing it: if nobody in
                    // the project holds the configured role, resolution continues down to the
                    // discipline candidates instead of silently leaving Issues unassigned.
                    var roleCandidates = PrependPreferredRole((PluginContext.Settings ?? Plugin5Settings.Load()).PreferredAssigneeRole, GetRoleCandidates(row));
                    var roleKey = string.Join("|", roleCandidates);
                    var assignee = row.ManualAssignee;
                    if (assignee != null && assignee.IsResolved && !string.Equals((assignee.ProjectId ?? ""), (issueEnvironment.ProjectId ?? "" ).Replace("b.", ""), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Selected assignee belongs to another Autodesk project. Select the assignee again.");
                    if ((assignee == null || !assignee.IsResolved) && !assigneeCache.TryGetValue(roleKey, out assignee))
                    {
                        assignee = await _client.ResolveAssigneeByRoleAsync(issueEnvironment, token, roleCandidates, currentUserId, ct, diagnosticProgress).ConfigureAwait(false);
                        assigneeCache[roleKey] = assignee;
                    }
                    var useElementA = !string.IsNullOrWhiteSpace(row.ElementAUniqueId);
                    var externalId = useElementA ? row.ElementAUniqueId : row.ElementBUniqueId;
                    var elementId = useElementA ? row.ElementAId : row.ElementBId;
                    var elementModelUid = useElementA ? row.ModelUidA : row.ModelUidB;
                    var linkInstanceUid = useElementA ? row.LinkInstanceAUniqueId : row.LinkInstanceBUniqueId;
                    var sourceName = useElementA ? row.SourceA : row.SourceB;
                    diagnosticProgress.Report("VIEWER-FIRST OBJECT RESOLUTION\nResult key: " + row.ResultKey +
                        "\nRequested externalId: " + (externalId ?? "") +
                        "\nSelected link instance UID: " + (linkInstanceUid ?? "") +
                        "\nSelected model UID: " + (elementModelUid ?? "") +
                        "\nFull Model Derivative properties download is disabled." +
                        "\nViewer fragment world bounds are the authoritative coordinate source.");

                    var probeOutcome = probeOutcomes[i];
                    if (!probeOutcome.IsSuccess)
                    {
                        var probeError = probeOutcome.Error ?? "";
                        if (probeError.IndexOf("CAMERA_CAPTURE_FAILED", StringComparison.Ordinal) >= 0 ||
                            probeError.IndexOf("CAMERA CAPTURE REJECTED", StringComparison.Ordinal) >= 0) cameraRejected++;
                        var category = probeError.IndexOf("PUSHPIN_VALIDATION_REJECTED", StringComparison.Ordinal) >= 0
                            ? "PUSHPIN_VALIDATION_REJECTED"
                            : probeError.IndexOf("CAMERA", StringComparison.Ordinal) >= 0
                                ? "CAMERA_CAPTURE_FAILED" : "OBJECT_RESOLUTION_FAILED";
                        throw new InvalidOperationException(category + " | Element UniqueId " +
                            (externalId ?? "<empty>") + ": " + probeError);
                    }                    var probe = probeOutcome.Result;
                    // Derive diagnostic quality from the accepted viewport; do not add fields to the Issue payload.
                    var cameraEye = probe.ViewerState?["viewport"]?["eye"] as Newtonsoft.Json.Linq.JArray;
                    var cameraTarget = probe.ViewerState?["viewport"]?["target"] as Newtonsoft.Json.Linq.JArray;
                    if (cameraEye != null && cameraTarget != null && cameraEye.Count == 3 && cameraTarget.Count == 3)
                    {
                        var distanceFt = Math.Sqrt(Enumerable.Range(0, 3).Sum(axis =>
                            Math.Pow((double)cameraEye[axis] - (double)cameraTarget[axis], 2)));
                        if (string.Equals(probe.CameraQuality, "FarFallback", StringComparison.Ordinal) ||
                            string.Equals(probe.CameraQuality, "DeterministicFallback", StringComparison.Ordinal)) cameraFarFallbackAccepted++;
                        else if (string.Equals(probe.CameraQuality, "NearFallback", StringComparison.Ordinal) ||
                                 distanceFt < 0.60 / 0.3048) cameraNearFallbackAccepted++;
                        else cameraPrimaryAccepted++;
                        if (string.Equals(probe.PushpinValidationOutcome, "ViewerBoundsCorrected", StringComparison.Ordinal)) pushpinCorrected++;
                        else pushpinExactAccepted++;
                    }

                    var resolvedExternalId = probe.ResolvedExternalId;

                    // The position is written exactly as the Viewer produced it:
                    // viewer-local, in Viewer units, alongside the Viewer's own
                    // globalOffset in the captured state. ACC applies that offset
                    // when it places the pin.
                    //
                    // 7.0.17 instead added globalOffset here and scaled everything
                    // to metres, then left a scaled globalOffset in the state for ACC
                    // to apply again. Measured on 03 Sep 2026 that put pins 54-68 m
                    // from their elements; replaying the route reproduces the observed
                    // pin to 2.4 ft on Issue #34, closer than a click on a floor slab,
                    // so neither the metre conversion nor the second offset is in doubt.
                    ValidatePushpinAnchor(probe);
                    var pushpinSource = probe.Anchor;
                    var viewerOffset = probe.ModelGlobalOffset == null
                        ? null
                        : new[] { probe.ModelGlobalOffset.X, probe.ModelGlobalOffset.Y, probe.ModelGlobalOffset.Z };
                    var pushpinX = pushpinSource.X;
                    var pushpinY = pushpinSource.Y;
                    var pushpinZ = pushpinSource.Z;

                    diagnosticProgress.Report(
                        "PUSHPIN COORDINATE FRAME" + Environment.NewLine +
                        "Frame written to Autodesk: viewer-local, Viewer units (ACC applies globalOffset)" + Environment.NewLine +
                        "Anchor method:       " + probe.AnchorMethod + Environment.NewLine +
                        "Viewer-local anchor: " + FormatPoint(probe.Anchor) + Environment.NewLine +
                        "Model-frame anchor:  " + FormatPoint(probe.GlobalAnchor) + " (diagnostic only, not sent)" + Environment.NewLine +
                        "Legacy AABB centre:  " + FormatPoint(probe.Center) + Environment.NewLine +
                        "Viewer globalOffset: " + FormatPoint(probe.ModelGlobalOffset) + " (passed through unmodified)" + Environment.NewLine +
                        "Position sent:       X=" + pushpinX.ToString("0.######", CultureInfo.InvariantCulture) +
                        "; Y=" + pushpinY.ToString("0.######", CultureInfo.InvariantCulture) +
                        "; Z=" + pushpinZ.ToString("0.######", CultureInfo.InvariantCulture));

                    var probeTitle = title;
                    var location = IssueLocationDetailsResolver.Resolve(row.Level, row.PrimaryLevel, row.SecondaryLevel, probeTitle);
                    diagnosticProgress.Report("LOCATION DETAILS RESOLUTION | value=" + (location.Value ?? "") +
                        " | source=" + location.Source + " | payloadField=locationDetails");
                    if (location.Source == IssueLocationDetailsSource.Missing)
                        PluginLog.Warn("Clash Issue level could not be resolved; locationDetails will be omitted", new { resultKey = Sanitize(row.ResultKey) });
                    var linked = IssueIntegrationClient.BuildPushpinDocument(
                        pushpinContext, currentUserId, pushpinX, pushpinY, pushpinZ,
                        resolvedExternalId, probe.DbId, probe.ViewerState, viewerOffset,
                        probe.SecondaryDbId > 0 ? (int?)probe.SecondaryDbId : null,
                        probe.SecondaryDbId > 0 &&
                        !string.Equals(probe.LoadedModelId, probe.SecondaryLoadedModelId, StringComparison.Ordinal),
                        cameraStateIsViewerLocal: true);
                    var request = new ApsCreateIssueRequest
                    {
                        Title = probeTitle,
                        // Autodesk limits Description to 1000 characters. Stable
                        // ids, Viewer dbId/externalId and exact coordinates already
                        // live in the local diagnostic log and linkedDocuments;
                        // duplicating them here made every real Clash POST invalid.
                        Description = BuildDescription(row, operationId, generatedAtUtc),
                        IssueSubtypeId = string.IsNullOrWhiteSpace(token.IssueSubtypeId) ? Plugin5Settings.DefaultIssueSubtypeId : token.IssueSubtypeId,
                        AssignedTo = assignee != null && assignee.IsResolved ? assignee.AssignedTo : null,
                        AssignedToType = assignee != null && assignee.IsResolved ? assignee.AssignedToType : null,
                        DueDate = DateTime.Now.ToString("yyyy-MM-dd"),
                        LocationDetails = location.Value,
                        LinkedDocuments = linked == null ? null : new List<ApsLinkedDocument> { linked }
                    };
                    progress?.Report(new IssueCreationProgress { Current=i, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Creating Issue " + (i + 1) + "/" + probeTotal + " from Viewer world coordinates..." });
                    postAttempted++;
                    var created = await _client.CreateApsIssueWithAssignmentFallbackAsync(
                        issueEnvironment, token, request, ct, diagnosticProgress, true, row.ManualAssignee?.IsResolved == true).ConfigureAwait(false);
                    row.ApsIssueId = created.Id;
                    row.ApsIssueDisplayId = created.DisplayId;
                    row.ApsIssueStatus = created.Status;
                    row.ApsIssueUrl = "https://acc.autodesk.com/issues/" + created.Id;
                    row.IsSelectedForIssueCreation = false;
                    row.CreationState = IssueCreationState.CreatedThisRun;
                    var savedIssue = ToSaved(row, probeTitle);
                    pendingStore.Enqueue(new SaveIssueBatchRequest
                    {
                        RevitModelUid = modelUid,
                        Source = "clash",
                        Issues = new List<SavedIssueDto> { savedIssue }
                    }, fingerprints[row]);
                    fingerprintStore.MarkCreated(fingerprints[row]);
                    ok++;
                    saved.Add(savedIssue);
                    completed = i + 1;
                    progress?.Report(new IssueCreationProgress { Current=completed, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Created: " + row.ResultKey });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    row.CreationState = IssueCreationState.CreationFailed;
                    completed = i + 1;
                    var error = BuildProgressError(row.ResultKey, ex);
                    PluginLog.Error("Failed to create APS Issue for " + row.ResultKey, ex);
                    progress?.Report(new IssueCreationProgress
                    {
                        Current = completed,
                        Total = probeTotal,
                        Succeeded = ok,
                        Failed = failed,
                        Message = "Failed: " + row.ResultKey,
                        Error = error
                    });
                }
            }
            diagnosticProgress.Report("CLASH_BATCH_SUMMARY | revitClashesConfirmed=" + revitClashesConfirmed +
                " | pushpinExactAccepted=" + pushpinExactAccepted + " | pushpinCorrected=" + pushpinCorrected +
                " | cameraPrimaryAccepted=" + cameraPrimaryAccepted +
                " | cameraNearFallbackAccepted=" + cameraNearFallbackAccepted +
                " | cameraFarFallbackAccepted=" + cameraFarFallbackAccepted + " | cameraRejected=" + cameraRejected +
                " | postAttempted=" + postAttempted + " | created=" + ok + " | failed=" + failed);
            if (saved.Count > 0)
            {
                var batch = new SaveIssueBatchRequest { RevitModelUid=modelUid, Source="clash", Issues=saved };
                try
                {
                    await _client.SaveIssuesAsync(batch, ct, diagnosticProgress).ConfigureAwait(false);
                    pendingStore.Acknowledge(batch);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("Created Clash Issues could not be synchronized to BuildAI; durable outbox and local fingerprints were retained", new { count = saved.Count, error = ex.Message });
                    throw new InvalidOperationException(
                        "PARTIAL: " + saved.Count + " Autodesk Issue(s) were created with pushpins, but BuildAI synchronization failed. " +
                        "The created Issues were retained and queued for BuildAI-only retry; they will not be posted to Autodesk again. " + ex.Message, ex);
                }
            }
            progress?.Report(new IssueCreationProgress { Current=probeTotal, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Viewer Coordinate Probe batch completed", IsCompleted=true });
        }

        private static ViewerCoordinateProbeRequest BuildProbeRequest(ClashItem row, ApsPushpinContext context)
        {
            var useElementA = !string.IsNullOrWhiteSpace(row.ElementAUniqueId);
            return new ViewerCoordinateProbeRequest
            {
                Context = context,
                ResultKey = row.ResultKey,
                RequestedExternalId = useElementA ? row.ElementAUniqueId : row.ElementBUniqueId,
                CompositeExternalId = "",
                SelectedLinkInstanceUid = useElementA ? row.LinkInstanceAUniqueId : row.LinkInstanceBUniqueId,
                SelectedModelUid = useElementA ? row.ModelUidA : row.ModelUidB,
                SourceName = useElementA ? row.SourceA : row.SourceB,
                ElementId = useElementA ? row.ElementAId : row.ElementBId,
                SecondaryRequestedExternalId = useElementA ? row.ElementBUniqueId : row.ElementAUniqueId,
                SecondaryLinkInstanceUid = useElementA ? row.LinkInstanceBUniqueId : row.LinkInstanceAUniqueId,
                SecondaryModelUid = useElementA ? row.ModelUidB : row.ModelUidA,
                SecondarySourceName = useElementA ? row.SourceB : row.SourceA,
                SecondaryElementId = useElementA ? row.ElementBId : row.ElementAId,
                HighlightSecondary = true,
                PreferIntersectionAnchor = true,
                // ClashSession stores the weighted centroid of the exact solid
                // intersection in Revit internal millimetres. The probe expects
                // model coordinates in feet and applies modelToViewerTransform.
                PreferredModelPoint = new ViewerProbePoint
                {
                    X = row.X / 304.8,
                    Y = row.Y / 304.8,
                    Z = row.Z / 304.8
                }
            };
        }


        private static string[] GetRoleCandidates(ClashItem row)
        {
            var categories = ((row?.CategoryA ?? "") + " " + (row?.CategoryB ?? "") + " " + (row?.ResponsibleDiscipline ?? "")).ToLowerInvariant();
            if (categories.Contains("pipe") || categories.Contains("plumb") || categories.Contains("sanitary"))
                return new[] { "Plumbing Engineer", "Water Supply Engineer", "MEP Engineer", "BIM Coordinator", "BIM Manager" };
            if (categories.Contains("duct") || categories.Contains("hvac") || categories.Contains("mechanical") || categories.Contains("air terminal"))
                return new[] { "HVAC Engineer", "Mechanical Engineer", "MEP Engineer", "BIM Coordinator", "BIM Manager" };
            if (categories.Contains("cable") || categories.Contains("conduit") || categories.Contains("electrical"))
                return new[] { "Electrical Engineer", "MEP Engineer", "BIM Coordinator", "BIM Manager" };
            if (categories.Contains("fire"))
                return new[] { "Fire Protection Engineer", "MEP Engineer", "BIM Coordinator", "BIM Manager" };
            if (categories.Contains("structural") || categories.Contains("column") || categories.Contains("beam") || categories.Contains("foundation"))
                return new[] { "Structural Engineer", "Structure Engineer", "BIM Coordinator", "BIM Manager" };
            if (categories.Contains("wall") || categories.Contains("door") || categories.Contains("window") || categories.Contains("ceiling") || categories.Contains("architect"))
                return new[] { "Architect", "Architecture", "BIM Coordinator", "BIM Manager" };
            return new[] { "BIM Coordinator", "BIM Manager" };
        }

        private static string BuildProgressError(string resultKey, Exception ex)
        {
            var message = ex?.Message ?? "Unknown error.";
            if (message.Length > 1800) message = message.Substring(0, 1800) + "...";
            return "Result key: " + (resultKey ?? "") + "\n" + message;
        }

        private static string BuildDescription(ClashItem x, string operationId, DateTime generatedAtUtc)
        {
            return IssueCaptionBuilder.Clash(x.CategoryA, x.CategoryB, x.Level, operationId).Description;
            /* Legacy detailed description retained below as source documentation for
               compatibility audits; 7.1.1 deliberately emits the human caption. */
#pragma warning disable CS0162
            var lines = new List<string> { "Automatically created by BuildAI", "Clash: " + x.Kind };
            AddDescriptionLine(lines, "Severity", x.AiSeverity);
            AddDescriptionLine(lines, "Assessment", x.AiAssessment);
            AddDescriptionLine(lines, "Reason", x.AiReason);
            AddDescriptionLine(lines, "Action", FirstNonEmpty(x.AiRecommendedAction, x.Recommendation));
            AddDescriptionLine(lines, "Primary", x.ElementA + " | " + x.CategoryA + " | ElementId " + x.ElementAId);
            AddDescriptionLine(lines, "Source", x.DisplaySourceA);
            AddDescriptionLine(lines, "Secondary", x.ElementB + " | " + x.CategoryB + " | ElementId " + x.ElementBId);
            AddDescriptionLine(lines, "Source", x.DisplaySourceB);
            AddDescriptionLine(lines, "Level", x.Level);
            lines.Add("Intersection volume: " + x.IntersectionVolumeMm3.ToString("0.###", CultureInfo.InvariantCulture) + " mm³");
            AddDescriptionLine(lines, "Run", operationId);
            return LimitDescription(string.Join("\n", lines), 1000);
#pragma warning restore CS0162
        }

        private static void AddDescriptionLine(ICollection<string> lines, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add(label + ": " + value.Trim());
        }

        private static string LimitDescription(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
            const string suffix = "\n[Description shortened by BuildAI]";
            var keep = Math.Max(0, maxLength - suffix.Length);
            // Avoid splitting a UTF-16 surrogate pair when a model or AI field
            // contains supplementary Unicode characters.
            if (keep > 0 && keep < value.Length && char.IsHighSurrogate(value[keep - 1])) keep--;
            return value.Substring(0, keep).TrimEnd() + suffix;
        }

        private static string FirstNonEmpty(string first, string second)
            => !string.IsNullOrWhiteSpace(first) ? first : (second ?? "");

        private static SavedIssueDto ToSaved(ClashItem x, string title)
        {
            return new SavedIssueDto { ResultKey=x.ResultKey, IssueId=x.ApsIssueId, DisplayId=x.ApsIssueDisplayId, IssueUrl=x.ApsIssueUrl, IssueTitle=title, IssueStatus=x.ApsIssueStatus,
                IssueType="Clash", Level=x.Level, CreatedAt=DateTime.UtcNow, DueDate=DateTime.Now.ToString("yyyy-MM-dd"), AssigneeRole=x.ResponsibleDiscipline,
                PrimaryElement=new RevitIssueElementDto{Category=x.CategoryA,ModelUid=x.ModelUidA,ElementId=x.ElementAId,ElementUniqueId=x.ElementAUniqueId,LinkInstanceUid=NullIfEmpty(x.LinkInstanceAUniqueId),Discipline=x.ResponsibleDiscipline},
                SecondaryElement=new RevitIssueElementDto{Category=x.CategoryB,ModelUid=x.ModelUidB,ElementId=x.ElementBId,ElementUniqueId=x.ElementBUniqueId,LinkInstanceUid=NullIfEmpty(x.LinkInstanceBUniqueId)},
                ResultData=new Dictionary<string,object>{{"location_x",x.X},{"location_y",x.Y},{"location_z",x.Z},{"intersection_volume",x.IntersectionVolumeMm3},{"description",x.Recommendation}},
                AiAnalysis=new AiIssueAnalysisDto{IsRealIssue=x.AiIsRealIssue,Assessment=x.AiAssessment,Severity=x.AiSeverity,Comment=x.AiComment,Reason=x.AiReason} };
        }
        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static ClashItem CreateHistoricalRow(ExistingRevitIssueDto issue)
        {
            return new ClashItem
            {
                ResultKey = issue.ResultKey ?? "", Level = issue.Level ?? "", PrimaryLevel = issue.Level ?? "",
                ElementA = issue.PrimaryElement?.ElementUniqueId ?? "Previously created Issue",
                ElementB = issue.SecondaryElement?.ElementUniqueId ?? "",
                CategoryA = issue.PrimaryElement?.Category ?? "", CategoryB = issue.SecondaryElement?.Category ?? "",
                ModelUidA = issue.PrimaryElement?.ModelUid ?? "", ModelUidB = issue.SecondaryElement?.ModelUid ?? "",
                ElementAId = issue.PrimaryElement?.ElementId ?? 0, ElementBId = issue.SecondaryElement?.ElementId ?? 0,
                ElementAUniqueId = issue.PrimaryElement?.ElementUniqueId ?? "", ElementBUniqueId = issue.SecondaryElement?.ElementUniqueId ?? "",
                LinkInstanceAUniqueId = issue.PrimaryElement?.LinkInstanceUid ?? "", LinkInstanceBUniqueId = issue.SecondaryElement?.LinkInstanceUid ?? "",
                Recommendation = issue.IssueTitle ?? ""
            };
        }

        private static string Sanitize(string value)
            => string.IsNullOrEmpty(value) ? "" : (value.Length <= 12 ? value : value.Substring(0, 8) + "...");
        public static void OpenIssue(string url){if(!string.IsNullOrWhiteSpace(url))Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}

        /// <summary>
        /// Mirrors the identifier precedence used by ResolveRegionalIssueEnvironmentAsync
        /// so the preflight probe and the real resolution cannot disagree.
        /// </summary>

        /// <summary>Invariant point formatting for diagnostics.</summary>

        /// <summary>
        /// Refuses to ship a PushPin whose parts disagree.
        /// <para>
        /// The position is written viewer-local and ACC applies the Viewer's
        /// globalOffset to place it, so a missing offset makes the position
        /// unplaceable, and a camera target that is not the anchor reproduces the
        /// 7.0.12 behaviour where the pin sits in one place and the Issue opens
        /// looking at another.
        /// </para>
        /// </summary>
        private static void ValidatePushpinAnchor(ViewerCoordinateProbeResult probe)
        {
            if (probe == null || probe.Anchor == null || !probe.Anchor.IsFinite)
                throw new InvalidOperationException("Viewer did not return a finite surface-aware pushpin anchor.");
            if (string.IsNullOrWhiteSpace(probe.AnchorMethod))
                throw new InvalidOperationException("Viewer did not identify the pushpin anchor method.");

            var target = probe.ViewerState?["viewport"]?["target"] as JArray;
            if (target == null || target.Count < 3 ||
                Math.Abs((double)target[0] - probe.Anchor.X) > 0.000001 ||
                Math.Abs((double)target[1] - probe.Anchor.Y) > 0.000001 ||
                Math.Abs((double)target[2] - probe.Anchor.Z) > 0.000001)
                throw new InvalidOperationException("Viewer camera target does not match the surface-aware pushpin anchor.");

            if (probe.ModelGlobalOffset == null || !probe.ModelGlobalOffset.IsFinite)
                throw new InvalidOperationException(
                    "Viewer did not report a finite globalOffset. ACC applies it to place a viewer-local pushpin, " +
                    "so the Issue was not created.");

            // The model-frame anchor is no longer sent, but the invariant is still
            // worth checking: if it fails, the probe's two frames disagree and the
            // viewer-local value cannot be trusted either.
            if (probe.GlobalAnchor != null)
            {
                var expectedX = probe.Anchor.X + probe.ModelGlobalOffset.X;
                var expectedY = probe.Anchor.Y + probe.ModelGlobalOffset.Y;
                var expectedZ = probe.Anchor.Z + probe.ModelGlobalOffset.Z;
                if (Math.Abs(probe.GlobalAnchor.X - expectedX) > 0.000001 ||
                    Math.Abs(probe.GlobalAnchor.Y - expectedY) > 0.000001 ||
                    Math.Abs(probe.GlobalAnchor.Z - expectedZ) > 0.000001)
                    throw new InvalidOperationException("Viewer pushpin anchor failed the local + globalOffset invariant.");
            }
        }

        private static string FormatPoint(ViewerProbePoint point)
        {
            if (point == null) return "<unavailable>";
            return "X=" + point.X.ToString("0.######", CultureInfo.InvariantCulture) +
                   "; Y=" + point.Y.ToString("0.######", CultureInfo.InvariantCulture) +
                   "; Z=" + point.Z.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string FirstNonEmptyId(string primary, string fallback)
        {
            return !string.IsNullOrWhiteSpace(primary) ? primary : (fallback ?? "");
        }


        /// <summary>
        /// Puts the user-configured assignee role ahead of the automatically
        /// derived discipline candidates, preserving the rest of the list as a
        /// fallback chain.
        /// </summary>
        private static string[] PrependPreferredRole(string preferredRole, string[] derivedCandidates)
        {
            var derived = derivedCandidates ?? new string[0];
            var configured = (preferredRole ?? "").Trim();
            if (configured.Length == 0)
                return derived.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            return new[] { configured }
                .Concat(derived)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

    }
}
