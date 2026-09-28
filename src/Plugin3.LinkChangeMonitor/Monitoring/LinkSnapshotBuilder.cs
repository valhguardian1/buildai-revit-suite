using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;
using BuildAI.RevitCompatibility;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public static class LinkSnapshotBuilder
    {
        public static LinkSnapshot Build(Document host, RevitLinkInstance linkInstance)
        {
            var linkDoc = linkInstance?.GetLinkDocument();
            if (host == null || linkInstance == null || linkDoc == null) return null;

            var snapshot = new LinkSnapshot
            {
                HostProjectId = ProjectId(host),
                HostDocumentTitle = SafeTitle(host),
                LinkInstanceUniqueId = Safe(() => linkInstance.UniqueId),
                LinkInstanceName = Safe(() => linkInstance.Name),
                LinkDocumentTitle = SafeTitle(linkDoc),
                LinkPath = Safe(() => linkDoc.PathName),
                LinkedLevelRange = ResolveLinkedLevelRange(linkDoc),
                CapturedAtUtc = DateTime.UtcNow
            };

            var transform = SafeTransform(linkInstance);
            // Stream elements directly. Avoid ToElements() and avoid reading every parameter;
            // both are expensive on large linked models.
            var collector = new FilteredElementCollector(linkDoc)
                .WhereElementIsNotElementType();

            foreach (var element in collector)
            {
                var item = BuildElement(linkDoc, element, transform);
                if (item == null || string.IsNullOrEmpty(item.UniqueId)) continue;
                snapshot.Elements[item.UniqueId] = item;
            }

            return snapshot;
        }

        private static LinkElementSnapshot BuildElement(Document doc, Element element, Transform transform)
        {
            try
            {
                if (element == null || element.Category == null) return null;
                var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
                AddTrackedParameter(values, element, BuiltInParameter.ALL_MODEL_MARK, "Mark");
                AddTrackedParameter(values, element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments");
                AddTrackedParameter(values, element, BuiltInParameter.ALL_MODEL_TYPE_MARK, "Type Mark");

                var level = ResolveLevel(doc, element);
                var levelElevation = ResolveLevelElevation(doc, element);
                var typeName = ResolveTypeName(doc, element);
                var bbox = BoundingBoxSignature(element, transform);
                var signature = string.Join("|", new[]
                {
                    CategoryNaming.Display(element),
                    Safe(() => element.Name),
                    level,
                    typeName,
                    bbox,
                    string.Join(";", values.Select(x => x.Key + "=" + x.Value))
                });

                return new LinkElementSnapshot
                {
                    UniqueId = Safe(() => element.UniqueId),
                    ElementId = Safe(() => element.Id.ToString()),
                    Name = Safe(() => element.Name),
                    Category = string.IsNullOrWhiteSpace(CategoryNaming.Display(element))
                        ? "Uncategorized"
                        : CategoryNaming.Display(element),
                    Level = level,
                    LevelElevation = levelElevation,
                    TypeName = typeName,
                    Fingerprint = Sha256(signature),
                    Parameters = values.ToDictionary(x => x.Key, x => x.Value)
                };
            }
            catch { return null; }
        }


        private static void AddTrackedParameter(IDictionary<string, string> values, Element element, BuiltInParameter parameterId, string name)
        {
            try
            {
                var p = element.get_Parameter(parameterId);
                if (p == null || !p.HasValue) return;
                var value = p.AsValueString() ?? p.AsString();
                if (!string.IsNullOrWhiteSpace(value)) values[name] = value;
            }
            catch { }
        }

        private static string ParameterValue(Document doc, Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? p.AsValueString();
                    case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        return p.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return "";
                        var target = doc.GetElement(id);
                        return target?.Name ?? id.ToString();
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static string ResolveLinkedLevelRange(Document linkDocument)
        {
            try
            {
                if (linkDocument == null) return "";

                var levels = new FilteredElementCollector(linkDocument)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                    .OrderBy(x => x.Elevation)
                    .ToList();

                if (levels.Count == 0) return "";
                if (levels.Count == 1) return levels[0].Name;

                var first = levels.First().Name;
                var last = levels.Last().Name;
                return string.Equals(first, last, StringComparison.OrdinalIgnoreCase)
                    ? first
                    : first + " — " + last;
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveLevel(Document doc, Element element)
        {
            try
            {
                var id = element.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    var p = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (p != null && p.StorageType == StorageType.ElementId) id = p.AsElementId();
                }
                return id == null || id == ElementId.InvalidElementId ? "" : (doc.GetElement(id) as Level)?.Name ?? "";
            }
            catch { return ""; }
        }

        private static double ResolveLevelElevation(Document doc, Element element)
        {
            try
            {
                var id = element.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    var p = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (p != null && p.StorageType == StorageType.ElementId) id = p.AsElementId();
                }
                var level = id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as Level;
                return level?.Elevation ?? 0.0;
            }
            catch { return 0.0; }
        }

        private static string ResolveTypeName(Document doc, Element element)
        {
            try
            {
                var id = element.GetTypeId();
                return id == null || id == ElementId.InvalidElementId ? "" : doc.GetElement(id)?.Name ?? "";
            }
            catch { return ""; }
        }

        private static string BoundingBoxSignature(Element element, Transform linkTransform)
        {
            try
            {
                var box = element.get_BoundingBox(null);
                if (box == null) return "";
                var local = box.Transform ?? Transform.Identity;
                var points = new List<XYZ>();
                for (var xi = 0; xi < 2; xi++)
                for (var yi = 0; yi < 2; yi++)
                for (var zi = 0; zi < 2; zi++)
                {
                    var p = new XYZ(xi == 0 ? box.Min.X : box.Max.X, yi == 0 ? box.Min.Y : box.Max.Y, zi == 0 ? box.Min.Z : box.Max.Z);
                    points.Add(linkTransform.OfPoint(local.OfPoint(p)));
                }
                var values = new[]
                {
                    points.Min(x => x.X), points.Min(x => x.Y), points.Min(x => x.Z),
                    points.Max(x => x.X), points.Max(x => x.Y), points.Max(x => x.Z)
                };
                return string.Join(",", values.Select(x => Math.Round(x, 6).ToString("R", CultureInfo.InvariantCulture)));
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"Bounding-box fingerprint failed for {element?.Id}: {ex.Message}");
                return "";
            }
        }

        private static Transform SafeTransform(RevitLinkInstance instance)
        {
            try { return instance.GetTotalTransform() ?? Transform.Identity; }
            catch { return Transform.Identity; }
        }

        public static string ProjectId(Document doc)
        {
            try
            {
                var info = doc.ProjectInformation;
                var guid = info?.UniqueId;
                if (!string.IsNullOrWhiteSpace(guid)) return guid;
            }
            catch { }
            try { if (!string.IsNullOrWhiteSpace(doc.PathName)) return Sha256(doc.PathName); } catch { }
            return Sha256(SafeTitle(doc));
        }

        private static string SafeTitle(Document doc)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(doc.Title); }
            catch { return "Untitled"; }
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; } catch { return ""; }
        }
    }
}
