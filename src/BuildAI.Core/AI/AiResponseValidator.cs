using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.AI
{
    public static class AiResponseValidator
    {
        private static readonly HashSet<string> Assessments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "real_issue", "minor_issue", "likely_noise", "manual_review" };
        private static readonly HashSet<string> Severities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "critical", "high", "medium", "low", "info" };

        public static List<AiRecommendation> ParseRecommendations(string json, IEnumerable<string> expectedKeys)
        {
            var expected = new HashSet<string>(expectedKeys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var root = JObject.Parse(ExtractJson(json));
            var rows = root["issues"] as JArray ?? throw new InvalidOperationException("AI response does not contain an issues array.");
            var result = new List<AiRecommendation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in rows)
            {
                var key = ((string)token["result_key"] ?? "").Trim();
                if (!expected.Contains(key) || !seen.Add(key)) continue;
                var assessment = ((string)token["assessment"] ?? "").Trim().ToLowerInvariant();
                var severity = ((string)token["severity"] ?? "").Trim().ToLowerInvariant();
                if (!Assessments.Contains(assessment)) throw new InvalidOperationException("Invalid AI assessment: " + assessment);
                if (!Severities.Contains(severity)) throw new InvalidOperationException("Invalid AI severity: " + severity);
                var confidence = (double?)token["confidence"] ?? 0.0;
                confidence = Math.Max(0.0, Math.Min(1.0, confidence));
                var shouldCreate = (bool?)token["should_create_issue"] ?? true;
                if (confidence < 0.60) { assessment = "manual_review"; shouldCreate = true; }
                if (assessment == "likely_noise") { shouldCreate = false; severity = "info"; }
                result.Add(new AiRecommendation
                {
                    ResultKey = key,
                    ShouldCreateIssue = shouldCreate,
                    IsRealIssue = (bool?)token["is_real_issue"] ?? assessment != "likely_noise",
                    Assessment = assessment,
                    Severity = severity,
                    Confidence = confidence,
                    Reason = ((string)token["reason"] ?? "").Trim(),
                    Comment = ((string)token["comment"] ?? "").Trim(),
                    ResponsibleDiscipline = ((string)token["responsible_discipline"] ?? "Coordination").Trim(),
                    RecommendedAction = ((string)token["recommended_action"] ?? "Manual review").Trim()
                });
            }
            var missing = expected.Where(k => !seen.Contains(k)).Take(10).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("AI omitted result keys: " + string.Join(", ", missing));
            return result;
        }

        public static string ParseSummary(string json)
        {
            var root = JObject.Parse(ExtractJson(json));
            return ((string)root["summary"] ?? "").Trim();
        }

        private static string ExtractJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("AI returned an empty response.");
            var start = value.IndexOf('{'); var end = value.LastIndexOf('}');
            if (start < 0 || end <= start) throw new InvalidOperationException("AI response is not valid JSON.");
            return value.Substring(start, end - start + 1);
        }
    }
}
