using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.Publishing
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
                throw new FileNotFoundException("Fayl RVT ne nayden.", filePath);
            if (string.IsNullOrWhiteSpace(revitModelUid))
                throw new InvalidOperationException("Ne udalos poluchit revit_model_uid modeli.");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("V nastroykakh ne ukazan BuildAI URL.");
            if (string.IsNullOrWhiteSpace(bearerToken))
                throw new InvalidOperationException("V nastroykakh ne ukazan Bearer-token BuildAI.");

            var endpoint = baseUrl.TrimEnd('/') + "/api/revit_publish/" + Uri.EscapeDataString(revitModelUid);
            progress?.Report("Podgotovka fayla k zagruzke...");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var multipart = new MultipartFormDataContent())
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var fileContent = new StreamContent(stream))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(fileContent, "file", Path.GetFileName(filePath));

                progress?.Report("Zagruzka modeli v BuildAI...");
                using (var response = await http.PostAsync(endpoint, multipart, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new BuildAiPublishException(
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            body);
                    }

                    var result = JsonConvert.DeserializeObject<RevitPublishResponse>(body);
                    if (result == null)
                        throw new InvalidOperationException("BuildAI vernul pustoy ili nekorrektnyy JSON.");

                    return result;
                }
            }
        }
    }

    public sealed class BuildAiPublishException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public BuildAiPublishException(int statusCode, string reasonPhrase, string responseBody)
            : base("BuildAI vernul HTTP " + statusCode +
                   (string.IsNullOrWhiteSpace(reasonPhrase) ? "" : " " + reasonPhrase) +
                   (string.IsNullOrWhiteSpace(responseBody) ? "" : ". Otvet: " + responseBody))
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? "";
        }
    }
}
