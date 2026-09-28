using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;
using BuildAI.RevitCompatibility;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Comparison
{
    internal static class LinkRefreshService
    {
        public static void RefreshSelectedLinks(Document host, ComparatorSettings settings, bool includeStructural, Action<string> progress = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            settings = settings ?? new ComparatorSettings();

            var targets = new List<RefreshTarget>();
            AddTarget(targets, "AR", LinkResolver.ResolveArchitectural(host, settings));
            if (includeStructural) AddTarget(targets, "ST", LinkResolver.ResolveStructural(host, settings));

            var missing = targets.FirstOrDefault(x => !x.IsResolved);
            if (missing != null)
                throw new InvalidOperationException("AR-ST comparison was not started. The selected " + missing.Role + " source could not be found or is unloaded.");

            var linkTargets = targets.Where(x => x.IsResolved && !x.IsHost).ToList();
            PluginLog.Info("AR-ST DATA REFRESH START", new
            {
                host = host.Title,
                selectedSources = targets.Select(x => new { x.Role, x.Name, x.InstanceUniqueId, hostModel = x.IsHost }).ToList(),
                linkTypesToReload = linkTargets.Select(x => x.LinkTypeId).Distinct().Count()
            });
            progress?.Invoke("Refreshing selected AR and ST links...");

            var reloadedTypeIds = new HashSet<int>();
            foreach (var target in linkTargets)
            {
                var typeId = new ElementId(target.LinkTypeId);
                if (!reloadedTypeIds.Add(target.LinkTypeId))
                {
                    PluginLog.Info("AR-ST LINK RELOAD SKIPPED", new { target.Role, target.Name, reason = "Link type already refreshed", linkTypeId = target.LinkTypeId });
                    continue;
                }

                var linkType = host.GetElement(typeId) as RevitLinkType;
                if (linkType == null)
                    throw RefreshFailure(target, "Its RevitLinkType could not be resolved.", null);

                try
                {
                    progress?.Invoke("Refreshing " + target.Role + " link: " + target.Name + "...");
                    PluginLog.Info("AR-ST LINK RELOAD START", new { target.Role, target.Name, linkInstanceUniqueId = target.InstanceUniqueId, linkTypeId = target.LinkTypeId });
                    var loadResult = linkType.Load();
                    PluginLog.Info("AR-ST LINK RELOAD RESULT", new { target.Role, target.Name, result = DescribeLoadResult(loadResult) });
                }
                catch (Exception ex)
                {
                    PluginLog.Error("AR-ST LINK RELOAD FAILED", ex, new { target.Role, target.Name, linkTypeId = target.LinkTypeId });
                    throw RefreshFailure(target, ex.Message, ex);
                }
            }

            foreach (var target in linkTargets)
            {
                var refreshedInstance = host.GetElement(target.InstanceUniqueId) as RevitLinkInstance;
                var refreshedDocument = refreshedInstance?.GetLinkDocument();
                if (refreshedInstance == null || refreshedDocument == null)
                    throw RefreshFailure(target, "The linked document is unavailable after reload.", null);

                var transform = refreshedInstance.GetTotalTransform() ?? Transform.Identity;
                PluginLog.Info("AR-ST LINK AFTER RELOAD", new
                {
                    target.Role,
                    link = refreshedInstance.Name,
                    document = refreshedDocument.Title,
                    linkInstanceUniqueId = refreshedInstance.UniqueId,
                    transformOrigin = new { transform.Origin.X, transform.Origin.Y, transform.Origin.Z }
                });
            }

            PluginLog.Info("AR-ST DATA REFRESH COMPLETE", new { refreshedLinkTypes = reloadedTypeIds.Count, selectedSources = targets.Count });
            progress?.Invoke("Selected AR and ST links are current. Recalculating AR-ST...");
        }

        private static void AddTarget(ICollection<RefreshTarget> targets, string role, ModelSource source)
        {
            targets.Add(new RefreshTarget
            {
                Role = role,
                IsResolved = source != null,
                IsHost = source?.IsHost == true,
                Name = source?.Name ?? "",
                InstanceUniqueId = source?.Id ?? "",
                LinkTypeId = ElementIdCompatibility.ToInt32(source?.LinkInstance?.GetTypeId())
            });
        }

        private static InvalidOperationException RefreshFailure(RefreshTarget target, string detail, Exception inner)
        {
            var name = target?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = target?.Role ?? "unknown";
            var message = "AR-ST comparison was not started. The selected link could not be updated: " + name + ". " + detail;
            return inner == null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner);
        }

        private static string DescribeLoadResult(object loadResult)
        {
            if (loadResult == null) return "null";
            try
            {
                var property = loadResult.GetType().GetProperty("LoadResult");
                var value = property?.GetValue(loadResult, null);
                return value?.ToString() ?? loadResult.ToString();
            }
            catch { return loadResult.ToString(); }
        }

        private sealed class RefreshTarget
        {
            public string Role { get; set; }
            public bool IsResolved { get; set; }
            public bool IsHost { get; set; }
            public string Name { get; set; }
            public string InstanceUniqueId { get; set; }
            public int LinkTypeId { get; set; }
        }
    }
}
