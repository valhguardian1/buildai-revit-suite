using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BuildAI.Core.Issues
{
    public sealed class IssueCaption
    {
        public string Title { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Human-scale Hebrew captions for Autodesk Issues.</summary>
    public static class IssueCaptionBuilder
    {
        private static readonly Dictionary<string, string> Nouns =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Floors"] = "רצפה", ["Walls"] = "קיר", ["Columns"] = "עמוד",
                ["Structural Framing"] = "קורה", ["Ceilings"] = "תקרה",
                ["Ducts"] = "תעלה", ["Duct Fittings"] = "תעלה",
                ["Pipes"] = "צינור", ["Pipe Fittings"] = "צינור",
                ["Sprinklers"] = "ספרינקלר", ["Mechanical Equipment"] = "מזגן",
                ["Cable Trays"] = "תעלת חשמל", ["Conduits"] = "מוביל",
                ["Lighting Fixtures"] = "גוף תאורה", ["Electrical Equipment"] = "לוח חשמל"
            };

        public static IssueCaption Clash(string primaryCategory, string secondaryCategory, string level, string runId)
        {
            var a = Noun(primaryCategory); var b = Noun(secondaryCategory);
            return Caption(a + " בתוך " + b, level, null, a + " חותך " + b + ".", runId);
        }

        public static IssueCaption MissingMatch(bool architecturalOnly, string category, string level, double? deviationMm, string runId)
        {
            var noun = Noun(category);
            var relation = architecturalOnly ? "ללא התאמה בקונסטרוקציה" : "ללא התאמה באדריכלות";
            var body = noun + (architecturalOnly ? " קיים במודל האדריכלי בלבד." : " קיים במודל הקונסטרוקטיבי בלבד.");
            if (deviationMm.HasValue) body += " סטייה: " + deviationMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " מ\"מ.";
            return Caption(noun + " " + relation, level, deviationMm, body, runId);
        }

        private static IssueCaption Caption(string title, string level, double? deviationMm, string body, string runId)
        {
            var suffix = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(level)) suffix.Append(level.Trim());
            if (deviationMm.HasValue)
            {
                if (suffix.Length > 0) suffix.Append(", ");
                suffix.Append(deviationMm.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append(" מ\"מ");
            }
            if (suffix.Length > 0) title += " — " + suffix;
            if (!string.IsNullOrWhiteSpace(runId)) body += "\n\n—\nריצה: " + (runId.Length > 8 ? runId.Substring(0, 8) : runId);
            if (title.Length > 90) title = title.Substring(0, 89).TrimEnd() + "…";
            return new IssueCaption { Title = title, Description = body };
        }

        private static string Noun(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "אלמנט";
            string noun;
            return Nouns.TryGetValue(value.Trim(), out noun) ? noun : value.Trim();
        }
    }
}
