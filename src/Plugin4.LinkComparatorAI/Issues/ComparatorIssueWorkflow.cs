using BuildAI.Core.ViewerProbe;
using System.Globalization;
using Plugin4.LinkComparatorAI.Revit;
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
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Issues
{
    public sealed class ComparatorIssueWorkflow
    {
        private const int ArStTransientPropertyRetries = 2;
        private static int _creationInProgress;
        private readonly IssueIntegrationClient _client;
        public Task<IReadOnlyList<ApsAssigneeResolution>> GetProjectAssigneesAsync(CancellationToken ct = default) => _client.GetProjectAssigneesAsync(ct);
        public bool IsBuildAiAvailable { get; private set; } = true;
        public ComparatorIssueWorkflow(IssueIntegrationClient client) { _client = client; }

        public async Task LoadExistingAsync(ComparisonReport report, string modelUid, CancellationToken ct = default)
        {
            try
            {
                var existing = await _client.GetExistingIssuesAsync(modelUid, "ar-st", ct).ConfigureAwait(false);
                IsBuildAiAvailable = true;
                var map = existing.Where(x => !string.IsNullOrWhiteSpace(x.ResultKey))
                    .GroupBy(x => NormalizeResultKey(x.ResultKey))
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First(), StringComparer.Ordinal);
                foreach (var row in report.Issues)
                {
                    ExistingRevitIssueDto issue;
                    if (map.TryGetValue(NormalizeResultKey(row.ResultKey), out issue) ||
                        (issue = existing.Where(x => string.IsNullOrWhiteSpace(x.ResultKey) && MatchesLegacyIdentity(row, x))
                            .OrderByDescending(x => x.CreatedAt).FirstOrDefault()) != null)
                    {
                        row.ApsIssueId = issue.IssueId; row.ApsIssueUrl = issue.IssueUrl; row.ApsIssueStatus = issue.IssueStatus;
                        row.ApsIssueDisplayId = issue.DisplayId;
                        row.IsSelectedForIssueCreation = false;
                    }
                }
                var visibleIds = new HashSet<string>(report.Issues.Select(x => x.ApsIssueId)
                    .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
                foreach (var issue in existing.Where(x => !string.IsNullOrWhiteSpace(x.IssueId)))
                {
                    if (!visibleIds.Add(issue.IssueId)) continue;
                    report.Issues.Add(new ComparisonIssue {
                        ResultKey = string.IsNullOrWhiteSpace(issue.ResultKey) ? "legacy:" + issue.IssueId : issue.ResultKey,
                        Title = string.IsNullOrWhiteSpace(issue.IssueTitle) ? "Previously created AR-ST Issue" : issue.IssueTitle,
                        Description = "Previously created Issue retained from BuildAI.",
                        Level = issue.Level ?? "",
                        ApsIssueId = issue.IssueId,
                        ApsIssueUrl = issue.IssueUrl,
                        ApsIssueStatus = issue.IssueStatus,
                        ApsIssueDisplayId = issue.DisplayId,
                        IsSelectedForIssueCreation = false
                    });
                }
            }
            catch (Exception ex) { IsBuildAiAvailable = false; PluginLog.Error("Failed to load existing AR-ST Issues", ex); }
        }

        private static string NormalizeResultKey(string value) => (value ?? "").Trim().ToLowerInvariant();

        private static bool MatchesLegacyIdentity(ComparisonIssue row, ExistingRevitIssueDto issue)
        {
            if (row == null || issue == null) return false;
            // AR-ST persists Architecture as primary and Structure as secondary.
            // An absent side is part of the identity: matching only the present
            // element could conflate two different findings on that element.
            bool Match(string uid, string linkUid, string modelUid, RevitIssueElementDto saved)
            {
                if (string.IsNullOrWhiteSpace(uid))
                    return saved == null || string.IsNullOrWhiteSpace(saved.ElementUniqueId);
                return saved != null &&
                    string.Equals(uid.Trim(), saved.ElementUniqueId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(linkUid?.Trim(), saved.LinkInstanceUid?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(modelUid?.Trim(), saved.ModelUid?.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            return (row.HasArchitecturalElement || row.HasStructuralElement) &&
                   Match(row.ArchitecturalElementUniqueId, row.ArchitecturalLinkUniqueId,
                       row.ArchitecturalModelUid, issue.PrimaryElement) &&
                   Match(row.StructuralElementUniqueId, row.StructuralLinkUniqueId,
                       row.StructuralModelUid, issue.SecondaryElement);
        }

        public async Task CreateSelectedAsync(ComparisonReport report, IReadOnlyList<ComparisonIssue> selectedRows, string modelUid, ApsCloudModelIdentity cloudModel, IProgress<IssueCreationProgress> progress, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _creationInProgress, 1) != 0)
                throw new InvalidOperationException("Another AR-ST Issue creation operation is already running.");
            try
            {
                var operationId = Guid.NewGuid().ToString("N");
                using (PluginLog.BeginOperation("Create AR-ST Issues", operationId))
                    await CreateSelectedCoreAsync(report, selectedRows, modelUid, cloudModel, progress, ct, operationId).ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref _creationInProgress, 0); }
        }

        private async Task CreateSelectedCoreAsync(ComparisonReport report, IReadOnlyList<ComparisonIssue> selectedRows, string modelUid, ApsCloudModelIdentity cloudModel, IProgress<IssueCreationProgress> progress, CancellationToken ct, string operationId)
        {
            var generatedAtUtc = DateTime.UtcNow;
            // Never POST two APS Issues for the same logical comparison finding,
            // even if a UI refresh supplied duplicate row instances.
            var rows = (selectedRows ?? Array.Empty<ComparisonIssue>())
                .Where(x => x != null)
                .GroupBy(x => NormalizeResultKey(x.ResultKey), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException("No rows are selected for Issue creation. Select at least one result and try again.");
            var selectedTotal = rows.Count;
            var alreadyCreated = rows.Count(x => x.HasApsIssue);
            var probeTotal = rows.Count;
            int ok = 0, failed = 0, postAttempted = 0, frameValidated = 0;
            int completed = 0;
            var batchStartedAt = DateTime.UtcNow;
            var failureReasons = new List<string>();
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message=BuildInfo.Banner, Error=BuildInfo.LoadedAssemblyIdentity(typeof(ComparatorIssueWorkflow).Assembly, null) });
            IProgress<string> diagnosticProgress = new Progress<string>(text => progress?.Report(new IssueCreationProgress
            {
                Current = completed, Total = probeTotal, Succeeded = ok, Failed = failed,
                Message = "Diagnostics", Error = text
            }));
            var pendingStore = new PendingIssueBatchStore();
            var fingerprintStore = new IssueFingerprintStore();
            // Validate the frozen BuildAI model/source pair before performing
            // any irreversible Autodesk Issue POST and drain its durable outbox.
            var serverIssues = await _client.GetExistingIssuesAsync(modelUid, "ar-st", ct).ConfigureAwait(false);
            var serverByKey = serverIssues.Where(x => !string.IsNullOrWhiteSpace(x.ResultKey))
                .GroupBy(x => NormalizeResultKey(x.ResultKey))
                .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.CreatedAt).First(), StringComparer.Ordinal);
            foreach (var row in rows.Where(x => !x.HasApsIssue))
            {
                ExistingRevitIssueDto mapped;
                if (!serverByKey.TryGetValue(NormalizeResultKey(row.ResultKey), out mapped) &&
                    (mapped = serverIssues.Where(x => string.IsNullOrWhiteSpace(x.ResultKey) &&
                        MatchesLegacyIdentity(row, x)).OrderByDescending(x => x.CreatedAt).FirstOrDefault()) == null)
                    continue;
                row.ApsIssueId = mapped.IssueId;
                row.ApsIssueUrl = mapped.IssueUrl;
                row.ApsIssueDisplayId = mapped.DisplayId;
                row.ApsIssueStatus = mapped.IssueStatus;
                row.IsSelectedForIssueCreation = false;
                alreadyCreated++;
            }
            rows = rows.Where(x => !x.HasApsIssue).ToList();
            probeTotal = rows.Count;
            if (rows.Count == 0)
            {
                diagnosticProgress.Report("DUPLICATE PROTECTION | selected=" + selectedTotal + " alreadyCreated=" + alreadyCreated);
                diagnosticProgress.Report("ARST_ISSUE_BATCH_SUMMARY | " + JsonConvert.SerializeObject(new {
                    selected = selectedTotal, cameraCaptured = 0, frameValidated = 0, postAttempted = 0,
                    created = 0, alreadyCreated, failed = 0, cancelled = 0
                }));
                progress?.Report(new IssueCreationProgress { Current=selectedTotal, Total=selectedTotal,
                    Succeeded=0, Failed=0, Message="All selected AR-ST Issues already exist in BuildAI.", IsCompleted=true });
                return;
            }
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
            foreach (var pending in pendingStore.GetPending(modelUid, "ar-st"))
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
                    ? await publication.ResolveExistingAsync(cloudModel, token, BuildAiViewNames.ArSt, ct, diagnosticProgress).ConfigureAwait(false)
                    : await publication.PublishAndResolveAsync(cloudModel, token, BuildAiViewNames.ArSt, ct, diagnosticProgress).ConfigureAwait(false);
            }
            if (!pushpinContext.IsUsable)
                throw new InvalidOperationException("The published Autodesk model cannot be attached to Issues. Required view: BuildAI AR-ST\n" + (pushpinContext.ValidationError ?? "Document URN, version, or 3D viewable GUID is missing."));
            // AR-ST placement and object identity must use the same viewable.
            // Coordination has a different dbId namespace and is valid only for
            // the MEP/Clash workflow that places its pin on Coordination.
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Validating Autodesk region, URN and Issues environment..." });
            var issueEnvironment = await _client.ResolveRegionalIssueEnvironmentAsync(project, cloudModel?.ProjectId, pushpinContext, token, ct, diagnosticProgress).ConfigureAwait(false);
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Resolving current APS user..." });
            string currentUserId = null;
            try { currentUserId = await _client.ResolveCurrentUserIdAsync(issueEnvironment, token, ct, diagnosticProgress).ConfigureAwait(false); }
            catch (Exception ex) { diagnosticProgress.Report("APS USER RESOLUTION WARNING\n" + ex.Message + "\nIssues will be created without assignment if BIM Manager lookup also fails."); }
            var assigneeCache = new Dictionary<string, ApsAssigneeResolution>(StringComparer.OrdinalIgnoreCase);
            var fingerprints = new Dictionary<ComparisonIssue, string>();
            foreach (var row in rows)
                fingerprints[row] = fingerprintStore.Build(
                    "ar-st", issueEnvironment.ProjectId, issueEnvironment.ContainerId,
                    "", "", "", NormalizeResultKey(row.ResultKey), "", "", "");
            var skipped = rows.Where(row => fingerprintStore.Contains(fingerprints[row])).ToList();
            foreach (var row in skipped) row.IsSelectedForIssueCreation = false;
            alreadyCreated += skipped.Count;
            rows = rows.Except(skipped).ToList();
            probeTotal = rows.Count;
            diagnosticProgress.Report("DUPLICATE PROTECTION\nWill create: " + rows.Count + "\nSkipped as already created: " + skipped.Count + "\nFailed results remain retryable.");
            if (rows.Count == 0)
            {
                diagnosticProgress.Report("ARST_ISSUE_BATCH_SUMMARY | " + JsonConvert.SerializeObject(new {
                    selected = selectedTotal, cameraCaptured = 0, frameValidated = 0, postAttempted = 0,
                    created = 0, alreadyCreated, failed = 0, cancelled = 0
                }));
                progress?.Report(new IssueCreationProgress { Current=0, Total=0, Succeeded=0, Failed=0, Message="No new Issues to create; all selected results were already confirmed locally.", IsCompleted=true });
                return;
            }
            var saved = new List<SavedIssueDto>();
            var rootCauseTexts = rows.Select(IssueTextLocalizer.RootCause).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rootCauseIds = await _client.ResolveRootCauseIdsAsync(issueEnvironment, token, rootCauseTexts, ct, diagnosticProgress).ConfigureAwait(false);
            progress?.Report(new IssueCreationProgress { Current=0, Total=probeTotal, Succeeded=0, Failed=0, Message="Loading the APS viewable once and resolving " + probeTotal + " Viewer objects..." });
            var probeRequests = rows.Select(row => BuildProbeRequest(row, pushpinContext)).ToList();
            var probeOutcomes = await ViewerCoordinateProbe.ResolveBatchAsync(probeRequests, token, ct, diagnosticProgress).ConfigureAwait(false);
            using (var authoritativeResolver = new ApsObjectResolver())
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                progress?.Report(new IssueCreationProgress { Current=i, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Preparing Viewer Coordinate Probe " + (i + 1) + "/" + probeTotal + " for " + row.Title });
                try
                {
                    IssueCaption compactCaption = null;
                    if (!row.HasStructuralElement && row.HasArchitecturalElement)
                        compactCaption = IssueCaptionBuilder.MissingMatch(true, row.ArchitecturalCategory, row.Level, row.DeltaMm, operationId);
                    else if (!row.HasArchitecturalElement && row.HasStructuralElement)
                        compactCaption = IssueCaptionBuilder.MissingMatch(false, row.StructuralCategory, row.Level, row.DeltaMm, operationId);
                    var title = compactCaption == null ? IssueTextLocalizer.IssueTitle(row) : compactCaption.Title;
                    var rootCauseText = IssueTextLocalizer.RootCause(row);
                    string rootCauseId;
                    rootCauseIds.TryGetValue(rootCauseText, out rootCauseId);
                    var useArchitecture = !string.IsNullOrWhiteSpace(row.ArchitecturalElementUniqueId);
                    var externalId = useArchitecture ? row.ArchitecturalElementUniqueId : row.StructuralElementUniqueId;
                    var elementId = useArchitecture ? row.ArchitecturalElementId : row.StructuralElementId;
                    var elementModelUid = useArchitecture ? row.ArchitecturalModelUid : row.StructuralModelUid;
                    var linkInstanceUid = useArchitecture ? row.ArchitecturalLinkUniqueId : row.StructuralLinkUniqueId;
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
                    var roleCandidates = PrependPreferredRole((PluginContext.Settings ?? ComparatorSettings.Load()).PreferredAssigneeRole, GetRoleCandidates(row));
                    var roleKey = string.Join("|", roleCandidates);
                    var assignee = row.ManualAssignee;
                    if (assignee != null && assignee.IsResolved && !string.Equals((assignee.ProjectId ?? ""), (issueEnvironment.ProjectId ?? "" ).Replace("b.", ""), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Selected assignee belongs to another Autodesk project. Select the assignee again.");
                    if ((assignee == null || !assignee.IsResolved) && !assigneeCache.TryGetValue(roleKey, out assignee))
                    {
                        assignee = await _client.ResolveAssigneeByRoleAsync(issueEnvironment, token, roleCandidates, currentUserId, ct, diagnosticProgress).ConfigureAwait(false);
                        assigneeCache[roleKey] = assignee;
                    }
                    var sourceName = useArchitecture ? row.ArchitecturalSourceName : row.StructuralSourceName;
                    diagnosticProgress.Report("VIEWER-FIRST OBJECT RESOLUTION\nResult key: " + row.ResultKey +
                        "\nRequested externalId: " + (externalId ?? "") +
                        "\nSelected link instance UID: " + (linkInstanceUid ?? "") +
                        "\nSelected model UID: " + (elementModelUid ?? "") +
                        "\nFull Model Derivative properties download is disabled." +
                        "\nViewer fragment geometry is the authoritative coordinate source for the surface-aware anchor.");

                    var probeOutcome = probeOutcomes[i];
                    if (!probeOutcome.IsSuccess)
                    {
                        if ((probeOutcome.Error ?? "").IndexOf("AR-ST CAMERA CAPTURE REJECTED", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidOperationException(probeOutcome.Error);
                        throw new InvalidOperationException("Viewer could not resolve a unique geometric object for Element UniqueId " + (externalId ?? "<empty>") + ": " + probeOutcome.Error);
                    }
                    var probe = probeOutcome.Result;

                    var resolvedExternalId = probe.ResolvedExternalId;
                    var compositeExternalId = new CompositeExternalId(resolvedExternalId);
                    var expectedCompositeExternalId = ArStIssueObjectIdentity.NormalizeExternalId(
                        (linkInstanceUid ?? "").Trim().Trim('/') + "/" + (externalId ?? "").Trim().Trim('/'));
                    var runtimeExternalIdMatch = string.Equals(
                        ArStIssueObjectIdentity.NormalizeExternalId(resolvedExternalId),
                        expectedCompositeExternalId, StringComparison.OrdinalIgnoreCase);
                    var runtimeViewDbId = new RuntimeViewDbId(probe.DbId);
                    var authoritativeMatch = await ResolveArStObjectWithTransientRetryAsync(
                        authoritativeResolver, pushpinContext, token, compositeExternalId.Value, elementId,
                        elementModelUid, linkInstanceUid, sourceName, row.ResultKey, ct, diagnosticProgress).ConfigureAwait(false);
                    var authoritativeIssueDbId = new AuthoritativeIssueDbId(authoritativeMatch?.LookupObjectId ?? 0);
                    var reverseExternalId = authoritativeIssueDbId.Value > 0
                        ? await authoritativeResolver.ResolveExternalIdByObjectIdAsync(pushpinContext, token, authoritativeIssueDbId.Value, ct, diagnosticProgress).ConfigureAwait(false)
                        : null;
                    ArStIssueObjectIdentity issueIdentity;
                    try
                    {
                        issueIdentity = ArStIssueObjectIdentity.Create(compositeExternalId, runtimeViewDbId,
                            authoritativeIssueDbId, runtimeExternalIdMatch, reverseExternalId, "Model Derivative PropertyDatabase / AR-ST viewable",
                            pushpinContext.ModelPropertiesGuid, pushpinContext.ViewableId, pushpinContext.ViewableId);
                    }
                    catch (Exception ex)
                    {
                        diagnosticProgress.Report("ARST_ISSUE_OBJECT_ID_AUDIT | " + JsonConvert.SerializeObject(new {
                            resultKey = row.ResultKey, compositeExternalId = compositeExternalId.Value,
                            runtimeViewDbId = runtimeViewDbId.Value, authoritativeIssueDbId = authoritativeIssueDbId.Value,
                            runtimeExternalIdMatch,
                            resolverSource = "Model Derivative PropertyDatabase / AR-ST viewable",
                            propertyDatabaseUrn = pushpinContext.DerivativeUrn,
                            viewableId = pushpinContext.ViewableId, reverseResolvedExternalId = reverseExternalId,
                            reverseMatch = false, targetPlacementViewableId = pushpinContext.ViewableId,
                            validationResult = "rejected", rejectionReason = ex.Message
                        }));
                        throw;
                    }
                    diagnosticProgress.Report("ARST_ISSUE_OBJECT_ID_AUDIT | " + JsonConvert.SerializeObject(new {
                        resultKey = row.ResultKey, compositeExternalId = compositeExternalId.Value,
                        runtimeViewDbId = runtimeViewDbId.Value, authoritativeIssueDbId = issueIdentity.AuthoritativeIssueDbId.Value,
                        runtimeExternalIdMatch = issueIdentity.RuntimeExternalIdMatch,
                        resolverSource = issueIdentity.ResolverSource, propertyDatabaseUrn = pushpinContext.DerivativeUrn,
                        viewableId = issueIdentity.PropertyDatabaseViewableId, reverseResolvedExternalId = issueIdentity.ReverseResolvedExternalId,
                        reverseMatch = issueIdentity.ReverseMatch, targetPlacementViewableId = issueIdentity.TargetPlacementViewableId,
                        validationResult = "accepted", rejectionReason = ""
                    }));
                    ArStProbeDerivativeGuard.Validate(probe, pushpinContext, row.ResultKey,
                        issueIdentity.RuntimeExternalIdMatch && issueIdentity.ReverseMatch,
                        diagnosticProgress.Report);

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
                    if (probe.SecondaryDbId <= 0)
                        diagnosticProgress.Report("ARST_SINGLE_ELEMENT_ANCHOR_WARNING | " +
                            JsonConvert.SerializeObject(new {
                                resultKey = row.ResultKey, primaryDbId = probe.DbId, secondaryDbId = probe.SecondaryDbId,
                                anchorMethod = probe.AnchorMethod,
                                targetToPrimaryBoundsDistance = DistanceToBounds(probe.Anchor, probe.BoundsMin, probe.BoundsMax),
                                targetInsideOrNearBounds = DistanceToBounds(probe.Anchor, probe.BoundsMin, probe.BoundsMax) <= 0.26
                            }));
                    ValidatePushpinAnchor(row, probe);
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
                    var location = IssueLocationDetailsResolver.Resolve(row.Level, null, null, null);
                    diagnosticProgress.Report("LOCATION DETAILS RESOLUTION | value=" + (location.Value ?? "") +
                        " | source=" + location.Source + " | payloadField=locationDetails");
                    var linked = ArStIssuePayloadAdapter.Build(
                        pushpinContext, currentUserId, new[] { pushpinX, pushpinY, pushpinZ },
                        compositeExternalId, issueIdentity, probe.ViewerState, viewerOffset,
                        row.ResultKey, message => PluginLog.Info(message));
                    diagnosticProgress.Report("ARST_ISSUE_PAYLOAD_GUARD | " + JsonConvert.SerializeObject(new {
                        cameraFrameValid = true, objectIdValid = issueIdentity.AuthoritativeIssueDbId.Value > 0 && issueIdentity.RuntimeExternalIdMatch && issueIdentity.ReverseMatch,
                        pushpinPositionFrame = "viewer-local", viewportFrame = "model-world",
                        authoritativeIssueDbId = issueIdentity.AuthoritativeIssueDbId.Value,
                        runtimeViewDbId = issueIdentity.RuntimeViewDbId.Value, issueObjectId = issueIdentity.AuthoritativeIssueDbId.Value,
                        externalIdMatch = issueIdentity.ReverseMatch, runtimeExternalIdMatch = issueIdentity.RuntimeExternalIdMatch,
                        validationResult = "accepted"
                    }));
                    frameValidated++;
                    var request = new ApsCreateIssueRequest
                    {
                        Title = probeTitle,
                        Description = BuildDescription(row, operationId, generatedAtUtc),
                        IssueSubtypeId = string.IsNullOrWhiteSpace(token.IssueSubtypeId) ? ComparatorSettings.DefaultIssueSubtypeId : token.IssueSubtypeId,
                        AssignedTo = assignee != null && assignee.IsResolved ? assignee.AssignedTo : null,
                        AssignedToType = assignee != null && assignee.IsResolved ? assignee.AssignedToType : null,
                        DueDate = DateTime.Now.ToString("yyyy-MM-dd"),
                        RootCauseId = string.IsNullOrWhiteSpace(rootCauseId) ? null : rootCauseId,
                        LocationDetails = location.Value,
                        LinkedDocuments = linked == null ? null : new List<ApsLinkedDocument> { linked }
                    };
                    progress?.Report(new IssueCreationProgress { Current=i, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Creating Issue " + (i + 1) + "/" + probeTotal + " from Viewer world coordinates..." });
                    postAttempted++;
                    var created = await _client.CreateApsIssueWithAssignmentFallbackAsync(
                        issueEnvironment, token, request, ct, diagnosticProgress, true, row.ManualAssignee?.IsResolved == true).ConfigureAwait(false);
                    if (created == null || created.HttpStatusCode != 201)
                        throw new InvalidOperationException("AR-ST Issue creation did not return HTTP 201; Created was not recorded.");
                    diagnosticProgress.Report("ARST_ISSUE_CREATED | " + JsonConvert.SerializeObject(new {
                        resultKey = row.ResultKey, issueId = created.Id, displayId = created.DisplayId,
                        httpStatus = created.HttpStatusCode, version = pushpinContext.FileVersion,
                        viewableId = pushpinContext.ViewableId, pushpinPosition = new[] { pushpinX, pushpinY, pushpinZ },
                        absoluteCameraTarget = new[] { pushpinX + viewerOffset[0], pushpinY + viewerOffset[1], pushpinZ + viewerOffset[2] },
                        authoritativeIssueDbId = issueIdentity.AuthoritativeIssueDbId.Value,
                        runtimeViewDbId = issueIdentity.RuntimeViewDbId.Value,
                        issueObjectId = issueIdentity.AuthoritativeIssueDbId.Value,
                        elapsedMs = (long)(DateTime.UtcNow - batchStartedAt).TotalMilliseconds
                    }));
                    row.ApsIssueId = created.Id;
                    row.ApsIssueStatus = created.Status;
                    row.ApsIssueDisplayId = created.DisplayId;
                    row.ApsIssueUrl = "https://acc.autodesk.com/issues/" + created.Id;
                    row.IsSelectedForIssueCreation = false;
                    var savedIssue = ToSaved(row, probeTitle);
                    pendingStore.Enqueue(new SaveIssueBatchRequest
                    {
                        RevitModelUid = modelUid,
                        Source = "ar-st",
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
                    completed = i + 1;
                    var error = BuildProgressError(row.ResultKey, ex);
                    failureReasons.Add(row.ResultKey + ": " + ex.Message);
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
            if (saved.Count > 0)
            {
                var batch = new SaveIssueBatchRequest { RevitModelUid=modelUid, Source="ar-st", Issues=saved };
                try
                {
                    await _client.SaveIssuesAsync(batch, ct, diagnosticProgress).ConfigureAwait(false);
                    pendingStore.Acknowledge(batch);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("Created AR-ST Issues could not be synchronized to BuildAI; durable outbox and local fingerprints were retained", new { count = saved.Count, error = ex.Message });
                    throw new InvalidOperationException(
                        "PARTIAL: " + saved.Count + " Autodesk Issue(s) were created with pushpins, but BuildAI synchronization failed. " +
                        "The created Issues were retained and queued for BuildAI-only retry; they will not be posted to Autodesk again. " + ex.Message, ex);
                }
            }
            diagnosticProgress.Report("ARST_ISSUE_BATCH_SUMMARY | " + JsonConvert.SerializeObject(new {
                selected = selectedTotal,
                cameraCaptured = probeOutcomes.Count(x => x.IsSuccess),
                frameValidated,
                postAttempted,
                created = ok,
                alreadyCreated,
                failed,
                cancelled = 0,
                failureReasons,
                elapsedMs = (long)(DateTime.UtcNow - batchStartedAt).TotalMilliseconds
            }));
            progress?.Report(new IssueCreationProgress { Current=probeTotal, Total=probeTotal, Succeeded=ok, Failed=failed, Message="Viewer Coordinate Probe batch completed", IsCompleted=true });
        }

        private static ViewerCoordinateProbeRequest BuildProbeRequest(ComparisonIssue row, ApsPushpinContext context)
        {
            var useArchitecture = !string.IsNullOrWhiteSpace(row.ArchitecturalElementUniqueId);
            return new ViewerCoordinateProbeRequest
            {
                Context = context,
                RequestedExternalId = useArchitecture ? row.ArchitecturalElementUniqueId : row.StructuralElementUniqueId,
                CompositeExternalId = "",
                SelectedLinkInstanceUid = useArchitecture ? row.ArchitecturalLinkUniqueId : row.StructuralLinkUniqueId,
                SelectedModelUid = useArchitecture ? row.ArchitecturalModelUid : row.StructuralModelUid,
                SourceName = useArchitecture ? row.ArchitecturalSourceName : row.StructuralSourceName,
                ElementId = useArchitecture ? row.ArchitecturalElementId : row.StructuralElementId,
                SecondaryRequestedExternalId = useArchitecture ? row.StructuralElementUniqueId : "",
                SecondaryLinkInstanceUid = useArchitecture ? row.StructuralLinkUniqueId : "",
                SecondaryModelUid = useArchitecture ? row.StructuralModelUid : "",
                SecondarySourceName = useArchitecture ? row.StructuralSourceName : "",
                SecondaryElementId = useArchitecture ? row.StructuralElementId : 0,
                PreferSurfacePairAnchor = true,
                // row.X/Y/Z are Revit host internal coordinates. They cannot be
                // passed as model-frame or Viewer-local points without a proven
                // link and model transform for this result.
                PreferredModelPoint = null
            };
        }

        private static async Task<ApsObjectMatch> ResolveArStObjectWithTransientRetryAsync(
            ApsObjectResolver resolver, ApsPushpinContext context, ApsTokenResponse token, string externalId,
            int elementId, string modelUid, string linkInstanceUid, string sourceName, string resultKey,
            CancellationToken ct, IProgress<string> diagnostics)
        {
            for (var retry = 0; retry <= ArStTransientPropertyRetries; retry++)
            {
                var match = await resolver.ResolveObjectAsync(context, token, externalId, elementId, modelUid,
                    linkInstanceUid, sourceName, null, ct, diagnostics).ConfigureAwait(false);
                if (match != null && match.IsValid) return match;
                if (retry == ArStTransientPropertyRetries) return null;
                var delay = TimeSpan.FromSeconds(30 * (retry + 1));
                diagnostics?.Report("ARST_PROPERTY_INDEX_RETRY | " + JsonConvert.SerializeObject(new {
                    resultKey, externalId, retry = retry + 1, maxRetries = ArStTransientPropertyRetries,
                    delaySeconds = delay.TotalSeconds,
                    reason = "Property Database remained pending; retrying authoritative lookup."
                }));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            return null;
        }

        private static void ValidatePushpinAnchor(ComparisonIssue row, ViewerCoordinateProbeResult probe)
        {
            if (probe == null || probe.Anchor == null || !probe.Anchor.IsFinite)
                throw new InvalidOperationException("Viewer did not return a finite surface-aware pushpin anchor.");
            if (string.IsNullOrWhiteSpace(probe.AnchorMethod))
                throw new InvalidOperationException("Viewer did not identify the pushpin anchor method.");
            // Fragment bounds and the pin are both Viewer-local. The configured
            // 0.25 ft outward marker offset plus 0.01 ft numerical allowance is
            // accepted; metres of separation are never accepted.
            var boundsDistance = DistanceToBounds(probe.Anchor, probe.BoundsMin, probe.BoundsMax);
            if (probe.BoundsMin == null || probe.BoundsMax == null ||
                !ArStPushpinGeometry.IsNearFragmentBounds(probe.Anchor.X, probe.Anchor.Y, probe.Anchor.Z,
                    probe.BoundsMin.X, probe.BoundsMin.Y, probe.BoundsMin.Z,
                    probe.BoundsMax.X, probe.BoundsMax.Y, probe.BoundsMax.Z))
                throw new InvalidOperationException("PushpinGeometryUnresolved: AR-ST anchor is too far from selected fragment bounds (" +
                    boundsDistance.ToString("0.####", CultureInfo.InvariantCulture) + " ft).");

            var target = probe.ViewerState?["viewport"]?["target"] as JArray;
            if (target == null || target.Count < 3)
                throw new InvalidOperationException("Viewer camera target is missing from the captured AR-ST state.");

            // globalOffset is now passed through to ACC rather than folded into the
            // position, so the Viewer has to have reported one: without it ACC has
            // nothing to apply and a viewer-local position is unplaceable.
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

        private static double DistanceToBounds(ViewerProbePoint point, ViewerProbePoint min, ViewerProbePoint max)
        {
            if (point == null || min == null || max == null) return double.PositiveInfinity;
            double Axis(double value, double lo, double hi) => value < lo ? lo - value : value > hi ? value - hi : 0;
            return Math.Sqrt(Math.Pow(Axis(point.X, min.X, max.X), 2) + Math.Pow(Axis(point.Y, min.Y, max.Y), 2) + Math.Pow(Axis(point.Z, min.Z, max.Z), 2));
        }


        private static string[] GetRoleCandidates(ComparisonIssue row)
        {
            var text = ((row?.Title ?? "") + " " + (row?.Description ?? "")).ToLowerInvariant();
            if (text.Contains("no st match")) return new[] { "Structural Engineer", "Structure Engineer", "Structural Designer", "Structural", "BIM Coordinator", "BIM Manager" };
            if (text.Contains("no ar match")) return new[] { "Architect", "Architecture", "Architectural Designer", "Lead Architect", "BIM Coordinator", "BIM Manager" };
            if (row != null && (row.CheckType == ComparatorCheckType.Column || row.CheckType == ComparatorCheckType.Beam))
                return new[] { "Structural Engineer", "Structure Engineer", "Structural Designer", "BIM Coordinator", "BIM Manager" };
            if (row != null && row.CheckType == ComparatorCheckType.Floor && row.HasStructuralElement)
                return new[] { "Structural Engineer", "Architect", "BIM Coordinator", "BIM Manager" };
            if (row != null && row.CheckType == ComparatorCheckType.Wall)
                // Same architect list as the "no ar match" branch above. Role matching is
                // now whole-word exact, so a project staffed with "Architectural Designer"
                // or "Lead Architect" but no plain "Architect" would otherwise skip
                // straight to the BIM fallback.
                return new[] { "Architect", "Architecture", "Architectural Designer", "Lead Architect", "BIM Coordinator", "BIM Manager" };
            return new[] { "BIM Coordinator", "BIM Manager" };
        }

        private static string BuildProgressError(string resultKey, Exception ex)
        {
            var message = ex?.Message ?? "Unknown error.";
            if (message.Length > 1800) message = message.Substring(0, 1800) + "...";
            return "Result key: " + (resultKey ?? "") + "\n" + message;
        }

        private static string BuildDescription(ComparisonIssue x, string operationId, DateTime generatedAtUtc)
        {
            if (!x.HasStructuralElement && x.HasArchitecturalElement)
                return IssueCaptionBuilder.MissingMatch(true, x.ArchitecturalCategory, x.Level, x.DeltaMm, operationId).Description;
            if (!x.HasArchitecturalElement && x.HasStructuralElement)
                return IssueCaptionBuilder.MissingMatch(false, x.StructuralCategory, x.Level, x.DeltaMm, operationId).Description;
            return IssueTextLocalizer.Details(x) +
                   "\n\nCheck run ID: " + (operationId ?? "") +
                   "\nGenerated at UTC: " + generatedAtUtc.ToString("O");
        }

        private static SavedIssueDto ToSaved(ComparisonIssue x, string title)
        {
            return new SavedIssueDto { ResultKey=x.ResultKey, IssueId=x.ApsIssueId, DisplayId=x.ApsIssueDisplayId, IssueUrl=x.ApsIssueUrl, IssueTitle=title, IssueStatus=x.ApsIssueStatus,
                IssueType="AR-ST", Level=x.Level, CreatedAt=DateTime.UtcNow, DueDate=DateTime.Now.ToString("yyyy-MM-dd"),
                PrimaryElement=new RevitIssueElementDto{Category=x.Category,ModelUid=x.ArchitecturalModelUid,ElementId=x.ArchitecturalElementId,ElementUniqueId=x.ArchitecturalElementUniqueId,LinkInstanceUid=NullIfEmpty(x.ArchitecturalLinkUniqueId),Discipline="Architecture"},
                SecondaryElement=new RevitIssueElementDto{Category=x.Category,ModelUid=x.StructuralModelUid,ElementId=x.StructuralElementId,ElementUniqueId=x.StructuralElementUniqueId,LinkInstanceUid=NullIfEmpty(x.StructuralLinkUniqueId),Discipline="Structure"},
                ResultData=new Dictionary<string,object>{{"delta_mm",x.DeltaMm},{"location_x",x.X},{"location_y",x.Y},{"location_z",x.Z},{"description",x.Description},{"check_type",x.CheckType.ToString()}},
                AiAnalysis=new AiIssueAnalysisDto{IsRealIssue=x.AiIsRealIssue,Assessment=x.AiAssessment,Severity=x.AiSeverity,Comment=x.AiComment,Reason=x.AiReason} };
        }
        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
        public static void OpenIssue(string url){if(!string.IsNullOrWhiteSpace(url))Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}

        /// <summary>
        /// Mirrors the identifier precedence used by ResolveRegionalIssueEnvironmentAsync
        /// so the preflight probe and the real resolution cannot disagree.
        /// </summary>

        /// <summary>Invariant point formatting for diagnostics.</summary>
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
