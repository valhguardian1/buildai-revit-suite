using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BuildAI.RevitCompatibility;

namespace Plugin5.ClashFormaIntegration.Clash
{
    /// <summary>
    /// One element prepared for clash detection: its transformed bounding box plus
    /// the identity fields a result needs.
    /// <para>
    /// Bounds are stored as plain doubles rather than <c>XYZ</c>. The bounding-box
    /// phase then touches no Revit type at all, which keeps it cheap and leaves the
    /// door open to running it off the Revit thread. Exact checking must stay on
    /// the Revit thread: <c>get_Geometry</c> and <c>BooleanOperationsUtils</c> are
    /// not thread-safe.
    /// </para>
    /// </summary>
    public sealed class ElementProxy
    {
        private static int _serialCounter;
        private List<Solid> _solids;

        public ElementProxy()
        {
            Serial = System.Threading.Interlocked.Increment(ref _serialCounter);
        }

        /// <summary>Process-unique id, used to deduplicate grid-cell hits cheaply.</summary>
        public int Serial { get; }

        public Document Document;
        public Element Element;
        public Transform Transform;

        public string SourceKey = "";
        public string SourceName = "";
        public int? LinkInstanceId;
        public string LinkInstanceUniqueId = "";
        public string ModelUid = "";

        public int ElementId;
        public string ElementUniqueId = "";
        public string CategoryName = "";
        public string DisplayName = "";
        public string LevelName = "";

        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>
        /// Extracted solids, cached for the lifetime of a page.
        /// <para>
        /// Caching is right within a page: a wall appearing in hundreds of pairs is
        /// extracted once. Across a paged session it would only grow, and solids are
        /// heavy, so <see cref="ReleaseSolids"/> is called between pages.
        /// </para>
        /// <para>
        /// The geometry options are the ones the engine has always used. Lowering
        /// <c>DetailLevel</c> to Medium and turning off
        /// <c>IncludeNonVisibleObjects</c> would cut this cost substantially and
        /// remove some false intersections, but both change what counts as a clash
        /// and are left as a deliberate decision rather than folded in here.
        /// </para>
        /// </summary>
        public List<Solid> GetSolids()
        {
            if (_solids != null) return _solids;
            _solids = new List<Solid>();
            GeometryElement geometry;
            try
            {
                geometry = Element.get_Geometry(new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = true,
                    DetailLevel = ViewDetailLevel.Fine
                });
            }
            catch { return _solids; }
            if (geometry == null) return _solids;
            ExtractSolids(geometry, Transform, _solids);
            return _solids;
        }

        /// <summary>Drops cached solids. Returns true when something was released.</summary>
        public bool ReleaseSolids()
        {
            if (_solids == null) return false;
            _solids = null;
            return true;
        }

        private static void ExtractSolids(GeometryElement g, Transform tr, ICollection<Solid> output)
        {
            foreach (var o in g)
            {
                var solid = o as Solid;
                if (solid != null && solid.Faces.Size > 0 && solid.Edges.Size > 0 && solid.Volume > 1e-10)
                {
                    try { output.Add(tr == null || tr.IsIdentity ? solid : SolidUtils.CreateTransformed(solid, tr)); }
                    catch { }
                    continue;
                }
                var instance = o as GeometryInstance;
                if (instance == null) continue;
                GeometryElement nested;
                try { nested = instance.GetInstanceGeometry(); }
                catch { continue; }
                if (nested != null) ExtractSolids(nested, tr, output);
            }
        }

        public static ElementProxy Create(Document doc, Element e, Transform tr, string key, string name, int? linkId, string linkUid, string modelUid)
        {
            BoundingBoxXYZ box;
            try { box = e.get_BoundingBox(null); }
            catch { return null; }
            if (box == null) return null;

            var boxTransform = box.Transform ?? Transform.Identity;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 2; z++)
                    {
                        var corner = boxTransform.OfPoint(new XYZ(
                            x == 0 ? box.Min.X : box.Max.X,
                            y == 0 ? box.Min.Y : box.Max.Y,
                            z == 0 ? box.Min.Z : box.Max.Z));
                        if (tr != null && !tr.IsIdentity) corner = tr.OfPoint(corner);
                        if (corner.X < minX) minX = corner.X;
                        if (corner.Y < minY) minY = corner.Y;
                        if (corner.Z < minZ) minZ = corner.Z;
                        if (corner.X > maxX) maxX = corner.X;
                        if (corner.Y > maxY) maxY = corner.Y;
                        if (corner.Z > maxZ) maxZ = corner.Z;
                    }

            var elementId = ElementIdCompatibility.ToInt32(e.Id);
            return new ElementProxy
            {
                Document = doc,
                Element = e,
                Transform = tr,
                SourceKey = key,
                SourceName = name,
                LinkInstanceId = linkId,
                LinkInstanceUniqueId = linkUid ?? "",
                ModelUid = modelUid ?? "",
                ElementId = elementId,
                ElementUniqueId = e.UniqueId ?? "",
                CategoryName = CategoryNaming.Display(e),   // stable English category name, independent of the Revit UI language
                DisplayName = (e.Name ?? e.GetType().Name) + " [" + elementId + "]",
                LevelName = ResolveLevelName(doc, e),
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ
            };
        }

        private static string ResolveLevelName(Document d, Element e)
        {
            try
            {
                var id = e.LevelId;
                if (id != null && id != Autodesk.Revit.DB.ElementId.InvalidElementId) return d.GetElement(id)?.Name ?? "";
            }
            catch { }
            foreach (var parameter in new[]
                     {
                         e.get_Parameter(BuiltInParameter.LEVEL_PARAM),
                         e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM),
                         e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                     })
            {
                if (parameter == null) continue;
                try
                {
                    var levelId = parameter.AsElementId();
                    if (levelId != null && levelId != Autodesk.Revit.DB.ElementId.InvalidElementId)
                    {
                        var level = d.GetElement(levelId);
                        if (level != null) return level.Name ?? "";
                    }
                }
                catch { }
            }
            return "";
        }
    }
}
