using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Configuration;
using BuildAI.Core.Logging;
using BuildAI.Core.Models;
using BuildAI.RevitCompatibility;

namespace Plugin2.VolumeEstimator.Estimation
{
    /// <summary>
    /// Computes material volumes/areas/counts from a federated model (specification §5.2–5.3,
    /// scenario A). Reads the host document AND every loaded Revit link in one
    /// pass, and applies the ownership rule (see <see cref="DisciplineRules"/>)
    /// so a material specified in more than one discipline (e.g. a concrete wall
    /// present in both AR and ST) is counted exactly once.
    ///
    /// READ-ONLY: only element/material data is read — no transactions, no edits —
    /// so it is safe from a command or an ExternalEvent handler. No network here;
    /// the caller hands the result to BuildAiClient for off-thread sending.
    ///
    /// Ownership filter, per element E from source model S with discipline D(S):
    ///   owner = explicit section param (if set) ?? DisciplineRules.OwnerByCategory(E)
    ///   • owner == null            -> count (E belongs to whatever model holds it)
    ///   • D(S) known & owner==D(S) -> count
    ///   • D(S) known & owner!=D(S) -> SKIP (counted from its owning model)
    ///   • D(S) unknown             -> count as-is, and flag the result
    /// Grouping key (specification §5.3): category + material_name + material_class + level + section.
    /// </summary>
    public static class VolumeTakeoff
    {
        public static EstimationResult Estimate(Document hostDoc, BuildAiOptions options)
        {
            var result = new EstimationResult();
            if (hostDoc == null) return result;
            options = options ?? new BuildAiOptions();

            var pi = hostDoc.ProjectInformation;
            try { result.ProjectId = pi?.UniqueId ?? ""; } catch { }
            try { result.ProjectName = pi?.Name ?? ""; } catch { }

            var groups = new Dictionary<GroupKey, Bucket>();
            var levelCache = new Dictionary<string, string>(); // key: docTitle|levelId

            foreach (var src in CollectSources(hostDoc, options))
            {
                string discipline = DisciplineRules.ResolveSource(src.Title, options);
                bool disciplineKnown = !string.IsNullOrEmpty(discipline);
                if (!disciplineKnown) result.HasUndeterminedSource = true;
                result.SourceNotes.Add($"{src.Title} -> {(disciplineKnown ? discipline : "?")}");

                var collector = new FilteredElementCollector(src.Doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                foreach (var el in collector)
                {
                    if (el?.Category == null) continue;
                    if (!IsTakeoffCategory(ElementIdCompatibility.ToInt32(el.Category.Id))) continue;
                    result.ElementsScanned++;

                    // --- ownership decision -------------------------------------
                    string owner = ExplicitSection(el, options.SectionParameterName)
                                   ?? DisciplineRules.OwnerByCategory(el);

                    string section;
                    if (owner == null)
                    {
                        // No rule: element belongs to its home model.
                        section = disciplineKnown ? discipline : "";
                    }
                    else if (!disciplineKnown || owner == discipline)
                    {
                        section = owner;
                    }
                    else
                    {
                        continue; // owned by another discipline — skip this copy
                    }

                    result.ElementsCounted++;
                    Accumulate(groups, src.Doc, el, section, levelCache);
                }
            }

            result.Rows = groups
                .Where(kv => kv.Value.VolumeM3 > 0 || kv.Value.AreaM2 > 0)
                .OrderBy(kv => kv.Key.Section)
                .ThenBy(kv => kv.Key.Level)
                .ThenBy(kv => kv.Key.Category)
                .ThenBy(kv => kv.Key.MaterialName)
                .Select(kv => new RevitMaterialItem
                {
                    Category      = kv.Key.Category,
                    MaterialName  = kv.Key.MaterialName,
                    MaterialClass = kv.Key.MaterialClass,
                    Level         = kv.Key.Level,
                    Section       = kv.Key.Section,
                    VolumeM3      = Round(kv.Value.VolumeM3),
                    AreaM2        = kv.Value.AreaM2 > 0 ? (double?)Round(kv.Value.AreaM2) : null,
                    Count         = kv.Value.Count
                })
                .ToList();

            PluginLog.Info(
                $"VolumeEstimator: sources=[{string.Join(", ", result.SourceNotes)}], " +
                $"scanned={result.ElementsScanned}, counted={result.ElementsCounted}, " +
                $"groups={result.Rows.Count}, undetermined={result.HasUndeterminedSource}");
            return result;
        }

        // ---- sources: host + de-duplicated loaded links ----------------------
        private struct Source { public Document Doc; public string Title; }

        private static IEnumerable<Source> CollectSources(Document hostDoc, BuildAiOptions options)
        {
            yield return new Source { Doc = hostDoc, Title = SafeTitle(hostDoc) };
            if (!options.IncludeLinkedModels) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IList<Element> links = null;
            try
            {
                links = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(RevitLinkInstance))
                    .ToElements();
            }
            catch (Exception ex) { PluginLog.Warn($"Enumerating links failed: {ex.Message}"); }
            if (links == null) yield break;

            foreach (var e in links)
            {
                Document ld = null;
                try { ld = (e as RevitLinkInstance)?.GetLinkDocument(); } catch { }
                if (ld == null) continue; // link unloaded / not found

                // A link file placed multiple times must be counted only once.
                var key = SafePath(ld);
                if (!seen.Add(key)) continue;

                yield return new Source { Doc = ld, Title = SafeTitle(ld) };
            }
        }

        // ---- accumulation ----------------------------------------------------
        private static void Accumulate(
            Dictionary<GroupKey, Bucket> groups, Document doc, Element el,
            string section, Dictionary<string, string> levelCache)
        {
            string categoryName = SafeCategory(el.Category);
            string level = ResolveLevel(doc, el, levelCache);

            bool hadMaterial = false;
            try
            {
                var matIds = el.GetMaterialIds(false);
                if (matIds != null)
                {
                    foreach (var matId in matIds)
                    {
                        double volInternal, areaInternal;
                        try { volInternal  = el.GetMaterialVolume(matId); }     catch { volInternal = 0; }
                        try { areaInternal = el.GetMaterialArea(matId, false); } catch { areaInternal = 0; }
                        if (volInternal <= 0 && areaInternal <= 0) continue;

                        var mat = doc.GetElement(matId) as Material;
                        var key = new GroupKey(categoryName, SafeMaterialName(mat),
                                               SafeMaterialClass(mat), level, section);
                        var bucket = Get(groups, key);
                        bucket.VolumeM3 += ToCubicMeters(volInternal);
                        bucket.AreaM2   += ToSquareMeters(areaInternal);
                        bucket.Count    += 1;
                        hadMaterial = true;
                    }
                }
            }
            catch (Exception ex) { PluginLog.Warn($"Material takeoff failed for {el.Id}: {ex.Message}"); }

            if (!hadMaterial)
            {
                double volInternal = 0;
                try
                {
                    var p = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                    if (p != null && p.StorageType == StorageType.Double) volInternal = p.AsDouble();
                }
                catch { }

                if (volInternal > 0)
                {
                    var key = new GroupKey(categoryName, "", "", level, section);
                    var bucket = Get(groups, key);
                    bucket.VolumeM3 += ToCubicMeters(volInternal);
                    bucket.Count += 1;
                }
            }
        }


        private static bool IsTakeoffCategory(int categoryId)
        {
            // Deliberately limited to the structured construction scope requested by BuildAI.
            return categoryId == (int)BuiltInCategory.OST_Walls
                || categoryId == (int)BuiltInCategory.OST_Floors
                || categoryId == (int)BuiltInCategory.OST_Columns
                || categoryId == (int)BuiltInCategory.OST_StructuralColumns
                || categoryId == (int)BuiltInCategory.OST_StructuralFraming
                || categoryId == (int)BuiltInCategory.OST_StructuralFoundation;
        }

        // ---- helpers ---------------------------------------------------------
        private static string ExplicitSection(Element el, string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return null;
            try
            {
                var p = el.LookupParameter(paramName);
                if (p != null && p.HasValue && p.StorageType == StorageType.String)
                {
                    var v = p.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }
            return null;
        }

        private static double ToCubicMeters(double internalVolume)
        {
            try { return UnitUtils.ConvertFromInternalUnits(internalVolume, UnitTypeId.CubicMeters); }
            catch { return internalVolume * 0.0283168466; } // ft^3 -> m^3
        }

        private static double ToSquareMeters(double internalArea)
        {
            try { return UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters); }
            catch { return internalArea * 0.09290304; } // ft^2 -> m^2
        }

        private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

        private static string SafeCategory(Category c)
        {
            try { return string.IsNullOrEmpty(c.Name) ? "Uncategorized" : c.Name; }
            catch { return "Uncategorized"; }
        }

        private static string SafeMaterialName(Material m)
        {
            try { return string.IsNullOrEmpty(m?.Name) ? "" : m.Name; } catch { return ""; }
        }

        private static string SafeMaterialClass(Material m)
        {
            try { return string.IsNullOrEmpty(m?.MaterialClass) ? "" : m.MaterialClass; } catch { return ""; }
        }

        private static string ResolveLevel(Document doc, Element el, Dictionary<string, string> cache)
        {
            try
            {
                var lid = el.LevelId;
                if (lid == null || lid == ElementId.InvalidElementId)
                {
                    var p = el.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                            ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                    if (p != null && p.StorageType == StorageType.ElementId) lid = p.AsElementId();
                }
                if (lid == null || lid == ElementId.InvalidElementId) return "";

                var key = SafeTitle(doc) + "|" + lid.ToString();
                if (cache.TryGetValue(key, out var cached)) return cached;
                var name = (doc.GetElement(lid) as Level)?.Name ?? "";
                cache[key] = name;
                return name;
            }
            catch { return ""; }
        }

        private static string SafeTitle(Document d)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(d.Title); }
            catch { try { return d?.Title ?? ""; } catch { return ""; } }
        }

        private static string SafePath(Document d)
        {
            try { return string.IsNullOrEmpty(d.PathName) ? SafeTitle(d) : d.PathName; }
            catch { return SafeTitle(d); }
        }

        private static Bucket Get(Dictionary<GroupKey, Bucket> groups, GroupKey key)
        {
            if (!groups.TryGetValue(key, out var b)) { b = new Bucket(); groups[key] = b; }
            return b;
        }

        private sealed class Bucket { public double VolumeM3; public double AreaM2; public int Count; }

        private struct GroupKey : IEquatable<GroupKey>
        {
            public readonly string Category, MaterialName, MaterialClass, Level, Section;
            public GroupKey(string category, string materialName, string materialClass, string level, string section)
            {
                Category = category ?? ""; MaterialName = materialName ?? "";
                MaterialClass = materialClass ?? ""; Level = level ?? ""; Section = section ?? "";
            }

            public bool Equals(GroupKey o) =>
                Category == o.Category && MaterialName == o.MaterialName &&
                MaterialClass == o.MaterialClass && Level == o.Level && Section == o.Section;

            public override bool Equals(object obj) => obj is GroupKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (Category?.GetHashCode() ?? 0);
                    h = h * 31 + (MaterialName?.GetHashCode() ?? 0);
                    h = h * 31 + (MaterialClass?.GetHashCode() ?? 0);
                    h = h * 31 + (Level?.GetHashCode() ?? 0);
                    h = h * 31 + (Section?.GetHashCode() ?? 0);
                    return h;
                }
            }
        }
    }
}
