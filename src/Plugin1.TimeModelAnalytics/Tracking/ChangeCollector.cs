using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BuildAI.Core.Models;
using BuildAI.RevitCompatibility;

namespace Plugin1.TimeModelAnalytics.Tracking
{
    /// <summary>
    /// Turns a DocumentChanged event into RevitWorkItem records.
    ///
    /// IMPORTANT (specification §12.1): DocumentChanged is READ-ONLY. We only read element
    /// data and project metadata here — no transactions, no model edits. The
    /// resulting items are handed to BuildAiClient, which sends them off-thread.
    /// </summary>
    public static class ChangeCollector
    {
        public static IEnumerable<RevitWorkItem> Collect(
            Document doc,
            IEnumerable<ElementId> ids,
            string method,
            string username)
        {
            var items = new List<RevitWorkItem>();
            if (doc == null || ids == null) return items;

            var pi = doc.ProjectInformation;
            var projectId   = SafeUniqueId(pi);
            var projectName = pi?.Name;
            var number      = pi?.Number;
            var buildingName= pi?.BuildingName;
            var title       = TryGetTitle(doc);
            var ts          = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            foreach (var id in ids)
            {
                Element el = null;
                try { el = doc.GetElement(id); } catch { /* deleted/unreadable */ }

                string name, category, uniqueId, level;
                if (el != null)
                {
                    name     = SafeName(el);
                    category = CategoryNaming.Display(el);
                    if (string.IsNullOrWhiteSpace(category)) category = "Uncategorized";
                    uniqueId = el.UniqueId;
                    level    = ResolveLevel(doc, el);
                }
                else
                {
                    // For deletions the element is already gone; record the id only.
                    name = "(deleted)"; category = ""; uniqueId = id.ToString(); level = "";
                }

                items.Add(new RevitWorkItem
                {
                    ItemName = name,
                    ItemUniqueId = uniqueId,
                    ProjectId = projectId,
                    ItemCategory = category,
                    ProjectTitle = title,
                    ProjectBuildingName = buildingName,
                    ProjectName = projectName,
                    ProjectNumber = number,
                    Method = method,
                    Level = level,
                    Time = ts,
                    Username = username
                });
            }
            return items;
        }

        private static string ResolveLevel(Document doc, Element el)
        {
            try
            {
                if (el.LevelId != null && el.LevelId != ElementId.InvalidElementId)
                    return (doc.GetElement(el.LevelId) as Level)?.Name ?? "";
                var p = el.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                        ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                if (p != null && p.StorageType == StorageType.ElementId)
                    return (doc.GetElement(p.AsElementId()) as Level)?.Name ?? "";
            }
            catch { }
            return "";
        }

        private static string SafeName(Element el)
        {
            try { return string.IsNullOrEmpty(el.Name) ? el.Id.ToString() : el.Name; }
            catch { return el.Id.ToString(); }
        }

        private static string SafeUniqueId(Element el)
        {
            try { return el?.UniqueId ?? ""; } catch { return ""; }
        }

        private static string TryGetTitle(Document doc)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(doc.Title); }
            catch { return doc?.Title ?? ""; }
        }
    }
}
