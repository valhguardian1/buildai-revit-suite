using System.Threading;
using System.Threading.Tasks;

namespace BuildAI.Core.Transport
{
    public sealed class SendResult
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Error { get; set; }
        public static SendResult Ok(int code) => new SendResult { Success = true, StatusCode = code };
        public static SendResult Fail(int code, string err) =>
            new SendResult { Success = false, StatusCode = code, Error = err };
    }

    /// <summary>
    /// Transport seam. REST today (RestBuildAiTransport); a WebSocket
    /// implementation (specification §4.4) can be added without touching plugin code.
    /// </summary>
    public interface IBuildAiTransport
    {
        /// <summary>POST a JSON body (already serialized) to an absolute URL.</summary>
        Task<SendResult> PostJsonAsync(string url, string json, CancellationToken ct = default);
    }
}
