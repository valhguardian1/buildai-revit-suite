using System;
using System.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Logging;
using Plugin4.LinkComparatorAI.Comparison;
using Plugin4.LinkComparatorAI.UI;

namespace Plugin4.LinkComparatorAI.Revit
{
    [Transaction(TransactionMode.Manual)]
    public sealed class CompareModelsCommand : IExternalCommand
    {
        private static int _running;
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => Run(data, ref message, true, false);
        internal static Result Run(ExternalCommandData data, ref string message, bool geometry, bool rooms)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return Result.Cancelled;
            try
            {
                var doc = data.Application.ActiveUIDocument?.Document; if (doc == null) return Result.Cancelled;
                PluginContext.UiApplication = data.Application;
                PluginContext.EnsureInitialized();
                var settings = Models.ComparatorSettings.Load();
                if (geometry)
                {
                    PluginLog.Info("ARST_SETTINGS_OPENED");
                    var dialog = new SettingsWindow(doc, settings);
                    if (dialog.ShowDialog() != true)
                    {
                        PluginLog.Info("ARST_SETTINGS_CANCELLED");
                        return Result.Cancelled;
                    }
                    dialog.Settings.Save();
                    settings = dialog.Settings;
                }
                PluginContext.Settings = settings;
                ResultsPane.BeginComparisonRefresh("Refreshing AR and ST links...");
                if (geometry) PluginLog.Info("ARST_CALCULATION_STARTED");
                LinkRefreshService.RefreshSelectedLinks(
                    doc,
                    PluginContext.Settings,
                    geometry,
                    ResultsPane.SetGlobalStatus);
                var report = PluginContext.Engine.Run(doc, PluginContext.Settings, geometry, rooms);
                PluginContext.LastReport = report;
                ResultsPane.ShowPane(data.Application, report);
                if (report.Warnings.Count > 0) TaskDialog.Show("BuildAI", string.Join("\n", report.Warnings));
                return Result.Succeeded;
            }
            catch (Exception ex) { PluginLog.Error("Plugin4 comparison failed", ex); TaskDialog.Show("BuildAI — Compare AR/ST", ex.ToString()); message = ex.Message; return Result.Failed; }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class CheckRoomsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => CompareModelsCommand.Run(data, ref message, false, true);
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class OpenResultsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                PluginContext.UiApplication = data.Application;
                PluginContext.EnsureInitialized();
                ResultsPane.ShowPane(data.Application, PluginContext.LastReport ?? new Models.ComparisonReport());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Plugin4 results initialization failed", ex);
                TaskDialog.Show("BuildAI — AR/ST Results", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var doc = data.Application.ActiveUIDocument?.Document;
                PluginContext.UiApplication = data.Application;
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var fullSettings=assembly.GetType("Plugin5.ClashFormaIntegration.UI.SettingsWindow",false);
                    if(fullSettings==null)continue;
                    var window=Activator.CreateInstance(fullSettings,doc) as System.Windows.Window;
                    window?.ShowDialog();
                    PluginLog.Info("ACC_SETTINGS_OPENED mode=FullSuite host=ARST");
                    return Result.Succeeded;
                }
                PluginContext.EnsureInitialized();
                new GeneralSettingsWindow().ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Plugin4 settings initialization failed", ex);
                TaskDialog.Show("BuildAI — AR/ST Settings", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
