using System;

namespace BuildAI.Core.Issues
{
    public enum IssueLocationDetailsSource
    {
        RowLevel,
        PrimaryElement,
        SecondaryElement,
        TitleFallback,
        Missing
    }

    public sealed class IssueLocationDetailsResolution
    {
        public string Value { get; set; }
        public IssueLocationDetailsSource Source { get; set; }
    }

    public static class IssueLocationDetailsResolver
    {
        private const string Separator = " — ";

        public static IssueLocationDetailsResolution Resolve(
            string rowLevel,
            string primaryElementLevel,
            string secondaryElementLevel,
            string title)
        {
            var value = CleanBoundary(rowLevel);
            if (value.Length > 0) return Found(value, IssueLocationDetailsSource.RowLevel);

            value = CleanBoundary(primaryElementLevel);
            if (value.Length > 0) return Found(value, IssueLocationDetailsSource.PrimaryElement);

            value = CleanBoundary(secondaryElementLevel);
            if (value.Length > 0) return Found(value, IssueLocationDetailsSource.SecondaryElement);

            var text = title ?? "";
            var separator = text.LastIndexOf(Separator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                value = CleanBoundary(text.Substring(separator + Separator.Length));
                if (value.Length > 0) return Found(value, IssueLocationDetailsSource.TitleFallback);
            }

            return Found(null, IssueLocationDetailsSource.Missing);
        }

        private static IssueLocationDetailsResolution Found(string value, IssueLocationDetailsSource source)
            => new IssueLocationDetailsResolution { Value = value, Source = source };

        // String.Trim removes surplus outer whitespace but deliberately does not
        // remove bidi controls such as RLM/LRM. Those can be essential to correct
        // Hebrew display and must survive the ACC payload unchanged.
        private static string CleanBoundary(string value) => (value ?? "").Trim();
    }
}
