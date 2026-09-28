using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using BuildAI.Core.Issues;
using BuildAI.Core.ViewerProbe;

namespace Plugin4.LinkComparatorAI.Issues
{
    /// <summary>
    /// Validates the loaded derivative by model, viewable and object identity.
    /// Viewer may fetch the same derivative through Autodesk REST or CDN hosts.
    /// </summary>
    public static class ArStProbeDerivativeGuard
    {
        /// <summary>
        /// Throws when the probe resolved its dbId in a derivative ACC will not open.
        /// Call it with the probe result and the placement context, before the payload is built.
        /// </summary>
        public static void Validate(ViewerCoordinateProbeResult probe, ApsPushpinContext context, string resultKey,
            bool externalIdRoundTrip, Action<string> diagnostics)
        {
            if (probe == null) throw new InvalidOperationException("DERIVATIVE IDENTITY VALIDATION: no probe result.");
            if (context == null) throw new InvalidOperationException("DERIVATIVE IDENTITY VALIDATION: no placement context.");

            var actual = (probe.DerivativeEndpoint ?? "").Trim().TrimEnd('/');
            var urnMatch = SameUrn(probe.LoadedModelUrn, context.DerivativeUrn);
            // The exact derivative URN is version-scoped. The Viewer model URN
            // must match it; the version/item context must also be present.
            var versionMatch = urnMatch && !string.IsNullOrWhiteSpace(context.VersionUrn) &&
                               !string.IsNullOrWhiteSpace(context.DocumentUrn);
            var viewableGuidMatch = string.Equals(probe.LoadedViewableGuid?.Trim(),
                context.ViewableGeometryGuid?.Trim(), StringComparison.OrdinalIgnoreCase);
            var viewableIdMatch = string.Equals(probe.LoadedViewableId?.Trim(),
                context.ViewableId?.Trim(), StringComparison.OrdinalIgnoreCase);
            var viewableNameMatch = string.Equals(probe.LoadedViewableName?.Trim(),
                context.ViewableName?.Trim(), StringComparison.OrdinalIgnoreCase);
            externalIdRoundTrip = externalIdRoundTrip && probe.DbId > 0 &&
                                  !string.IsNullOrWhiteSpace(probe.ResolvedExternalId);
            var fragmentFound = probe.FragmentIds != null && probe.FragmentIds.Length > 0;

            string rejection = null;
            if (!urnMatch) rejection = "the loaded model URN differs from the exact published derivative URN.";
            else if (!versionMatch) rejection = "the exact item/version context is missing.";
            else if (!viewableGuidMatch) rejection = "the loaded viewable GUID differs from the published viewable.";
            else if (!viewableIdMatch) rejection = "the loaded viewable ID differs from the published viewable.";
            else if (!viewableNameMatch) rejection = "the loaded viewable name differs from the published viewable.";
            else if (!externalIdRoundTrip) rejection = "the runtime externalId/dbId mapping is unresolved.";
            else if (!fragmentFound) rejection = "the selected runtime dbId has no fragment.";

            // isOtg / otgNodeFound are internal Viewer details and differ between Viewer
            // builds, so they are reported but never block: a false negative there would
            // stop a run whose object ids are in fact correct. The object id itself is
            // verified against the property database of the same viewable.
            var svf2Warning = !probe.IsOtg
                ? "SVF2/OTG not reported by the probe (otg node: " +
                  (probe.OtgNodeFound ? "found, status " + probe.OtgStatus : "missing") +
                  "). If the property database cross-check disagrees with the probe dbId, this is the reason."
                : "";

            diagnostics?.Invoke("DERIVATIVE IDENTITY VALIDATION | " + JsonConvert.SerializeObject(new
            {
                resultKey,
                urnMatch,
                versionMatch,
                viewableGuidMatch,
                viewableIdMatch,
                viewableNameMatch,
                versionMatchBasis = "exact loaded derivative URN and published version context",
                externalIdRoundTrip,
                fragmentFound,
                resourceHost = actual,
                apiFlavor = probe.ApiFlavor,
                isOtg = probe.IsOtg,
                otgNodeFound = probe.OtgNodeFound,
                otgStatus = probe.OtgStatus,
                sceneFragmentCount = probe.SceneFragmentCount,
                sceneNodeCount = probe.SceneNodeCount,
                warning = svf2Warning,
                dbId = probe.DbId,
                result = rejection == null ? "accepted" : "rejected",
                rejectionReason = rejection ?? ""
            }));

            if (rejection != null)
                throw new InvalidOperationException("DERIVATIVE IDENTITY VALIDATION: " + rejection);
        }

        private static bool SameUrn(string loaded, string expected)
        {
            if (string.IsNullOrWhiteSpace(loaded) || string.IsNullOrWhiteSpace(expected)) return false;
            string Normalize(string value) => value.Trim().StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
                ? value.Trim().Substring(4) : value.Trim();
            return string.Equals(Normalize(loaded), Normalize(expected), StringComparison.Ordinal);
        }
    }
}
