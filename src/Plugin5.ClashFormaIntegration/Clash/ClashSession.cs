using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;
using BuildAI.RevitCompatibility;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Clash
{
    /// <summary>
    /// A chunked clash run held open between pages.
    /// <para>
    /// Element collection and the bounding-box sweep run once, in
    /// <see cref="Begin"/>; only the solid checks are paged. That split follows
    /// the cost profile: collection is one bounding box per element, the sweep is
    /// one comparison per candidate, and <c>get_Geometry</c> plus the Boolean
    /// intersections are everything else.
    /// </para>
    /// </summary>
    public sealed class ClashSession
    {
        private const double FeetToMm = 304.8;
        private const double CubicFeetToCubicMm = FeetToMm * FeetToMm * FeetToMm;

        private readonly List<CandidatePair> _candidates = new List<CandidatePair>();
        private readonly double _minVolumeMm3;
        private readonly bool _boxesOnly;

        private ClashSession(ClashReport report, ClashCursor cursor, double minVolumeMm3, bool boxesOnly)
        {
            Report = report;
            Cursor = cursor;
            _minVolumeMm3 = minVolumeMm3;
            _boxesOnly = boxesOnly;
        }

        public ClashReport Report { get; }
        public ClashCursor Cursor { get; }

        /// <summary>Bounding-box phase: collect, index, pair. Runs once.</summary>
        public static ClashSession Begin(
            Document host,
            ClashRunOptions options,
            IList<ElementProxy> a,
            IList<ElementProxy> b,
            IList<ElementProxy> c,
            ClashReport report)
        {
            var timer = Stopwatch.StartNew();
            var cursor = new ClashCursor { ModelFingerprint = Fingerprint(host, a.Count, b.Count, c.Count) };
            var session = new ClashSession(report, cursor, options.MinimumIntersectionVolumeMm3, options.BoundingBoxesOnly);

            var seenPairs = new HashSet<string>(StringComparer.Ordinal);
            if (options.CompareAB) session.Pair(a, b, seenPairs, 0);
            if (options.CompareAC) session.Pair(a, c, seenPairs, 1);
            if (options.CompareBC) session.Pair(b, c, seenPairs, 2);

            // Level-major order is not only for determinism. Results arrive grouped
            // by floor, which is how coordinators work through them: a logged run
            // filtered 3251 findings down to one level and processed 90 rows, a set
            // this ordering would have delivered first with no filtering at all.
            session._candidates.Sort(CompareCandidates);

            cursor.CandidateCount = session._candidates.Count;
            report.CandidatePairs = session._candidates.Count;
            timer.Stop();
            report.MsInSweep = timer.ElapsedMilliseconds;

            PluginLog.Info("Clash candidate phase completed", new
            {
                elementsA = a.Count,
                elementsB = b.Count,
                elementsC = c.Count,
                candidatePairs = session._candidates.Count,
                sweepMs = timer.ElapsedMilliseconds
            });
            return session;
        }

        /// <summary>
        /// Solid-checks the next slice of candidates. Stops on the first budget
        /// limit reached, leaving the cursor on the next unchecked candidate.
        /// </summary>
        public ClashPageResult RunPage(ClashPageBudget budget, Func<bool> cancelled = null)
        {
            var page = new ClashPageResult();
            var timer = Stopwatch.StartNew();
            var checksThisPage = 0;

            while (Cursor.NextCandidateIndex < _candidates.Count)
            {
                if (cancelled != null && cancelled())
                {
                    page.StopReason = ClashPageStopReason.Cancelled;
                    break;
                }
                if (page.Items.Count >= budget.MaxResults)
                {
                    page.StopReason = ClashPageStopReason.ResultCapReached;
                    break;
                }
                if (checksThisPage >= budget.MaxExactChecks)
                {
                    page.StopReason = ClashPageStopReason.WorkCapReached;
                    break;
                }
                if (timer.Elapsed >= budget.MaxDuration)
                {
                    page.StopReason = ClashPageStopReason.TimeCapReached;
                    break;
                }

                var candidate = _candidates[Cursor.NextCandidateIndex++];
                page.CandidatesConsumed++;

                if (!Cursor.SeenPairKeys.Add(candidate.PairKey)) continue;

                if (_boxesOnly)
                {
                    page.Items.Add(BuildItem(candidate.A, candidate.B, Midpoint(candidate.A, candidate.B), 0,
                        "Bounding-box diagnostic candidate. Confirm with exact solid mode."));
                    continue;
                }

                // Free exact rejection: the intersection volume of two solids can
                // never exceed the intersection volume of their bounding boxes, so a
                // box overlap below the threshold rules the pair out without touching
                // geometry. Six multiplications instead of a Boolean operation.
                if (_minVolumeMm3 > 0 && BoxOverlapMm3(candidate.A, candidate.B) < _minVolumeMm3) continue;

                checksThisPage++;
                var item = CheckExact(candidate.A, candidate.B);
                if (item != null) page.Items.Add(item);
            }

            timer.Stop();
            if (Cursor.NextCandidateIndex >= _candidates.Count && page.StopReason == ClashPageStopReason.Exhausted)
                page.StopReason = ClashPageStopReason.Exhausted;

            page.ExactChecksPerformed = checksThisPage;
            page.ElapsedMs = timer.ElapsedMilliseconds;

            Cursor.TotalExactChecks += checksThisPage;
            Report.ExactChecks += checksThisPage;
            Report.MsInExact += timer.ElapsedMilliseconds;
            if (checksThisPage > 0) Cursor.MsPerExactCheck = (double)timer.ElapsedMilliseconds / checksThisPage;

            Report.Items.AddRange(page.Items);

            PluginLog.Info("Clash page completed", new
            {
                stop = page.StopReason.ToString(),
                results = page.Items.Count,
                exactChecks = checksThisPage,
                candidatesConsumed = page.CandidatesConsumed,
                cursor = Cursor.NextCandidateIndex,
                of = _candidates.Count,
                elapsedMs = page.ElapsedMs,
                msPerCheck = Cursor.MsPerExactCheck
            });
            return page;
        }

        /// <summary>
        /// Releases cached solids. Called between pages: the cache is right within
        /// a page, where one wall may appear in hundreds of pairs, but across a
        /// paged session it only grows, and solids are heavy. Level-major ordering
        /// keeps re-extraction at page boundaries small.
        /// </summary>
        public void ReleaseGeometryCaches()
        {
            var released = 0;
            foreach (var candidate in _candidates)
            {
                if (candidate.A.ReleaseSolids()) released++;
                if (candidate.B.ReleaseSolids()) released++;
            }
            PluginLog.Info("Clash geometry caches released", new { proxies = released });
        }

        /// <summary>
        /// Confirms the model still matches the one the candidate array was built
        /// from. Revit exposes no cheap "document changed" signal, so this is an
        /// approximation: it will miss an in-place edit that alters no element
        /// counts. The failure mode is a stale result the user can re-run, not a
        /// wrong Issue.
        /// </summary>
        public bool MatchesModel(Document host, int countA, int countB, int countC)
        {
            return string.Equals(Cursor.ModelFingerprint, Fingerprint(host, countA, countB, countC), StringComparison.Ordinal);
        }

        // ---- bounding-box phase -------------------------------------------------

        /// <summary>
        /// Pairs two element sets through a uniform spatial grid.
        /// <para>
        /// The previous implementation sorted both sides by Min.X and, for every
        /// left element, walked the right list from index zero skipping everything
        /// already behind the sweep line. The tail was pruned but the head was not,
        /// so the skip phase alone was O(|A|x|B|) - at 20 000 elements per source,
        /// four hundred million comparisons spent doing nothing. Bucketing by cell
        /// makes the cost proportional to the pairs actually reported.
        /// </para>
        /// </summary>
        private void Pair(IList<ElementProxy> left, IList<ElementProxy> right, HashSet<string> seenPairs, int sourcePairIndex)
        {
            if (left.Count == 0 || right.Count == 0) return;

            var cell = ChooseCellSize(right);
            var grid = new Dictionary<long, List<ElementProxy>>();
            foreach (var proxy in right)
                foreach (var key in CellKeys(proxy, cell))
                {
                    List<ElementProxy> bucket;
                    if (!grid.TryGetValue(key, out bucket)) grid[key] = bucket = new List<ElementProxy>();
                    bucket.Add(proxy);
                }

            var localSeen = new HashSet<long>();
            foreach (var l in left)
            {
                localSeen.Clear();
                foreach (var key in CellKeys(l, cell))
                {
                    List<ElementProxy> bucket;
                    if (!grid.TryGetValue(key, out bucket)) continue;
                    foreach (var r in bucket)
                    {
                        // One element can occupy several cells, so the same pair can
                        // surface more than once per left element.
                        if (!localSeen.Add(r.Serial)) continue;
                        if (l.SourceKey == r.SourceKey && l.ElementId == r.ElementId) continue;
                        if (!BoxesOverlap(l, r)) continue;

                        var pairKey = PairKeyOf(l, r);
                        if (!seenPairs.Add(pairKey)) continue;

                        _candidates.Add(new CandidatePair
                        {
                            A = l,
                            B = r,
                            PairKey = pairKey,
                            SourcePairIndex = sourcePairIndex,
                            LevelSortKey = FirstNonEmpty(l.LevelName, r.LevelName) ?? ""
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Cell size drives the grid's efficiency: too small and large elements
        /// occupy thousands of cells, too large and every query returns everything.
        /// The median element extent is a reasonable compromise and needs no tuning
        /// per model.
        /// </summary>
        private static double ChooseCellSize(IList<ElementProxy> proxies)
        {
            var extents = new List<double>(proxies.Count);
            foreach (var p in proxies)
            {
                var dx = p.MaxX - p.MinX;
                var dy = p.MaxY - p.MinY;
                var dz = p.MaxZ - p.MinZ;
                var largest = dx > dy ? dx : dy;
                if (dz > largest) largest = dz;
                extents.Add(largest);
            }
            extents.Sort();
            var median = extents.Count == 0 ? 0 : extents[extents.Count / 2];
            if (median < 1.0) median = 1.0;      // feet; avoids a degenerate grid
            if (median > 50.0) median = 50.0;
            return median;
        }

        private static IEnumerable<long> CellKeys(ElementProxy p, double cell)
        {
            var x0 = (int)Math.Floor(p.MinX / cell);
            var x1 = (int)Math.Floor(p.MaxX / cell);
            var y0 = (int)Math.Floor(p.MinY / cell);
            var y1 = (int)Math.Floor(p.MaxY / cell);
            var z0 = (int)Math.Floor(p.MinZ / cell);
            var z1 = (int)Math.Floor(p.MaxZ / cell);

            // An element spanning an absurd number of cells would defeat the grid.
            // Falling back to a single sentinel cell keeps it correct: it is then
            // compared against everything, which is what the old code did anyway.
            var span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (span > 4096) { yield return long.MinValue; yield break; }

            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    for (var z = z0; z <= z1; z++)
                        yield return Key(x, y, z);
        }

        private static long Key(int x, int y, int z)
        {
            unchecked
            {
                long h = x;
                h = (h * 1000003) ^ y;
                h = (h * 1000003) ^ z;
                return h;
            }
        }

        private static int CompareCandidates(CandidatePair l, CandidatePair r)
        {
            var byLevel = string.CompareOrdinal(l.LevelSortKey, r.LevelSortKey);
            if (byLevel != 0) return byLevel;
            if (l.SourcePairIndex != r.SourcePairIndex) return l.SourcePairIndex.CompareTo(r.SourcePairIndex);
            var byX = l.A.MinX.CompareTo(r.A.MinX);
            if (byX != 0) return byX;
            if (l.A.ElementId != r.A.ElementId) return l.A.ElementId.CompareTo(r.A.ElementId);
            return l.B.ElementId.CompareTo(r.B.ElementId);
        }

        private static string PairKeyOf(ElementProxy a, ElementProxy b)
        {
            var first = a.SourceKey + ":" + a.ElementId;
            var second = b.SourceKey + ":" + b.ElementId;
            return string.CompareOrdinal(first, second) < 0 ? first + "|" + second : second + "|" + first;
        }

        // ---- exact phase --------------------------------------------------------

        private ClashItem CheckExact(ElementProxy a, ElementProxy b)
        {
            var sa = a.GetSolids();
            var sb = b.GetSolids();
            if (sa.Count == 0 || sb.Count == 0) { Report.ElementsWithoutSolid++; return null; }

            double volume = 0;
            var weighted = XYZ.Zero;
            foreach (var x in sa)
                foreach (var y in sb)
                    try
                    {
                        var i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                        if (i == null || i.Volume <= 1e-10) continue;
                        volume += i.Volume;
                        weighted += GetCenter(i).Multiply(i.Volume);
                    }
                    catch { Report.BooleanFailures++; }

            var mm3 = volume * CubicFeetToCubicMm;
            if (mm3 < Math.Max(0, _minVolumeMm3)) return null;
            var point = volume > 0 ? weighted.Divide(volume) : Midpoint(a, b);
            return BuildItem(a, b, point, mm3, "Resolve the geometric intersection and run the check again.");
        }

        private ClashItem BuildItem(ElementProxy a, ElementProxy b, XYZ point, double volumeMm3, string description)
        {
            return new ClashItem
            {
                Kind = ClashKind.Hard,
                ResultKey = BuildAI.Core.Issues.ResultKeyBuilder.Clash(
                    a.ModelUid, a.LinkInstanceUniqueId, a.ElementUniqueId, a.ElementId,
                    b.ModelUid, b.LinkInstanceUniqueId, b.ElementUniqueId, b.ElementId),
                ModelUidA = a.ModelUid,
                ModelUidB = b.ModelUid,
                ElementAUniqueId = a.ElementUniqueId,
                ElementBUniqueId = b.ElementUniqueId,
                LinkInstanceAUniqueId = a.LinkInstanceUniqueId,
                LinkInstanceBUniqueId = b.LinkInstanceUniqueId,
                ElementAId = a.ElementId,
                ElementBId = b.ElementId,
                LinkInstanceAId = a.LinkInstanceId,
                LinkInstanceBId = b.LinkInstanceId,
                SourceA = a.SourceName,
                SourceB = b.SourceName,
                ElementA = a.DisplayName,
                ElementB = b.DisplayName,
                CategoryA = a.CategoryName,
                CategoryB = b.CategoryName,
                Level = FirstNonEmpty(a.LevelName, b.LevelName) ?? "",
                PrimaryLevel = a.LevelName ?? "",
                SecondaryLevel = b.LevelName ?? "",
                IntersectionVolumeMm3 = volumeMm3,
                X = point.X * FeetToMm,
                Y = point.Y * FeetToMm,
                Z = point.Z * FeetToMm,
                Recommendation = description
            };
        }

        // ---- helpers ------------------------------------------------------------

        private static double BoxOverlapMm3(ElementProxy a, ElementProxy b)
        {
            var dx = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var dy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            var dz = Math.Min(a.MaxZ, b.MaxZ) - Math.Max(a.MinZ, b.MinZ);
            if (dx <= 0 || dy <= 0 || dz <= 0) return 0;
            return dx * dy * dz * CubicFeetToCubicMm;
        }

        private static bool BoxesOverlap(ElementProxy a, ElementProxy b)
        {
            return a.MinX <= b.MaxX && a.MaxX >= b.MinX &&
                   a.MinY <= b.MaxY && a.MaxY >= b.MinY &&
                   a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
        }

        private static XYZ Midpoint(ElementProxy a, ElementProxy b)
        {
            return new XYZ(
                (Math.Max(a.MinX, b.MinX) + Math.Min(a.MaxX, b.MaxX)) / 2,
                (Math.Max(a.MinY, b.MinY) + Math.Min(a.MaxY, b.MaxY)) / 2,
                (Math.Max(a.MinZ, b.MinZ) + Math.Min(a.MaxZ, b.MaxZ)) / 2);
        }

        private static XYZ GetCenter(Solid s)
        {
            try { return s.ComputeCentroid(); }
            catch { var b = s.GetBoundingBox(); return b.Transform.OfPoint((b.Min + b.Max).Multiply(.5)); }
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return string.IsNullOrWhiteSpace(a) ? b : a;
        }

        private static string Fingerprint(Document host, int countA, int countB, int countC)
        {
            var sb = new StringBuilder();
            sb.Append(host.Title).Append('|').Append(countA).Append('|').Append(countB).Append('|').Append(countC);
            try
            {
                var links = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(x =>
                    {
                        var doc = x.GetLinkDocument();
                        return x.UniqueId + ":" + (doc == null ? "unloaded" : doc.Title);
                    })
                    .OrderBy(x => x, StringComparer.Ordinal);
                foreach (var link in links) sb.Append('|').Append(link);
            }
            catch { /* a fingerprint without link detail is still better than none */ }

            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");
        }

        private sealed class CandidatePair
        {
            public ElementProxy A;
            public ElementProxy B;
            public string PairKey;
            public int SourcePairIndex;
            public string LevelSortKey;
        }
    }
}
