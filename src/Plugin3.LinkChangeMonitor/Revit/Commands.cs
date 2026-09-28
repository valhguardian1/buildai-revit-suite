using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Logging;
using Plugin3.LinkChangeMonitor.UI;

namespace Plugin3.LinkChangeMonitor.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CheckLinksCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var sw = Stopwatch.StartNew();
            PluginLog.Info("Manual Check Links started.");
            try
            {
                var doc = data.Application.ActiveUIDocument?.Document;
                if (doc == null) return Result.Cancelled;
                PluginContext.UiApplication = data.Application;
                var result = PluginContext.Comparer.CompareAndSave(doc);
                PluginContext.LastResult = result;
                PluginContext.Uploader?.Enqueue(result, doc.ProjectInformation?.UniqueId ?? doc.Title, doc.ProjectInformation?.Number ?? "", "manual");
                MonitorWindow.ShowSingleton(result);
                if (result.LinksScanned == 0)
                    TaskDialog.Show("BuildAI — Check Links", "No loaded Revit links were found. The check did not run.");
                else if (result.BaselinesCreated > 0 && result.Changes.Count == 0)
                    TaskDialog.Show("BuildAI — Check Links", $"Baseline created for {result.BaselinesCreated} link(s). No previous snapshot was available.");
                else if (result.Changes.Count == 0)
                    TaskDialog.Show("BuildAI — Check Links", $"Check completed. Links scanned: {result.LinksScanned}. No changes found.");
                sw.Stop();
                PluginLog.Info($"Manual Check Links finished in {sw.ElapsedMilliseconds} ms. Links scanned: {result.LinksScanned}; changes: {result.Changes.Count}.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("CheckLinksCommand failed", ex);
                TaskDialog.Show("BuildAI — Check Links", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class OpenMonitorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            PluginContext.UiApplication = data.Application;
            MonitorWindow.ShowSingleton(PluginContext.LastResult ?? new Models.ComparisonResult());
            return Result.Succeeded;
        }
    }
}
