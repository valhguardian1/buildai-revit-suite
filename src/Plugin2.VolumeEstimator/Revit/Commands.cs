using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Configuration;
using BuildAI.Core.Issues;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using Plugin2.VolumeEstimator.UI;
using Plugin2.VolumeEstimator.Publishing;

namespace Plugin2.VolumeEstimator.Revit
{
    /// <summary>Recalculate volumes now and push the result to BuildAI (specification §5.2.2).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CalculateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            string recalculateLogPath = null;
            var ownsPublication = false;
            try
            {
                var doc = data?.Application?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show(Loc.T("Ribbon_Tab"), Loc.T("Vol_NoDoc"));
                    return Result.Cancelled;
                }
                ownsPublication = PublicationFlow.TryBeginAccPublication();
                if (!ownsPublication)
                {
                    TaskDialog.Show("BuildAI", "An ACC publication is already running. Wait for it to finish before recalculating.");
                    return Result.Cancelled;
                }

                recalculateLogPath = IssueCreationFileLog.BeginOperationSession("Recalculate", "Volumes");
                IssueCreationFileLog.WriteTo(recalculateLogPath,
                    "RECALCULATE STARTED" + Environment.NewLine +
                    "Document: " + (doc.Title ?? "") + Environment.NewLine +
                    "Path: " + (doc.PathName ?? "") + Environment.NewLine +
                    "Revit: " + (doc.Application?.VersionNumber ?? "unknown"));

                // A command runs in a valid API context, so estimate inline for
                // immediate feedback (Runner also enqueues + shows the window).
                var result = Runner.Run(doc, showWindow: false);
                IssueCreationFileLog.WriteTo(recalculateLogPath,
                    "CALCULATION COMPLETED" + Environment.NewLine +
                    "ProjectId: " + (result.ProjectId ?? "") + Environment.NewLine +
                    "Elements scanned: " + result.ElementsScanned + Environment.NewLine +
                    "Elements counted: " + result.ElementsCounted + Environment.NewLine +
                    "Rows: " + result.Rows.Count + Environment.NewLine +
                    "Sources: " + string.Join(", ", result.SourceNotes));
                ResultsPane.ShowPane(data.Application, result);
                ResultsPane.SetStatus("Diagnostic log: " + recalculateLogPath);
                var revitModelUid = doc.ProjectInformation?.UniqueId ?? "";
                try
                {
                    result.PublicationStatus = "preparing-acc";
                    ResultsPane.UpdatePublication(result);
                    var identity = AccViewPreparation.PrepareAndSynchronize(doc,
                        text => IssueCreationFileLog.WriteTo(recalculateLogPath, text));
                    _ = PublicationFlow.PublishAsync(identity, revitModelUid, result, ResultsPane.UpdatePublication, recalculateLogPath);
                    ownsPublication = false; // The asynchronous flow now owns the gate.
                }
                catch (Exception publicationError)
                {
                    result.PublicationStatus = "failed";
                    result.PublicationError = publicationError.Message;
                    ResultsPane.UpdatePublication(result);
                    IssueCreationFileLog.WriteFatalTo(recalculateLogPath, "ACC VIEW PREPARATION FAILED" + Environment.NewLine + publicationError);
                    TaskDialog.Show("BuildAI — ACC publication", "Calculation completed. " + publicationError.Message);
                }
                if (result.Rows.Count == 0)
                    TaskDialog.Show("BuildAI — Material Volumes", $"Calculation completed but produced no rows.\nElements scanned: {result.ElementsScanned}\nElements counted: {result.ElementsCounted}\nSources: {string.Join(", ", result.SourceNotes)}\nCheck model discipline rules and material volumes.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                IssueCreationFileLog.WriteFatalTo(recalculateLogPath, ex.ToString());
                PluginLog.Error("CalculateCommand failed", ex);
                TaskDialog.Show("BuildAI — Volume Estimator", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
            finally
            {
                if (ownsPublication) PublicationFlow.EndAccPublication();
            }
        }
    }

    /// <summary>Open the BuildAI web app for full visualization (specification §5.4).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class OpenInBuildAiCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var options = PluginContext.Options ?? BuildAiOptions.Load();
                var url = string.IsNullOrEmpty(options.BaseUrl) ? "https://app.buildai.me" : options.BaseUrl;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("OpenInBuildAiCommand failed", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Enter/replace the BuildAI API token (shared with Plugin 1 via
    /// Windows Credential Manager).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var store = new WindowsCredentialStore();
                if (TokenDialog.TryPrompt(store.GetApiToken(), out var token))
                {
                    store.SetApiToken(token);
                    PluginContext.Client?.Resume();
                    TaskDialog.Show(Loc.T("Ribbon_Tab"), Loc.T("Token_Saved"));
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("ConnectCommand failed", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
