using System;
using System.Collections.Generic;

namespace Plugin5.ClashFormaIntegration.Models
{
    /// <summary>
    /// Resumable position inside a chunked clash run.
    /// <para>
    /// Clash detection splits into a cheap phase that enumerates candidate pairs
    /// once, and an expensive phase that solid-checks them. Only the expensive
    /// phase is paged, so the whole cursor is an index into the candidate array
    /// plus the state that must survive between pages.
    /// </para>
    /// </summary>
    public sealed class ClashCursor
    {
        /// <summary>
        /// Identifies the model state the candidate array was built from. When it
        /// stops matching, the array describes geometry that no longer exists and
        /// continuing would produce results against stale elements.
        /// </summary>
        public string ModelFingerprint { get; set; } = "";

        /// <summary>Index of the first candidate not yet solid-checked.</summary>
        public int NextCandidateIndex { get; set; }

        /// <summary>Total candidate pairs found by the bounding-box phase.</summary>
        public int CandidateCount { get; set; }

        /// <summary>
        /// Pair keys already emitted. Must persist across pages: a pair reachable
        /// from two source combinations would otherwise be reported twice on
        /// different pages, where the in-page duplicate check cannot see it.
        /// </summary>
        public HashSet<string> SeenPairKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Exact checks performed across all pages so far.</summary>
        public int TotalExactChecks { get; set; }

        /// <summary>
        /// Measured cost of one exact check, carried between pages so the work
        /// budget adapts to the model instead of relying on a fixed guess.
        /// Per-check cost varies by an order of magnitude between models.
        /// </summary>
        public double MsPerExactCheck { get; set; }

        public bool IsExhausted => NextCandidateIndex >= CandidateCount;

        public int RemainingCandidates =>
            CandidateCount - NextCandidateIndex < 0 ? 0 : CandidateCount - NextCandidateIndex;
    }

    /// <summary>
    /// Budget for a single page. A page ends when ANY limit is reached.
    /// <para>
    /// All three are required. A result-only limit never terminates on a clean
    /// model, where hundreds of thousands of candidates can yield a handful of
    /// clashes. A time-only limit makes page size vary unpredictably between runs.
    /// </para>
    /// </summary>
    public sealed class ClashPageBudget
    {
        public int MaxResults { get; set; }
        public int MaxExactChecks { get; set; }
        public TimeSpan MaxDuration { get; set; }

        /// <summary>Scaling constant for the result cap. See <see cref="ForCandidates"/>.</summary>
        public const double ResultScale = 20000d;
        public const int MinResults = 50;
        public const int MaxResultsCap = 400;

        public static readonly TimeSpan FirstPageDuration = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan LaterPageDuration = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Derives a page budget from the candidate-pair count.
        /// <para>
        /// The result cap falls as the inverse square root of the candidate count:
        /// a larger model needs a SMALLER first page, not a larger one. Each exact
        /// check costs more, the user needs something on screen sooner, and nobody
        /// triages four hundred clashes in one sitting. Observed working batches
        /// across logged production runs are 1-104 rows.
        /// </para>
        /// <para>
        /// These constants are calibration starting points. Instrument
        /// <c>MsPerExactCheck</c>, hit rate and page wall time on real models and
        /// refit them; anything chosen without that data is an educated guess.
        /// </para>
        /// </summary>
        public static ClashPageBudget ForCandidates(int candidateCount, bool firstPage, double msPerExactCheck)
        {
            var p = candidateCount < 1 ? 1 : candidateCount;

            var results = (int)Math.Round(ResultScale / Math.Sqrt(p));
            if (results < MinResults) results = MinResults;
            if (results > MaxResultsCap) results = MaxResultsCap;

            var duration = firstPage ? FirstPageDuration : LaterPageDuration;

            // Once a page has been measured, size the next one by time rather than
            // by a fraction of the candidate set.
            int checks;
            if (msPerExactCheck > 0.0001)
            {
                checks = (int)Math.Round(duration.TotalMilliseconds / msPerExactCheck);
                if (checks < 500) checks = 500;
                if (checks > 50000) checks = 50000;
            }
            else
            {
                checks = p / 8;
                if (checks < 2000) checks = 2000;
                if (checks > 50000) checks = 50000;
            }

            return new ClashPageBudget
            {
                MaxResults = results,
                MaxExactChecks = checks,
                MaxDuration = duration
            };
        }

        /// <summary>Budget with the result and work caps removed, for "check all remaining".</summary>
        public static ClashPageBudget Unlimited()
        {
            return new ClashPageBudget
            {
                MaxResults = int.MaxValue,
                MaxExactChecks = int.MaxValue,
                MaxDuration = TimeSpan.FromHours(12)
            };
        }
    }

    /// <summary>Why a page stopped, so the UI can say something truthful.</summary>
    public enum ClashPageStopReason
    {
        Exhausted = 0,
        ResultCapReached = 1,
        WorkCapReached = 2,
        TimeCapReached = 3,
        Cancelled = 4
    }

    /// <summary>Outcome of one page, appended to the report the UI already holds.</summary>
    public sealed class ClashPageResult
    {
        public List<ClashItem> Items { get; set; } = new List<ClashItem>();
        public ClashPageStopReason StopReason { get; set; }
        public int ExactChecksPerformed { get; set; }
        public int CandidatesConsumed { get; set; }
        public long ElapsedMs { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
