using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Issues
{
    public static class IssueCreationFileLog
    {
        private static readonly object Gate = new object();
        private static string _currentPath;

        public static string CurrentPath
        {
            get { lock (Gate) return _currentPath; }
        }

        public static string BeginSession(string source)
        {
            lock (Gate)
            {
                var folder = EnsureLogFolder();
                var safeSource = SanitizeFileName(string.IsNullOrWhiteSpace(source) ? "Issues" : source);
                _currentPath = Path.Combine(folder, "BuildAI_Issue_Creation_" + safeSource + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                File.WriteAllText(_currentPath,
                    "BuildAI Issue Creation Log" + Environment.NewLine +
                    "Format: BuildAI-LLM-Diagnostic/1 (compact; full successful HTTP bodies omitted)" + Environment.NewLine +
                    "Started: " + DateTime.Now.ToString("O") + Environment.NewLine +
                    "Source: " + source + Environment.NewLine +
                    new string('=', 90) + Environment.NewLine,
                    new UTF8Encoding(false));
                return _currentPath;
            }
        }

        public static string BeginOperationSession(string operation, string source)
        {
            lock (Gate)
            {
                var folder = EnsureLogFolder();
                var safeOperation = SanitizeFileName(string.IsNullOrWhiteSpace(operation) ? "Operation" : operation);
                var safeSource = SanitizeFileName(string.IsNullOrWhiteSpace(source) ? "BuildAI" : source);
                var path = Path.Combine(folder, "BuildAI_" + safeOperation + "_" + safeSource + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                File.WriteAllText(path,
                    "BuildAI " + operation + " Log" + Environment.NewLine +
                    "Format: BuildAI-LLM-Diagnostic/1 (compact; full successful HTTP bodies omitted)" + Environment.NewLine +
                    "Started: " + DateTime.Now.ToString("O") + Environment.NewLine +
                    "Source: " + source + Environment.NewLine +
                    new string('=', 90) + Environment.NewLine,
                    new UTF8Encoding(false));
                return path;
            }
        }

        public static void Write(string message)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(_currentPath)) return;
                WriteToCore(_currentPath, message);
            }
        }

        public static void WriteTo(string path, string message)
        {
            lock (Gate) WriteToCore(path, message);
        }

        public static void WriteProgress(IssueCreationProgress progress)
        {
            if (progress == null) return;
            var text = "PROGRESS " + progress.Current + "/" + progress.Total +
                       " | Created=" + progress.Succeeded +
                       " | Failed=" + progress.Failed +
                       " | Message=" + (progress.Message ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(progress.Error)) text += Environment.NewLine + progress.Error;
            if (progress.IsCompleted) text += Environment.NewLine + "COMPLETED";
            Write(text);
        }

        public static void WriteFatal(string message)
        {
            Write("FATAL" + Environment.NewLine + (message ?? "Unknown error."));
        }

        public static void WriteFatalTo(string path, string message)
        {
            WriteTo(path, "FATAL" + Environment.NewLine + (message ?? "Unknown error."));
        }

        private static string EnsureLogFolder()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var root = string.IsNullOrWhiteSpace(desktop)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : desktop;
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            var folder = Path.Combine(root, "BuildAI Logs");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void WriteToCore(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var compact = CompactForLlm(message ?? string.Empty);
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("O") + "] " + Redact(compact) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never interrupt the Revit operation.
            }
        }

        // The desktop log is intended to be attached to an LLM. Keep decisions,
        // ids, coordinates, errors and final payload invariants, but replace large
        // Autodesk/BuildAI JSON documents with deterministic summaries. This does
        // not change network traffic, exceptions or the structured PluginLog.
        private static string CompactForLlm(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            var normalized = message.Replace("\r\n", "\n");

            if (normalized.StartsWith("APS ISSUE REQUEST", StringComparison.Ordinal))
                return CompactIssueRequest(normalized);
            if (normalized.StartsWith("APS ISSUE RESPONSE", StringComparison.Ordinal))
                return CompactIssueResponse(normalized);
            if (normalized.StartsWith("VIEWER COORDINATE PROBE RESULT", StringComparison.Ordinal))
                return CompactViewerResult(normalized);
            if (normalized.IndexOf("Viewer candidate diagnostics:", StringComparison.Ordinal) >= 0)
                return CompactViewerCandidates(normalized);

            var bodyMarker = normalized.IndexOf("\nBody:\n", StringComparison.Ordinal);
            if (bodyMarker >= 0)
            {
                var prefix = normalized.Substring(0, bodyMarker);
                var body = normalized.Substring(bodyMarker + "\nBody:\n".Length);
                return prefix + "\nBody summary: " + SummarizeJson(body);
            }

            return Limit(normalized, 6000);
        }

        private static string CompactIssueRequest(string message)
        {
            string prefix;
            var json = ParseJsonTail(message, out prefix);
            if (json == null) return Limit(message, 6000);
            var linked = json["linkedDocuments"]?.FirstOrDefault();
            var details = linked?["details"];
            var state = details?["viewerState"];
            return prefix + "\n" +
                   "Issue: title=" + Short((string)json["title"], 180) +
                   " | subtype=" + ((string)json["issueSubtypeId"] ?? "<none>") +
                   " | assignee=" + ((string)json["assignedTo"] ?? "<none>") +
                   " | due=" + ((string)json["dueDate"] ?? "<none>") + "\n" +
                   "Pushpin: position=" + OneLine(details?["position"]) +
                   " | objectId=" + (details?["objectId"]?.ToString() ?? "<none>") +
                   " | externalId=" + Short((string)details?["externalId"], 180) + "\n" +
                   ViewerStateSummary(state);
        }

        private static string CompactIssueResponse(string message)
        {
            string prefix;
            var json = ParseJsonTail(message, out prefix);
            if (json == null) return Limit(message, 6000);
            var linked = json["linkedDocuments"]?.FirstOrDefault();
            var details = linked?["details"];
            return prefix + "\n" +
                   "Issue stored: id=" + ((string)json["id"] ?? "<none>") +
                   " | displayId=" + (json["displayId"]?.ToString() ?? "<none>") +
                   " | status=" + ((string)json["status"] ?? "<none>") +
                   " | linkedDocuments=" + (json["linkedDocuments"] is JArray a ? a.Count : 0) + "\n" +
                   "Stored pushpin: position=" + OneLine(details?["position"]) +
                   " | objectId=" + (details?["objectId"]?.ToString() ?? "<none>") +
                   " | externalId=" + Short((string)details?["externalId"], 180) + "\n" +
                   ViewerStateSummary(details?["viewerState"]);
        }

        private static string CompactViewerResult(string message)
        {
            var lines = message.Split('\n');
            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (line.StartsWith("modelToViewerTransform:", StringComparison.Ordinal) ||
                    line.StartsWith("placementTransform:", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("Viewer state: ", StringComparison.Ordinal))
                {
                    try { kept.Add(ViewerStateSummary(JToken.Parse(line.Substring("Viewer state: ".Length)))); }
                    catch { kept.Add("ViewerState: <summary unavailable>"); }
                    continue;
                }
                kept.Add(line);
            }
            return Limit(string.Join("\n", kept), 6000);
        }

        private static string CompactViewerCandidates(string message)
        {
            const string marker = "Viewer candidate diagnostics:";
            var index = message.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return Limit(message, 6000);
            var prefix = message.Substring(0, index) + "Viewer candidates:";
            try
            {
                var items = JArray.Parse(message.Substring(index + marker.Length).Trim());
                var summaries = items.Take(8).Select(item =>
                    "dbId=" + (item["dbId"]?.ToString() ?? "?") +
                    ", score=" + (item["score"]?.ToString() ?? "?") +
                    ", fragments=" + (item["fragmentCount"]?.ToString() ?? "0") +
                    ", node=" + Short((string)item["nodeName"], 80) +
                    ", externalId=" + Short((string)item["externalId"], 140));
                return prefix + " count=" + items.Count + "\n" + string.Join("\n", summaries);
            }
            catch { return Limit(message, 6000); }
        }

        private static string ViewerStateSummary(JToken state)
        {
            if (state == null) return "ViewerState: <absent>";
            var viewport = state["viewport"];
            var objectSets = state["objectSet"] as JArray;
            var selected = objectSets == null
                ? "<none>"
                : string.Join(";", objectSets.OfType<JObject>().Select(x => OneLine(x["id"])));
            var isolated = objectSets == null
                ? "<none>"
                : string.Join(";", objectSets.OfType<JObject>().Select(x => OneLine(x["isolated"])));
            return "ViewerState: seedURN=" + Short((string)state["seedURN"], 100) +
                   " | target=" + OneLine(viewport?["target"]) +
                   " | objectSets=" + (objectSets?.Count ?? 0) +
                   " | selected=" + selected +
                   " | isolated=" + isolated +
                   " | ghostHidden=" + (state["renderOptions"]?["appearance"]?["ghostHidden"]?.ToString() ?? "<none>") +
                   " | globalOffset=" + OneLine(state["globalOffset"]);
        }

        private static JToken ParseJsonTail(string message, out string prefix)
        {
            var objectStart = message.IndexOf('{');
            var arrayStart = message.IndexOf('[');
            var start = objectStart < 0 ? arrayStart : arrayStart < 0 ? objectStart : Math.Min(objectStart, arrayStart);
            if (start < 0) { prefix = message; return null; }
            prefix = message.Substring(0, start).TrimEnd();
            try { return JToken.Parse(message.Substring(start)); }
            catch { return null; }
        }

        private static string SummarizeJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || body == "<empty>") return "<empty>";
            try
            {
                var token = JToken.Parse(body);
                var parts = new List<string>();
                var obj = token as JObject;
                if (obj != null)
                {
                    AddScalar(obj, parts, "status");
                    AddScalar(obj, parts, "progress");
                    AddScalar(obj, parts, "code");
                    AddScalar(obj, parts, "errorCode");
                    AddScalar(obj, parts, "message");
                    AddScalar(obj, parts, "id");
                    AddScalar(obj, parts, "displayId");
                    AddScalar(obj, parts, "saved");
                    AddScalar(obj, parts, "updated");
                    AddScalar(obj, parts, "failed");
                    foreach (var name in new[] { "data", "results", "items", "issues", "linkedDocuments" })
                        if (obj[name] is JArray array) parts.Add(name + ".count=" + array.Count);
                    var important = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "error", "errors", "errorCode", "code", "message",
                        "developerMessage", "reason", "detail", "title"
                    };
                    foreach (var property in obj.Properties().Concat(obj.Descendants().OfType<JProperty>()))
                    {
                        if (!important.Contains(property.Name) ||
                            property.Value.Type == JTokenType.Array ||
                            property.Value.Type == JTokenType.Object) continue;
                        var entry = property.Name + "=" + Short(property.Value.ToString(), 240);
                        if (!parts.Contains(entry)) parts.Add(entry);
                        if (parts.Count >= 16) break;
                    }
                }
                else if (token is JArray rootArray)
                {
                    parts.Add("array.count=" + rootArray.Count);
                }
                return parts.Count == 0
                    ? "JSON " + token.Type + " (" + Encoding.UTF8.GetByteCount(body) + " bytes; body omitted)"
                    : string.Join(" | ", parts.Take(16));
            }
            catch { return "non-JSON " + Encoding.UTF8.GetByteCount(body) + " bytes: " + Short(body, 500); }
        }

        private static void AddScalar(JObject token, ICollection<string> parts, string name)
        {
            var value = token[name];
            if (value != null && value.Type != JTokenType.Array && value.Type != JTokenType.Object)
                parts.Add(name + "=" + Short(value.ToString(), 180));
        }

        private static string OneLine(JToken token)
            => token == null ? "<none>" : Short(token.ToString(Formatting.None), 300);

        private static string Short(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<none>";
            var oneLine = Regex.Replace(value, "\\s+", " ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }

        private static string Limit(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max) + "\n[LLM_LOG_TRUNCATED original_chars=" + value.Length + "]";

        private static string SanitizeFileName(string value)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace(' ', '_');
        }

        private static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = Regex.Replace(value, "(?i)(Authorization\\s*:\\s*Bearer\\s+)[^\\s\\\"']+", "$1***REDACTED***");
            value = Regex.Replace(value, "(?i)(\\\"(?:access_token|refresh_token|api_key|apikey|authorization)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")", "$1***REDACTED***$2");
            value = Regex.Replace(value, "(?i)(sk-or-v1-)[A-Za-z0-9_-]+", "$1***REDACTED***");
            return value;
        }
    }
}
