using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Logging;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Revit
{
    public sealed class LinkActionHandler : IExternalEventHandler
    {
        private readonly object _gate = new object();
        private ActionRequest _pending;
        public void RequestHighlight(LinkChangeItem item) { lock (_gate) _pending = new ActionRequest { Kind = ActionKind.Highlight, Item = item }; }
        public void RequestReset() { lock (_gate) _pending = new ActionRequest { Kind = ActionKind.Reset }; }

        public void Execute(UIApplication app)
        {
            ActionRequest request; lock (_gate) { request = _pending; _pending = null; }
            if (request == null) return;
            try
            {
                var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; if (doc == null) return;
                if (request.Kind == ActionKind.Reset) { uidoc.Selection.SetElementIds(new List<ElementId>()); uidoc.RefreshActiveView(); return; }
                var item = request.Item; if (item == null || !item.CanHighlight) return;
                var link = doc.GetElement(item.LinkInstanceUniqueId) as RevitLinkInstance;
                var linkDoc = link?.GetLinkDocument();
                var element = linkDoc?.GetElement(item.ElementUniqueId);
                if (link == null || element == null) { TaskDialog.Show("BuildAI", "The linked element is not available."); return; }

                var view = EnsureCoordination3D(doc);
                if (view != null)
                {
                    uidoc.ActiveView = view;
                    var box = ToHostBox(element.get_BoundingBox(null), link.GetTotalTransform() ?? Transform.Identity);
                    if (box != null)
                    {
                        const double paddingFeet = 3.0;
                        box.Min = new XYZ(box.Min.X - paddingFeet, box.Min.Y - paddingFeet, box.Min.Z - paddingFeet);
                        box.Max = new XYZ(box.Max.X + paddingFeet, box.Max.Y + paddingFeet, box.Max.Z + paddingFeet);
                        using (var transaction = new Transaction(doc, "BuildAI: focus changed element"))
                        {
                            transaction.Start(); view.IsSectionBoxActive = true; view.SetSectionBox(box); transaction.Commit();
                        }
                    }
                }

                var reference = new Reference(element).CreateLinkReference(link);
                uidoc.Selection.SetReferences(new List<Reference> { reference });
                uidoc.RefreshActiveView();
                PluginLog.Info("Linked element opened", new { item.Level, item.ElementUniqueId, view = uidoc.ActiveView?.Name });
            }
            catch (Exception ex) { PluginLog.Error("Link highlight failed", ex); TaskDialog.Show("BuildAI", ex.Message); }
        }

        private static View3D EnsureCoordination3D(Document doc)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name == "BuildAI Changes Coordination");
            if (existing != null) return existing;
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null) return null;
            using (var transaction = new Transaction(doc, "BuildAI: create changes coordination view"))
            {
                transaction.Start();
                var view = View3D.CreateIsometric(doc, type.Id);
                view.Name = "BuildAI Changes Coordination";
                transaction.Commit();
                return view;
            }
        }

        private static BoundingBoxXYZ ToHostBox(BoundingBoxXYZ box, Transform linkTransform)
        {
            if (box == null) return null;
            var local = box.Transform ?? Transform.Identity;
            var points = new List<XYZ>();
            for (var x = 0; x < 2; x++) for (var y = 0; y < 2; y++) for (var z = 0; z < 2; z++)
                points.Add(linkTransform.OfPoint(local.OfPoint(new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z))));
            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(points.Min(q => q.X), points.Min(q => q.Y), points.Min(q => q.Z)),
                Max = new XYZ(points.Max(q => q.X), points.Max(q => q.Y), points.Max(q => q.Z))
            };
        }

        private static ViewPlan FindFloorPlan(Document doc, string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName)) return null;
            var normalized = levelName.Split(new[] { " — " }, StringSplitOptions.None)[0].Trim();
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (level == null) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(x => !x.IsTemplate && x.ViewType == ViewType.FloorPlan)
                .FirstOrDefault(x => x.GenLevel != null && x.GenLevel.Id == level.Id);
        }

        public string GetName() => "BuildAI Link Change Monitor actions";
        private enum ActionKind { Highlight, Reset }
        private sealed class ActionRequest { public ActionKind Kind; public LinkChangeItem Item; }
    }
}
