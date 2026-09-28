using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Issues
{
    /// <summary>
    /// Guards the coordinate contract of a PushPin linked document.
    /// <para>
    /// This type used to CONVERT coordinates: it added the Viewer globalOffset,
    /// scaled everything to metres, and rewrote globalOffset in the Viewer state
    /// as a scaled value. Measured against ACC on 03 Sep 2026 that produced pins
    /// 54-68 m away from their elements, because it applied the offset a second
    /// time. Reconstructing the erroneous route reproduces the observed pin to
    /// 2.4 ft on Issue #34 and 16 ft on #33 - closer than a human can click on a
    /// floor slab - so the mechanism is not in doubt.
    /// </para>
    /// <para>
    /// Two independent faults were folded into one route:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The metre conversion. ACC stores <c>details.position</c> in the Viewer's
    /// own units. For a Revit derivative those are international feet, which is
    /// also what globalOffset, the AEC ref-point translation and ACC's own
    /// coordinate readout use. Scaling by 0.3048 pulled every pin to 30.48% of
    /// its distance from the model origin.
    /// </description></item>
    /// <item><description>
    /// The offset was applied twice. The plugin added globalOffset to the anchor
    /// AND left a globalOffset in the Viewer state, which ACC applies itself.
    /// Only one of the two may happen, and the platform's own application is the
    /// one to keep.
    /// </description></item>
    /// </list>
    /// <para>
    /// The position is therefore written exactly as the Viewer produced it -
    /// viewer-local, Viewer units - together with the Viewer's untouched
    /// globalOffset, and ACC applies the offset once. Nothing here rewrites a
    /// coordinate any more; it only refuses to ship an inconsistent document.
    /// </para>
    /// </summary>
    public static class PushpinFrame
    {
        /// <summary>
        /// Verifies that the position and the captured Viewer state describe the
        /// same point in the same frame, and that the Viewer's globalOffset
        /// survived intact. Throws rather than mutating.
        /// </summary>
        /// <param name="viewerState">State captured from the Viewer, modified only by callers adding objectSet.</param>
        /// <param name="positionViewerUnits">The position about to be written, in viewer-local Viewer units.</param>
        /// <param name="expectedGlobalOffset">globalOffset as the Viewer reported it, or null to skip that check.</param>
        public static void ValidateViewerState(
            JObject viewerState,
            double[] positionViewerUnits,
            double[] expectedGlobalOffset,
            Action<string> log)
        {
            if (viewerState == null) throw new ArgumentNullException(nameof(viewerState));
            if (positionViewerUnits == null || positionViewerUnits.Length < 3)
                throw new ArgumentException("Expected [x, y, z].", nameof(positionViewerUnits));

            var viewport = viewerState["viewport"] as JObject
                ?? throw new InvalidOperationException("Viewer state has no viewport object.");

            var offset = ReadGlobalOffset(viewerState);
            if (expectedGlobalOffset != null && expectedGlobalOffset.Length >= 3)
            {
                var drift = Math.Sqrt(
                    Sq(offset.X - expectedGlobalOffset[0]) +
                    Sq(offset.Y - expectedGlobalOffset[1]) +
                    Sq(offset.Z - expectedGlobalOffset[2]));
                if (drift > 1e-6)
                    throw new InvalidOperationException(
                        "PUSHPIN GLOBAL OFFSET ALTERED | the globalOffset in the Viewer state differs from the " +
                        "value the Viewer reported by " + drift.ToString("F6") + " Viewer units. " +
                        "ACC applies this offset itself, so it must be passed through untouched.");
            }

            var absolutePosition = new[] {
                positionViewerUnits[0] + offset.X,
                positionViewerUnits[1] + offset.Y,
                positionViewerUnits[2] + offset.Z
            };
            AssertConsistent(absolutePosition, ReadPoint(viewport, "target"), log);

            log?.Invoke("PUSHPIN FRAME OK | absolute viewport.target == local position + globalOffset, globalOffset passed through unmodified " +
                        "(X=" + offset.X.ToString("0.######") +
                        "; Y=" + offset.Y.ToString("0.######") +
                        "; Z=" + offset.Z.ToString("0.######") + ")");
        }

        public static void AssertConsistent(double[] position, double[] target, Action<string> log)
        {
            var dx = position[0] - target[0];
            var dy = position[1] - target[1];
            var dz = position[2] - target[2];
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > 1e-6)
                throw new InvalidOperationException(
                    "PUSHPIN FRAME MISMATCH | position and viewport.target differ by " +
                    distance.ToString("F4") + " Viewer units. Both fields must use the same frame.");
        }

        // AR-ST explicitly sends both the pin and viewport points in the
        // Viewer-local frame. Keep this contract separate from the legacy Clash
        // validator above, whose behavior is intentionally unchanged.
        public static void ValidateArStViewerLocalState(
            JObject viewerState, double[] positionViewerUnits, double[] expectedGlobalOffset,
            string resultKey, Action<string> log)
        {
            if (viewerState == null) throw new ArgumentNullException(nameof(viewerState));
            var viewport = viewerState["viewport"] as JObject
                ?? throw new InvalidOperationException("Viewer state has no viewport object.");
            var target = ReadPoint(viewport, "target");
            var pivot = ReadPoint(viewport, "pivotPoint");
            var delta = Distance(positionViewerUnits, target);
            var pivotDelta = Distance(positionViewerUnits, pivot);
            var offset = ReadGlobalOffset(viewerState);
            var magnitude = Math.Sqrt(Sq(offset.X) + Sq(offset.Y) + Sq(offset.Z));
            var offsetPosition = new[] { positionViewerUnits[0] + offset.X, positionViewerUnits[1] + offset.Y, positionViewerUnits[2] + offset.Z };
            var offsetMismatch = delta > 1e-4 && Distance(offsetPosition, target) <= 1e-4;
            // The Issue contract is defined by pushpin.position and
            // viewport.target. pivotPoint is diagnostic only: Viewer may keep
            // a navigation pivot elsewhere without changing the Issue target.
            var accepted = delta <= 1e-4;
            log?.Invoke("ARST_ISSUE_FRAME_AUDIT | " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                resultKey, coordinateFrame = "viewer-local", pushpinPosition = positionViewerUnits,
                viewportEye = ReadPoint(viewport, "eye"), viewportTarget = target, viewportPivotPoint = pivot,
                globalOffset = new[] { offset.X, offset.Y, offset.Z }, positionTargetDelta = delta,
                positionPivotDelta = pivotDelta, globalOffsetMagnitude = magnitude,
                looksLikeSingleOffsetMismatch = offsetMismatch, validationResult = accepted ? "accepted" : "rejected"
            }));
            if (!accepted)
                throw new InvalidOperationException(offsetMismatch
                    ? "AR-ST FRAME CONTRACT VIOLATION: one point appears to have globalOffset applied exactly once"
                    : "AR-ST FRAME CONTRACT VIOLATION: viewer-local pushpin and viewport target differ.");
        }

        public static void ValidateArStIssuePayload(
            JObject issueState, double[] pushpinPositionViewerLocal, ArstIssueCameraPayload camera,
            double[] expectedGlobalOffset, string resultKey, Action<string> log)
        {
            string rejection = null;
            try
            {
                if (issueState == null) rejection = "viewerState is null";
                if (camera == null) rejection = rejection ?? "camera DTO is null";
                if (pushpinPositionViewerLocal == null || pushpinPositionViewerLocal.Length < 3) rejection = rejection ?? "pushpin position is incomplete";
                if (expectedGlobalOffset == null || expectedGlobalOffset.Length < 3) rejection = rejection ?? "globalOffset is incomplete";
                var viewport = issueState?["viewport"] as JObject;
                var target = viewport == null ? null : ReadPoint(viewport, "target");
                var pivot = viewport == null ? null : ReadPoint(viewport, "pivotPoint");
                var eye = viewport == null ? null : ReadPoint(viewport, "eye");
                var up = viewport == null ? null : ReadPoint(viewport, "up");
                var expectedTarget = Add(pushpinPositionViewerLocal, expectedGlobalOffset);
                var targetDelta = Distance(target, expectedTarget);
                var pivotDelta = Distance(pivot, expectedTarget);
                var actualDirection = Normalize(Sub(target, eye));
                var capturedDirection = Normalize(Sub(camera.LocalTarget, camera.LocalEye));
                var directionAngleDeg = AngleDegrees(actualDirection, capturedDirection);
                var distanceToOrbit = Distance(eye, target);
                var finite = new[] { target, pivot, eye, up, expectedTarget, actualDirection, capturedDirection }
                    .Where(x => x != null).SelectMany(x => x).All(IsFinite) &&
                    IsFinite(distanceToOrbit) && IsFinite(camera.DistanceToOrbit) && expectedGlobalOffset.All(IsFinite);
                var targetTolerance = 1e-4;
                var angleTolerance = 1e-4;
                var directionOk = finite && directionAngleDeg <= angleTolerance;
                var distanceOk = finite && Math.Abs(distanceToOrbit - camera.DistanceToOrbit) <= targetTolerance;
                var upOk = finite && Distance(up, camera.Up) <= targetTolerance;
                var offsetOk = camera.GlobalOffset != null && Distance(camera.GlobalOffset, expectedGlobalOffset) <= targetTolerance;
                if (rejection == null && targetDelta > targetTolerance) rejection = "viewport.target is not pushpin.position + globalOffset";
                if (rejection == null && pivotDelta > targetTolerance) rejection = "viewport.pivotPoint is not pushpin.position + globalOffset";
                if (rejection == null && !directionOk) rejection = "camera direction changed";
                if (rejection == null && !distanceOk) rejection = "distanceToOrbit changed";
                if (rejection == null && !upOk) rejection = "up changed";
                if (rejection == null && !offsetOk) rejection = "globalOffset changed or was applied inconsistently";
                var accepted = rejection == null && finite;
                log?.Invoke("ARST_ISSUE_CAMERA_FRAME_AUDIT | " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    resultKey, pushpinPositionViewerLocal, globalOffset = expectedGlobalOffset, expectedAbsoluteTarget = expectedTarget,
                    actualViewportTarget = target, actualPivotPoint = pivot, localEye = camera.LocalEye, absoluteEye = camera.Eye,
                    targetDelta, pivotDelta, directionAngleDeg, distanceToOrbit,
                    validationResult = accepted ? "accepted" : "rejected", rejectionReason = rejection ?? (finite ? "" : "non-finite camera value")
                }));
                if (!accepted) throw new InvalidOperationException("AR-ST CAMERA FRAME REJECTED: " + (rejection ?? "non-finite camera value"));
            }
            catch (Exception ex) when (!(ex is InvalidOperationException && ex.Message.StartsWith("AR-ST CAMERA FRAME REJECTED", StringComparison.Ordinal)))
            {
                log?.Invoke("ARST_ISSUE_CAMERA_FRAME_AUDIT | " + Newtonsoft.Json.JsonConvert.SerializeObject(new { resultKey, validationResult = "rejected", rejectionReason = ex.Message }));
                throw new InvalidOperationException("AR-ST CAMERA FRAME REJECTED: " + ex.Message, ex);
            }
        }

        private static double Sq(double v) => v * v;

        private static double Distance(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return double.PositiveInfinity;
            return Math.Sqrt(Sq(a[0] - b[0]) + Sq(a[1] - b[1]) + Sq(a[2] - b[2]));
        }

        private static double[] Add(double[] a, double[] b) => a == null || b == null || a.Length < 3 || b.Length < 3 ? null : new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Sub(double[] a, double[] b) => a == null || b == null || a.Length < 3 || b.Length < 3 ? null : new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Normalize(double[] value)
        {
            if (value == null || value.Length < 3) return null;
            var length = Math.Sqrt(Sq(value[0]) + Sq(value[1]) + Sq(value[2]));
            return length <= 1e-12 ? null : new[] { value[0] / length, value[1] / length, value[2] / length };
        }
        private static double AngleDegrees(double[] a, double[] b)
        {
            if (a == null || b == null) return double.PositiveInfinity;
            var dot = Math.Max(-1.0, Math.Min(1.0, a[0] * b[0] + a[1] * b[1] + a[2] * b[2]));
            return Math.Acos(dot) * 180.0 / Math.PI;
        }
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static GlobalOffset ReadGlobalOffset(JObject viewerState)
        {
            var token = viewerState["globalOffset"];
            if (token is JObject obj)
                return new GlobalOffset((double?)obj["x"] ?? 0, (double?)obj["y"] ?? 0, (double?)obj["z"] ?? 0);
            if (token is JArray array && array.Count >= 3)
                return new GlobalOffset((double)array[0], (double)array[1], (double)array[2]);
            throw new InvalidOperationException(
                "Viewer state has no globalOffset. ACC needs it to place a viewer-local PushPin position.");
        }

        private static double[] ReadPoint(JObject viewport, string property)
        {
            var point = viewport[property] as JArray;
            if (point == null || point.Count < 3)
                throw new InvalidOperationException("Viewer viewport has no valid " + property + " point.");
            return new[] { (double)point[0], (double)point[1], (double)point[2] };
        }

        internal readonly struct GlobalOffset
        {
            public GlobalOffset(double x, double y, double z) { X = x; Y = y; Z = z; }
            public double X { get; }
            public double Y { get; }
            public double Z { get; }
        }
    }
}
