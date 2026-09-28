using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BuildAI.RevitCompatibility;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Revit
{
    internal sealed class MepComparisonViewCompositionResult
    {
        public int SelectedSystems { get; set; }
        public List<int> SelectedCategoryIds { get; set; } = new List<int>();
        public int VisibleResultElements { get; set; }
        public bool StructuralFramingIncluded { get; set; }
        public string Details { get; set; } = "";
    }

    internal static class MepComparisonViewComposer
    {
        public static MepComparisonViewCompositionResult Compose(Document doc, View3D view, ClashReport report)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));
            report = report ?? new ClashReport();
            if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
            if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

            var currentRows = (report.Items ?? new List<ClashItem>())
                .Where(x => x.CreationState != BuildAI.Core.Issues.IssueCreationState.PreviouslyCreatedNotDetected).ToList();
            var selected = new HashSet<int>(report.SelectedCategoryIds ?? new List<int>());
            int structuralFramingId;
            if (MepClashCategories.TryResolve("OST_StructuralFraming", out structuralFramingId)) selected.Remove(structuralFramingId);
            var visibleElementIds = new List<ElementId>();
            var requiredLinkIds = new HashSet<int>();
            foreach (var row in currentRows)
            {
                AddParticipant(doc, row.LinkInstanceAId, row.ElementAId, selected, visibleElementIds, requiredLinkIds);
                AddParticipant(doc, row.LinkInstanceBId, row.ElementBId, selected, visibleElementIds, requiredLinkIds);
            }
            if (structuralFramingId != 0) selected.Remove(structuralFramingId);

            foreach (Category category in doc.Settings.Categories)
            {
                if (category == null || category.CategoryType != CategoryType.Model || !view.CanCategoryBeHidden(category.Id)) continue;
                var id = ElementIdCompatibility.ToInt32(category.Id);
                view.SetCategoryHidden(category.Id, !(selected.Contains(id) || id == (int)BuiltInCategory.OST_RvtLinks));
            }
            foreach (var filterId in view.GetFilters()) view.SetFilterVisibility(filterId, true);

            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var requiredTypeIds = new HashSet<int>(links.Where(x => requiredLinkIds.Contains(ElementIdCompatibility.ToInt32(x.Id)))
                .Select(x => ElementIdCompatibility.ToInt32(x.GetTypeId())));
            foreach (var typeId in requiredTypeIds)
            {
                var type = doc.GetElement(new ElementId(typeId));
                if (type != null && type.IsHidden(view)) view.UnhideElements(new[] { type.Id });
            }
            foreach (var link in links)
            {
                var id = ElementIdCompatibility.ToInt32(link.Id);
                if (requiredLinkIds.Contains(id))
                {
                    if (link.IsHidden(view)) view.UnhideElements(new[] { link.Id });
                    if (view.GetWorksetVisibility(link.WorksetId) == WorksetVisibility.Hidden)
                        view.SetWorksetVisibility(link.WorksetId, WorksetVisibility.Visible);
                }
                else if (!link.IsHidden(view)) view.HideElements(new[] { link.Id });
            }
            if (visibleElementIds.Count > 0) view.UnhideElements(visibleElementIds.Distinct().ToList());
            foreach (var link in links)
            {
                var id = ElementIdCompatibility.ToInt32(link.Id);
                var required = requiredLinkIds.Contains(id);
                if (link.IsHidden(view) == required)
                    throw new InvalidOperationException("MEP comparison view link visibility postcondition failed for " + id + ".");
                if (required)
                {
                    var type = doc.GetElement(link.GetTypeId());
                    if (type != null && type.IsHidden(view)) throw new InvalidOperationException("Required Revit link type remained hidden for " + id + ".");
                }
            }

            var structuralIncluded = structuralFramingId != 0 && selected.Contains(structuralFramingId);
            if (structuralFramingId != 0)
            {
                var category = Category.GetCategory(doc, (BuiltInCategory)structuralFramingId);
                structuralIncluded = category != null && !view.GetCategoryHidden(category.Id);
            }
            if (structuralIncluded) throw new InvalidOperationException("OST_StructuralFraming remained visible in the MEP comparison view.");
            var result = new MepComparisonViewCompositionResult
            {
                SelectedSystems = new[] { report.SourceA, report.SourceB, report.SourceC }.Count(x => !string.IsNullOrWhiteSpace(x)),
                SelectedCategoryIds = selected.OrderBy(x => x).ToList(),
                VisibleResultElements = visibleElementIds.Count + requiredLinkIds.Count,
                StructuralFramingIncluded = false
            };
            result.Details = "MEP_COMPARISON_VIEW_COMPOSITION" + Environment.NewLine +
                "selectedSystems=" + result.SelectedSystems + Environment.NewLine +
                "selectedCategories=" + string.Join(",", result.SelectedCategoryIds) + Environment.NewLine +
                "visibleResultElements=" + result.VisibleResultElements + Environment.NewLine +
                "structuralFramingIncluded=false";
            return result;
        }
        private static void AddParticipant(Document host, int? linkInstanceId, int elementId,
            ISet<int> selectedCategories, ICollection<ElementId> hostElementIds, ISet<int> requiredLinkIds)
        {
            Element element = null;
            if (linkInstanceId.HasValue)
            {
                var link = host.GetElement(new ElementId(linkInstanceId.Value)) as RevitLinkInstance;
                if (link == null) return;
                requiredLinkIds.Add(linkInstanceId.Value);
                element = link.GetLinkDocument()?.GetElement(new ElementId(elementId));
            }
            else
            {
                element = host.GetElement(new ElementId(elementId));
                if (element != null) hostElementIds.Add(element.Id);
            }
            if (element?.Category != null)
                selectedCategories.Add(ElementIdCompatibility.ToInt32(element.Category.Id));
        }
    }
}
