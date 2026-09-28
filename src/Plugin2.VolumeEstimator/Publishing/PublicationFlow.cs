using System;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Configuration;
using BuildAI.Core.APS;
using BuildAI.Core.Issues;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using Plugin2.VolumeEstimator.Estimation;

namespace Plugin2.VolumeEstimator.Publishing
{
    public static class PublicationFlow
    {
        private static int _accPublicationInProgress;
        public static bool TryBeginAccPublication() => Interlocked.CompareExchange(ref _accPublicationInProgress, 1, 0) == 0;
        public static void EndAccPublication() => Interlocked.Exchange(ref _accPublicationInProgress, 0);

        public static async Task PublishAsync(
            ApsCloudModelIdentity identity, string revitModelUid, EstimationResult result,
            Action<EstimationResult> stateChanged, string operationLogPath)
        {
            try
            {
                result.PublicationStatus = "publishing-acc";
                Notify(stateChanged, result);
                var options = Plugin2.VolumeEstimator.Revit.PluginContext.Options ?? BuildAiOptions.Load();
                var diagnostics = new InlineProgress(text => IssueCreationFileLog.WriteTo(operationLogPath, text));
                using (var client = new IssueIntegrationClient(options.BaseUrl, new WindowsCredentialStore()))
                using (var publication = new ApsPublicationClient())
                {
                    var token = await client.GetApsTokenAsync(default, diagnostics).ConfigureAwait(false);
                    var context = await publication.PublishAndResolveAsync(identity, token,
                        Plugin2.VolumeEstimator.Revit.AccViewPreparation.ViewName, default, diagnostics,
                        checkPreviousPublishSet: false).ConfigureAwait(false);
                    if (!context.Is3D || string.IsNullOrWhiteSpace(context.DerivativeUrn) ||
                        string.IsNullOrWhiteSpace(context.ViewableGeometryGuid))
                        throw new InvalidOperationException("ACC did not return a usable 3D view.");
                    result.AccView = context;
                    result.PublicationStatus = "published-acc";
                    result.PublicationError = "";
                    IssueCreationFileLog.WriteTo(operationLogPath, "ACC VIEW PUBLICATION COMPLETED" + Environment.NewLine +
                        Newtonsoft.Json.JsonConvert.SerializeObject(new { revitModelUid, cloudModel = identity, view = context }));
                }
            }
            catch (Exception ex)
            {
                result.PublicationStatus = "failed";
                result.PublicationError = ex.Message;
                IssueCreationFileLog.WriteFatalTo(operationLogPath, "ACC VIEW PUBLICATION FAILED" + Environment.NewLine + ex);
                PluginLog.Error("Recalculate ACC publication failed", ex);
            }
            finally
            {
                EndAccPublication();
                Notify(stateChanged, result);
            }
        }

        private sealed class InlineProgress : IProgress<string>
        {
            private readonly Action<string> _report;
            public InlineProgress(Action<string> report) { _report = report; }
            public void Report(string value) => _report(value);
        }

        public static async Task PublishAsync(
            string filePath,
            string revitModelUid,
            EstimationResult result,
            Action<EstimationResult> stateChanged = null,
            string operationLogPath = null,
            CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            using (PluginLog.BeginOperation("VolumeRecalculateViewerPublication"))
            {
                try
                {
                    IssueCreationFileLog.WriteTo(operationLogPath, "VIEWER PUBLICATION STARTED");
                    result.PublicationStatus = "uploading";
                    Notify(stateChanged, result);
                    var options = Plugin2.VolumeEstimator.Revit.PluginContext.Options ?? BuildAiOptions.Load();
                    var token = new WindowsCredentialStore().GetApiToken();
                    PluginLog.Info("Volume recalculation started the paired Viewer publication", new
                    {
                        result.ProjectId,
                        revitModelUid,
                        filePath,
                        materialsRows = result.Rows?.Count ?? 0
                    });

                    var response = await new BuildAiPublicationClient().PublishRvtAsync(
                        filePath, revitModelUid, options.BaseUrl, token, null, cancellationToken).ConfigureAwait(false);
                    result.PublicationId = response.Id;
                    result.PublishedRevitModelId = response.RevitModelId;
                    result.PublishedFileName = response.FileName;
                    Uri absoluteUrl;
                    result.PublicationUrl = Uri.TryCreate(response.Url, UriKind.Absolute, out absoluteUrl)
                        ? absoluteUrl.AbsoluteUri
                        : options.BaseUrl.TrimEnd('/') + "/" + response.Url.TrimStart('/');
                    result.PublicationError = "";
                    result.PublicationStatus = "published";
                    IssueCreationFileLog.WriteTo(operationLogPath,
                        "VIEWER PUBLICATION COMPLETED" + Environment.NewLine +
                        "PublicationId: " + (result.PublicationId.HasValue ? result.PublicationId.Value.ToString() : "") + Environment.NewLine +
                        "RevitModelId: " + (result.PublishedRevitModelId.HasValue ? result.PublishedRevitModelId.Value.ToString() : "") + Environment.NewLine +
                        "File: " + (result.PublishedFileName ?? "") + Environment.NewLine +
                        "URL: " + (result.PublicationUrl ?? ""));
                    Notify(stateChanged, result);
                }
                catch (Exception ex)
                {
                    result.PublicationStatus = "failed";
                    result.PublicationError = ex.Message;
                    IssueCreationFileLog.WriteFatalTo(operationLogPath, "VIEWER PUBLICATION FAILED" + Environment.NewLine + ex);
                    PluginLog.Error("Volume recalculation Viewer publication failed", ex, new
                    {
                        result.ProjectId,
                        revitModelUid,
                        filePath
                    });
                    Notify(stateChanged, result);
                }
            }
        }

        private static void Notify(Action<EstimationResult> callback, EstimationResult result)
        {
            try { callback?.Invoke(result); }
            catch (Exception ex) { PluginLog.Warn("Viewer publication UI status could not be refreshed", new { error = ex.Message }); }
        }
    }
}
