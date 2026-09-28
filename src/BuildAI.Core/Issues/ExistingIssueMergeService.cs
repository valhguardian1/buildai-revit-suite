using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildAI.Core.Issues
{
    public enum IssueCreationState
    {
        New,
        AlreadyCreated,
        PreviouslyCreatedNotDetected,
        CreationFailed,
        CreatedThisRun
    }

    public interface IIssueCreationRow
    {
        string ResultKey { get; set; }
        string ApsIssueId { get; set; }
        int? ApsIssueDisplayId { get; set; }
        string ApsIssueStatus { get; set; }
        string ApsIssueUrl { get; set; }
        DateTime? ApsIssueCreatedAt { get; set; }
        bool IsSelectedForIssueCreation { get; set; }
        IssueCreationState CreationState { get; set; }
    }

    public sealed class ExistingIssueMergeResult
    {
        public int Calculated { get; set; }
        public int Existing { get; set; }
        public int MatchedByResultKey { get; set; }
        public int New { get; set; }
        public int AlreadyCreated { get; set; }
        public int HistoricalNotDetected { get; set; }
    }

    public static class ExistingIssueMergeService
    {
        public static ExistingIssueMergeResult Merge<T>(
            IList<T> rows,
            IReadOnlyList<ExistingRevitIssueDto> existing,
            Func<ExistingRevitIssueDto, T> historicalFactory,
            Action<string> diagnostic = null) where T : class, IIssueCreationRow
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            existing = existing ?? Array.Empty<ExistingRevitIssueDto>();

            // Historical rows are projections of the server response. Rebuild them
            // on refresh so stale entries cannot accumulate or lose their status.
            var calculated = rows.Where(x => x.CreationState != IssueCreationState.PreviouslyCreatedNotDetected).ToList();
            rows.Clear();
            foreach (var row in calculated) rows.Add(row);

            var result = new ExistingIssueMergeResult { Calculated = calculated.Count, Existing = existing.Count };
            var map = existing.Where(x => !string.IsNullOrWhiteSpace(x.ResultKey))
                .GroupBy(x => x.ResultKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(i => i.CreatedAt).First(), StringComparer.Ordinal);
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in calculated)
            {
                ExistingRevitIssueDto issue;
                if (!string.IsNullOrWhiteSpace(row.ResultKey) && map.TryGetValue(row.ResultKey, out issue))
                {
                    Apply(row, issue, IssueCreationState.AlreadyCreated);
                    matched.Add(row.ResultKey);
                    result.MatchedByResultKey++;
                    result.AlreadyCreated++;
                    diagnostic?.Invoke("EXISTING_ISSUE_MATCH | resultKey=" + Sanitize(row.ResultKey) +
                                       " | issueId=" + Sanitize(issue.IssueId) +
                                       " | displayId=" + (issue.DisplayId?.ToString() ?? "") +
                                       " | matchMethod=ExactResultKey");
                }
                else
                {
                    if (row.CreationState != IssueCreationState.CreatedThisRun &&
                        row.CreationState != IssueCreationState.CreationFailed)
                        row.CreationState = IssueCreationState.New;
                    result.New++;
                }
            }

            if (historicalFactory != null)
            {
                foreach (var issue in map.Values.Where(x => !matched.Contains(x.ResultKey)))
                {
                    var historical = historicalFactory(issue);
                    if (historical == null) continue;
                    Apply(historical, issue, IssueCreationState.PreviouslyCreatedNotDetected);
                    rows.Add(historical);
                    result.HistoricalNotDetected++;
                }
            }

            diagnostic?.Invoke("EXISTING_ISSUES_MERGE | calculated=" + result.Calculated +
                               " | existing=" + result.Existing +
                               " | matchedByResultKey=" + result.MatchedByResultKey +
                               " | new=" + result.New +
                               " | alreadyCreated=" + result.AlreadyCreated +
                               " | historicalNotDetected=" + result.HistoricalNotDetected);
            return result;
        }

        private static void Apply(IIssueCreationRow row, ExistingRevitIssueDto issue, IssueCreationState state)
        {
            row.ApsIssueId = issue.IssueId ?? "";
            row.ApsIssueDisplayId = issue.DisplayId;
            row.ApsIssueStatus = issue.IssueStatus ?? "";
            row.ApsIssueUrl = issue.IssueUrl ?? "";
            row.ApsIssueCreatedAt = issue.CreatedAt;
            row.CreationState = state;
            row.IsSelectedForIssueCreation = false;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= 12 ? value : value.Substring(0, 8) + "...";
        }
    }
}
