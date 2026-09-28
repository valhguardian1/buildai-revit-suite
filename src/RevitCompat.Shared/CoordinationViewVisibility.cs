using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BuildAI.RevitCompatibility
{
    internal sealed class CoordinationViewVisibilityResult
    {
        public bool TemplateDetached { get; set; }
        public int CategoriesUnhidden { get; set; }
        public int LinkInstancesUnhidden { get; set; }
        public int LinkInstanceCount { get; set; }
        public int LinkTypesUnhidden { get; set; }
        public int FiltersEnabled { get; set; }
        public int WorksetsShown { get; set; }
        public int LoadedLinkInstanceCount { get; set; }
        public string Details { get; set; } = "";
    }

    internal static class CoordinationViewVisibility
    {
        private static readonly BuiltInCategory[] CriticalCategories =
        {
            BuiltInCategory.OST_RvtLinks,
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralFoundation
        };

        public static CoordinationViewVisibilityResult EnsureFull(Document doc, View3D view)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));

            var result = new CoordinationViewVisibilityResult();

            // BuildAI views must not inherit a user template which can silently
            // re-hide Revit links or architecture/structure model categories.
            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                view.ViewTemplateId = ElementId.InvalidElementId;
                result.TemplateDetached = true;
            }

            foreach (Category category in doc.Settings.Categories)
            {
                if (category == null || category.CategoryType != CategoryType.Model) continue;
                try
                {
                    if (view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id))
                    {
                        view.SetCategoryHidden(category.Id, false);
                        result.CategoriesUnhidden++;
                    }
                }
                catch
                {
                    // Critical categories are verified below. Non-critical Revit
                    // pseudo-categories are allowed to reject visibility changes.
                }
            }

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            result.LinkInstanceCount = links.Count;
            result.LoadedLinkInstanceCount = links.Count(x => IsLoaded(x));

            // A legacy BuildAI view may have invisible filters left by an older
            // isolation workflow. Coordination is a service view, so all of its
            // filters must be visible; the specialized AR-ST view keeps its own
            // filtering rules.
            foreach (var filterId in view.GetFilters())
            {
                try
                {
                    if (!view.GetFilterVisibility(filterId))
                    {
                        view.SetFilterVisibility(filterId, true);
                        result.FiltersEnabled++;
                    }
                }
                catch { }
            }

            foreach (var worksetId in links.Select(x => x.WorksetId).Distinct())
            {
                try
                {
                    if (view.GetWorksetVisibility(worksetId) == WorksetVisibility.Hidden)
                    {
                        view.SetWorksetVisibility(worksetId, WorksetVisibility.Visible);
                        result.WorksetsShown++;
                    }
                }
                catch { }
            }

            var hiddenLinkTypes = links.Select(x => x.GetTypeId())
                .Where(x => x != null && x != ElementId.InvalidElementId)
                .Distinct()
                .Where(id => IsHidden(doc.GetElement(id), view))
                .ToList();
            if (hiddenLinkTypes.Count > 0)
            {
                view.UnhideElements(hiddenLinkTypes);
                result.LinkTypesUnhidden = hiddenLinkTypes.Count;
            }

            var hiddenLinks = links.Where(link => IsHidden(link, view)).Select(link => link.Id).ToList();
            if (hiddenLinks.Count > 0)
            {
                view.UnhideElements(hiddenLinks);
                result.LinkInstancesUnhidden = hiddenLinks.Count;
            }

            var failures = new List<string>();
            foreach (var builtInCategory in CriticalCategories)
            {
                try
                {
                    var category = Category.GetCategory(doc, builtInCategory);
                    if (category != null && view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id))
                        failures.Add(builtInCategory.ToString());
                }
                catch (Exception ex)
                {
                    failures.Add(builtInCategory + " (" + ex.Message + ")");
                }
            }

            var stillHidden = links.Where(link => IsHidden(link, view)).Select(link => DescribeLink(doc, view, link)).ToList();
            if (stillHidden.Count > 0)
                failures.Add("hidden Revit links: " + string.Join(", ", stillHidden));

            result.Details = "View filters enabled: " + result.FiltersEnabled +
                             "\nLink worksets forced visible: " + result.WorksetsShown +
                             "\nLink types unhidden: " + result.LinkTypesUnhidden +
                             "\nLoaded Revit link instances: " + result.LoadedLinkInstanceCount + "/" + result.LinkInstanceCount;

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    "BuildAI Coordination visibility validation failed. Publication was stopped before synchronization. " +
                    string.Join("; ", failures));

            return result;
        }

        private static bool IsHidden(Element element, View view)
        {
            try { return element != null && element.IsHidden(view); }
            catch { return false; }
        }

        private static bool IsLoaded(RevitLinkInstance link)
        {
            try { return link != null && link.GetLinkDocument() != null; }
            catch { return false; }
        }

        private static string DescribeLink(Document doc, View view, RevitLinkInstance link)
        {
            if (link == null) return "<null>";
            string categoryHidden = "unknown", typeHidden = "unknown", worksetVisibility = "unknown";
            try { categoryHidden = view.GetCategoryHidden(link.Category.Id).ToString(); } catch { }
            try { typeHidden = IsHidden(doc.GetElement(link.GetTypeId()), view).ToString(); } catch { }
            try { worksetVisibility = view.GetWorksetVisibility(link.WorksetId).ToString(); } catch { }
            return (link.Name ?? link.Id.ToString()) +
                   " [id=" + ElementIdCompatibility.ToInt32(link.Id) +
                   ", loaded=" + IsLoaded(link) +
                   ", categoryHidden=" + categoryHidden +
                   ", typeHidden=" + typeHidden +
                   ", workset=" + worksetVisibility + "]";
        }
    }
}
