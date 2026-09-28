using System;
using System.Linq;

namespace BuildAI.Core.Presentation
{
    public static class SummaryLines
    {
        public static string[] Split(string summary) => (summary ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(new[] { '\n' }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

        public static bool HasMore(string summary) => Split(summary).Length > 3;

        public static string Visible(string summary, bool expanded) =>
            string.Join(Environment.NewLine, expanded ? Split(summary) : Split(summary).Take(3));
    }
}
