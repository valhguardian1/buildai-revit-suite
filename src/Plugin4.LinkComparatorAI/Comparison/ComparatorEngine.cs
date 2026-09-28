using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Issues;
using BuildAI.RevitCompatibility;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Comparison
{
    public sealed class ComparatorEngine
    {
        public ComparisonReport Run(Document host, ComparatorSettings settings, bool includeGeometry, bool includeRooms)
        {
            var sw = Stopwatch.StartNew();
            var report = new ComparisonReport();
            settings = settings ?? new ComparatorSettings();
            try
            {
                var ar = LinkResolver.ResolveArchitectural(host, settings);
                var kr = LinkResolver.ResolveStructural(host, settings);
                if (ar == null) report.Warnings.Add("Architectural source was not found or is unloaded. Open Settings and select a valid source.");
                if (kr == null && includeGeometry) report.Warnings.Add("Structural source was not found or is unloaded. Open Settings and select a valid source.");
                if (ar == null || (includeGeometry && kr == null)) return Finish(report, sw);
                if (includeGeometry && ar.Id == kr.Id)
                {
                    report.Warnings.Add("Architectural and structural sources are the same. Select two different sources.");
                    return Finish(report, sw);
                }

                report.ArchitecturalModel = ar.Name;
                report.StructuralModel = kr?.Name ?? "";
                report.Diagnostics.Add("AR source: " + ar.Name + (ar.IsHost ? " (Host Model)" : " (Revit Link)"));
                if (kr != null) report.Diagnostics.Add("ST source: " + kr.Name + (kr.IsHost ? " (Host Model)" : " (Revit Link)"));
                if (includeGeometry) CompareModels(ar, kr, settings, report);
                if (includeRooms) CheckRoomHeights(ar, settings, report);

                if (report.Issues.Count == 0 && report.Warnings.Count == 0)
                    report.Warnings.Add("Comparison completed successfully. No mismatches were found.");
                return Finish(report, sw);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Comparator engine failed", ex);
                report.Warnings.Add("Comparison failed: " + ex.Message);
                return Finish(report, sw);
            }
        }

        private static ComparisonReport Finish(ComparisonReport report, Stopwatch sw)
        {
            sw.Stop(); report.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            report.Diagnostics.Add("AR elements collected: " + report.ArchitecturalElementsCollected);
            report.Diagnostics.Add("ST elements collected: " + report.StructuralElementsCollected);
            report.Diagnostics.Add("Proxies created: " + report.ProxiesCreated);
            report.Diagnostics.Add("Elements skipped (no usable bounding box): " + report.ElementsSkipped);
            report.Diagnostics.Add("Pairs compared: " + report.PairsCompared);
            report.Diagnostics.Add("Issues found: " + report.Issues.Count);
            report.Diagnostics.Add("Elapsed: " + report.ElapsedMilliseconds + " ms");
            PluginLog.Info("AR/ST comparison finished", new { report.ArchitecturalModel, report.StructuralModel, report.ArchitecturalElementsCollected, report.StructuralElementsCollected, report.ProxiesCreated, report.ElementsSkipped, report.PairsCompared, issues = report.Issues.Count, report.ElapsedMilliseconds });
            return report;
        }

        private static void CompareModels(ModelSource ar, ModelSource kr, ComparatorSettings s, ComparisonReport r)
        {
            // Coordination scope requested by BuildAI: only physical load-bearing/building elements.
            // Views, detail groups, openings, grids, annotations and other non-physical objects are intentionally excluded.
            CompareCategory(ar, kr, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Walls, ComparatorCheckType.Wall, s, r);
            CompareCategory(ar, kr, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Floors, ComparatorCheckType.Floor, s, r);
            CompareCategory(ar, kr, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, ComparatorCheckType.Column, s, r);
            CompareCategory(ar, kr, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumns, ComparatorCheckType.Column, s, r);
            CompareCategory(ar, kr, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFraming, ComparatorCheckType.Beam, s, r);
        }

        private static List<ElementProxy> Collect(ModelSource source, BuiltInCategory bic, ComparisonReport report, bool architectural)
        {
            var result = new List<ElementProxy>();
            if (source?.Document == null) return result;
            IList<Element> elements;
            try { elements = new FilteredElementCollector(source.Document).OfCategory(bic).WhereElementIsNotElementType().ToElements(); }
            catch (Exception ex) { report.Warnings.Add($"Failed to collect {bic} from {source.Name}: {ex.Message}"); return result; }
            if (architectural) report.ArchitecturalElementsCollected += elements.Count; else report.StructuralElementsCollected += elements.Count;
            foreach (var element in elements)
            {
                var proxy = GeometryUtil.CreateProxy(source.Document, element, source.Transform);
                if (proxy == null) report.ElementsSkipped++; else { report.ProxiesCreated++; result.Add(proxy); }
            }
            return result;
        }

        private static void CompareCategory(ModelSource arSource, ModelSource krSource, BuiltInCategory arCategory, BuiltInCategory krCategory, ComparatorCheckType type, ComparatorSettings settings, ComparisonReport report, bool matchByName = false)
        {
            var ar = Collect(arSource, arCategory, report, true); var kr = Collect(krSource, krCategory, report, false);
            report.Diagnostics.Add($"{type}: AR={ar.Count}, ST={kr.Count}");
            if (ar.Count == 0 && kr.Count == 0) return;
            var used = new HashSet<string>();
            foreach (var a in ar)
            {
                ElementProxy best = null;
                if (matchByName) best = kr.FirstOrDefault(x => !used.Contains(x.UniqueId) && string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
                if (best == null)
                {
                    var levelToleranceFeet = GeometryUtil.ToFeet(Math.Max(200.0, settings.ElevationToleranceMm * 2.0));
                    var candidates = kr.Where(x => !used.Contains(x.UniqueId) && LevelsCompatible(a, x, levelToleranceFeet));
                    best = candidates.OrderBy(x => GeometryUtil.HorizontalDistanceMm(a, x)).FirstOrDefault();
                }
                var searchLimit = Math.Max(500, settings.PositionToleranceMm * 20);
                if (best == null || GeometryUtil.HorizontalDistanceMm(a, best) > searchLimit) { report.Issues.Add(NewMissing(type, a, arSource, krSource, true)); continue; }
                used.Add(best.UniqueId); report.PairsCompared++; AddMismatchIfNeeded(type, a, best, arSource, krSource, settings, report);
            }
            foreach (var b in kr.Where(x => !used.Contains(x.UniqueId))) report.Issues.Add(NewMissing(type, b, arSource, krSource, false));
            var levelSummary = report.Issues.Where(x => x.CheckType == type).GroupBy(x => string.IsNullOrWhiteSpace(x.Level) ? "<no level>" : x.Level).OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Count());
            report.Diagnostics.Add(type + " issues by level: " + string.Join(", ", levelSummary));
        }


        private static bool LevelsCompatible(ElementProxy a, ElementProxy b, double toleranceFeet)
        {
            if (!double.IsNaN(a.LevelElevation) && !double.IsNaN(b.LevelElevation))
                return Math.Abs(a.LevelElevation - b.LevelElevation) <= toleranceFeet;
            if (!string.IsNullOrWhiteSpace(a.Level) && !string.IsNullOrWhiteSpace(b.Level))
                return string.Equals(a.Level.Trim(), b.Level.Trim(), StringComparison.OrdinalIgnoreCase);
            return Math.Abs(a.Center.Z - b.Center.Z) <= Math.Max(toleranceFeet, 1.0);
        }
        private static void AddMismatchIfNeeded(ComparatorCheckType type, ElementProxy a, ElementProxy b, ModelSource ar, ModelSource kr, ComparatorSettings s, ComparisonReport report)
        {
            var pos=GeometryUtil.HorizontalDistanceMm(a,b); var z=GeometryUtil.ElevationDeltaMm(a,b); var size=GeometryUtil.MaxSizeDeltaMm(a,b);
            if(pos<=s.PositionToleranceMm&&z<=s.ElevationToleranceMm&&size<=s.PositionToleranceMm)return;
            var delta=Math.Max(pos,Math.Max(z,size)); var severity=delta>100?IssueSeverity.Critical:delta>25?IssueSeverity.Important:IssueSeverity.Minor;
            var issue=new ComparisonIssue{CheckType=type,Severity=severity,Level=!string.IsNullOrWhiteSpace(a.Level)?a.Level:b.Level,Category=a.Category,ArchitecturalCategory=a.Category,StructuralCategory=b.Category,Title=$"Mismatch: {a.Name}",Description=$"Position: {pos:0.#} mm; elevation: {z:0.#} mm; size: {size:0.#} mm.",Recommendation="Check coordinates, elevation and type size in AR and ST.",DeltaMm=delta,ArchitecturalLinkUniqueId=ar.LinkInstance?.UniqueId??"",ArchitecturalElementUniqueId=a.UniqueId,ArchitecturalElementId=a.Id,StructuralLinkUniqueId=kr.LinkInstance?.UniqueId??"",StructuralElementUniqueId=b.UniqueId,StructuralElementId=b.Id,X=(a.Center.X+b.Center.X)*0.5,Y=(a.Center.Y+b.Center.Y)*0.5,Z=(a.Center.Z+b.Center.Z)*0.5};ApplyIssueIdentity(issue,ar,kr);report.Issues.Add(issue);
        }

        private static ComparisonIssue NewMissing(ComparatorCheckType type, ElementProxy x, ModelSource ar, ModelSource kr, bool missingInKr){var issue=new ComparisonIssue{CheckType=type,Severity=IssueSeverity.Critical,Level=x.Level,Category=x.Category,ArchitecturalCategory=missingInKr?x.Category:"",StructuralCategory=missingInKr?"":x.Category,Title=missingInKr?$"No ST match: {x.Name}":$"No AR match: {x.Name}",Description=missingInKr?"The architectural element has no structural pair.":"The structural element has no architectural pair.",Recommendation="Check whether the element is required and coordinate it between disciplines.",ArchitecturalLinkUniqueId=ar.LinkInstance?.UniqueId??"",ArchitecturalElementUniqueId=missingInKr?x.UniqueId:"",ArchitecturalElementId=missingInKr?x.Id:0,StructuralLinkUniqueId=kr?.LinkInstance?.UniqueId??"",StructuralElementUniqueId=missingInKr?"":x.UniqueId,StructuralElementId=missingInKr?0:x.Id,X=x.Center.X,Y=x.Center.Y,Z=x.Center.Z};ApplyIssueIdentity(issue,ar,kr);return issue;}

        private static void CompareOpenings(ModelSource ar, ModelSource kr, ComparatorSettings s, ComparisonReport r)
        {
            var a=CollectOpenings(ar,r,true);var b=CollectOpenings(kr,r,false);var used=new HashSet<string>();r.Diagnostics.Add($"Opening: AR={a.Count}, ST={b.Count}");
            foreach(var x in a){var y=b.Where(z=>!used.Contains(z.UniqueId)).OrderBy(z=>GeometryUtil.CenterDistanceMm(x,z)).FirstOrDefault();if(y==null||GeometryUtil.CenterDistanceMm(x,y)>1000){r.Issues.Add(NewMissing(ComparatorCheckType.Opening,x,ar,kr,true));continue;}used.Add(y.UniqueId);r.PairsCompared++;AddMismatchIfNeeded(ComparatorCheckType.Opening,x,y,ar,kr,s,r);}foreach(var y in b.Where(z=>!used.Contains(z.UniqueId)))r.Issues.Add(NewMissing(ComparatorCheckType.Opening,y,ar,kr,false));
        }

        private static List<ElementProxy> CollectOpenings(ModelSource source, ComparisonReport report, bool architectural)
        {
            var result=new List<ElementProxy>();if(source?.Document==null)return result;
            foreach(var e in new FilteredElementCollector(source.Document).WhereElementIsNotElementType())
            {
                var text=CategoryNaming.Key(e)+" "+(e.Name??"");   // BuiltInCategory key, not the Revit-UI name if(text.IndexOf("Opening",StringComparison.OrdinalIgnoreCase)<0&&text.IndexOf("Shaft",StringComparison.OrdinalIgnoreCase)<0&&text.IndexOf("פתח",StringComparison.OrdinalIgnoreCase)<0)continue;
                if(architectural)report.ArchitecturalElementsCollected++;else report.StructuralElementsCollected++;
                var p=GeometryUtil.CreateProxy(source.Document,e,source.Transform);if(p==null)report.ElementsSkipped++;else{report.ProxiesCreated++;result.Add(p);}
            }return result;
        }

        private static void CheckRoomHeights(ModelSource ar, ComparatorSettings s, ComparisonReport report)
        {
            if(ar?.Document==null)return;
            var checkedRooms = 0;
            var outsideRange = 0;
            report.Diagnostics.Add($"Room height settings applied: min={s.RoomMinHeightMm:0.###} mm; max={s.RoomMaxHeightMm:0.###} mm");
            PluginLog.Info("Room height calculation started", new { source = ar.Name, minHeightMm = s.RoomMinHeightMm, maxHeightMm = s.RoomMaxHeightMm });
            foreach(var room in new FilteredElementCollector(ar.Document).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                report.ArchitecturalElementsCollected++;
                var p=room.get_Parameter(BuiltInParameter.ROOM_HEIGHT)??room.LookupParameter("Unbounded Height")??room.LookupParameter("Vysota");if(p==null||p.StorageType!=StorageType.Double)continue;
                checkedRooms++;
                var mm=GeometryUtil.ToMm(p.AsDouble());if(mm>=s.RoomMinHeightMm&&mm<=s.RoomMaxHeightMm)continue;outsideRange++;var level=GeometryUtil.ResolveLevel(ar.Document,room);var name=room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()??room.Name;var number=room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString()??"";var delta=mm<s.RoomMinHeightMm?s.RoomMinHeightMm-mm:mm-s.RoomMaxHeightMm;
                var bb=room.get_BoundingBox(null);var center=bb==null?XYZ.Zero:(bb.Min+bb.Max)*0.5;center=ar.Transform?.OfPoint(center)??center;
                var issue=new ComparisonIssue{CheckType=ComparatorCheckType.RoomHeight,Severity=delta>200?IssueSeverity.Critical:IssueSeverity.Important,Level=level,Category=CategoryNaming.Display(room),ArchitecturalCategory=CategoryNaming.Display(room),Title=$"Room {number} {name}: height {mm:0} mm",Description=$"Allowed range: {s.RoomMinHeightMm:0}-{s.RoomMaxHeightMm:0} mm. Actual height: {mm:0} mm.",Recommendation="Check the room upper boundary, level and offset.",DeltaMm=delta,ArchitecturalLinkUniqueId=ar.LinkInstance?.UniqueId??"",ArchitecturalElementUniqueId=room.UniqueId,ArchitecturalElementId=ElementIdCompatibility.ToInt32(room.Id),X=center.X,Y=center.Y,Z=center.Z};ApplyIssueIdentity(issue,ar,null);report.Issues.Add(issue);
            }
            report.Diagnostics.Add($"Room heights checked: {checkedRooms}; outside configured range: {outsideRange}");
            PluginLog.Info("Room height calculation completed", new { source = ar.Name, checkedRooms, outsideRange, minHeightMm = s.RoomMinHeightMm, maxHeightMm = s.RoomMaxHeightMm });
        }

        private static string ModelUid(ModelSource source)
        {
            try { return source?.Document?.ProjectInformation?.UniqueId ?? source?.Document?.Title ?? ""; }
            catch { return source?.Document?.Title ?? ""; }
        }

        private static void ApplyIssueIdentity(ComparisonIssue issue, ModelSource ar, ModelSource st)
        {
            issue.ArchitecturalModelUid = ModelUid(ar);
            issue.StructuralModelUid = ModelUid(st);
            // Source names are user-authored file/link names and may be in any
            // language. Russian is forbidden product-wide, so a Cyrillic name is
            // replaced by the neutral discipline label rather than shipped into an Issue.
            issue.ArchitecturalSourceName = CategoryNaming.OrFallback(
                ar?.Name ?? ar?.Document?.Title ?? "", Loc.T("Discipline_Architecture"));
            issue.StructuralSourceName = CategoryNaming.OrFallback(
                st?.Name ?? st?.Document?.Title ?? "", Loc.T("Discipline_Structure"));
            issue.ResultKey = ResultKeyBuilder.ArSt(
                issue.ArchitecturalModelUid, issue.ArchitecturalLinkUniqueId, issue.ArchitecturalElementUniqueId, issue.ArchitecturalElementId,
                issue.StructuralModelUid, issue.StructuralLinkUniqueId, issue.StructuralElementUniqueId, issue.StructuralElementId,
                issue.CheckType.ToString());
        }
    }
}
