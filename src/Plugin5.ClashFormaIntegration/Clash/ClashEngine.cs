using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;
using BuildAI.RevitCompatibility;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Clash
{
    public sealed class ClashEngine
    {
        public IList<ClashSourceOption> GetSources(Document host)
        {
            var result = new List<ClashSourceOption> { new ClashSourceOption { Key = "host", DisplayName = "Host model: " + host.Title, Kind = ClashSourceKind.Host } };
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                if (link.GetLinkDocument() == null) continue;
                var linkId = ElementIdCompatibility.ToInt32(link.Id);
                result.Add(new ClashSourceOption { Key = "link:" + linkId, DisplayName = "Revit link: " + link.Name, Kind = ClashSourceKind.Link, LinkInstanceId = linkId });
            }
            return result;
        }

        /// <summary>
        /// Runs the cheap phase and returns a session positioned at the first
        /// candidate. No solid checking has happened yet; call
        /// <see cref="ClashSession.RunPage"/> for that.
        /// <para>
        /// Splitting here follows the cost profile. Collection is one bounding box
        /// per element and the sweep is one comparison per candidate, both cheap;
        /// geometry extraction and Boolean intersection are everything else. Paging
        /// the collection would repeat cheap work, while paging the solid checks
        /// puts the boundary where the time actually is.
        /// </para>
        /// </summary>
        public ClashSession BeginSession(Document host, ClashRunOptions options)
        {
            var timer = Stopwatch.StartNew();
            using (PluginLog.BeginOperation("Plugin5.HardClashDetection"))
            {
                var sources = GetSources(host);
                var sa = sources.FirstOrDefault(x => x.Key == options.SourceAKey);
                var sb = sources.FirstOrDefault(x => x.Key == options.SourceBKey);
                var sc = sources.FirstOrDefault(x => x.Key == options.SourceCKey);
                if (sa == null || sb == null || sc == null)
                    throw new InvalidOperationException("One or more selected sources are unavailable or unloaded.");

                var report = new ClashReport
                {
                    DocumentTitle = host.Title,
                    SourceA = sa.DisplayName,
                    SourceB = sb.DisplayName,
                    SourceC = sc.DisplayName,
                    SelectedCategoryIds = options.GroupACategories.Concat(options.GroupBCategories).Concat(options.GroupCCategories)
                        .Where(id => !MepClashCategories.HardExcludedCategoryIds().Contains(id)).Distinct().ToList()
                };

                var a = Collect(host, sa, options.GroupACategories, report, 'A');
                var b = Collect(host, sb, options.GroupBCategories, report, 'B');
                var c = Collect(host, sc, options.GroupCCategories, report, 'C');
                report.ElementsA = a.Count;
                report.ElementsB = b.Count;
                report.ElementsC = c.Count;
                report.MsInCollect = timer.ElapsedMilliseconds;

                if (a.Count == 0 || b.Count == 0 || c.Count == 0)
                    report.Warnings.Add("One or more sources produced zero usable elements. Check loaded links and category filters.");

                var session = ClashSession.Begin(host, options, a, b, c, report);

                if (report.CandidatePairs == 0)
                    report.Warnings.Add("No overlapping bounding-box candidates were found.");

                timer.Stop();
                report.DurationMs = timer.ElapsedMilliseconds;
                return session;
            }
        }

        /// <summary>
        /// Convenience wrapper preserving the previous all-at-once behaviour:
        /// begins a session and checks every candidate in one go.
        /// </summary>
        public ClashReport Run(Document host, ClashRunOptions options)
        {
            var session = BeginSession(host, options);
            session.RunPage(ClashPageBudget.Unlimited());
            session.ReleaseGeometryCaches();
            FinalizeReport(session.Report);
            return session.Report;
        }

        /// <summary>Adds the warnings that can only be judged once checking has run.</summary>
        public static void FinalizeReport(ClashReport report)
        {
            if (report.CandidatePairs > 0 && report.ExactChecks == 0 && report.Items.Count == 0)
                report.Warnings.Add("Candidates were found but none were solid-checked. Load more results to continue.");
            if (report.ExactChecks > 0 && report.Items.Count == 0)
                report.Warnings.Add("Candidates were checked, but exact solid intersections were empty or failed. Try bounding-box diagnostic mode.");
            PluginLog.Info("Hard clash detection state", new
            {
                report.ElementsA,
                report.ElementsB,
                report.ElementsC,
                report.CandidatePairs,
                report.ExactChecks,
                report.ElementsWithoutBoundingBox,
                report.ElementsWithoutSolid,
                report.BooleanFailures,
                results = report.Items.Count,
                report.MsInCollect,
                report.MsInSweep,
                report.MsInExact
            });
        }

        private static List<ElementProxy> Collect(Document host, ClashSourceOption source, IList<int> categories, ClashReport report, char group)
        {
            Document doc;
            Transform tr;
            int? linkId = null;
            string linkUid = "";
            if (source.Kind == ClashSourceKind.Host)
            {
                doc = host;
                tr = Transform.Identity;
            }
            else
            {
                var link = host.GetElement(new ElementId(source.LinkInstanceId)) as RevitLinkInstance;
                var linkDoc = link?.GetLinkDocument();
                if (link == null || linkDoc == null) return new List<ElementProxy>();
                doc = linkDoc;
                tr = link.GetTotalTransform();
                linkId = source.LinkInstanceId;
                linkUid = link.UniqueId;
            }

            var modelUid = GetModelUid(doc);
            var set = new HashSet<int>(categories ?? new List<int>());
            var result = new List<ElementProxy>();
            var before = 0;

            // Hard exclusions win over any selection. These categories own no solid
            // geometry of their own - logical systems, analytical volumes, view
            // graphics, placeholders, run containers - so admitting them produces
            // only false pairs. Enforced here rather than in the UI so a stale saved
            // selection or a hand-edited settings file cannot reintroduce them.
            var blocked = MepClashCategories.HardExcludedCategoryIds();
            var blockedHits = 0;

            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category == null || e.ViewSpecific) continue;
                if (e.Category.CategoryType != CategoryType.Model) continue;
                if (e is RevitLinkInstance || e is ImportInstance) continue;
                before++;
                var categoryId = ElementIdCompatibility.ToInt32(e.Category.Id);
                if (blocked.Contains(categoryId)) { blockedHits++; continue; }
                if (set.Count > 0 && !set.Contains(categoryId)) continue;
                var proxy = ElementProxy.Create(doc, e, tr, source.Key, source.DisplayName, linkId, linkUid, modelUid);
                if (proxy == null) { report.ElementsWithoutBoundingBox++; continue; }
                result.Add(proxy);
            }

            if (group == 'A') report.CollectedBeforeFilterA = before;
            else if (group == 'B') report.CollectedBeforeFilterB = before;
            else report.CollectedBeforeFilterC = before;

            if (set.Count == 0)
                report.Warnings.Add("Source " + source.DisplayName + " ran with an EMPTY category filter, so every model category was admitted. Expect a very high false-positive rate; check the clash category settings.");

            PluginLog.Info("Clash source collected", new { source = source.DisplayName, beforeFilter = before, elements = result.Count, categoryFilterCount = set.Count, blockedByCatalogue = blockedHits });
            return result;
        }

        private static string GetModelUid(Document d)
        {
            try { return d.ProjectInformation?.UniqueId ?? d.Title; }
            catch { return d.Title; }
        }
    }
}
