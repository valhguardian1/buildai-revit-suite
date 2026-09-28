using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BuildAI.RevitCompatibility
{
    /// <summary>Shared local-result navigation for AR-ST and Clash.</summary>
    internal static class PushpinLocator
    {
        /// <summary>
        /// Points a 3D view at <paramref name="target"/>, switches it to wireframe and
        /// selects the elements.
        ///
        /// Order matters and is not interchangeable:
        ///   - UIDocument.ActiveView must be assigned BEFORE the transaction, because
        ///     assigning it ends any open transaction and clears the selection;
        ///   - the selection must be set AFTER the transaction commits, for the same
        ///     reason;
        ///   - SetOrientation is silently ignored on a locked 3D view, hence Unlock;
        ///   - SetOrientation changes direction but not zoom, so without
        ///     ZoomAndCenterRectangle the camera faces the right way from far away,
        ///     which looks identical to "the camera flew somewhere wrong".
        /// </summary>
        public static void FocusOn(
            UIDocument uidoc,
            View3D view,
            XYZ target,
            IList<Reference> references,
            IList<ElementId> hostElementIds,
            double radiusFeet = 4.0,
            bool wireframe = true,
            bool useSectionBox = true)
        {
            if (uidoc == null || view == null || target == null) return;
            var doc = uidoc.Document;

            if (uidoc.ActiveView == null || uidoc.ActiveView.Id != view.Id)
                uidoc.ActiveView = view;

            var direction = new XYZ(1.0, -1.0, -0.7).Normalize();
            var eye = target - direction * Math.Max(radiusFeet * 6.0, 20.0);
            var pad = Math.Max(radiusFeet, 1.0);

            using (var t = new Transaction(doc, "BuildAI: focus issue"))
            {
                t.Start();
                try { if (view.IsLocked) view.Unlock(); } catch { }
                try { view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, direction)); } catch { }
                if (wireframe) try { view.DisplayStyle = DisplayStyle.Wireframe; } catch { }
                if (useSectionBox)
                {
                    try
                    {
                        view.IsSectionBoxActive = true;
                        view.SetSectionBox(new BoundingBoxXYZ
                        {
                            Transform = Transform.Identity,
                            Min = new XYZ(target.X - pad, target.Y - pad, target.Z - pad),
                            Max = new XYZ(target.X + pad, target.Y + pad, target.Z + pad)
                        });
                    }
                    catch { }
                }
                t.Commit();
            }

            var refs = (references ?? new List<Reference>()).Where(x => x != null).ToList();
            var ids = (hostElementIds ?? new List<ElementId>()).Where(x => x != null).Distinct().ToList();
            try
            {
                if (refs.Count > 0) uidoc.Selection.SetReferences(refs);
                else if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
            }
            catch { }

            uidoc.RefreshActiveView();

            try
            {
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == view.Id);
                uiView?.ZoomAndCenterRectangle(
                    new XYZ(target.X - pad, target.Y - pad, target.Z - pad),
                    new XYZ(target.X + pad, target.Y + pad, target.Z + pad));
            }
            catch { }
        }

        /// <summary>
        /// Finds a named 3D view, creating it only if absent. Callers must pass the
        /// canonical BuildAiViewNames value: a view created under any other name is
        /// not in the cloud publish set, so it carries no pushpins and opens zoomed
        /// to the whole model.
        /// </summary>
        public static View3D EnsureNamed3D(Document doc, string name)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == name);
            if (existing != null) return existing;

            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null) return null;

            using (var t = new Transaction(doc, "BuildAI: create coordination view"))
            {
                t.Start();
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = name;
                t.Commit();
                return view;
            }
        }
    }
}
