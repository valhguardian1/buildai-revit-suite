using System;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using Plugin2.VolumeEstimator.Estimation;
using Plugin2.VolumeEstimator.UI;
using Plugin2.VolumeEstimator.Publishing;

namespace Plugin2.VolumeEstimator.Revit
{
    /// <summary>
    /// Runs a Volume Estimator pass from a valid Revit API context. Used by the
    /// auto-on-sync path (specification §5.2.2), where the triggering event is not itself a
    /// command. The manual ribbon button runs <see cref="Runner.Run"/> directly.
    /// </summary>
    public sealed class RecalcExternalEventHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            string recalculateLogPath = null;
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return;
                recalculateLogPath = IssueCreationFileLog.BeginOperationSession("Recalculate", "Volumes-AutoSync");
                IssueCreationFileLog.WriteTo(recalculateLogPath,
                    "AUTOMATIC RECALCULATE STARTED" + Environment.NewLine +
                    "Document: " + (doc.Title ?? "") + Environment.NewLine +
                    "Path: " + (doc.PathName ?? ""));
                var result = Runner.Run(doc, showWindow: false);
                IssueCreationFileLog.WriteTo(recalculateLogPath,
                    "CALCULATION COMPLETED" + Environment.NewLine +
                    "Elements scanned: " + result.ElementsScanned + Environment.NewLine +
                    "Elements counted: " + result.ElementsCounted + Environment.NewLine +
                    "Rows: " + result.Rows.Count);
                var path = doc.PathName;
                var uid = doc.ProjectInformation?.UniqueId ?? "";
                _ = PublicationFlow.PublishAsync(path, uid, result, operationLogPath: recalculateLogPath);
            }
            catch (Exception ex)
            {
                IssueCreationFileLog.WriteFatalTo(recalculateLogPath, ex.ToString());
                PluginLog.Error("RecalcExternalEventHandler failed", ex);
            }
        }

        public string GetName() => "BuildAI Volume Estimator Recalc";
    }

    /// <summary>Shared estimate → enqueue → (optionally) show logic.</summary>
    public static class Runner
    {
        public static EstimationResult Run(Document doc, bool showWindow)
        {
            var options = PluginContext.Options ?? BuildAI.Core.Configuration.BuildAiOptions.Load();
            var sw = Stopwatch.StartNew();
            var result = VolumeTakeoff.Estimate(doc, options);
            sw.Stop();
            PluginLog.Info($"Volume calculation: {sw.Elapsed.TotalSeconds:N2} s; scanned={result.ElementsScanned}; rows={result.Rows.Count}");

            var client = PluginContext.Client;
            if (client != null && result.Rows.Count > 0)
            {
                PluginLog.Info("Volume materials queued for BuildAI", new { result.ProjectId, rows = result.Rows.Count, endpoint = options.ResolveMaterialsUrl(result.ProjectId) });
                client.EnqueueMaterials(result.ProjectId, result.Rows);
            }
            else
            {
                PluginLog.Warn("Volume materials were not queued", new { hasClient = client != null, rows = result.Rows.Count, result.ProjectId });
            }

            if (showWindow)
                ResultsPane.ShowPane(new UIApplication(doc.Application), result);

            return result;
        }
    }
}
