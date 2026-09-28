using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.AI
{
    public sealed class AiClient
    {
        private readonly HttpClient _http;
        public AiClient(TimeSpan? timeout = null) { _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(180) }; }

        public async Task<string> CompleteJsonAsync(string endpoint, string apiKey, string model, string systemPrompt, object payload, string operation, CancellationToken ct = default)
        {
            var url = endpoint.TrimEnd('/'); if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url += "/chat/completions";
            Exception last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    object body = attempt < 3
                        ? (object)new { model, temperature = 0.1, response_format = new { type = "json_object" }, messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = JsonConvert.SerializeObject(payload) } } }
                        : new { model, temperature = 0.1, messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = JsonConvert.SerializeObject(payload) } } };
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                        PluginLog.Info(operation + " request", new { attempt, model, payload });
                        using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            PluginLog.Info(operation + " response", new { attempt, status = (int)response.StatusCode, body = text.Length > 4000 ? text.Substring(0, 4000) : text });
                            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("OpenRouter " + (int)response.StatusCode + ": " + text);
                            var root = JObject.Parse(text); var content = (string)root["choices"]?[0]?["message"]?["content"];
                            if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("OpenRouter returned an empty response.");
                            JObject.Parse(ExtractJson(content));
                            return content;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex; PluginLog.Error(operation + " attempt " + attempt + " failed", ex);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("AI unavailable after 3 attempts. Existing results were preserved.", last);
        }
        private static string ExtractJson(string value) { var start = value.IndexOf('{'); var end = value.LastIndexOf('}'); if (start < 0 || end <= start) throw new InvalidOperationException("AI response is not JSON."); return value.Substring(start, end - start + 1); }
    }
}
