using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BuildAI.Core.Issues;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.ViewerProbe
{
    public sealed class ViewerCoordinateProbeRequest
    {
        public ApsPushpinContext Context { get; set; }
        public string ResultKey { get; set; } = "";
        public string RequestedExternalId { get; set; } = "";
        public string CompositeExternalId { get; set; } = "";
        public string SelectedLinkInstanceUid { get; set; } = "";
        public string SelectedModelUid { get; set; } = "";
        public string SourceName { get; set; } = "";
        public int ElementId { get; set; }
        public string SecondaryRequestedExternalId { get; set; } = "";
        public string SecondaryLinkInstanceUid { get; set; } = "";
        public string SecondaryModelUid { get; set; } = "";
        public string SecondarySourceName { get; set; } = "";
        public int SecondaryElementId { get; set; }
        public bool PreferSurfacePairAnchor { get; set; }
        public bool PreferIntersectionAnchor { get; set; }
        public bool HighlightSecondary { get; set; }
        public ViewerProbePoint PreferredModelPoint { get; set; }

        internal JObject ToBrowserPayload()
        {
            return new JObject
            {
                ["resultKey"] = ResultKey ?? "",
                ["derivativeUrn"] = Context?.DerivativeUrn ?? "",
                ["viewableGeometryGuid"] = Context?.ViewableGeometryGuid ?? "",
                ["viewableId"] = Context?.ViewableId ?? "",
                ["viewableName"] = Context?.ViewableName ?? "",
                ["derivativeRegion"] = Context?.DerivativeRegion ?? "",
                ["requestedExternalId"] = RequestedExternalId ?? "",
                ["compositeExternalId"] = CompositeExternalId ?? "",
                ["selectedLinkInstanceUid"] = SelectedLinkInstanceUid ?? "",
                ["selectedModelUid"] = SelectedModelUid ?? "",
                ["sourceName"] = SourceName ?? "",
                ["elementId"] = ElementId,
                ["secondaryRequestedExternalId"] = SecondaryRequestedExternalId ?? "",
                ["secondaryLinkInstanceUid"] = SecondaryLinkInstanceUid ?? "",
                ["secondaryModelUid"] = SecondaryModelUid ?? "",
                ["secondarySourceName"] = SecondarySourceName ?? "",
                ["secondaryElementId"] = SecondaryElementId,
                ["preferSurfacePairAnchor"] = PreferSurfacePairAnchor,
                ["preferIntersectionAnchor"] = PreferIntersectionAnchor,
                ["highlightSecondary"] = HighlightSecondary,
                ["viewerUnitScaleToMeters"] = Context?.ViewerUnitScaleToMeters ?? 0.0,
                ["preferredModelPoint"] = PreferredModelPoint == null ? null : JObject.FromObject(PreferredModelPoint)
            };
        }

        internal void Validate()
        {
            if (Context == null || !Context.IsUsable)
                throw new InvalidOperationException("Viewer Coordinate Probe requires a validated APS pushpin context.");
            if (string.IsNullOrWhiteSpace(Context.DerivativeUrn))
                throw new InvalidOperationException("Viewer Coordinate Probe requires the exact Model Derivative URN.");
            if (string.IsNullOrWhiteSpace(Context.ViewableGeometryGuid) || string.IsNullOrWhiteSpace(Context.ViewableId))
                throw new InvalidOperationException("Viewer Coordinate Probe requires the exact geometry GUID and viewableID.");
            if (string.IsNullOrWhiteSpace(CompositeExternalId) && string.IsNullOrWhiteSpace(RequestedExternalId))
                throw new InvalidOperationException("Viewer Coordinate Probe requires an externalId for deterministic object lookup.");
        }
    }

    public sealed class ViewerProbePoint
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("z")] public double Z { get; set; }

        public bool IsFinite =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z);
    }

    public sealed class ViewerCoordinateProbeResult
    {
        [JsonProperty("dbId")] public int DbId { get; set; }
        [JsonProperty("resolvedExternalId")] public string ResolvedExternalId { get; set; } = "";
        [JsonProperty("mappingDirection")] public string MappingDirection { get; set; } = "";
        [JsonProperty("fragmentIds")] public int[] FragmentIds { get; set; } = Array.Empty<int>();
        [JsonProperty("boundsMin")] public ViewerProbePoint BoundsMin { get; set; }
        [JsonProperty("boundsMax")] public ViewerProbePoint BoundsMax { get; set; }
        [JsonProperty("center")] public ViewerProbePoint Center { get; set; }
        [JsonProperty("anchor")] public ViewerProbePoint Anchor { get; set; }
        [JsonProperty("anchorMethod")] public string AnchorMethod { get; set; } = "";
        [JsonProperty("primarySurfacePoint")] public ViewerProbePoint PrimarySurfacePoint { get; set; }
        [JsonProperty("secondarySurfacePoint")] public ViewerProbePoint SecondarySurfacePoint { get; set; }
        [JsonProperty("sampledTriangleCount")] public int SampledTriangleCount { get; set; }
        [JsonProperty("pushpinValidationOutcome")] public string PushpinValidationOutcome { get; set; } = "";
        [JsonProperty("insidePrimary")] public bool? InsidePrimary { get; set; }
        [JsonProperty("insideSecondary")] public bool? InsideSecondary { get; set; }
        [JsonProperty("correctionApplied")] public bool CorrectionApplied { get; set; }
        [JsonProperty("correctionDistanceMm")] public double CorrectionDistanceMm { get; set; }
        [JsonProperty("cameraQuality")] public string CameraQuality { get; set; } = "";
        /// <summary>
        /// Bounds of the paired element. Without them an AR-ST anchor cannot be
        /// reasoned about from a log: whether a surface point lies on the secondary
        /// element or merely coincides with the primary's own face is undecidable,
        /// and the AABB overlap of the pair cannot be computed at all.
        /// </summary>
        [JsonProperty("secondaryBoundsMin")] public ViewerProbePoint SecondaryBoundsMin { get; set; }
        [JsonProperty("secondaryBoundsMax")] public ViewerProbePoint SecondaryBoundsMax { get; set; }

        [JsonProperty("secondaryDbId")] public int SecondaryDbId { get; set; }
        [JsonProperty("secondaryLoadedModelId")] public string SecondaryLoadedModelId { get; set; } = "";
        [JsonProperty("secondaryResolvedExternalId")] public string SecondaryResolvedExternalId { get; set; } = "";
        [JsonProperty("secondaryFragmentIds")] public int[] SecondaryFragmentIds { get; set; } = Array.Empty<int>();
        [JsonProperty("viewerState")] public JObject ViewerState { get; set; }
        [JsonProperty("stateGlobalOffset")] public JToken StateGlobalOffset { get; set; }
        [JsonProperty("modelGlobalOffset")] public ViewerProbePoint ModelGlobalOffset { get; set; }
        [JsonProperty("modelToViewerTransform")] public double[] ModelToViewerTransform { get; set; }
        [JsonProperty("placementTransform")] public double[] PlacementTransform { get; set; }
        [JsonProperty("loadedViewableGuid")] public string LoadedViewableGuid { get; set; } = "";
        [JsonProperty("loadedViewableId")] public string LoadedViewableId { get; set; } = "";
        /// <summary>Data-residency region the probe loaded the derivative from.</summary>
        [JsonProperty("derivativeRegion")] public string DerivativeRegion { get; set; } = "";
        /// <summary>Model Derivative host the probe used. Must match the region ACC serves the model from.</summary>
        [JsonProperty("derivativeEndpoint")] public string DerivativeEndpoint { get; set; } = "";
        [JsonProperty("apiFlavor")] public string ApiFlavor { get; set; } = "";
        /// <summary>True when the probe loaded the SVF2/OTG derivative, which is what ACC opens.</summary>
        [JsonProperty("isOtg")] public bool IsOtg { get; set; }
        /// <summary>The document exposes an SVF2 (OTG) graphics node for this viewable.</summary>
        [JsonProperty("otgNodeFound")] public bool OtgNodeFound { get; set; }
        [JsonProperty("otgStatus")] public string OtgStatus { get; set; } = "";
        /// <summary>Scene fingerprint: compare with the ACC session to prove both loaded the same derivative.</summary>
        [JsonProperty("sceneFragmentCount")] public int SceneFragmentCount { get; set; } = -1;
        [JsonProperty("sceneNodeCount")] public int SceneNodeCount { get; set; } = -1;
        [JsonProperty("loadedViewableName")] public string LoadedViewableName { get; set; } = "";
        [JsonProperty("loadedModelId")] public string LoadedModelId { get; set; } = "";
        [JsonProperty("loadedModelUrn")] public string LoadedModelUrn { get; set; } = "";
        [JsonProperty("sceneModelCount")] public int SceneModelCount { get; set; }

        /// <summary>
        /// The probe centre expressed in the model's own coordinate frame, i.e.
        /// with the viewer's load-time <c>globalOffset</c> added back.
        /// <para>
        /// <see cref="Center"/> is viewer-local. globalOffset is not a property of
        /// the model: the viewer picks it when it loads, from what it loaded. Two
        /// sessions that load different viewables, or load with different options,
        /// get different offsets, so a viewer-local coordinate is only meaningful
        /// inside the session that produced it. Anything persisted for another
        /// session to read has to be offset-independent.
        /// </para>
        /// <para>
        /// Returns null when the viewer reported no offset, in which case the two
        /// frames coincide and <see cref="Center"/> is already global.
        /// </para>
        /// </summary>
        public ViewerProbePoint GlobalCenter
        {
            get
            {
                if (Center == null || !Center.IsFinite) return null;
                if (ModelGlobalOffset == null || !ModelGlobalOffset.IsFinite) return Center;
                return new ViewerProbePoint
                {
                    X = Center.X + ModelGlobalOffset.X,
                    Y = Center.Y + ModelGlobalOffset.Y,
                    Z = Center.Z + ModelGlobalOffset.Z
                };
            }
        }

        public ViewerProbePoint GlobalAnchor
        {
            get
            {
                if (Anchor == null || !Anchor.IsFinite) return null;
                if (ModelGlobalOffset == null || !ModelGlobalOffset.IsFinite) return Anchor;
                return new ViewerProbePoint
                {
                    X = Anchor.X + ModelGlobalOffset.X,
                    Y = Anchor.Y + ModelGlobalOffset.Y,
                    Z = Anchor.Z + ModelGlobalOffset.Z
                };
            }
        }

        public bool IsValid =>
            DbId > 0 && !string.IsNullOrWhiteSpace(ResolvedExternalId) &&
            Center != null && Center.IsFinite &&
            Anchor != null && Anchor.IsFinite && !string.IsNullOrWhiteSpace(AnchorMethod) &&
            BoundsMin != null && BoundsMin.IsFinite &&
            BoundsMax != null && BoundsMax.IsFinite &&
            FragmentIds != null && FragmentIds.Length > 0 && ViewerState != null;

        public string ToDiagnosticText()
        {
            return "VIEWER COORDINATE PROBE RESULT" +
                   "\nRuntime dbId used for Issue objectId/objectSet: " + DbId +
                   "\nResolved externalId: " + ResolvedExternalId +
                   "\nMapping direction: " + MappingDirection +
                   "\nLoaded viewable GUID: " + LoadedViewableGuid +
                   "\nLoaded viewableID: " + LoadedViewableId +
                   "\nLoaded viewable name: " + LoadedViewableName +
                   "\nDerivative region: " + (string.IsNullOrWhiteSpace(DerivativeRegion) ? "<default us>" : DerivativeRegion) +
                   "\nDerivative endpoint: " + DerivativeEndpoint + " (" + ApiFlavor + ")" +
                   "\nSVF2/OTG scene: " + (IsOtg ? "yes" : "NO") + " (otg node: " + (OtgNodeFound ? "found" : "missing") +
                   (string.IsNullOrWhiteSpace(OtgStatus) ? "" : ", status " + OtgStatus) + ")" +
                   "\nScene fingerprint: fragments=" + SceneFragmentCount + ", nodes=" + SceneNodeCount +
                   "\nScene model count: " + SceneModelCount +
                   "\nMatched scene model id: " + LoadedModelId +
                   "\nMatched scene model URN: " + LoadedModelUrn +
                   "\nSecondary dbId: " + SecondaryDbId +
                   "\nSecondary scene model id: " + SecondaryLoadedModelId +
                   "\nFragment count: " + (FragmentIds?.Length ?? 0) +
                   "\nFragment ids: " + string.Join(",", FragmentIds ?? Array.Empty<int>()) +
                   "\nWorld bounds min: X=" + BoundsMin.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + BoundsMin.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + BoundsMin.Z.ToString("0.############", CultureInfo.InvariantCulture) +
                   "\nWorld bounds max: X=" + BoundsMax.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + BoundsMax.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + BoundsMax.Z.ToString("0.############", CultureInfo.InvariantCulture) +
                   "\nWorld bounds centre (viewer-local): X=" + Center.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + Center.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + Center.Z.ToString("0.############", CultureInfo.InvariantCulture) +
                   "\nPushpin anchor method: " + AnchorMethod +
                   "\nPushpin anchor (viewer-local): X=" + Anchor.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + Anchor.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + Anchor.Z.ToString("0.############", CultureInfo.InvariantCulture) +
                   "\nPushpin anchor (model frame): " + (GlobalAnchor == null ? "<unavailable>" :
                       "X=" + GlobalAnchor.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + GlobalAnchor.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + GlobalAnchor.Z.ToString("0.############", CultureInfo.InvariantCulture)) +
                   "\nPrimary surface point: " + FormatPoint(PrimarySurfacePoint) +
                   "\nSecondary surface point: " + FormatPoint(SecondarySurfacePoint) +
                   "\nSecondary bounds min: " + FormatOptionalPoint(SecondaryBoundsMin) +
                   "\nSecondary bounds max: " + FormatOptionalPoint(SecondaryBoundsMax) +
                   "\nSecondary runtime dbId: " + SecondaryDbId +
                   "\nSecondary resolved externalId: " + SecondaryResolvedExternalId +
                   "\nSampled triangle count: " + SampledTriangleCount +
                   "\nPushpin validation: " + PushpinValidationOutcome +
                   " (inside primary=" + (InsidePrimary?.ToString() ?? "unknown") +
                   ", inside secondary=" + (InsideSecondary?.ToString() ?? "unknown") +
                   ", correction=" + CorrectionDistanceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm)" +
                   "\nCamera quality: " + CameraQuality +
                   "\nWorld bounds centre (model frame, globalOffset added): " + (GlobalCenter == null ? "<unavailable>" :
                       "X=" + GlobalCenter.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + GlobalCenter.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + GlobalCenter.Z.ToString("0.############", CultureInfo.InvariantCulture)) +
                   "\nviewer.getState().globalOffset: " + (StateGlobalOffset?.ToString(Formatting.None) ?? "<absent>") +
                   "\nmodel.getData().globalOffset: " + (ModelGlobalOffset == null ? "<absent>" :
                       "X=" + ModelGlobalOffset.X.ToString("0.############", CultureInfo.InvariantCulture) + "; Y=" + ModelGlobalOffset.Y.ToString("0.############", CultureInfo.InvariantCulture) + "; Z=" + ModelGlobalOffset.Z.ToString("0.############", CultureInfo.InvariantCulture)) +
                   "\nmodelToViewerTransform: " + FormatMatrix(ModelToViewerTransform) +
                   "\nplacementTransform: " + FormatMatrix(PlacementTransform) +
                   "\nViewer state: " + ViewerState.ToString(Formatting.None);
        }


        private static string FormatOptionalPoint(ViewerProbePoint point)
        {
            if (point == null || !point.IsFinite) return "<not reported>";
            return "X=" + point.X.ToString("0.############", CultureInfo.InvariantCulture) +
                   "; Y=" + point.Y.ToString("0.############", CultureInfo.InvariantCulture) +
                   "; Z=" + point.Z.ToString("0.############", CultureInfo.InvariantCulture);
        }

        private static string FormatMatrix(double[] values)
            => values == null || values.Length == 0
                ? "<absent>"
                // ", " rather than ",": with a comma decimal separator the elements
                // and the decimals were indistinguishable, and a 16-element matrix
                // printed as 19 numbers. Invariant formatting fixes the separator;
                // the space keeps the array readable even if that ever regresses.
                : "[" + string.Join(", ", values.Select(x => x.ToString("0.############", CultureInfo.InvariantCulture))) + "]";

        private static string FormatPoint(ViewerProbePoint point)
            => point == null ? "<absent>" :
                "X=" + point.X.ToString("0.############", CultureInfo.InvariantCulture) +
                "; Y=" + point.Y.ToString("0.############", CultureInfo.InvariantCulture) +
                "; Z=" + point.Z.ToString("0.############", CultureInfo.InvariantCulture);
    }

    public sealed class ViewerCoordinateProbeOutcome
    {
        public int Index { get; set; }
        public ViewerCoordinateProbeResult Result { get; set; }
        public string Error { get; set; } = "";
        public string JavaScriptStack { get; set; } = "";
        public bool IsSuccess => Result != null && Result.IsValid && string.IsNullOrWhiteSpace(Error);
    }

    public static class ViewerCoordinateProbe
    {
        public static async Task<ViewerCoordinateProbeResult> ResolveAsync(
            ViewerCoordinateProbeRequest request,
            ApsTokenResponse token,
            CancellationToken cancellationToken = default,
            IProgress<string> diagnostics = null)
        {
            var outcomes = await ResolveBatchAsync(new[] { request }, token, cancellationToken, diagnostics).ConfigureAwait(false);
            var outcome = outcomes.FirstOrDefault();
            if (outcome == null || !outcome.IsSuccess)
                throw new InvalidOperationException("Viewer Coordinate Probe failed: " + (outcome?.Error ?? "No result was returned."));
            return outcome.Result;
        }

        public static async Task<IReadOnlyList<ViewerCoordinateProbeOutcome>> ResolveBatchAsync(
            IReadOnlyList<ViewerCoordinateProbeRequest> requests,
            ApsTokenResponse token,
            CancellationToken cancellationToken = default,
            IProgress<string> diagnostics = null)
        {
            if (requests == null || requests.Count == 0) throw new ArgumentException("Viewer Coordinate Probe batch is empty.", nameof(requests));
            foreach (var request in requests)
            {
                if (request == null) throw new ArgumentException("Viewer Coordinate Probe batch contains a null request.", nameof(requests));
                request.Validate();
            }
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("Viewer Coordinate Probe requires a non-empty APS access token.");

            // The access token is embedded in the Viewer bootstrap page. Refresh it
            // before creating WebView2 so a long publication wait cannot hand the
            // Viewer a token that is about to expire.
            await token.EnsureFreshAsync(cancellationToken, diagnostics).ConfigureAwait(false);

            var first = requests[0].Context;
            if (requests.Any(x => !string.Equals(x.Context.DerivativeUrn, first.DerivativeUrn, StringComparison.Ordinal) ||
                                  !string.Equals(x.Context.ViewableGeometryGuid, first.ViewableGeometryGuid, StringComparison.OrdinalIgnoreCase) ||
                                  !string.Equals(x.Context.ViewableId, first.ViewableId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("All Viewer Coordinate Probe batch requests must target the same published viewable.");

            var completion = new TaskCompletionSource<IReadOnlyList<ViewerCoordinateProbeOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(3, Math.Min(20, 2 + requests.Count / 10))));

            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    ViewerProbeWindow window = null;
                    try
                    {
                        window = new ViewerProbeWindow(requests, token, diagnostics);
                        var result = await window.RunAsync(timeout.Token);
                        completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                    finally
                    {
                        try { window?.Close(); } catch { }
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }));
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "BuildAI Viewer Coordinate Probe"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return await DisposeCancellationSourceAfterAsync(completion.Task, timeout).ConfigureAwait(false);
        }

        private static async Task<IReadOnlyList<ViewerCoordinateProbeOutcome>> DisposeCancellationSourceAfterAsync(
            Task<IReadOnlyList<ViewerCoordinateProbeOutcome>> task,
            CancellationTokenSource source)
        {
            try { return await task.ConfigureAwait(false); }
            finally { source.Dispose(); }
        }
    }

    internal sealed class ViewerProbeWindow : Window
    {
        private readonly IReadOnlyList<ViewerCoordinateProbeRequest> _requests;
        private readonly ApsTokenResponse _token;
        private readonly IProgress<string> _diagnostics;
        private readonly WebView2 _browser;
        private readonly TaskCompletionSource<IReadOnlyList<ViewerCoordinateProbeOutcome>> _completion =
            new TaskCompletionSource<IReadOnlyList<ViewerCoordinateProbeOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _probeStarted;

        public ViewerProbeWindow(
            IReadOnlyList<ViewerCoordinateProbeRequest> requests,
            ApsTokenResponse token,
            IProgress<string> diagnostics)
        {
            _requests = requests;
            _token = token;
            _diagnostics = diagnostics;
            Width = 16;
            Height = 16;
            Left = -32000;
            Top = -32000;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            _browser = new WebView2();
            Content = _browser;
        }

        public async Task<IReadOnlyList<ViewerCoordinateProbeOutcome>> RunAsync(CancellationToken cancellationToken)
        {
            Show();
            string runtimeVersion;
            try
            {
                runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Microsoft Edge WebView2 Runtime is not installed or cannot be started. Install the Evergreen WebView2 Runtime and retry.", ex);
            }
            if (string.IsNullOrWhiteSpace(runtimeVersion))
                throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen Runtime and retry.");

            var firstRequest = _requests[0];
            _diagnostics?.Report("VIEWER COORDINATE PROBE BATCH START" +
                                 "\nRequest count: " + _requests.Count +
                                 "\nWebView2 Runtime: " + runtimeVersion +
                                 "\nDerivative URN: " + firstRequest.Context.DerivativeUrn +
                                 "\nRequired geometry GUID: " + firstRequest.Context.ViewableGeometryGuid +
                                 "\nRequired viewableID: " + firstRequest.Context.ViewableId +
                                 "\nThe viewable and externalId mapping will be loaded once for the entire batch.");

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BuildAI", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.WebMessageReceived += BrowserMessageReceived;

            var htmlPath = Path.Combine(
                Path.GetDirectoryName(typeof(ViewerCoordinateProbe).Assembly.Location) ?? "",
                "ViewerProbe", "viewer-probe.html");
            if (!File.Exists(htmlPath))
                throw new FileNotFoundException("Viewer Coordinate Probe HTML resource is missing from the add-in payload.", htmlPath);
            var resourceFolder = Path.GetDirectoryName(htmlPath) ?? "";
            _browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "buildai.viewer",
                resourceFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _browser.CoreWebView2.Navigate("https://buildai.viewer/viewer-probe.html");

            using (cancellationToken.Register(() => _completion.TrySetCanceled()))
                return await _completion.Task;
        }

        private async void BrowserMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = JObject.Parse(e.WebMessageAsJson);
                var type = (string)message["type"] ?? "";

                if (string.Equals(type, "ready", StringComparison.Ordinal))
                {
                    if (_probeStarted) return;
                    _probeStarted = true;
                    var payload = new JArray(_requests.Select(x => x.ToBrowserPayload()));
                    await _browser.CoreWebView2.ExecuteScriptAsync("window.__startProbeBatch(" + payload.ToString(Formatting.None) + ");");
                    return;
                }

                if (string.Equals(type, "token-request", StringComparison.Ordinal))
                {
                    var requestId = (string)message["requestId"] ?? "";
                    var expiresIn = _token.ExpiresIn > 30 ? _token.ExpiresIn : 3600;
                    var script = "window.__deliverToken(" +
                                 JsonConvert.SerializeObject(requestId) + "," +
                                 JsonConvert.SerializeObject(_token.AccessToken) + "," +
                                 expiresIn + ");";
                    await _browser.CoreWebView2.ExecuteScriptAsync(script);
                    return;
                }

                if (string.Equals(type, "progress", StringComparison.Ordinal))
                {
                    _diagnostics?.Report("VIEWER COORDINATE PROBE\n" + ((string)message["message"] ?? ""));
                    return;
                }

                if (string.Equals(type, "batch-result", StringComparison.Ordinal))
                {
                    var outcomes = new List<ViewerCoordinateProbeOutcome>();
                    foreach (var item in message["outcomes"] as JArray ?? new JArray())
                    {
                        var outcome = new ViewerCoordinateProbeOutcome
                        {
                            Index = (int?)item["index"] ?? outcomes.Count,
                            Result = item["result"]?.ToObject<ViewerCoordinateProbeResult>(),
                            Error = (string)item["error"] ?? "",
                            JavaScriptStack = (string)item["stack"] ?? ""
                        };
                        if (outcome.IsSuccess) _diagnostics?.Report(outcome.Result.ToDiagnosticText());
                        else _diagnostics?.Report(
                            "VIEWER COORDINATE PROBE ITEM FAILED\nIndex: " + outcome.Index + "\n" + outcome.Error +
                            (string.IsNullOrWhiteSpace(outcome.JavaScriptStack)
                                ? "\nJavaScript stack: <not supplied>"
                                : "\nJavaScript stack:\n" + outcome.JavaScriptStack));
                        outcomes.Add(outcome);
                    }
                    if (outcomes.Count != _requests.Count)
                        throw new InvalidOperationException("APS Viewer returned " + outcomes.Count + " batch results for " + _requests.Count + " requests.");
                    var invalidIndices = outcomes.Where(x => x.Index < 0 || x.Index >= _requests.Count)
                        .Select(x => x.Index).Distinct().OrderBy(x => x).ToList();
                    if (invalidIndices.Count > 0)
                        throw new InvalidOperationException(
                            "APS Viewer returned out-of-range batch indices: " + string.Join(", ", invalidIndices) +
                            ". Expected 0.." + (_requests.Count - 1) + ".");
                    var duplicateIndices = outcomes.GroupBy(x => x.Index).Where(x => x.Count() != 1)
                        .Select(x => x.Key).OrderBy(x => x).ToList();
                    if (duplicateIndices.Count > 0)
                        throw new InvalidOperationException(
                            "APS Viewer returned duplicate batch indices: " + string.Join(", ", duplicateIndices) + ".");
                    var returnedIndices = new HashSet<int>(outcomes.Select(x => x.Index));
                    var missingIndices = Enumerable.Range(0, _requests.Count).Where(x => !returnedIndices.Contains(x)).ToList();
                    if (missingIndices.Count > 0)
                        throw new InvalidOperationException(
                            "APS Viewer omitted batch indices: " + string.Join(", ", missingIndices) + ".");
                    _completion.TrySetResult(outcomes.OrderBy(x => x.Index).ToList());
                    return;
                }

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    var error = (string)message["message"] ?? "Unknown APS Viewer error.";
                    var stack = (string)message["stack"] ?? "";
                    _completion.TrySetException(new InvalidOperationException(
                        "Viewer Coordinate Probe failed: " + error +
                        (string.IsNullOrWhiteSpace(stack) ? "" : "\nJavaScript stack:\n" + stack)));
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_browser.CoreWebView2 != null)
                    _browser.CoreWebView2.WebMessageReceived -= BrowserMessageReceived;
                _browser.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }
    }
}
