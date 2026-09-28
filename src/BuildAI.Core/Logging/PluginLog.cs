using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildAI.Core.Configuration;
using BuildAI.Core.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Logging
{
    /// <summary>
    /// Central logger for all BuildAI Revit plugins.
    /// - structured JSONL + readable text files;
    /// - correlation/session/plugin context;
    /// - local retention;
    /// - non-blocking batch delivery to BuildAI;
    /// - delivery failures never affect Revit and remain in the local spool.
    /// </summary>
    public static class PluginLog
    {
        private static readonly object FileGate = new object();
        private static readonly ConcurrentQueue<LogEntry> Queue = new ConcurrentQueue<LogEntry>();
        private static readonly SemaphoreSlim FlushGate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly string SessionId = Guid.NewGuid().ToString("N");
        private static Timer _timer;
        private static BuildAiOptions _options;
        private static string _plugin = "BuildAI";
        private static string _pluginVersion = "unknown";
        private static string _revitVersion = "unknown";
        private static string _projectId;
        private static string _document;
        private static bool _spoolRestored;

        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuildAI", "logs");
        public static string SpoolPath => Path.Combine(LogDirectory, "remote-spool.jsonl");

        public static void Configure(string plugin, string pluginVersion = null, string revitVersion = null,
            string projectId = null, string document = null, BuildAiOptions options = null)
        {
            try
            {
                _plugin = string.IsNullOrWhiteSpace(plugin) ? "BuildAI" : plugin;
                _pluginVersion = pluginVersion ?? "unknown";
                _revitVersion = revitVersion ?? "unknown";
                _projectId = Pseudonymize("project", projectId);
                _document = Pseudonymize("document", document);
                _options = options ?? BuildAiOptions.Load();
                CleanupOldFiles(_options.LogRetentionDays);
                if (!_spoolRestored)
                {
                    RestoreSpool();
                    _spoolRestored = true;
                }
                if (_options.RemoteLoggingEnabled && _timer == null)
                    _timer = new Timer(_ => _ = FlushAsync(), null,
                        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(Math.Max(10, _options.LogFlushSeconds)));
            }
            catch { }
        }

        public static IDisposable BeginOperation(string operation, string correlationId = null)
            => new OperationScope(operation, correlationId ?? Guid.NewGuid().ToString("N"));

        public static void Debug(string message, object data = null) => Write("DEBUG", message, null, data);
        public static void Info(string message, object data = null)  => Write("INFO", message, null, data);
        public static void Warn(string message, object data = null)  => Write("WARN", message, null, data);
        public static void Error(string message, Exception ex = null, object data = null) => Write("ERROR", message, ex, data);
        public static void Critical(string message, Exception ex = null, object data = null) => Write("CRITICAL", message, ex, data);

        private static readonly AsyncLocal<string> Correlation = new AsyncLocal<string>();
        private static readonly AsyncLocal<string> Operation = new AsyncLocal<string>();

        private static void Write(string level, string message, Exception ex, object data)
        {
            try
            {
                var entry = new LogEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Level = level,
                    Message = Sanitize(message),
                    ExceptionType = ex?.GetType().FullName,
                    ExceptionMessage = Sanitize(ex?.Message),
                    StackTrace = Sanitize(ex?.StackTrace),
                    Plugin = _plugin,
                    PluginVersion = _pluginVersion,
                    RevitVersion = _revitVersion,
                    SessionId = SessionId,
                    CorrelationId = Correlation.Value,
                    Operation = Operation.Value,
                    ProjectId = _projectId,
                    Document = _document,
                    Machine = Pseudonymize("machine", Environment.MachineName),
                    User = Pseudonymize("user", Environment.UserName),
                    Data = SanitizeData(data)
                };
                Directory.CreateDirectory(LogDirectory);
                var date = entry.TimestampUtc.ToString("yyyyMMdd");
                var json = JsonConvert.SerializeObject(entry, Formatting.None);
                var text = $"{entry.TimestampUtc:O} [{level}] [{entry.Plugin}] [{entry.CorrelationId}] {entry.Message}" +
                           (ex == null ? "" : " | " + ex);
                var logOptions = _options ?? BuildAiOptions.Load();
                var remoteLoggingEnabled = logOptions.RemoteLoggingEnabled;
                lock (FileGate)
                {
                    File.AppendAllText(Path.Combine(LogDirectory, $"buildai-{date}.jsonl"), json + Environment.NewLine, Encoding.UTF8);
                    File.AppendAllText(Path.Combine(LogDirectory, $"buildai-{date}.log"), text + Environment.NewLine, Encoding.UTF8);
                    var category = ResolveCategory(message);
                    File.AppendAllText(Path.Combine(LogDirectory, category + ".log"), text + Environment.NewLine, Encoding.UTF8);
                    if (remoteLoggingEnabled)
                    {
                        File.AppendAllText(SpoolPath, json + Environment.NewLine, Encoding.UTF8);
                        // Keep spool and in-memory queue insertion in the same
                        // critical section so a concurrent flush cannot rewrite
                        // the spool between these two operations.
                        Queue.Enqueue(entry);
                    }
                }
                if (remoteLoggingEnabled)
                {
                    if (Queue.Count >= Math.Max(1, logOptions.LogBatchSize)) _ = FlushAsync();
                }
            }
            catch { }
        }


        private static string ResolveCategory(string message)
        {
            var value = message ?? "";
            if (value.IndexOf("AI ", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("OpenRouter", StringComparison.OrdinalIgnoreCase) >= 0) return "AI";
            if (value.IndexOf("APS", StringComparison.OrdinalIgnoreCase) >= 0) return "APS";
            if (value.IndexOf("BuildAI", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Issues", StringComparison.OrdinalIgnoreCase) >= 0) return "BuildAI";
            return "Plugin";
        }
        public static async Task FlushAsync()
        {
            var options = _options ?? BuildAiOptions.Load();
            if (!options.RemoteLoggingEnabled || !await FlushGate.WaitAsync(0).ConfigureAwait(false)) return;
            List<LogEntry> batch = null;
            try
            {
                batch = new List<LogEntry>();
                while (batch.Count < Math.Max(1, options.LogBatchSize) && Queue.TryDequeue(out var item)) batch.Add(item);
                if (batch.Count == 0) return;
                var url = options.BaseUrl.TrimEnd('/') + "/" + options.LogsPath.TrimStart('/');
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var token = new WindowsCredentialStore().GetApiToken();
                    if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Content = new StringContent(JsonConvert.SerializeObject(new { entries = batch }), Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            foreach (var item in batch)
                            {
                                item.DeliveryAttempts++;
                                if (item.DeliveryAttempts <= 5) Queue.Enqueue(item);
                            }
                            RewriteSpoolFromQueue();
                            return;
                        }
                    }
                }
                RewriteSpoolFromQueue();
            }
            catch
            {
                // Network exceptions happen after the batch has been dequeued.
                // Return it immediately instead of waiting for a Revit restart
                // to restore the still-present disk spool.
                if (batch != null)
                {
                    foreach (var item in batch)
                    {
                        item.DeliveryAttempts++;
                        if (item.DeliveryAttempts <= 5) Queue.Enqueue(item);
                    }
                    RewriteSpoolFromQueue();
                }
            }
            finally { FlushGate.Release(); }
        }

        private static void RestoreSpool()
        {
            try
            {
                if (!File.Exists(SpoolPath)) return;
                foreach (var line in File.ReadLines(SpoolPath).Take(5000))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonConvert.DeserializeObject<LogEntry>(line);
                    if (entry != null)
                    {
                        entry.Message = Sanitize(entry.Message);
                        entry.ExceptionMessage = Sanitize(entry.ExceptionMessage);
                        entry.StackTrace = Sanitize(entry.StackTrace);
                        entry.Machine = Pseudonymize("machine", entry.Machine);
                        entry.User = Pseudonymize("user", entry.User);
                        entry.Data = SanitizeData(entry.Data);
                        Queue.Enqueue(entry);
                    }
                }
                RewriteSpoolFromQueue();
            }
            catch { }
        }

        private static void RewriteSpoolFromQueue()
        {
            try
            {
                lock (FileGate)
                {
                    var remaining = Queue.ToArray();
                    var temp = SpoolPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    var backup = SpoolPath + ".bak";
                    try
                    {
                        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            foreach (var item in remaining)
                                writer.WriteLine(JsonConvert.SerializeObject(item));
                            writer.Flush();
                            stream.Flush(true);
                        }
                        if (File.Exists(SpoolPath))
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Replace(temp, SpoolPath, backup, true);
                        }
                        else File.Move(temp, SpoolPath);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
            }
            catch { }
        }

        private static void CleanupOldFiles(int retentionDays)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;
                var threshold = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
                foreach (var file in Directory.GetFiles(LogDirectory, "buildai-*.*"))
                    if (File.GetLastWriteTimeUtc(file) < threshold) File.Delete(file);
            }
            catch { }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var result = value.Replace("\r", " ").Replace("\n", " ");
            result = Regex.Replace(result, @"(?i)(authorization\s*[:=]\s*bearer\s+|bearer\s+)[A-Za-z0-9._~+/=-]+", "$1<redacted>");
            result = Regex.Replace(result, @"(?i)(access_token|refresh_token|api[_-]?token|client_secret|password)(\s*[=:]\s*)[^\s,;\""']+", "$1$2<redacted>");
            result = Regex.Replace(result, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "<email>");
            result = Regex.Replace(result, @"(?i)\b[A-Z]:\\[^\r\n\""']+", "<local-path>");
            result = Regex.Replace(result, @"(?i)urn:[^\s,;\""']+", "<urn>");
            result = Regex.Replace(result, @"(?i)(/designdata/)[^/?\s]+", "$1<urn>");
            result = Regex.Replace(result, @"(?i)(/(?:containers|projects|hubs|items|versions|users)/)[^/?\s]+", "$1<id>");
            result = Regex.Replace(result, @"(?i)((?:project|container|model|document|user|owner|assignee)[ _-]?id\s*[:=]\s*)[^\s,;]+", "$1<id>");
            if (result.Length > 8000) result = result.Substring(0, 8000) + "…";
            return result;
        }

        private static object SanitizeData(object data)
        {
            if (data == null) return null;
            try
            {
                var token = JToken.FromObject(data);
                RedactToken(token);
                return token;
            }
            catch { return new { value = Sanitize(data.ToString()) }; }
        }

        private static void RedactToken(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsSecretName(property.Name)) property.Value = "<redacted>";
                    else if (IsPrivateIdentifierName(property.Name))
                    {
                        var text = property.Value.Type == JTokenType.String ? (string)property.Value : property.Value.ToString(Formatting.None);
                        property.Value = Pseudonymize("id", text);
                    }
                    else RedactToken(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array) RedactToken(child);
            }
            else if (token is JValue value && value.Type == JTokenType.String)
            {
                value.Value = Sanitize(value.Value as string);
            }
        }

        private static bool IsSecretName(string name)
        {
            var normalized = (name ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            return normalized.Contains("token") || normalized.Contains("secret") || normalized.Contains("password") || normalized.Contains("authorization");
        }

        private static bool IsPrivateIdentifierName(string name)
        {
            var normalized = (name ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            return normalized == "projectid" || normalized == "containerid" || normalized == "document" ||
                   normalized == "documenttitle" || normalized == "modelurn" || normalized == "seedurn" ||
                   normalized == "urn" || normalized == "userid" || normalized == "ownerid" ||
                   normalized == "assigneeid" || normalized == "email" || normalized == "username" ||
                   normalized == "body" || normalized == "title";
        }

        private static string Pseudonymize(string kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "anonymous";
            if (value.StartsWith(kind + "-", StringComparison.OrdinalIgnoreCase)) return value;
            try
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("BuildAI|" + kind + "|" + value));
                    return kind + "-" + BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return "anonymous"; }
        }

        private sealed class OperationScope : IDisposable
        {
            private readonly string _oldCorrelation = Correlation.Value;
            private readonly string _oldOperation = Operation.Value;
            private readonly DateTime _started = DateTime.UtcNow;
            private readonly string _operation;
            public OperationScope(string operation, string correlationId)
            {
                _operation = operation;
                Correlation.Value = correlationId;
                Operation.Value = operation;
                Info("Operation started");
            }
            public void Dispose()
            {
                Info("Operation completed", new { durationMs = (DateTime.UtcNow - _started).TotalMilliseconds });
                Correlation.Value = _oldCorrelation;
                Operation.Value = _oldOperation;
            }
        }
    }

    public sealed class LogEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
        public string StackTrace { get; set; }
        public string Plugin { get; set; }
        public string PluginVersion { get; set; }
        public string RevitVersion { get; set; }
        public string SessionId { get; set; }
        public string CorrelationId { get; set; }
        public string Operation { get; set; }
        public string ProjectId { get; set; }
        public string Document { get; set; }
        public string Machine { get; set; }
        public string User { get; set; }
        public object Data { get; set; }
        public int DeliveryAttempts { get; set; }
    }
}
