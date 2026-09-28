using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.APS
{
    /// <summary>
    /// Publishes the current Revit Cloud Model directly through Autodesk Data Management.
    /// BuildAI is used only to supply the 3-legged APS token.
    /// </summary>
    public sealed class ApsPublicationClient : IDisposable
    {
        // International foot: exact by definition. Revit SVF/SVF2 Viewer
        // geometry uses feet internally even when the source project displays metres.
        private const double RevitViewerUnitScaleToMeters = 0.3048;
        // APS can report Autodesk.AEC.ModelData as successful before the
        // complete derivative is ready. Never try to download that resource
        // while the root manifest is still in progress (including 99%). After
        // root success, retain a short eventual-consistency retry window.
        // Autodesk.AEC.ModelData lives under output/Resource of the ROOT derivative
        // and only becomes downloadable once the root manifest completes, even
        // though the manifest marks the resource node "success" much earlier.
        // A fixed ladder of nine attempts (75 s in total) could never span the
        // 12-20 minutes a large federated model needs, so the wait is bounded by
        // wall clock and by translation stalling instead of by attempt count.
        private static readonly TimeSpan AecModelDataTotalBudget = TimeSpan.FromMinutes(25);
        private static readonly TimeSpan AecModelDataStallBudget = TimeSpan.FromMinutes(5);
        private const int AecModelDataMinDelaySeconds = 3;
        private const int AecModelDataMaxDelaySeconds = 15;
        private static readonly string[] AutodeskDataRegions = { "US", "EMEA", "AUS", "GBR", "DEU", "JPN", "CAN", "IND" };
        private readonly HttpClient _http;
        private readonly Dictionary<string, string> _regionByUrn = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DerivativeRoute> _routeByUrn = new Dictionary<string, DerivativeRoute>(StringComparer.Ordinal);
        public ApsPublicationClient(HttpClient http = null)
        {
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        }


        public async Task<ApsPushpinContext> ResolveExistingAsync(
            ApsCloudModelIdentity identity,
            ApsTokenResponse token,
            string preferredView,
            CancellationToken ct,
            IProgress<string> diagnostics = null)
        {
            if (identity == null || !identity.IsUsable)
                throw new InvalidOperationException("The active Revit document does not expose enough Autodesk cloud-model identity data.");
            ValidateToken(token);
            diagnostics?.Report("FAST PUBLICATION MODE\nUsing the latest already-published Autodesk Docs version. Synchronize and publish are skipped.\nRequired view: " + preferredView);
            var item = await ResolveCloudModelItemAsync(identity, token, ct, diagnostics).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(item.ItemId))
                throw new InvalidOperationException("The active Revit Cloud Model could not be matched to an Autodesk Docs item.");
            var published = await GetLatestVersionAsync(identity.ProjectId, item.ItemId, token, ct, diagnostics).ConfigureAwait(false);
            var derivativeUrn = ResolveCurrentVersionDerivativeUrn(published);
            RegisterDerivativeRoute(published, derivativeUrn, diagnostics);
            var view = await ResolveViewableAsync(derivativeUrn, preferredView, token, ct, diagnostics).ConfigureAwait(false);
            if (view == null)
                throw new InvalidOperationException("The latest published model does not contain the required 3D view: " + preferredView + ". Run Safe publication once after adding the view to Publish Settings.");
            var context = new ApsPushpinContext
            {
                DocumentUrn = item.ItemId,
                ProjectId = identity.ProjectId,
                VersionUrn = published.VersionId,
                DerivativeUrn = derivativeUrn,
                DerivativeManifestUrl = published.DerivativeManifestUrl,
                DerivativeRegion = published.DerivativeRegion,
                DerivativeScopes = published.DerivativeScopes,
                FileVersion = published.VersionNumber,
                ViewableGeometryGuid = view.GeometryGuid,
                ViewableId = view.ViewableId,
                ModelPropertiesGuid = view.ModelPropertiesGuid,
                ViewableName = view.Name,
                Is3D = true,
                ViewerUnitScaleToMeters = view.UnitScaleToMeters,
                ViewerGlobalOffsetX = view.GlobalOffsetX,
                ViewerGlobalOffsetY = view.GlobalOffsetY,
                ViewerGlobalOffsetZ = view.GlobalOffsetZ,
                ViewerTransformM00 = view.TransformM00,
                ViewerTransformM01 = view.TransformM01,
                ViewerTransformM02 = view.TransformM02,
                ViewerTransformM10 = view.TransformM10,
                ViewerTransformM11 = view.TransformM11,
                ViewerTransformM12 = view.TransformM12,
                ViewerTransformM20 = view.TransformM20,
                ViewerTransformM21 = view.TransformM21,
                ViewerTransformM22 = view.TransformM22,
                ViewerTransformSource = view.TransformSource,
                Status = "ready"
            };
            ReportResolvedContext(context, diagnostics);
            return context;
        }

        public async Task<ApsPushpinContext> PublishAndResolveAsync(
            ApsCloudModelIdentity identity,
            ApsTokenResponse token,
            string preferredView,
            CancellationToken ct,
            IProgress<string> diagnostics = null,
            bool checkPreviousPublishSet = true)
        {
            if (identity == null || !identity.IsUsable)
                throw new InvalidOperationException("The active Revit document does not expose enough Autodesk cloud-model identity data. Project ID and model GUID or document title are required.");
            ValidateToken(token);

            diagnostics?.Report("NATIVE APS PUBLICATION\nBuildAI publication endpoints are not used.\nProject ID: " + identity.ProjectId +
                                "\nHub ID: " + Safe(identity.HubId) +
                                "\nProject GUID: " + Safe(identity.ProjectGuid) +
                                "\nModel GUID: " + Safe(identity.ModelGuid) +
                                "\nDocument: " + Safe(identity.DocumentTitle) +
                                "\nRequired view: " + preferredView);

            var item = await ResolveCloudModelItemAsync(identity, token, ct, diagnostics).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(item.ItemId))
                throw new InvalidOperationException("The active Revit Cloud Model could not be matched to an Autodesk Docs item. The plugin searched Project Files by project/model GUID and document name.");

            var before = await GetLatestVersionAsync(identity.ProjectId, item.ItemId, token, ct, diagnostics).ConfigureAwait(false);

            // Publishing and translating a large federated model costs 15-25
            // minutes. If the required view is not in the Revit publish set the
            // result cannot contain it, so verify against the previous version's
            // manifest first: one HTTP call instead of a 25-minute dead end.
            // Recalculate may have just synchronized a newly configured publish
            // set. Its previous manifest cannot prove the current membership.
            // Existing Issue workflows retain their fast preflight by default.
            if (checkPreviousPublishSet)
                await PreflightPublishSetAsync(before, preferredView, token, ct, diagnostics).ConfigureAwait(false);

            var commandId = await StartPublishAsync(identity.ProjectId, item.ItemId, token, ct, diagnostics).ConfigureAwait(false);

            var published = string.Equals(commandId, "__ALREADY_PUBLISHED__", StringComparison.Ordinal)
                ? before
                : await WaitForPublishedVersionAsync(identity.ProjectId, item.ItemId, before, token, ct, diagnostics).ConfigureAwait(false);
            diagnostics?.Report("AUTODESK PUBLICATION COMPLETE\nCommand ID: " + Safe(commandId) +
                                "\nItem: " + item.ItemId +
                                "\nVersion: " + published.VersionNumber +
                                "\nVersion URN: " + published.VersionId);

            var derivativeUrn = ResolveCurrentVersionDerivativeUrn(published);
            RegisterDerivativeRoute(published, derivativeUrn, diagnostics);
            var view = await ResolveViewableAsync(derivativeUrn, preferredView, token, ct, diagnostics).ConfigureAwait(false);
            if (view == null)
                throw new InvalidOperationException("The model was published, but the required 3D view was not found in Autodesk metadata: " + preferredView +
                    ". Add this view to Collaborate > Publish Settings, synchronize, and retry.");

            var context = new ApsPushpinContext
            {
                DocumentUrn = item.ItemId,
                ProjectId = identity.ProjectId,
                VersionUrn = published.VersionId,
                DerivativeUrn = derivativeUrn,
                DerivativeManifestUrl = published.DerivativeManifestUrl,
                DerivativeRegion = published.DerivativeRegion,
                DerivativeScopes = published.DerivativeScopes,
                FileVersion = published.VersionNumber,
                ViewableGeometryGuid = view.GeometryGuid,
                ViewableId = view.ViewableId,
                ModelPropertiesGuid = view.ModelPropertiesGuid,
                ViewableName = view.Name,
                Is3D = true,
                ViewerUnitScaleToMeters = view.UnitScaleToMeters,
                ViewerGlobalOffsetX = view.GlobalOffsetX,
                ViewerGlobalOffsetY = view.GlobalOffsetY,
                ViewerGlobalOffsetZ = view.GlobalOffsetZ,
                ViewerTransformM00 = view.TransformM00,
                ViewerTransformM01 = view.TransformM01,
                ViewerTransformM02 = view.TransformM02,
                ViewerTransformM10 = view.TransformM10,
                ViewerTransformM11 = view.TransformM11,
                ViewerTransformM12 = view.TransformM12,
                ViewerTransformM20 = view.TransformM20,
                ViewerTransformM21 = view.TransformM21,
                ViewerTransformM22 = view.TransformM22,
                ViewerTransformSource = view.TransformSource,
                Status = "ready"
            };
            ReportResolvedContext(context, diagnostics);
            return context;
        }

        /// <summary>
        /// Checks the already-published previous version for the required 3D view.
        /// A view can only reach Autodesk if it belongs to the Revit publish set,
        /// and publish-set membership does not change during publication, so the
        /// previous manifest is an accurate predictor of the next one.
        /// Deliberately non-blocking when the answer is genuinely unknowable
        /// (no previous derivative, translation still running, transient APS error):
        /// a false stop is worse than the check it replaces.
        /// </summary>
        private async Task PreflightPublishSetAsync(
            ApsModelItemContext previousVersion,
            string preferredView,
            ApsTokenResponse token,
            CancellationToken ct,
            IProgress<string> diagnostics)
        {
            string skipReason = null;
            try
            {
                var previousUrn = ResolveCurrentVersionDerivativeUrn(previousVersion);
                if (string.IsNullOrWhiteSpace(previousUrn))
                {
                    skipReason = "the previous version does not expose a Model Derivative URN (first publication of this model).";
                }
                else
                {
                    RegisterDerivativeRoute(previousVersion, previousUrn, diagnostics);
                    var url = "https://developer.api.autodesk.com/modelderivative/v2/designdata/" + Esc(previousUrn) + "/manifest";
                    var response = await SendWithStatusAsync(
                        HttpMethod.Get, url, null, token, ct, diagnostics,
                        "Preflight publish-set check").ConfigureAwait(false);

                    if (response.StatusCode < 200 || response.StatusCode >= 300 || string.IsNullOrWhiteSpace(response.Body))
                    {
                        skipReason = "the previous manifest is not readable (HTTP " + response.StatusCode + ").";
                    }
                    else
                    {
                        var manifest = JObject.Parse(response.Body);
                        var status = ((string)manifest["status"] ?? "").Trim();
                        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                        {
                            skipReason = "the previous version is still translating (status '" + status + "').";
                        }
                        else
                        {
                            var readiness = EvaluateRequiredViewableReadiness(manifest, preferredView);
                            if (readiness.ViewableMissing)
                            {
                                diagnostics?.Report(
                                    "PREFLIGHT PUBLISH-SET CHECK FAILED" + Environment.NewLine +
                                    "Required view: " + preferredView + Environment.NewLine +
                                    "Checked version: " + (previousVersion == null ? "<unknown>" : previousVersion.VersionNumber.ToString()) + Environment.NewLine +
                                    "Published 3D viewables: " + Safe(readiness.AvailableViewNames) + Environment.NewLine +
                                    "Publish set: " + (string.IsNullOrWhiteSpace(readiness.PublishSetHint) ? "<unknown>" : readiness.PublishSetHint) + Environment.NewLine +
                                    "Publication was NOT started. No Autodesk processing time was spent.");
                                throw new ApsPublishSetException(BuildMissingViewableMessage(preferredView, readiness));
                            }
                            diagnostics?.Report(
                                "PREFLIGHT PUBLISH-SET CHECK PASSED" + Environment.NewLine +
                                "Required view: " + preferredView + Environment.NewLine +
                                "Found in the previously published version " + (previousVersion == null ? "<unknown>" : previousVersion.VersionNumber.ToString()) + "." + Environment.NewLine +
                                "Publish set: " + (string.IsNullOrWhiteSpace(readiness.PublishSetHint) ? "<not reported by Autodesk>" : readiness.PublishSetHint) + Environment.NewLine +
                                "Published 3D viewables: " + Safe(readiness.AvailableViewNames));
                            return;
                        }
                    }
                }
            }
            catch (ApsPublishSetException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                skipReason = "the check itself failed (" + ex.GetType().Name + ": " + ex.Message + ").";
            }

            diagnostics?.Report(
                "PREFLIGHT PUBLISH-SET CHECK SKIPPED" + Environment.NewLine +
                "Required view: " + preferredView + Environment.NewLine +
                "Reason: " + skipReason + Environment.NewLine +
                "Publication continues; the view will be verified after translation instead.");
        }

        private static void ReportResolvedContext(ApsPushpinContext context, IProgress<string> diagnostics)
        {
            diagnostics?.Report("LINKED DOCUMENT CONTEXT READY\nDocument URN: " + context.DocumentUrn +
                                "\nVersion: " + context.FileVersion +
                                "\nDerivative URN: " + context.DerivativeUrn +
                                "\nDerivative region: " + Safe(context.DerivativeRegion) +
                                "\nView: " + context.ViewableName +
                                "\nManifest geometry GUID -> viewable.guid: " + context.ViewableGeometryGuid +
                                "\nManifest viewableID -> viewable.viewableId: " + context.ViewableId +
                                "\nGraphics resource GUID -> Model Properties only: " + context.ModelPropertiesGuid +
                                "\nviewable.id: omitted from the create payload" +
                                "\nViewer unit scale to metres: " + context.ViewerUnitScaleToMeters.ToString("0.############") +
                                "\nViewer globalOffset: X=" + context.ViewerGlobalOffsetX.ToString("0.######") +
                                "; Y=" + context.ViewerGlobalOffsetY.ToString("0.######") +
                                "; Z=" + context.ViewerGlobalOffsetZ.ToString("0.######") +
                                "\nViewer ref-point linear matrix: [" +
                                context.ViewerTransformM00.ToString("0.############") + ", " + context.ViewerTransformM01.ToString("0.############") + ", " + context.ViewerTransformM02.ToString("0.############") + "; " +
                                context.ViewerTransformM10.ToString("0.############") + ", " + context.ViewerTransformM11.ToString("0.############") + ", " + context.ViewerTransformM12.ToString("0.############") + "; " +
                                context.ViewerTransformM20.ToString("0.############") + ", " + context.ViewerTransformM21.ToString("0.############") + ", " + context.ViewerTransformM22.ToString("0.############") + "]" +
                                "\nViewer ref-point determinant: " + context.ViewerTransformDeterminant.ToString("0.############") +
                                "\nViewer transform source: " + context.ViewerTransformSource);
        }

        private async Task<ApsModelItemContext> ResolveCloudModelItemAsync(ApsCloudModelIdentity identity, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            if (!string.IsNullOrWhiteSpace(identity.CloudModelUrn))
                return await ResolveRevitModelUrnAsync(identity, token, ct, diagnostics).ConfigureAwait(false);
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(identity.HubId))
            {
                var url = "https://developer.api.autodesk.com/project/v1/hubs/" + Esc(identity.HubId) + "/projects/" + Esc(identity.ProjectId) + "/topFolders";
                var root = JObject.Parse(await SendAsync(HttpMethod.Get, url, null, token, ct, diagnostics, "Get Autodesk project top folders").ConfigureAwait(false));
                foreach (var x in root["data"] as JArray ?? new JArray())
                {
                    var id = (string)x["id"];
                    var name = (string)x["attributes"]?["name"];
                    var module = (string)x["attributes"]?["extension"]?["data"]?["module"];
                    if (!string.IsNullOrWhiteSpace(id) && (string.Equals(module, "projectFiles", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(module, "projects", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Project Files", StringComparison.OrdinalIgnoreCase))) roots.Insert(0, id);
                    else if (!string.IsNullOrWhiteSpace(id)) roots.Add(id);
                }
            }
            if (roots.Count == 0)
                throw new InvalidOperationException("Hub ID/top-level Project Files folder could not be resolved from the active cloud model.");

            var queue = new Queue<string>(roots.Distinct(StringComparer.OrdinalIgnoreCase));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApsModelItemContext nameFallback = null;
            var scanned = 0;
            while (queue.Count > 0 && scanned < 1500)
            {
                var folderId = queue.Dequeue();
                if (!visited.Add(folderId)) continue;
                var url = "https://developer.api.autodesk.com/data/v1/projects/" + Esc(identity.ProjectId) + "/folders/" + Esc(folderId) + "/contents";
                var root = JObject.Parse(await SendAsync(HttpMethod.Get, url, null, token, ct, diagnostics, "Search Autodesk Docs folder").ConfigureAwait(false));
                foreach (var x in root["data"] as JArray ?? new JArray())
                {
                    scanned++;
                    var type = (string)x["type"];
                    var id = (string)x["id"];
                    if (string.Equals(type, "folders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(id)) queue.Enqueue(id);
                        continue;
                    }
                    if (!string.Equals(type, "items", StringComparison.OrdinalIgnoreCase)) continue;
                    var attrs = x["attributes"];
                    var ext = attrs?["extension"]?["data"];
                    var projectGuid = First((string)ext?["projectGuid"], (string)ext?["projectGUID"]);
                    var modelGuid = First((string)ext?["modelGuid"], (string)ext?["modelGUID"]);
                    var name = (string)attrs?["displayName"] ?? (string)attrs?["name"];
                    var candidate = new ApsModelItemContext { ItemId = id ?? "", ItemName = name ?? "" };
                    if (GuidEquals(projectGuid, identity.ProjectGuid) && GuidEquals(modelGuid, identity.ModelGuid))
                    {
                        diagnostics?.Report("Cloud model item matched by Revit project/model GUID.\nItem ID: " + id + "\nName: " + name);
                        return candidate;
                    }
                    if (nameFallback == null && FileNameEquals(name, identity.DocumentTitle)) nameFallback = candidate;
                }
            }
            if (nameFallback != null && !identity.RequireExactModelMatch)
            {
                diagnostics?.Report("Cloud model item matched by document name fallback.\nItem ID: " + nameFallback.ItemId + "\nName: " + nameFallback.ItemName);
                return nameFallback;
            }
            return new ApsModelItemContext();
        }

        private async Task<ApsModelItemContext> ResolveRevitModelUrnAsync(ApsCloudModelIdentity identity, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var urn = (identity.CloudModelUrn ?? "").Trim();
            var baseUrl = "https://developer.api.autodesk.com/data/v1/projects/" + Esc(identity.ProjectId);
            // Revit is authoritative for the active cloud document. Resolve a
            // version URN through its item relationship, never by replacing text.
            if (urn.StartsWith("urn:", StringComparison.Ordinal) && urn.IndexOf(":fs.file:", StringComparison.Ordinal) >= 0)
            {
                var versionRoot = JObject.Parse(await SendAsync(HttpMethod.Get, baseUrl + "/versions/" + Esc(urn), null,
                    token, ct, diagnostics, "Resolve Revit cloud version URN").ConfigureAwait(false));
                var version = versionRoot["data"];
                if ((string)version?["type"] != "versions" || (string)version?["id"] != urn)
                    throw new InvalidOperationException("Autodesk returned a different version for the active Revit cloud model URN.");
                var itemLink = version["relationships"]?["item"]?["data"];
                if ((string)itemLink?["type"] != "items")
                    throw new InvalidOperationException("The Revit cloud version has no Autodesk Docs item relationship.");
                urn = (string)itemLink["id"] ?? "";
            }
            if (!urn.StartsWith("urn:", StringComparison.Ordinal) || urn.IndexOf(":dm.lineage:", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Revit returned an unsupported ACC model URN: " + urn);
            var root = JObject.Parse(await SendAsync(HttpMethod.Get, baseUrl + "/items/" + Esc(urn), null,
                token, ct, diagnostics, "Resolve exact Revit cloud model item").ConfigureAwait(false));
            var item = root["data"];
            if ((string)item?["type"] != "items" || !string.Equals((string)item?["id"], urn, StringComparison.Ordinal))
                throw new InvalidOperationException("Autodesk returned a different item for the active Revit cloud model URN. Publication was stopped.");
            var name = (string)item["attributes"]?["displayName"] ?? (string)item["attributes"]?["name"] ?? "";
            diagnostics?.Report("CLOUD MODEL MATCHED BY REVIT URN\nProject ID: " + identity.ProjectId +
                "\nItem ID: " + urn + "\nName: " + name + "\nFolder scanning and document-name fallback were not used.");
            return new ApsModelItemContext { ItemId = urn, ItemName = name };
        }

        private async Task<ApsModelItemContext> GetLatestVersionAsync(string projectId, string itemId, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var url = "https://developer.api.autodesk.com/data/v1/projects/" + Esc(projectId) + "/items/" + Esc(itemId) + "/versions";
            var root = JObject.Parse(await SendAsync(HttpMethod.Get, url, null, token, ct, diagnostics, "Get latest Autodesk Docs model version").ConfigureAwait(false));
            var versions = (root["data"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var selected = versions.OrderByDescending(x => (int?)x["attributes"]?["versionNumber"] ?? 0).FirstOrDefault();
            if (selected == null) throw new InvalidOperationException("Autodesk Docs returned no versions for cloud model item " + itemId + ".");
            return ParseVersion(selected);
        }

        private async Task<string> StartPublishAsync(string projectId, string itemId, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var url = "https://developer.api.autodesk.com/data/v1/projects/" + Esc(projectId) + "/commands";
            var body = new JObject
            {
                ["jsonapi"] = new JObject { ["version"] = "1.0" },
                ["data"] = new JObject
                {
                    ["type"] = "commands",
                    ["attributes"] = new JObject
                    {
                        ["extension"] = new JObject
                        {
                            ["type"] = "commands:autodesk.bim360:C4RModelPublish",
                            ["version"] = "1.0.0"
                        }
                    },
                    ["relationships"] = new JObject
                    {
                        ["resources"] = new JObject
                        {
                            ["data"] = new JArray(new JObject { ["type"] = "items", ["id"] = itemId })
                        }
                    }
                }
            };
            var raw = await SendAsync(HttpMethod.Post, url, body.ToString(Formatting.None), token, ct, diagnostics, "Publish Revit Cloud Model").ConfigureAwait(false);
            var root = JObject.Parse(raw);
            if (root["data"] == null || root["data"].Type == JTokenType.Null)
            {
                diagnostics?.Report("Autodesk reports that the latest synchronized model is already published. Reusing the current Docs version.");
                return "__ALREADY_PUBLISHED__";
            }
            var commandId = (string)root["data"]?["id"] ?? (string)root["data"]?["relationships"]?["job"]?["data"]?["id"];
            return commandId ?? "";
        }

        private async Task<ApsModelItemContext> WaitForPublishedVersionAsync(string projectId, string itemId, ApsModelItemContext before, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            var attempt = 0;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 8), ct).ConfigureAwait(false);
                var current = await GetLatestVersionAsync(projectId, itemId, token, ct, diagnostics).ConfigureAwait(false);
                diagnostics?.Report("Publication polling attempt " + attempt + ": version " + current.VersionNumber +
                                    " (before: " + before.VersionNumber + ")");
                if (current.VersionNumber > before.VersionNumber ||
                    (!string.Equals(current.VersionId, before.VersionId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(current.VersionId)))
                    return current;
            }
            throw new TimeoutException("Autodesk publication did not produce a new Docs version within 10 minutes.");
        }

        private async Task<Viewable> ResolveViewableAsync(string derivativeUrn, string preferredView, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var manifest = await WaitForDerivativeReadyAsync(derivativeUrn, preferredView, token, ct, diagnostics).ConfigureAwait(false);
            var identity = ResolveManifestViewableIdentity(manifest, preferredView);
            if (identity == null)
                throw new InvalidOperationException("The Model Derivative manifest does not contain the required 3D geometry node: " + preferredView + ".");
            var viewerTransform = await ResolveViewerCoordinateTransformAsync(derivativeUrn, manifest, token, ct, diagnostics).ConfigureAwait(false);

            var url = "https://developer.api.autodesk.com/modelderivative/v2/designdata/" + Esc(derivativeUrn) + "/metadata";
            JObject root = null;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            var attempt = 0;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                var response = await SendWithStatusAsync(HttpMethod.Get, url, null, token, ct, diagnostics, "Get Model Derivative metadata").ConfigureAwait(false);
                if (response.StatusCode >= 200 && response.StatusCode < 300)
                {
                    root = JObject.Parse(response.Body);
                    break;
                }
                if (response.StatusCode != 404 && response.StatusCode != 202)
                    throw new InvalidOperationException("Get Model Derivative metadata HTTP " + response.StatusCode + ": " + response.Body);
                diagnostics?.Report("Model Derivative metadata is not available yet (HTTP " + response.StatusCode + "). Retry " + attempt + " in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            if (root == null)
                throw new TimeoutException("Autodesk Model Derivative metadata did not become available within 3 minutes after the manifest reported success.");

            var available = new List<string>();
            foreach (var x in root["data"]?["metadata"] as JArray ?? new JArray())
            {
                var name = (string)x["name"] ?? "";
                var role = (string)x["role"] ?? "";
                var guid = (string)x["guid"] ?? "";
                available.Add(name + " [" + role + "] guid=" + guid);
                if (string.Equals(name.Trim(), (preferredView ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (role.IndexOf("3d", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("viewable", StringComparison.OrdinalIgnoreCase) >= 0 || string.IsNullOrWhiteSpace(role)))
                {
                    if (!string.Equals(guid, identity.ModelPropertiesGuid, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "The Model Derivative metadata GUID for view '" + name + "' does not match its manifest graphics resource GUID. " +
                            "Metadata GUID: " + guid + "; manifest graphics GUID: " + identity.ModelPropertiesGuid + ".");
                    return new Viewable
                    {
                        GeometryGuid = identity.GeometryGuid,
                        ViewableId = identity.ViewableId,
                        ModelPropertiesGuid = guid,
                        Name = name,
                        UnitScaleToMeters = viewerTransform.UnitScaleToMeters,
                        GlobalOffsetX = viewerTransform.GlobalOffsetX,
                        GlobalOffsetY = viewerTransform.GlobalOffsetY,
                        GlobalOffsetZ = viewerTransform.GlobalOffsetZ,
                        TransformM00 = viewerTransform.TransformM00,
                        TransformM01 = viewerTransform.TransformM01,
                        TransformM02 = viewerTransform.TransformM02,
                        TransformM10 = viewerTransform.TransformM10,
                        TransformM11 = viewerTransform.TransformM11,
                        TransformM12 = viewerTransform.TransformM12,
                        TransformM20 = viewerTransform.TransformM20,
                        TransformM21 = viewerTransform.TransformM21,
                        TransformM22 = viewerTransform.TransformM22,
                        TransformSource = viewerTransform.Source
                    };
                }
            }
            diagnostics?.Report("Required view was not found. Available metadata views:" + Environment.NewLine + string.Join(Environment.NewLine, available.Take(100)));
            return null;
        }

        private static ViewableIdentity ResolveManifestViewableIdentity(JObject manifest, string preferredView)
        {
            var expectedName = (preferredView ?? "").Trim();
            var geometry = manifest?
                .Descendants()
                .OfType<JObject>()
                .FirstOrDefault(x =>
                    string.Equals((string)x["type"], "geometry", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(((string)x["name"] ?? "").Trim(), expectedName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["role"], "3d", StringComparison.OrdinalIgnoreCase));
            if (geometry == null) return null;

            var children = geometry["children"] as JArray ?? new JArray();
            var viewNode = children
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals((string)x["type"], "view", StringComparison.OrdinalIgnoreCase));
            var graphicsNode = children
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals((string)x["role"], "graphics", StringComparison.OrdinalIgnoreCase));

            var result = new ViewableIdentity
            {
                GeometryGuid = (string)geometry["guid"] ?? "",
                ViewableId = First((string)geometry["viewableID"], (string)geometry["viewableId"], (string)viewNode?["guid"]),
                ModelPropertiesGuid = (string)graphicsNode?["guid"] ?? ""
            };
            if (string.IsNullOrWhiteSpace(result.GeometryGuid) ||
                string.IsNullOrWhiteSpace(result.ViewableId) ||
                string.IsNullOrWhiteSpace(result.ModelPropertiesGuid))
                throw new InvalidOperationException(
                    "The manifest entry for 3D view '" + preferredView + "' does not contain all required identities: " +
                    "geometry.guid, geometry.viewableID, and child role=graphics guid.");
            return result;
        }

        private async Task<JObject> WaitForDerivativeReadyAsync(string derivativeUrn, string preferredView, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics)
        {
            var url = "https://developer.api.autodesk.com/modelderivative/v2/designdata/" + Esc(derivativeUrn) + "/manifest";
            // Large federated/cloud models can remain at 99% for more than the
            // former 20-minute limit. The request remains cancellable from UI.
            var deadline = DateTime.UtcNow.AddMinutes(45);
            var attempt = 0;
            var consecutiveTransportFailures = 0;
            DateTime? targetReadySinceUtc = null;
            DateTime? rootSuccessWithoutTargetSinceUtc = null;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                ApsHttpResult response;
                try
                {
                    response = await SendWithStatusAsync(HttpMethod.Get, url, null, token, ct, diagnostics, "Check Model Derivative manifest").ConfigureAwait(false);
                    consecutiveTransportFailures = 0;
                }
                catch (Exception ex) when (IsTransientTransportFailure(ex, ct))
                {
                    consecutiveTransportFailures++;
                    var retrySeconds = Math.Min(30, 5 * consecutiveTransportFailures);
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;
                    var retryDelay = TimeSpan.FromSeconds(Math.Min(retrySeconds, Math.Max(1, remaining.TotalSeconds)));
                    diagnostics?.Report(
                        "APS TRANSIENT TRANSPORT FAILURE" + Environment.NewLine +
                        "Operation: Check Model Derivative manifest" + Environment.NewLine +
                        "Polling attempt: " + attempt + Environment.NewLine +
                        "Consecutive transport failures: " + consecutiveTransportFailures + Environment.NewLine +
                        "Exception: " + DescribeTransportFailure(ex) + Environment.NewLine +
                        "Caller cancellation requested: " + ct.IsCancellationRequested + Environment.NewLine +
                        "Decision: preserve published version and retry the same authoritative derivative route in " +
                        retryDelay.TotalSeconds.ToString("0") + " seconds." + Environment.NewLine +
                        "Remaining derivative wait budget: " + Math.Max(0, remaining.TotalMinutes).ToString("0.0") + " minutes.");
                    PluginLog.Info("APS transient transport failure; derivative polling will continue", new
                    {
                        operation = "Check Model Derivative manifest",
                        attempt,
                        consecutiveTransportFailures,
                        exceptionType = ex.GetType().FullName,
                        innerExceptionType = ex.InnerException?.GetType().FullName,
                        hresult = "0x" + ex.HResult.ToString("X8"),
                        retrySeconds = retryDelay.TotalSeconds
                    });
                    await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                    continue;
                }
                if (response.StatusCode == 404 || response.StatusCode == 202)
                {
                    diagnostics?.Report("Model Derivative manifest is not available yet (HTTP " + response.StatusCode + "). Polling attempt " + attempt + ".");
                }
                else if (response.StatusCode >= 200 && response.StatusCode < 300)
                {
                    var root = JObject.Parse(response.Body);
                    var status = ((string)root["status"] ?? "").Trim().ToLowerInvariant();
                    var progress = (string)root["progress"] ?? "";
                    var readiness = EvaluateRequiredViewableReadiness(root, preferredView);
                    diagnostics?.Report("Model Derivative polling attempt " + attempt + ": status=" + status + ", progress=" + progress +
                                        Environment.NewLine + readiness.Summary);
                    if (status == "failed" || status == "timeout")
                        throw new InvalidOperationException("Autodesk Model Derivative finished with status '" + status + "'. Manifest: " + response.Body);
                    if (readiness.HasTerminalFailure)
                        throw new InvalidOperationException(
                            "The required Autodesk viewable or one of its mandatory resources failed while Model Derivative was processing. " +
                            readiness.Summary);
                    var rootReady = status == "success" && IsCompleteProgress(progress);
                    if (readiness.IsReady && rootReady && readiness.Svf2Declared && !readiness.Svf2Ready)
                    {
                        // Reported only. Verified in ACC on 2026-09-19: the probe scene and
                        // the ACC session had the same fragment count (33584) on version 57,
                        // so the probe was already in the derivative ACC opens. Blocking here
                        // would only add minutes to every run.
                        diagnostics?.Report(
                            "SVF2 STILL BUILDING (reported only)" + Environment.NewLine + readiness.Summary);
                    }
                    if (readiness.IsReady && rootReady)
                    {
                        diagnostics?.Report("MODEL DERIVATIVE TARGET VIEWABLE READY" + Environment.NewLine +
                                            "Required view: " + preferredView + Environment.NewLine +
                                            "Root status: " + status + Environment.NewLine +
                                            "Root progress: " + progress + Environment.NewLine +
                                            "Decision: continue because the root manifest, exact viewable, PropertyDatabase and Autodesk.AEC.ModelData are ready.");
                        return root;
                    }
                    if (status == "success" && !readiness.IsReady)
                    {
                        if (!rootSuccessWithoutTargetSinceUtc.HasValue)
                            rootSuccessWithoutTargetSinceUtc = DateTime.UtcNow;
                        var resourceLag = DateTime.UtcNow - rootSuccessWithoutTargetSinceUtc.Value;

                        // Two very different situations share "not ready" and must not
                        // share a grace period:
                        //   * the viewable node exists but its resources lag  -> genuine
                        //     eventual consistency, worth waiting three minutes for;
                        //   * the viewable node is absent from a COMPLETED manifest ->
                        //     the view was never published, because it is not in the
                        //     Revit publish set. Waiting cannot fix this.
                        if (readiness.ViewableMissing)
                        {
                            diagnostics?.Report(
                                "REQUIRED VIEWABLE NOT PUBLISHED" + Environment.NewLine +
                                "Required view: " + preferredView + Environment.NewLine +
                                "Root manifest: success / " + progress + Environment.NewLine +
                                "Published 3D viewables: " + Safe(readiness.AvailableViewNames) + Environment.NewLine +
                                "Publish set: " + (string.IsNullOrWhiteSpace(readiness.PublishSetHint) ? "<unknown>" : readiness.PublishSetHint) + Environment.NewLine +
                                "Confirmation window: " + resourceLag.TotalSeconds.ToString("0") + "/" +
                                MissingViewableConfirmationSeconds + " seconds.");
                            if (resourceLag >= TimeSpan.FromSeconds(MissingViewableConfirmationSeconds))
                                throw new ApsPublishSetException(BuildMissingViewableMessage(preferredView, readiness));
                        }
                        else
                        {
                            diagnostics?.Report(
                                "Root manifest reports success, but the exact viewable's mandatory resources are still converging. " +
                                "Eventual-consistency grace period: " + resourceLag.TotalSeconds.ToString("0") + "/180 seconds.\n" +
                                readiness.Summary);
                            if (resourceLag >= TimeSpan.FromMinutes(3))
                                throw new InvalidOperationException(
                                    "Autodesk Model Derivative reported root status 'success', but the required viewable " +
                                    "did not become usable during the 3-minute eventual-consistency grace period. " + readiness.Summary);
                        }
                    }
                    else
                    {
                        rootSuccessWithoutTargetSinceUtc = null;
                    }
                    if (readiness.IsReady && !rootReady)
                    {
                        if (!targetReadySinceUtc.HasValue) targetReadySinceUtc = DateTime.UtcNow;
                        var readyFor = DateTime.UtcNow - targetReadySinceUtc.Value;
                        diagnostics?.Report(
                            "All target children are ready, but the root manifest is still '" + status +
                            "' at '" + progress + "'. Grace period: " + readyFor.TotalSeconds.ToString("0") +
                            "/45 seconds before guarded resource download.");
                        if (readyFor >= TimeSpan.FromSeconds(45))
                        {
                            diagnostics?.Report(
                                "MODEL DERIVATIVE TARGET VIEWABLE READY (ROOT LAG TOLERATED)" + Environment.NewLine +
                                "Required view: " + preferredView + Environment.NewLine +
                                "The exact viewable and mandatory resources have remained ready for 45 seconds. " +
                                "Continuing while the root manifest remains at " + status + " / " + progress + ". " +
                                "Resource downloads retain their own bounded 404/202 retries.");
                            return root;
                        }
                    }
                    else
                    {
                        targetReadySinceUtc = null;
                    }
                }
                else
                {
                    throw new InvalidOperationException("Check Model Derivative manifest HTTP " + response.StatusCode + ": " + response.Body);
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt < 4 ? 5 : 10), ct).ConfigureAwait(false);
            }
            throw new TimeoutException(
                "Autodesk Model Derivative did not make the required viewable '" + preferredView +
                "' and the root manifest ready within 45 minutes.");
        }

        private static bool IsTransientTransportFailure(Exception ex, CancellationToken callerToken)
        {
            if (callerToken.IsCancellationRequested)
                return false;

            if (ex is HttpRequestException)
                return true;

            // HttpClient represents its own request timeout as TaskCanceledException.
            // A cancellation requested by the caller must still leave immediately.
            return ex is TaskCanceledException;
        }

        private static string DescribeTransportFailure(Exception ex)
        {
            if (ex == null) return "<unknown>";
            var details = new List<string>();
            var current = ex;
            for (var depth = 0; current != null && depth < 3; depth++, current = current.InnerException)
            {
                var message = (current.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length > 240) message = message.Substring(0, 240) + "...";
                details.Add(current.GetType().FullName +
                            " [HResult=0x" + current.HResult.ToString("X8") + "]" +
                            (message.Length == 0 ? string.Empty : ": " + message));
            }
            return string.Join(" -> ", details);
        }

        private static RequiredViewableReadiness EvaluateRequiredViewableReadiness(JObject manifest, string preferredView)
        {
            var expectedName = (preferredView ?? "").Trim();
            var geometry = manifest?
                .Descendants()
                .OfType<JObject>()
                .FirstOrDefault(x =>
                    string.Equals((string)x["type"], "geometry", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["role"], "3d", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(((string)x["name"] ?? "").Trim(), expectedName, StringComparison.OrdinalIgnoreCase));

            var allObjects = manifest?.Descendants().OfType<JObject>().ToList() ?? new List<JObject>();
            var propertyDatabase = allObjects.FirstOrDefault(x =>
                string.Equals((string)x["type"], "resource", StringComparison.OrdinalIgnoreCase) &&
                (((string)x["role"] ?? "").IndexOf("PropertyDatabase", StringComparison.OrdinalIgnoreCase) >= 0));
            var aecModelData = allObjects.FirstOrDefault(x =>
                string.Equals((string)x["type"], "resource", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)x["role"], "Autodesk.AEC.ModelData", StringComparison.OrdinalIgnoreCase));

            if (geometry == null)
            {
                var published = DescribePublished3dViewables(allObjects);
                return new RequiredViewableReadiness
                {
                    ViewableMissing = true,
                    PublishSetHint = ExtractPublishSetName(allObjects),
                    AvailableViewNames = published,
                    Summary = "Required viewable '" + expectedName + "' is not present in the manifest. " +
                              "Published 3D viewables: " + published + ". " +
                              ResourceSummary(propertyDatabase, "PropertyDatabase") + "; " +
                              ResourceSummary(aecModelData, "Autodesk.AEC.ModelData")
                };
            }

            var children = geometry["children"] as JArray ?? new JArray();
            var view = children.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["type"], "view", StringComparison.OrdinalIgnoreCase));
            var graphics = children.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["role"], "graphics", StringComparison.OrdinalIgnoreCase));

            // ACC opens the model from the SVF2 (OTG) derivative. Reported so that a lag
            // is visible in the log; it does not gate the run, because the probe was
            // measured to be in the same scene as ACC (same fragment count) even while
            // the manifest still showed SVF2 as building.
            var svf2 = allObjects.FirstOrDefault(x =>
                string.Equals(((string)x["outputType"] ?? "").Trim(), "svf2", StringComparison.OrdinalIgnoreCase));
            var svf2Declared = svf2 != null;
            var svf2Ready = !svf2Declared || IsSuccessful(svf2, true);
            // ACC opens SVF2 and the property database is keyed to it, while SVF is
            // produced first. Probing before SVF2 exists puts the Viewer probe in a
            // different scene: object ids and the geometry frame do not match ACC.
            // Measured 19.09: probe dbIds 342903/49461/33544 against property database
            // 760984/760970/747142 for the same elements.
            var svf2Summary = !svf2Declared
                ? "SVF2=not declared in manifest (legacy SVF-only file)"
                : "SVF2=" + NodeSummary(svf2) + (svf2Ready ? "" : " (still building)");

            var geometryReady = IsSuccessful(geometry, true);
            var viewReady = view == null || IsSuccessful(view, true);
            var graphicsReady = graphics != null &&
                                !string.IsNullOrWhiteSpace((string)graphics["guid"]) &&
                                !string.IsNullOrWhiteSpace((string)graphics["urn"]);
            var propertyDatabaseReady = IsSuccessful(propertyDatabase, false) &&
                                        !string.IsNullOrWhiteSpace((string)propertyDatabase?["urn"]);
            var aecModelDataReady = IsSuccessful(aecModelData, false) &&
                                    !string.IsNullOrWhiteSpace((string)aecModelData?["urn"]);
            var identityReady = !string.IsNullOrWhiteSpace((string)geometry["guid"]) &&
                                !string.IsNullOrWhiteSpace(First(
                                    (string)geometry["viewableID"],
                                    (string)geometry["viewableId"],
                                    (string)view?["guid"]));

            return new RequiredViewableReadiness
            {
                // Populated on every path, not only when the view is missing, so
                // diagnostics can name the publish set in success messages too.
                PublishSetHint = ((string)geometry["ViewSets"] ?? (string)geometry["viewSets"] ?? "").Trim(),
                AvailableViewNames = DescribePublished3dViewables(allObjects),
                IsReady = geometryReady && viewReady && graphicsReady &&
                          propertyDatabaseReady && aecModelDataReady && identityReady,
                Svf2Declared = svf2Declared,
                Svf2Ready = svf2Ready,
                HasTerminalFailure = IsTerminalFailure(geometry) || IsTerminalFailure(view) ||
                                     IsTerminalFailure(propertyDatabase) || IsTerminalFailure(aecModelData),
                Summary = "Required viewable '" + expectedName + "': " + NodeSummary(geometry) +
                          "; view=" + NodeSummary(view) +
                          "; graphicsIdentity=" + (graphicsReady ? "ready" : "not ready") +
                          "; viewableIdentity=" + (identityReady ? "ready" : "not ready") +
                          "; " + ResourceSummary(propertyDatabase, "PropertyDatabase") +
                          "; " + ResourceSummary(aecModelData, "Autodesk.AEC.ModelData") +
                          "; " + svf2Summary
            };
        }

        /// <summary>
        /// Lists the 3D viewables Autodesk actually produced, so a missing-view
        /// error can tell the user what was published instead of what was not.
        /// </summary>
        private static string DescribePublished3dViewables(List<JObject> allObjects)
        {
            var names = (allObjects ?? new List<JObject>())
                .Where(x => string.Equals((string)x["type"], "geometry", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals((string)x["role"], "3d", StringComparison.OrdinalIgnoreCase))
                .Select(x => ((string)x["name"] ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return names.Count == 0 ? "<none>" : string.Join(", ", names);
        }

        /// <summary>
        /// Revit reports the publish set that produced each viewable in the
        /// "ViewSets" property. Reading it from a sibling viewable lets the
        /// plugin name the exact set the user must edit.
        /// </summary>
        private static string ExtractPublishSetName(List<JObject> allObjects)
        {
            return (allObjects ?? new List<JObject>())
                .Where(x => string.Equals((string)x["type"], "geometry", StringComparison.OrdinalIgnoreCase))
                .Select(x => ((string)x["ViewSets"] ?? (string)x["viewSets"] ?? "").Trim())
                .FirstOrDefault(x => x.Length > 0) ?? "";
        }

        private static bool IsSuccessful(JObject node, bool checkProgress)
        {
            if (node == null || !string.Equals(((string)node["status"] ?? "").Trim(), "success", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!checkProgress) return true;
            var progress = ((string)node["progress"] ?? "").Trim();
            return string.IsNullOrWhiteSpace(progress) ||
                   string.Equals(progress, "complete", StringComparison.OrdinalIgnoreCase) ||
                   progress.StartsWith("100%", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompleteProgress(string progress)
        {
            var value = (progress ?? "").Trim();
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value, "complete", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("100%", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTerminalFailure(JObject node)
        {
            var status = ((string)node?["status"] ?? "").Trim();
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase);
        }

        private static string NodeSummary(JObject node)
        {
            if (node == null) return "missing";
            return "status=" + Safe((string)node["status"]) + ", progress=" + Safe((string)node["progress"]);
        }

        private static string ResourceSummary(JObject node, string name)
        {
            return name + "=" + NodeSummary(node) +
                   ", urn=" + (node != null && !string.IsNullOrWhiteSpace((string)node["urn"]) ? "present" : "missing");
        }

        private async Task<ViewerCoordinateTransform> ResolveViewerCoordinateTransformAsync(
            string derivativeUrn,
            JObject manifest,
            ApsTokenResponse token,
            CancellationToken ct,
            IProgress<string> diagnostics)
        {
            var manifestUrl = "https://developer.api.autodesk.com/modelderivative/v2/designdata/" +
                              Esc(derivativeUrn) + "/manifest";
            var currentManifest = manifest;
            JArray transform = null;
            string lastFailure = "Autodesk.AEC.ModelData was not available.";
            var startedUtc = DateTime.UtcNow;
            var deadlineUtc = startedUtc + AecModelDataTotalBudget;
            var lastProgressChangeUtc = startedUtc;
            var lastRootProgress = "";
            var attempt = 0;
            var incompleteRootStallReported = false;

            diagnostics?.Report("AEC MODEL DATA WAIT START" + Environment.NewLine +
                                "Total budget: " + AecModelDataTotalBudget.TotalMinutes.ToString("0") + " minutes" + Environment.NewLine +
                                "Completed-root consistency budget: " + AecModelDataStallBudget.TotalMinutes.ToString("0") +
                                " minutes" + Environment.NewLine +
                                "The resource is served only after the root manifest completes, so the wait " +
                                "uses the full total budget while the root remains inprogress. A stalled percentage " +
                                "is diagnostic only and does not terminate an active Autodesk translation.");

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;

                if (attempt > 1)
                {
                    var manifestResponse = await SendWithStatusAsync(
                        HttpMethod.Get,
                        manifestUrl,
                        null,
                        token,
                        ct,
                        diagnostics,
                        "Refresh Model Derivative manifest for Autodesk.AEC.ModelData").ConfigureAwait(false);

                    if (manifestResponse.StatusCode == 401 || manifestResponse.StatusCode == 403)
                        throw new InvalidOperationException(
                            "Refresh Model Derivative manifest HTTP " + manifestResponse.StatusCode +
                            ". APS authorization or project access must be corrected before retrying.");

                    if (manifestResponse.StatusCode >= 200 && manifestResponse.StatusCode < 300 &&
                        !string.IsNullOrWhiteSpace(manifestResponse.Body))
                    {
                        try
                        {
                            currentManifest = JObject.Parse(manifestResponse.Body);
                        }
                        catch (JsonException ex)
                        {
                            lastFailure = "The refreshed Model Derivative manifest was not valid JSON: " + ex.Message;
                            currentManifest = null;
                        }
                    }
                    else if (IsTemporaryApsStatus(manifestResponse.StatusCode))
                    {
                        lastFailure = "Refresh manifest HTTP " + manifestResponse.StatusCode + ".";
                        currentManifest = null;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Refresh Model Derivative manifest HTTP " + manifestResponse.StatusCode + ": " +
                            manifestResponse.Body);
                    }
                }

                var resource = currentManifest?
                    .Descendants()
                    .OfType<JObject>()
                    .FirstOrDefault(x => string.Equals((string)x["role"], "Autodesk.AEC.ModelData", StringComparison.OrdinalIgnoreCase));
                var resourceUrn = (string)resource?["urn"];
                var resourceStatus = ((string)resource?["status"] ?? "missing").Trim();

                var rootProgress = ((string)currentManifest?["progress"] ?? "").Trim();
                var rootStatus = ((string)currentManifest?["status"] ?? "").Trim();
                if (!string.Equals(rootProgress, lastRootProgress, StringComparison.OrdinalIgnoreCase))
                {
                    lastRootProgress = rootProgress;
                    lastProgressChangeUtc = DateTime.UtcNow;
                    incompleteRootStallReported = false;
                }

                diagnostics?.Report("AEC MODEL DATA WAIT ATTEMPT" + Environment.NewLine +
                                    "Attempt: " + attempt + Environment.NewLine +
                                    "Elapsed: " + (DateTime.UtcNow - startedUtc).TotalSeconds.ToString("0") + "s of " +
                                    AecModelDataTotalBudget.TotalSeconds.ToString("0") + "s" + Environment.NewLine +
                                    "Root manifest: " + (rootStatus.Length == 0 ? "<unknown>" : rootStatus) +
                                    " / " + (rootProgress.Length == 0 ? "<unknown>" : rootProgress) + Environment.NewLine +
                                    "Manifest resource status: " + resourceStatus + Environment.NewLine +
                                    "Resource URN: " + (string.IsNullOrWhiteSpace(resourceUrn) ? "missing" : resourceUrn));

                if (!string.IsNullOrWhiteSpace(resourceUrn))
                {
                    var url = "https://developer.api.autodesk.com/modelderivative/v2/designdata/" + Esc(derivativeUrn) +
                              "/manifest/" + Esc(resourceUrn);
                    diagnostics?.Report("APS VIEWER TRANSFORM REQUEST" + Environment.NewLine +
                                        "Source: Autodesk.AEC.ModelData / refPointTransformation" + Environment.NewLine +
                                        "GET " + url);

                    var aecResponse = await SendWithStatusAsync(
                        HttpMethod.Get, url, null, token, ct, diagnostics,
                        "Download Autodesk.AEC.ModelData").ConfigureAwait(false);
                    var statusCode = aecResponse.StatusCode;
                    var body = aecResponse.Body ?? "";
                    diagnostics?.Report("APS VIEWER TRANSFORM RESPONSE" + Environment.NewLine +
                                        "Attempt: " + attempt + Environment.NewLine +
                                        "HTTP " + statusCode + Environment.NewLine +
                                        "Body length: " + body.Length);

                    if (statusCode == 401 || statusCode == 403)
                        throw new InvalidOperationException(
                            "Download Autodesk.AEC.ModelData HTTP " + statusCode +
                            ". APS authorization or project access must be corrected before retrying.");

                    if (statusCode >= 200 && statusCode < 300 && !string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            var aecData = JObject.Parse(body);
                            transform = aecData["refPointTransformation"] as JArray;
                            if (transform != null && transform.Count >= 12)
                            {
                                diagnostics?.Report("AEC MODEL DATA READY" + Environment.NewLine +
                                                    "Attempt: " + attempt + Environment.NewLine +
                                                    "Elapsed: " + (DateTime.UtcNow - startedUtc).TotalSeconds.ToString("0") + "s" + Environment.NewLine +
                                                    "Download HTTP: " + statusCode + Environment.NewLine +
                                                    "refPointTransformation: found");
                                break;
                            }
                            lastFailure = "Autodesk.AEC.ModelData did not yet contain refPointTransformation[12].";
                        }
                        catch (JsonException ex)
                        {
                            lastFailure = "Autodesk.AEC.ModelData was not valid JSON yet: " + ex.Message;
                        }
                    }
                    else if (IsTemporaryApsStatus(statusCode) ||
                             (statusCode >= 200 && statusCode < 300 && string.IsNullOrWhiteSpace(body)))
                    {
                        lastFailure = "Download Autodesk.AEC.ModelData HTTP " + statusCode +
                                      (string.IsNullOrWhiteSpace(body) ? " with an empty body." : ".");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Download Autodesk.AEC.ModelData HTTP " + statusCode + ": " + body);
                    }
                }
                else
                {
                    lastFailure = "The refreshed manifest did not expose an Autodesk.AEC.ModelData resource URN.";
                }

                var now = DateTime.UtcNow;
                var stalledFor = now - lastProgressChangeUtc;
                var rootComplete = string.Equals(rootStatus, "success", StringComparison.OrdinalIgnoreCase) &&
                                   IsCompleteProgress(rootProgress);

                // Give up only when there is real evidence that waiting cannot help:
                // the overall budget is spent, or completed-root consistency expires.
                // An unchanged incomplete root remains eligible for the full budget.
                if (now >= deadlineUtc)
                {
                    lastFailure += " The " + AecModelDataTotalBudget.TotalMinutes.ToString("0") +
                                   "-minute budget elapsed while the root manifest was at '" +
                                   (rootProgress.Length == 0 ? "unknown" : rootProgress) + "'.";
                    break;
                }
                if (rootComplete && stalledFor >= AecModelDataStallBudget)
                {
                    lastFailure += " The root manifest completed " + stalledFor.TotalMinutes.ToString("0") +
                                   " minutes ago but the resource is still not downloadable.";
                    break;
                }
                if (!rootComplete && stalledFor >= AecModelDataStallBudget && !incompleteRootStallReported)
                {
                    incompleteRootStallReported = true;
                    diagnostics?.Report(
                        "AEC MODEL DATA INCOMPLETE ROOT STALL TOLERATED" + Environment.NewLine +
                        "Root manifest: " + (rootStatus.Length == 0 ? "<unknown>" : rootStatus) + " / " +
                        (rootProgress.Length == 0 ? "<unknown>" : rootProgress) + Environment.NewLine +
                        "Unchanged for: " + stalledFor.TotalMinutes.ToString("0.0") + " minutes" + Environment.NewLine +
                        "Decision: continue waiting because Autodesk has not returned a terminal status." + Environment.NewLine +
                        "Remaining total budget: " + Math.Max(0, (deadlineUtc - now).TotalMinutes).ToString("0.0") +
                        " minutes." + Environment.NewLine +
                        "Published version and coordinate formula remain unchanged.");
                }

                // Poll gently while translation is clearly still running, and
                // quickly once the root is complete and only consistency remains.
                var delaySeconds = rootComplete ? AecModelDataMinDelaySeconds : AecModelDataMaxDelaySeconds;
                diagnostics?.Report("Autodesk.AEC.ModelData is not downloadable yet; retrying in " + delaySeconds + " seconds." +
                                    Environment.NewLine + "Root manifest: " +
                                    (rootStatus.Length == 0 ? "<unknown>" : rootStatus) + " / " +
                                    (rootProgress.Length == 0 ? "<unknown>" : rootProgress) +
                                    Environment.NewLine + "Elapsed: " + (now - startedUtc).TotalSeconds.ToString("0") + "s" +
                                    Environment.NewLine + "Reason: " + lastFailure);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }

            if (transform == null || transform.Count < 12)
                throw new TimeoutException(
                    "Autodesk.AEC.ModelData did not become downloadable with a complete refPointTransformation[12] " +
                    "after " + attempt + " attempts over " +
                    (DateTime.UtcNow - startedUtc).TotalMinutes.ToString("0") + " minutes. " + lastFailure + " " +
                    "The published model version was preserved; retry Issue creation to reuse it without changing the coordinate formula.");

            var result = new ViewerCoordinateTransform
            {
                UnitScaleToMeters = RevitViewerUnitScaleToMeters,
                // refPointTransformation is a Matrix4x3 serialized in Three.js
                // column-major order. Convert it to ordinary row/column fields.
                TransformM00 = ReadFiniteDouble(transform[0], "refPointTransformation[0]"),
                TransformM01 = ReadFiniteDouble(transform[3], "refPointTransformation[3]"),
                TransformM02 = ReadFiniteDouble(transform[6], "refPointTransformation[6]"),
                TransformM10 = ReadFiniteDouble(transform[1], "refPointTransformation[1]"),
                TransformM11 = ReadFiniteDouble(transform[4], "refPointTransformation[4]"),
                TransformM12 = ReadFiniteDouble(transform[7], "refPointTransformation[7]"),
                TransformM20 = ReadFiniteDouble(transform[2], "refPointTransformation[2]"),
                TransformM21 = ReadFiniteDouble(transform[5], "refPointTransformation[5]"),
                TransformM22 = ReadFiniteDouble(transform[8], "refPointTransformation[8]"),
                GlobalOffsetX = ReadFiniteDouble(transform[9], "refPointTransformation[9]"),
                GlobalOffsetY = ReadFiniteDouble(transform[10], "refPointTransformation[10]"),
                GlobalOffsetZ = ReadFiniteDouble(transform[11], "refPointTransformation[11]"),
                Source = "Autodesk.AEC.ModelData.refPointTransformation[0..11]"
            };
            if (Math.Abs(result.Determinant) <= 0.000000000001)
                throw new InvalidOperationException(
                    "Autodesk.AEC.ModelData refPointTransformation has a singular 3x3 linear matrix.");
            diagnostics?.Report("APS VIEWER COORDINATE TRANSFORM" + Environment.NewLine +
                                "Internal unit: international foot" + Environment.NewLine +
                                "unitScale (metres per Viewer unit): " + result.UnitScaleToMeters.ToString("0.############") + Environment.NewLine +
                                "globalOffset (Viewer internal units): X=" + result.GlobalOffsetX.ToString("0.######") +
                                "; Y=" + result.GlobalOffsetY.ToString("0.######") +
                                "; Z=" + result.GlobalOffsetZ.ToString("0.######") + Environment.NewLine +
                                "linear matrix: [" +
                                result.TransformM00.ToString("0.############") + ", " + result.TransformM01.ToString("0.############") + ", " + result.TransformM02.ToString("0.############") + "; " +
                                result.TransformM10.ToString("0.############") + ", " + result.TransformM11.ToString("0.############") + ", " + result.TransformM12.ToString("0.############") + "; " +
                                result.TransformM20.ToString("0.############") + ", " + result.TransformM21.ToString("0.############") + ", " + result.TransformM22.ToString("0.############") + "]" + Environment.NewLine +
                                "determinant: " + result.Determinant.ToString("0.############") + Environment.NewLine +
                                "Mapping: inverse(linear matrix) * (bboxCenter / unitScale - globalOffset)" + Environment.NewLine +
                                "Revit-coordinate fallback: not used");
            return result;
        }

        /// <summary>
        /// A viewable absent from a completed manifest is deterministic, but the
        /// manifest can still be briefly inconsistent right after root success.
        /// Half a minute is enough to rule that out; three minutes is not needed
        /// and only delays an error the user must act on.
        /// </summary>
        private const int MissingViewableConfirmationSeconds = 30;

        private static string BuildMissingViewableMessage(string preferredView, RequiredViewableReadiness readiness)
        {
            var setName = string.IsNullOrWhiteSpace(readiness?.PublishSetHint)
                ? "the active publish set"
                : "publish set '" + readiness.PublishSetHint + "'";
            return "Autodesk finished translating the model, but the 3D view '" + preferredView +
                   "' was not published and therefore cannot carry an Issue pushpin." + Environment.NewLine +
                   "Autodesk published these 3D views instead: " + Safe(readiness?.AvailableViewNames) + "." + Environment.NewLine +
                   Environment.NewLine +
                   "This means '" + preferredView + "' is not included in " + setName + "." + Environment.NewLine +
                   "Fix: Revit > Collaborate > Publish Settings, add '" + preferredView + "' to " + setName +
                   ", save the settings, synchronize, then run Issue creation again.";
        }

        private static bool IsTemporaryApsStatus(int statusCode)
        {
            return statusCode == 202 || statusCode == 204 || statusCode == 404 ||
                   statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }

        /// <summary>
        /// Reads a finite double out of a JSON token without going through text.
        /// <para>
        /// The previous implementation called <c>token.ToString()</c> and parsed the
        /// result with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
        /// <c>JValue.ToString()</c> formats with the CURRENT culture, so on any machine
        /// whose regional format uses a comma as the decimal separator a perfectly
        /// valid value such as 0.4923634128912784 was rendered "0,4923634128912784"
        /// and then rejected by the invariant parser, because
        /// <see cref="System.Globalization.NumberStyles.Float"/> does not permit group
        /// separators. Every refPointTransformation on such a machine failed at
        /// element [0]. It also lost precision: on .NET Framework the default double
        /// formatting is G15, not round-trip.
        /// </para>
        /// <para>
        /// Numeric tokens are therefore converted natively, which is culture-independent
        /// and lossless. Only genuinely textual tokens fall back to parsing, invariant
        /// first and then the current culture.
        /// </para>
        /// </summary>
        private static double ReadFiniteDouble(JToken token, string name)
        {
            double value;
            if (!TryReadFiniteDouble(token, out value))
                throw new InvalidOperationException(
                    "Invalid " + name + " in Autodesk.AEC.ModelData. Token type: " +
                    (token == null ? "<null>" : token.Type.ToString()) +
                    ", raw value: " + DescribeToken(token) + ".");
            return value;
        }

        private static bool TryReadFiniteDouble(JToken token, out double value)
        {
            value = 0d;
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return false;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                try { value = (double)token; }
                catch (Exception) { return false; }
            }
            else if (token.Type == JTokenType.String)
            {
                var text = ((string)token ?? "").Trim();
                if (text.Length == 0) return false;
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value) &&
                    !double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture, out value))
                    return false;
            }
            else
            {
                return false;
            }

            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string DescribeToken(JToken token)
        {
            if (token == null) return "<null>";
            var text = token.ToString(Newtonsoft.Json.Formatting.None);
            return text.Length > 64 ? text.Substring(0, 64) + "..." : text;
        }

        private async Task<ApsHttpResult> SendWithStatusAsync(HttpMethod method, string url, string json, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics, string operation)
        {
            var forcedRefreshAttempted = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await token.EnsureFreshAsync(ct, diagnostics).ConfigureAwait(false);
                var usedAccessToken = token.AccessToken;
                var requestUrls = BuildRegionalRequestUrls(url).ToList();
                var retryWithFreshToken = false;
                ApsHttpResult last = null;
                ApsHttpResult bestTemporary = null;
                foreach (var regional in requestUrls)
                {
                    using (var req = CreateRequest(method, regional.Url, json, token))
                    {
                        LogRequest(req, method, regional.Url, json, diagnostics, operation + " (attempt " + (attempt + 1) + "/2, region " + regional.Region + ")");
                        using (var response = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var statusCode = (int)response.StatusCode;
                            diagnostics?.Report("APS RESPONSE" + Environment.NewLine + "Operation: " + operation + Environment.NewLine + "Region: " + regional.Region + Environment.NewLine + "HTTP " + statusCode + Environment.NewLine + "Body:" + Environment.NewLine + body);
                            PluginLog.Info("APS publication response", new { operation, region = regional.Region, status = statusCode, url = regional.Url, body, attempt = attempt + 1 });
                            if (statusCode == 401 && attempt == 0 && !forcedRefreshAttempted && IsExpiredApsTokenResponse(body))
                            {
                                forcedRefreshAttempted = true;
                                if (await token.EnsureFreshAsync(ct, diagnostics, true, usedAccessToken).ConfigureAwait(false))
                                {
                                    retryWithFreshToken = true;
                                    break;
                                }
                                diagnostics?.Report("APS REGIONAL ROUTE REJECTED\nRegion: " + regional.Region +
                                                    "\nHTTP 401; token was not rotated. Continuing with the remaining routes.");
                                last = new ApsHttpResult { StatusCode = statusCode, Body = body };
                                continue;
                            }
                            last = new ApsHttpResult { StatusCode = statusCode, Body = body };
                            if (IsTemporaryApsStatus(statusCode))
                            {
                                if (bestTemporary == null || statusCode == 404 || statusCode == 202)
                                    bestTemporary = last;
                                // An authoritative route answering 404/202 has given a
                                // real answer: the resource exists here but is not ready.
                                // Probing other regions cannot improve on that and only
                                // generates 401 noise, so return immediately.
                                if (regional.IsAuthoritative)
                                {
                                    diagnostics?.Report("APS AUTHORITATIVE ROUTE NOT READY" + Environment.NewLine +
                                                        "Region: " + regional.Region + Environment.NewLine +
                                                        "HTTP " + statusCode + Environment.NewLine +
                                                        "The regional sweep is skipped; the caller will retry this route.");
                                    return last;
                                }
                                if (requestUrls.Count > 1) continue;
                            }
                            if ((statusCode == 401 || statusCode == 403) && requestUrls.Count > 1)
                            {
                                diagnostics?.Report("APS REGIONAL ROUTE REJECTED\nRegion: " + regional.Region +
                                                    "\nHTTP " + statusCode + "; continuing with the remaining routes.");
                                continue;
                            }
                            if ((statusCode < 200 || statusCode >= 300) && requestUrls.Count > 1)
                            {
                                diagnostics?.Report("APS REGIONAL ROUTE REJECTED\nRegion: " + regional.Region +
                                                    "\nHTTP " + statusCode + "; continuing with the remaining routes.");
                                continue;
                            }
                            if (statusCode >= 200 && statusCode < 300 && regional.Region != "AUTO")
                            {
                                var urnKey = ExtractDerivativeUrnKey(url);
                                if (!string.IsNullOrWhiteSpace(urnKey)) _regionByUrn[urnKey] = regional.Region;
                            }
                            return last;
                        }
                    }
                }
                if (retryWithFreshToken) continue;
                if (bestTemporary != null) return bestTemporary;
                if (requestUrls.Count > 1 && last != null && (last.StatusCode < 200 || last.StatusCode >= 300))
                {
                    diagnostics?.Report(
                        "APS REGIONAL SWEEP INCONCLUSIVE\nEvery regional derivative route returned a negative response in this pass. " +
                        "The publication workflow will wait and retry the complete sweep instead of failing.");
                    return new ApsHttpResult
                    {
                        StatusCode = 202,
                        Body = "Regional derivative discovery is temporarily inconclusive after a complete authorization sweep."
                    };
                }
                if (last != null) return last;
            }
            return new ApsHttpResult { StatusCode = 503, Body = operation + " exhausted all Autodesk routes without a response." };
        }

        /// <summary>
        /// Builds the ordered list of regional routes for one derivative request.
        /// <para>
        /// When Data Management has already told us which region hosts the
        /// derivative, that route is authoritative and no fallbacks are emitted.
        /// Sweeping the other seven regions after an authoritative answer produced
        /// seven guaranteed 401s per attempt and buried the real response in noise.
        /// Fallbacks remain only for the genuinely unknown case.
        /// </para>
        /// </summary>
        private IEnumerable<RegionalRequest> BuildRegionalRequestUrls(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                url.IndexOf("/modelderivative/v2/", StringComparison.OrdinalIgnoreCase) < 0 ||
                url.IndexOf("/designdata/", StringComparison.OrdinalIgnoreCase) < 0 ||
                url.IndexOf("/regions/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("region=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                yield return new RegionalRequest { Region = "AUTO", Url = url };
                yield break;
            }

            var urnKey = ExtractDerivativeUrnKey(url);
            DerivativeRoute route;
            if (!string.IsNullOrWhiteSpace(urnKey) && _routeByUrn.TryGetValue(urnKey, out route))
            {
                yield return new RegionalRequest
                {
                    Region = route.Region,
                    Url = BuildAuthoritativeDerivativeUrl(url, route),
                    IsAuthoritative = true
                };
                // Fallbacks are emitted lazily: SendWithStatusAsync only consumes
                // them if the authoritative route answers 401/403, which is the
                // one case where the recorded region may be wrong. A 404 or 202
                // from the authoritative route means "not ready yet" and is
                // returned as-is.
                foreach (var fallbackRegion in AutodeskDataRegions
                    .Where(x => !string.Equals(x, route.Region, StringComparison.OrdinalIgnoreCase)))
                    yield return new RegionalRequest
                    {
                        Region = fallbackRegion,
                        Url = BuildRoutedDerivativeUrl(url, fallbackRegion, route.Query)
                    };
                yield break;
            }
            string cached;
            var regions = _regionByUrn.TryGetValue(urnKey ?? "", out cached)
                ? new[] { cached }.Concat(AutodeskDataRegions)
                : AutodeskDataRegions.AsEnumerable();
            foreach (var region in regions.Distinct(StringComparer.OrdinalIgnoreCase))
                yield return new RegionalRequest
                {
                    Region = region,
                    Url = BuildRoutedDerivativeUrl(url, region, "")
                };
        }

        private void RegisterDerivativeRoute(ApsModelItemContext version, string derivativeUrn, IProgress<string> diagnostics)
        {
            if (version == null || string.IsNullOrWhiteSpace(derivativeUrn) ||
                string.IsNullOrWhiteSpace(version.DerivativeManifestUrl)) return;
            _routeByUrn[derivativeUrn] = new DerivativeRoute
            {
                Region = string.IsNullOrWhiteSpace(version.DerivativeRegion) ? "AUTO" : version.DerivativeRegion,
                Query = version.DerivativeScopes,
                ManifestUrl = version.DerivativeManifestUrl
            };
            if (!string.IsNullOrWhiteSpace(version.DerivativeRegion))
                _regionByUrn[derivativeUrn] = version.DerivativeRegion;
            diagnostics?.Report("APS AUTHORITATIVE DERIVATIVE ROUTE\nRegion: " + Safe(version.DerivativeRegion) +
                                "\nSource: Data Management relationships.derivatives.meta.link.href" +
                                "\nScopes preserved: " + (!string.IsNullOrWhiteSpace(version.DerivativeScopes) ? "yes" : "no"));
        }

        private static string BuildRoutedDerivativeUrl(string url, string region, string authoritativeQuery)
        {
            Uri source;
            if (!Uri.TryCreate(url, UriKind.Absolute, out source)) return url;
            const string marker = "/designdata/";
            var markerIndex = source.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return url;
            var urnStart = markerIndex + marker.Length;
            var suffixStart = source.AbsolutePath.IndexOf('/', urnStart);
            if (suffixStart < 0) suffixStart = source.AbsolutePath.Length;
            var encodedUrn = source.AbsolutePath.Substring(urnStart, suffixStart - urnStart);
            var suffix = source.AbsolutePath.Substring(suffixStart);
            var prefix = source.GetLeftPart(UriPartial.Authority) + "/modelderivative/v2";
            if (!string.IsNullOrWhiteSpace(region) && !string.Equals(region, "AUTO", StringComparison.OrdinalIgnoreCase))
                prefix += "/regions/" + Uri.EscapeDataString(region.ToLowerInvariant());
            var query = MergeQuery(authoritativeQuery, source.Query);
            return prefix + "/designdata/" + encodedUrn + suffix + query;
        }

        private static string BuildAuthoritativeDerivativeUrl(string requestedUrl, DerivativeRoute route)
        {
            Uri source;
            if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out source)) return requestedUrl;
            var path = source.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(source.Query) && !string.IsNullOrWhiteSpace(route.ManifestUrl))
                return route.ManifestUrl;
            return BuildRoutedDerivativeUrl(requestedUrl, route.Region, route.Query);
        }

        private static string MergeQuery(string authoritativeQuery, string requestQuery)
        {
            var values = new List<string>();
            foreach (var raw in new[] { authoritativeQuery, requestQuery })
            {
                var value = (raw ?? "").Trim().TrimStart('?');
                if (value.Length > 0) values.AddRange(value.Split('&').Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            var unique = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return unique.Count == 0 ? "" : "?" + string.Join("&", unique);
        }

        private static string ExtractDerivativeUrnKey(string url)
        {
            const string marker = "/designdata/";
            var start = (url ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += marker.Length;
            var end = url.IndexOf('/', start);
            if (end < 0) end = url.IndexOf('?', start);
            if (end < 0) end = url.Length;
            return Uri.UnescapeDataString(url.Substring(start, end - start));
        }

        /// <summary>
        /// Distinguishes a genuinely expired token from an unrelated 401.
        /// <para>
        /// Autodesk answers 401 with an EMPTY body when a derivative is requested
        /// from a region that does not host it. Treating that as an expiry used to
        /// trigger a pointless token refresh on every wrong-region probe, and made
        /// the log claim AUTH-006 while the token was perfectly valid. An empty
        /// body is therefore not evidence of expiry.
        /// </para>
        /// </summary>
        private static bool IsExpiredApsTokenResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            return body.IndexOf("AUTH-006", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("invalid or expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   body.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url, string json, ApsTokenResponse token)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue(string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType, token.AccessToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            if (json != null)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                req.Content = new ByteArrayContent(bytes);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.api+json");
            }
            return req;
        }

        private static void LogRequest(HttpRequestMessage req, HttpMethod method, string url, string json, IProgress<string> diagnostics, string operation)
        {
            var contentType = req.Content?.Headers?.ContentType?.ToString() ?? "<none>";
            var accept = string.Join(", ", req.Headers.Accept.Select(x => x.ToString()));
            diagnostics?.Report("APS REQUEST" + Environment.NewLine + "Operation: " + operation + Environment.NewLine + method + " " + url + Environment.NewLine + "Accept: " + accept + Environment.NewLine + "Content-Type: " + contentType + (json == null ? "" : Environment.NewLine + "Body:" + Environment.NewLine + json));
            PluginLog.Info("APS publication request", new { operation, method = method.Method, url, accept, contentType, body = json });
        }

        private async Task<string> SendAsync(HttpMethod method, string url, string json, ApsTokenResponse token, CancellationToken ct, IProgress<string> diagnostics, string operation)
        {
            var response = await SendWithStatusAsync(method, url, json, token, ct, diagnostics, operation).ConfigureAwait(false);
            if (response.StatusCode < 200 || response.StatusCode >= 300)
                throw new InvalidOperationException(operation + " HTTP " + response.StatusCode + ": " + response.Body);
            return response.Body;
        }

        private static ApsModelItemContext ParseVersion(JObject version)
        {
            var created = (DateTime?)version["attributes"]?["createTime"];
            var derivativeLink = (string)version["relationships"]?["derivatives"]?["meta"]?["link"]?["href"] ?? "";
            var derivativeUrn = ExtractDerivativeUrnKey(derivativeLink);
            var derivativeRegion = ExtractRegion(derivativeLink);
            var derivativeScopes = ExtractQuery(derivativeLink);
            return new ApsModelItemContext
            {
                VersionId = (string)version["id"] ?? "",
                VersionNumber = (int?)version["attributes"]?["versionNumber"] ?? 0,
                VersionCreatedAt = created,
                DerivativeUrn = derivativeUrn,
                DerivativeManifestUrl = derivativeLink,
                DerivativeRegion = derivativeRegion,
                DerivativeScopes = derivativeScopes
            };
        }
        private static string ResolveCurrentVersionDerivativeUrn(ApsModelItemContext version)
        {
            if (version == null || string.IsNullOrWhiteSpace(version.VersionId))
                throw new InvalidOperationException("The selected Autodesk Docs version does not contain a version URN.");
            var expected = ToBase64Urn(version.VersionId).TrimEnd('=');
            var linked = (version.DerivativeUrn ?? "").Trim().TrimEnd('=');
            if (linked.Length == 0) return expected;
            if (!string.Equals(linked, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The derivative link returned by Autodesk does not belong to the selected current model version. " +
                    "Issue creation was stopped before freezing the operation context.");
            return linked;
        }
        private static string ExtractRegion(string url)
        {
            const string marker = "/regions/";
            var start = (url ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += marker.Length;
            var end = url.IndexOf('/', start);
            if (end < 0) end = url.Length;
            return url.Substring(start, end - start).ToUpperInvariant();
        }
        private static string ExtractQuery(string url)
        {
            var index = (url ?? "").IndexOf('?');
            return index < 0 || index + 1 >= url.Length ? "" : url.Substring(index + 1);
        }
        private static string ToBase64Urn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        private static void ValidateToken(ApsTokenResponse token)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) throw new InvalidOperationException("APS access token is empty.");
        }
        private static string Esc(string s) => Uri.EscapeDataString(s ?? "");
        private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? "not available" : s;
        private static string First(params string[] v) => v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        private static bool GuidEquals(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim().Trim('{', '}'), b.Trim().Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
        }
        private static bool FileNameEquals(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var x = a.Trim(); var y = b.Trim();
            if (!x.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) x += ".rvt";
            if (!y.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) y += ".rvt";
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }
        private sealed class ApsHttpResult { public int StatusCode; public string Body; }
        private sealed class RegionalRequest
        {
            public string Region;
            public string Url;
            // True when the region came from Data Management's own
            // relationships.derivatives.meta.link.href, i.e. Autodesk told us
            // where the derivative lives. Such a route's answer is final unless
            // it is an authorization failure.
            public bool IsAuthoritative;
        }
        private sealed class DerivativeRoute { public string Region; public string Query; public string ManifestUrl; }
        private sealed class ViewerCoordinateTransform
        {
            public double UnitScaleToMeters;
            public double GlobalOffsetX;
            public double GlobalOffsetY;
            public double GlobalOffsetZ;
            public double TransformM00;
            public double TransformM01;
            public double TransformM02;
            public double TransformM10;
            public double TransformM11;
            public double TransformM12;
            public double TransformM20;
            public double TransformM21;
            public double TransformM22;
            public double Determinant =>
                TransformM00 * (TransformM11 * TransformM22 - TransformM12 * TransformM21) -
                TransformM01 * (TransformM10 * TransformM22 - TransformM12 * TransformM20) +
                TransformM02 * (TransformM10 * TransformM21 - TransformM11 * TransformM20);
            public string Source;
        }
        private sealed class Viewable
        {
            public string GeometryGuid;
            public string ViewableId;
            public string ModelPropertiesGuid;
            public string Name;
            public double UnitScaleToMeters;
            public double GlobalOffsetX;
            public double GlobalOffsetY;
            public double GlobalOffsetZ;
            public double TransformM00;
            public double TransformM01;
            public double TransformM02;
            public double TransformM10;
            public double TransformM11;
            public double TransformM12;
            public double TransformM20;
            public double TransformM21;
            public double TransformM22;
            public string TransformSource;
        }
        private sealed class ViewableIdentity
        {
            public string GeometryGuid;
            public string ViewableId;
            public string ModelPropertiesGuid;
        }
        private sealed class RequiredViewableReadiness
        {
            public bool IsReady;
            public bool HasTerminalFailure;
            // The manifest declares an SVF2 (OTG) derivative. ACC opens that one.
            public bool Svf2Declared;
            // SVF2 finished. Until then the Viewer probe would fall back to SVF,
            // whose dbIds belong to a different id space than the scene ACC loads.
            public bool Svf2Ready;
            // True when the named 3D geometry node is absent from the manifest
            // entirely. Once the root manifest reports success this is a
            // deterministic, terminal condition: the view is not part of the
            // Revit publish set. It is NOT eventual consistency.
            public bool ViewableMissing;
            // Name of the Revit publish set observed on sibling viewables,
            // used to tell the user exactly where to add the missing view.
            public string PublishSetHint;
            // Names of the 3D viewables Autodesk actually published.
            public string AvailableViewNames;
            public string Summary;
        }
        public void Dispose() { _http?.Dispose(); }
    }
}
