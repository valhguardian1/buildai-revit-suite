using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.Core.Logging;
using BuildAI.RevitCompatibility;

namespace Plugin4.LinkComparatorAI.Comparison
{
    internal static class GeometryUtil
    {
        public const double FeetToMm = 304.8;
        public static double ToMm(double feet) => feet * FeetToMm;
        public static double ToFeet(double mm) => mm / FeetToMm;

        public static ElementProxy CreateProxy(Document doc, Element e, Transform sourceTransform)
        {
            try
            {
                var box = e.get_BoundingBox(null);
                if (box == null) return null;
                var boxTransform = box.Transform ?? Transform.Identity;
                var points = new List<XYZ>();
                for (var x = 0; x < 2; x++)
                    for (var y = 0; y < 2; y++)
                        for (var z = 0; z < 2; z++)
                        {
                            var local = new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z);
                            points.Add(sourceTransform.OfPoint(boxTransform.OfPoint(local)));
                        }
                return new ElementProxy
                {
                    Element = e,
                    UniqueId = e.UniqueId,
                    Id = ElementIdCompatibility.ToInt32(e.Id),
                    Name = e.Name ?? "",
                    Category = CategoryNaming.Display(e),   // stable English category name, independent of the Revit UI language
                    Level = ResolveLevel(doc, e),
                    LevelElevation = ResolveLevelElevation(doc, e, sourceTransform),
                    Min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                    Max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z))
                };
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"Comparator proxy failed for element {e?.Id}: {ex.Message}");
                return null;
            }
        }

        public static string ResolveLevel(Document doc, Element e)
        {
            try
            {
                var levelId = e.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                    return doc.GetElement(levelId)?.Name ?? "";
                foreach (var p in new[]
                {
                    e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM),
                    e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM),
                    e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT),
                    e.get_Parameter(BuiltInParameter.LEVEL_PARAM),
                    e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                })
                {
                    if (p != null && p.StorageType == StorageType.ElementId)
                    {
                        var name = doc.GetElement(p.AsElementId())?.Name;
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch (Exception ex) { PluginLog.Warn($"Comparator level resolution failed for {e?.Id}: {ex.Message}"); }
            return "";
        }


        public static double ResolveLevelElevation(Document doc, Element e, Transform sourceTransform)
        {
            try
            {
                ElementId id = e.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    foreach (var p in new[] { e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM), e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM), e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT), e.get_Parameter(BuiltInParameter.LEVEL_PARAM), e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM) })
                        if (p != null && p.StorageType == StorageType.ElementId && p.AsElementId() != ElementId.InvalidElementId) { id = p.AsElementId(); break; }
                }
                var level = id == null ? null : doc.GetElement(id) as Level;
                if (level == null) return double.NaN;
                return (sourceTransform ?? Transform.Identity).OfPoint(new XYZ(0, 0, level.Elevation)).Z;
            }
            catch { return double.NaN; }
        }
        public static double CenterDistanceMm(ElementProxy a, ElementProxy b) { var d = a.Center - b.Center; return ToMm(Math.Sqrt(d.X*d.X+d.Y*d.Y+d.Z*d.Z)); }
        public static double HorizontalDistanceMm(ElementProxy a, ElementProxy b) { var d = a.Center - b.Center; return ToMm(Math.Sqrt(d.X*d.X+d.Y*d.Y)); }
        public static double ElevationDeltaMm(ElementProxy a, ElementProxy b) => Math.Abs(ToMm(a.Center.Z - b.Center.Z));
        public static double MaxSizeDeltaMm(ElementProxy a, ElementProxy b) => Math.Max(Math.Abs(ToMm(a.Width-b.Width)), Math.Max(Math.Abs(ToMm(a.Depth-b.Depth)), Math.Abs(ToMm(a.Height-b.Height))));
    }
}
