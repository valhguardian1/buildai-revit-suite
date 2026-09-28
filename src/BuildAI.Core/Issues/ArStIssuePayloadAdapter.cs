using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Issues
{
    public readonly struct RuntimeViewDbId
    {
        public RuntimeViewDbId(int value) { Value = value; }
        public int Value { get; }
    }

    public readonly struct AuthoritativeIssueDbId
    {
        public AuthoritativeIssueDbId(int value) { Value = value; }
        public int Value { get; }
    }

    public sealed class CompositeExternalId
    {
        public CompositeExternalId(string value)
        {
            Value = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(Value)) throw new ArgumentException("Composite externalId is required.", nameof(value));
        }
        public string Value { get; }
    }

    public sealed class ArStIssueObjectIdentity
    {
        private ArStIssueObjectIdentity(CompositeExternalId externalId, RuntimeViewDbId runtime, AuthoritativeIssueDbId authoritative,
            bool runtimeExternalIdMatch,
            string reverseExternalId, string resolverSource, string propertyDatabaseUrn, string propertyDatabaseViewableId,
            string targetPlacementViewableId)
        {
            ExternalId = externalId; RuntimeViewDbId = runtime; AuthoritativeIssueDbId = authoritative;
            RuntimeExternalIdMatch = runtimeExternalIdMatch;
            ReverseResolvedExternalId = reverseExternalId ?? ""; ResolverSource = resolverSource ?? "";
            PropertyDatabaseUrn = propertyDatabaseUrn ?? ""; PropertyDatabaseViewableId = propertyDatabaseViewableId ?? "";
            TargetPlacementViewableId = targetPlacementViewableId ?? "";
        }

        public CompositeExternalId ExternalId { get; }
        public RuntimeViewDbId RuntimeViewDbId { get; }
        public AuthoritativeIssueDbId AuthoritativeIssueDbId { get; }
        public bool RuntimeExternalIdMatch { get; }
        public string ReverseResolvedExternalId { get; }
        public string ResolverSource { get; }
        public string PropertyDatabaseUrn { get; }
        public string PropertyDatabaseViewableId { get; }
        public string TargetPlacementViewableId { get; }
        public bool ReverseMatch => string.Equals(NormalizeExternalId(ReverseResolvedExternalId), NormalizeExternalId(ExternalId.Value), StringComparison.OrdinalIgnoreCase);

        public static ArStIssueObjectIdentity Create(CompositeExternalId externalId, RuntimeViewDbId runtime,
            AuthoritativeIssueDbId authoritative, bool runtimeExternalIdMatch, string reverseExternalId, string resolverSource,
            string propertyDatabaseUrn, string propertyDatabaseViewableId, string targetPlacementViewableId)
        {
            if (externalId == null) throw new ArgumentNullException(nameof(externalId));
            if (runtime.Value <= 0 || !runtimeExternalIdMatch)
                throw new InvalidOperationException("AR-ST OBJECT ID RESOLUTION REJECTED: runtime dbId was not returned for the exact composite externalId.");
            if (authoritative.Value <= 0) throw new InvalidOperationException("AR-ST OBJECT ID RESOLUTION REJECTED: authoritative dbId is missing or non-positive.");
            var identity = new ArStIssueObjectIdentity(externalId, runtime, authoritative, runtimeExternalIdMatch, reverseExternalId,
                resolverSource, propertyDatabaseUrn, propertyDatabaseViewableId, targetPlacementViewableId);
            if (!identity.ReverseMatch)
                throw new InvalidOperationException("AR-ST OBJECT ID RESOLUTION REJECTED: reverse externalId does not exactly match the source composite externalId.");
            return identity;
        }

        public static string NormalizeExternalId(string value)
            => (value ?? "").Trim().Replace("\\", "/").Trim('/');
    }

    public sealed class ArstIssueCameraPayload
    {
        private ArstIssueCameraPayload(double[] localEye, double[] localTarget, double[] localPivot,
            double[] eye, double[] target, double[] pivot, double[] up, double[] worldUp,
            double distanceToOrbit, double fieldOfView, double aspectRatio, string projection,
            bool isOrthographic, double orthographicHeight, double[] globalOffset)
        {
            LocalEye = Copy(localEye); LocalTarget = Copy(localTarget); LocalPivotPoint = Copy(localPivot);
            Eye = Copy(eye); Target = Copy(target); PivotPoint = Copy(pivot); Up = Copy(up); WorldUpVector = Copy(worldUp);
            DistanceToOrbit = distanceToOrbit; FieldOfView = fieldOfView; AspectRatio = aspectRatio;
            Projection = projection ?? ""; IsOrthographic = isOrthographic; OrthographicHeight = orthographicHeight;
            GlobalOffset = Copy(globalOffset);
        }

        public double[] LocalEye { get; }
        public double[] LocalTarget { get; }
        public double[] LocalPivotPoint { get; }
        public double[] Eye { get; }
        public double[] Target { get; }
        public double[] PivotPoint { get; }
        public double[] Up { get; }
        public double[] WorldUpVector { get; }
        public double DistanceToOrbit { get; }
        public double FieldOfView { get; }
        public double AspectRatio { get; }
        public string Projection { get; }
        public bool IsOrthographic { get; }
        public double OrthographicHeight { get; }
        public double[] GlobalOffset { get; }

        public static ArstIssueCameraPayload Create(JObject capturedViewerState, double[] globalOffset)
        {
            if (capturedViewerState == null) throw new ArgumentNullException(nameof(capturedViewerState));
            if (globalOffset == null || globalOffset.Length < 3 || globalOffset.Any(x => !Finite(x)))
                throw new InvalidOperationException("AR-ST camera payload requires a finite globalOffset.");
            var viewport = capturedViewerState["viewport"] as JObject ?? throw new InvalidOperationException("AR-ST camera payload has no viewport.");
            var localEye = Point(viewport, "eye"); var localTarget = Point(viewport, "target"); var localPivot = Point(viewport, "pivotPoint");
            var up = Point(viewport, "up"); var worldUp = Point(viewport, "worldUpVector", up);
            return new ArstIssueCameraPayload(localEye, localTarget, localPivot,
                Add(localEye, globalOffset), Add(localTarget, globalOffset), Add(localPivot, globalOffset), up, worldUp,
                Number(viewport, "distanceToOrbit"), Number(viewport, "fieldOfView"), Number(viewport, "aspectRatio"),
                (string)viewport["projection"] ?? "", (bool?)viewport["isOrthographic"] ?? false,
                Number(viewport, "orthographicHeight"), globalOffset);
        }

        public JObject ApplyTo(JObject capturedViewerState)
        {
            var state = (JObject)capturedViewerState.DeepClone();
            var viewport = (JObject)state["viewport"];
            viewport["eye"] = new JArray(Eye); viewport["target"] = new JArray(Target); viewport["pivotPoint"] = new JArray(PivotPoint);
            return state;
        }

        private static double Number(JObject obj, string name) => (double?)obj[name] ?? double.NaN;
        private static double[] Point(JObject obj, string name, double[] fallback = null)
        {
            var a = obj[name] as JArray;
            if ((a == null || a.Count < 3) && fallback != null) return Copy(fallback);
            if (a == null || a.Count < 3) throw new InvalidOperationException("AR-ST camera viewport has no valid " + name + ".");
            return new[] { (double)a[0], (double)a[1], (double)a[2] };
        }
        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Copy(double[] a) => a == null ? null : (double[])a.Clone();
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static class ArStIssuePayloadAdapter
    {
        public static ApsLinkedDocument Build(ApsPushpinContext context, string currentUserId, double[] pushpinPosition,
            CompositeExternalId externalId, ArStIssueObjectIdentity identity, JObject capturedViewerState, double[] globalOffset,
            string resultKey, Action<string> log)
        {
            if (context == null || !context.IsUsable) throw new InvalidOperationException("AR-ST placement context is incomplete.");
            if (identity == null || !identity.ReverseMatch) throw new InvalidOperationException("AR-ST OBJECT ID RESOLUTION REJECTED: identity is not reverse-validated.");
            var camera = ArstIssueCameraPayload.Create(capturedViewerState, globalOffset);
            var state = camera.ApplyTo(capturedViewerState);
            PushpinFrame.ValidateArStIssuePayload(state, pushpinPosition, camera, globalOffset, resultKey, log);
            // Measured in ACC on 2026-09-19 (project 0e08f294, viewable BuildAI AR-ST,
            // version 57, the same version the issues were created at):
            //   runtime probe dbId 342903  -> no node, 0 fragments, nothing to select;
            //   property database dbId 760984 -> "Перекрытие [7327971/5547309]", 2 fragments,
            //   centre (-42.53, -105.94, 22.34), i.e. one foot from pushpin #123.
            // ACC resolves selection and isolation through the property database id, so
            // that is the value that goes into details.objectId and objectSet. Writing the
            // runtime id left every AR-ST issue without highlight and without the ghosted
            // wireframe, because objectSet pointed at an id that does not exist in the scene.
            var id = identity.AuthoritativeIssueDbId.Value;
            if (id <= 0)
                throw new InvalidOperationException(
                    "AR-ST OBJECT ID RESOLUTION REJECTED: the property database returned no objectId for " +
                    externalId.Value + "; the runtime probe dbId " + identity.RuntimeViewDbId.Value +
                    " is not valid for ACC selection.");
            state["objectSet"] = new JArray(new JObject {
                ["id"] = new JArray(id), ["isolated"] = new JArray(id), ["hidden"] = new JArray(), ["idType"] = "lmv",
                ["explodeScale"] = 0, ["explodeOptions"] = new JObject { ["magnitude"] = 4, ["depthDampening"] = 0 }
            });
            // AR-ST Issues open in the selected AR-ST viewable. Keep the same
            // authoritative id in both selection slots and explicitly request the
            // line/structural display mode for that viewable.
            // Appearance copied from the AR-ST issues of 6.6.24 that showed the
            // ghosted wireframe correctly (run 2026-08-11, project 63c76fd3,
            // issue #1019): ghostHidden + displayLines are what draw the rest of
            // the model as a wireframe once objectSet isolates the element.
            var renderOptions = state["renderOptions"] as JObject ?? new JObject();
            var appearance = renderOptions["appearance"] as JObject ?? new JObject();
            appearance["ghostHidden"] = true;
            appearance["antiAliasing"] = true;
            appearance["displayLines"] = true;
            appearance["ambientShadow"] = true;
            appearance["displayPoints"] = true;
            appearance["swapBlackAndWhite"] = false;
            appearance["progressiveDisplay"] = true;
            appearance["screenSpaceLineWidth"] = false;
            renderOptions["appearance"] = appearance;
            if (renderOptions["environment"] == null) renderOptions["environment"] = "Boardwalk";
            if (renderOptions["ambientOcclusion"] == null)
                renderOptions["ambientOcclusion"] = new JObject { ["enabled"] = true, ["radius"] = 13.123359580052492, ["intensity"] = 1 };
            state["renderOptions"] = renderOptions;
            ValidateArStHighlightAndWireframe(state, id);
            return new ApsLinkedDocument {
                Type = "TwoDVectorPushpin", Urn = context.DocumentUrn, CreatedBy = currentUserId ?? "",
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), CreatedAtVersion = context.FileVersion > 0 ? context.FileVersion : 1,
                Details = new ApsPushpinDetails {
                    Viewable = new ApsPushpinViewable { Guid = context.ViewableGeometryGuid, ViewableId = context.ViewableId, Name = context.ViewableName ?? "3D View", Is3D = true },
                    Position = new ApsPushpinPosition { X = pushpinPosition[0], Y = pushpinPosition[1], Z = pushpinPosition[2] },
                    ObjectId = id, ExternalId = externalId.Value, ViewerState = state
                }
            };
        }

        /// <summary>
        /// Checks only what actually produces highlight + ghosted wireframe in ACC:
        /// the object id has to be in both selection slots, ghostHidden and displayLines
        /// have to be on. Cosmetic flags (ambientShadow, displayPoints, antiAliasing) are
        /// copied from the 6.6.24 reference issues and are deliberately not asserted:
        /// asserting ambientShadow == false is what rejected four valid payloads in the
        /// 2026-09-20 run after the appearance was aligned with that reference.
        /// </summary>
        private static void ValidateArStHighlightAndWireframe(JObject state, int issueObjectId)
        {
            var entry = (state?["objectSet"] as JArray)?.OfType<JObject>().FirstOrDefault();
            var appearance = state?["renderOptions"]?["appearance"] as JObject;
            var ids = entry?["id"] as JArray;
            var isolated = entry?["isolated"] as JArray;
            var reasons = new List<string>();
            if (issueObjectId <= 0) reasons.Add("objectId is not positive");
            if (ids == null || !ids.Values<int>().Contains(issueObjectId)) reasons.Add("objectSet.id does not contain " + issueObjectId);
            if (isolated == null || !isolated.Values<int>().Contains(issueObjectId)) reasons.Add("objectSet.isolated does not contain " + issueObjectId);
            if ((bool?)appearance?["ghostHidden"] != true) reasons.Add("renderOptions.appearance.ghostHidden is not true");
            if ((bool?)appearance?["displayLines"] != true) reasons.Add("renderOptions.appearance.displayLines is not true");
            if (reasons.Count > 0)
                throw new InvalidOperationException("ARST_ISSUE_PAYLOAD_GUARD: " + string.Join("; ", reasons) + ".");
        }
    }
}
