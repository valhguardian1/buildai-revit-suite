using Plugin5.ClashFormaIntegration.Models;
using Plugin5.ClashFormaIntegration.Clash;
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plugin5.ClashFormaIntegration.UI;

namespace Plugin5.ClashFormaIntegration.Revit
{
    [Transaction(TransactionMode.Manual)]
    public sealed class RunClashCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                PluginContext.UiApplication = commandData.Application;
                var document = commandData.Application.ActiveUIDocument?.Document;
                if (document == null) return Result.Cancelled;
                var dialog = new ClashRunWindow(document);
                if (dialog.ShowDialog() != true || dialog.Options == null) return Result.Cancelled;

                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    // Bounding-box phase first: cheap, and it yields the candidate
                    // count that sizes the first page. Only then is anything
                    // solid-checked.
                    var session = PluginContext.Engine.BeginSession(document, dialog.Options);
                    var budget = ClashPageBudget.ForCandidates(session.Cursor.CandidateCount, true, 0);
                    session.RunPage(budget);
                    session.ReleaseGeometryCaches();
                    ClashEngine.FinalizeReport(session.Report);
                    PluginContext.Session = session;
                    PluginContext.Report = session.Report;
                    PluginContext.SaveReport();
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }
                Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BuildAI", ex.Message);
                return Result.Failed;
            }
        }

        internal static void Show()
        {
            if (PluginContext.Report == null) PluginContext.Report = PluginContext.LoadReport();
            if (PluginContext.Window == null || !PluginContext.Window.IsLoaded)
                PluginContext.Window = new ResultsWindow();
            PluginContext.Window.RefreshData();
            PluginContext.Window.Show();
            PluginContext.Window.Activate();
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class OpenResultsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RunClashCommand.Show();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class CreateViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            PluginContext.Handler.Action = ModelAction.CreateViews;
            PluginContext.Event.Raise();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            PluginContext.UiApplication=commandData.Application;
            new SettingsWindow(commandData.Application.ActiveUIDocument?.Document).ShowDialog();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class PublishCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var document = commandData.Application.ActiveUIDocument?.Document;
            if (document == null)
            {
                TaskDialog.Show("BuildAI", "Open a Revit project.");
                return Result.Cancelled;
            }
            if (document.IsFamilyDocument)
            {
                TaskDialog.Show("BuildAI", "Family documents cannot be published. Open an RVT project.");
                return Result.Cancelled;
            }
            if (string.IsNullOrWhiteSpace(document.PathName))
            {
                TaskDialog.Show("BuildAI", "Save the RVT model first.");
                return Result.Cancelled;
            }

            string uid;
            try { uid = document.ProjectInformation?.UniqueId ?? ""; }
            catch { uid = ""; }
            if (string.IsNullOrWhiteSpace(uid))
            {
                TaskDialog.Show("BuildAI", "ProjectInformation.UniqueId could not be read.");
                return Result.Failed;
            }

            new PublishWindow(document.PathName, uid).ShowDialog();
            return Result.Succeeded;
        }
    }
}
