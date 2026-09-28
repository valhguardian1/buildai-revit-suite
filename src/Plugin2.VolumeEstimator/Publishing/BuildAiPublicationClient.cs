using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Logging;
using Newtonsoft.Json;

namespace Plugin2.VolumeEstimator.Publishing
{
    public sealed class BuildAiPublicationClient
    {
        public async Task<RevitPublishResponse> PublishRvtAsync(
            string filePath,
            string revitModelUid,
            string baseUrl,
            string bearerToken,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Save the RVT model before publishing it to BuildAI.", filePath);
            if (string.IsNullOrWhiteSpace(revitModelUid))
                throw new InvalidOperationException("ProjectInformation.UniqueId could not be read.");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("BuildAI BaseUrl is empty.");
            if (string.IsNullOrWhiteSpace(bearerToken))
                throw new InvalidOperationException("BuildAI API token is missing. Use Connect first.");

            var endpoint = baseUrl.TrimEnd('/') + "/api/revit_publish/" + Uri.EscapeDataString(revitModelUid);
            var fileInfo = new FileInfo(filePath);
            PluginLog.Info("BuildAI Viewer publication request prepared", new
            {
                endpoint,
                revitModelUid,
                file = fileInfo.Name,
                sizeBytes = fileInfo.Length
            });
            progress?.Report("uploading");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var multipart = new MultipartFormDataContent())
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var fileContent = new StreamContent(stream))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(fileContent, "file", fileInfo.Name);

                PluginLog.Info("BuildAI Viewer RVT upload started", new { endpoint, revitModelUid, file = fileInfo.Name });
                using (var response = await http.PostAsync(endpoint, multipart, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    PluginLog.Info("BuildAI Viewer publication response received", new
                    {
                        endpoint,
                        status = (int)response.StatusCode,
                        response = Truncate(body, 4000)
                    });
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("BuildAI publication HTTP " + (int)response.StatusCode + ": " + Truncate(body, 1800));

                    var result = JsonConvert.DeserializeObject<RevitPublishResponse>(body);
                    if (result == null)
                        throw new InvalidOperationException("BuildAI returned an empty or invalid publication response.");
                    if (string.IsNullOrWhiteSpace(result.Url))
                        throw new InvalidOperationException("BuildAI publication succeeded but the response does not contain url.");

                    PluginLog.Info("BuildAI Viewer publication completed", new
                    {
                        result.Id,
                        result.RevitModelId,
                        result.RevitModelUid,
                        result.FileName,
                        result.Url
                    });
                    return result;
                }
            }
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "...";
    }
}
