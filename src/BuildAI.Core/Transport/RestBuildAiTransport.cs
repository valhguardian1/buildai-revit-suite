using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;

namespace BuildAI.Core.Transport
{
    /// <summary>
    /// REST transport: HTTPS + "Authorization: Bearer {API_TOKEN}" +
    /// "Content-Type: application/json" — exactly the scheme the live backend
    /// uses today. A single shared HttpClient (best practice for socket reuse).
    /// </summary>
    public sealed class RestBuildAiTransport : IBuildAiTransport, IDisposable
    {
        private readonly HttpClient _http;
        private readonly ICredentialStore _credentials;

        public RestBuildAiTransport(ICredentialStore credentials, HttpClient http = null)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<SendResult> PostJsonAsync(string url, string json, CancellationToken ct = default)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var token = _credentials.GetApiToken();
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var code = (int)resp.StatusCode;
                        if (resp.IsSuccessStatusCode)
                            return SendResult.Ok(code);

                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        PluginLog.Warn($"POST {url} -> {code}: {Truncate(body, 300)}");
                        return SendResult.Fail(code, body);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"POST {url} threw", ex);
                return SendResult.Fail(0, ex.Message);
            }
        }

        private static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

        public void Dispose() => _http.Dispose();
    }
}
