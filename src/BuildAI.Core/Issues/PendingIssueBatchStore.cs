using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BuildAI.Core.Issues
{
    /// <summary>
    /// Durable outbox for Autodesk Issues that already exist but have not yet
    /// been acknowledged by the BuildAI batch endpoint.
    /// </summary>
    public sealed class PendingIssueBatchStore
    {
        private static readonly object Gate = new object();
        private readonly string _path;

        public PendingIssueBatchStore(string path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BuildAI", "issues", "pending-buildai-sync.json");
        }

        public IReadOnlyList<SaveIssueBatchRequest> GetPending(string revitModelUid, string source)
        {
            lock (Gate)
            {
                return Load()
                    .Where(x => !x.Retired)
                    .Where(x => Same(x.RevitModelUid, revitModelUid) && Same(x.Source, source))
                    .GroupBy(x => new { Model = Normalize(x.RevitModelUid), Source = Normalize(x.Source) })
                    .Select(g => new SaveIssueBatchRequest
                    {
                        RevitModelUid = g.First().RevitModelUid,
                        Source = g.First().Source,
                        Issues = g.Select(x => x.Issue).Where(x => x != null).ToList()
                    })
                    .Where(x => x.Issues.Count > 0)
                    .ToList();
            }
        }

        public void Enqueue(SaveIssueBatchRequest request, string fingerprint = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.RevitModelUid))
                throw new InvalidOperationException("Pending BuildAI synchronization requires revit_model_uid.");
            if (string.IsNullOrWhiteSpace(request.Source))
                throw new InvalidOperationException("Pending BuildAI synchronization requires source.");

            lock (Gate)
            {
                var entries = Load();
                foreach (var issue in request.Issues ?? new List<SavedIssueDto>())
                {
                    if (issue == null || (string.IsNullOrWhiteSpace(issue.IssueId) && string.IsNullOrWhiteSpace(issue.ResultKey)))
                        continue;
                    entries.RemoveAll(x => Same(x.RevitModelUid, request.RevitModelUid) &&
                                           Same(x.Source, request.Source) &&
                                           Same(IssueKey(x.Issue), IssueKey(issue)));
                    entries.Add(new PendingIssueEntry
                    {
                        RevitModelUid = request.RevitModelUid,
                        Source = request.Source,
                        Issue = issue,
                        Fingerprint = fingerprint ?? "",
                        QueuedAtUtc = DateTime.UtcNow
                    });
                }
                WriteAtomically(entries);
            }
        }

        public IReadOnlyList<string> GetFingerprints(SaveIssueBatchRequest request)
        {
            if (request == null) return Array.Empty<string>();
            lock (Gate)
            {
                var keys = new HashSet<string>((request.Issues ?? new List<SavedIssueDto>())
                    .Where(x => x != null).Select(IssueKey), StringComparer.OrdinalIgnoreCase);
                return Load().Where(x => Same(x.RevitModelUid, request.RevitModelUid) &&
                                         Same(x.Source, request.Source) &&
                                         keys.Contains(IssueKey(x.Issue)) &&
                                         !string.IsNullOrWhiteSpace(x.Fingerprint))
                    .Select(x => x.Fingerprint).Distinct(StringComparer.Ordinal).ToList();
            }
        }

        public void Acknowledge(SaveIssueBatchRequest request)
        {
            if (request == null) return;
            lock (Gate)
            {
                var keys = new HashSet<string>((request.Issues ?? new List<SavedIssueDto>())
                    .Where(x => x != null).Select(IssueKey), StringComparer.OrdinalIgnoreCase);
                var entries = Load();
                entries.RemoveAll(x => Same(x.RevitModelUid, request.RevitModelUid) &&
                                       Same(x.Source, request.Source) && keys.Contains(IssueKey(x.Issue)));
                WriteAtomically(entries);
            }
        }

        /// <summary>
        /// Maximum drain attempts before an entry is retired.
        /// <para>
        /// A batch the server rejects for a structural reason - an unrecognised
        /// revit_model_uid, for one - will be rejected identically forever. Without a
        /// ceiling such an entry is retried at the start of every run, fails, and
        /// takes the whole run down with it, so a single unsyncable Issue disables
        /// the plugin for that model permanently. Three attempts distinguish a real
        /// outage from a permanent rejection without discarding data prematurely.
        /// </para>
        /// </summary>
        public const int MaxDrainAttempts = 3;

        /// <summary>
        /// Records a failed drain attempt and retires the batch once it has used up
        /// its attempts. Retired entries stay in the journal, flagged, so support can
        /// still see which Autodesk Issues never reached BuildAI.
        /// </summary>
        /// <returns>True when this call retired the batch.</returns>
        public bool RecordFailure(SaveIssueBatchRequest request, string error)
        {
            if (request == null) return false;
            lock (Gate)
            {
                var keys = new HashSet<string>((request.Issues ?? new List<SavedIssueDto>())
                    .Where(x => x != null).Select(IssueKey), StringComparer.OrdinalIgnoreCase);
                var entries = Load();
                var retired = false;
                foreach (var entry in entries.Where(x => Same(x.RevitModelUid, request.RevitModelUid) &&
                                                         Same(x.Source, request.Source) &&
                                                         keys.Contains(IssueKey(x.Issue))))
                {
                    entry.FailedAttempts++;
                    entry.LastError = (error ?? "").Length > 500 ? (error ?? "").Substring(0, 500) : (error ?? "");
                    if (entry.FailedAttempts >= MaxDrainAttempts)
                    {
                        entry.Retired = true;
                        retired = true;
                    }
                }
                WriteAtomically(entries);
                return retired;
            }
        }

        /// <summary>
        /// Attempts already recorded against a batch, for diagnostics.
        /// </summary>
        public int AttemptsSoFar(SaveIssueBatchRequest request)
        {
            if (request == null) return 0;
            lock (Gate)
            {
                var keys = new HashSet<string>((request.Issues ?? new List<SavedIssueDto>())
                    .Where(x => x != null).Select(IssueKey), StringComparer.OrdinalIgnoreCase);
                return Load().Where(x => Same(x.RevitModelUid, request.RevitModelUid) &&
                                         Same(x.Source, request.Source) &&
                                         keys.Contains(IssueKey(x.Issue)))
                    .Select(x => x.FailedAttempts)
                    .DefaultIfEmpty(0)
                    .Max();
            }
        }

        /// <summary>
        /// Retires every queued batch older than the given age, regardless of how many
        /// attempts it has recorded.
        /// <para>
        /// Needed once, on upgrade: journals written before attempt counting existed
        /// carry FailedAttempts = 0, so a batch that has already failed dozens of times
        /// would still get a full fresh allowance. Anything queued long ago and never
        /// acknowledged is not waiting out a transient outage.
        /// </para>
        /// </summary>
        /// <returns>Number of batches retired.</returns>
        public int RetireStale(TimeSpan olderThan)
        {
            lock (Gate)
            {
                var cutoff = DateTime.UtcNow - olderThan;
                var entries = Load();
                var count = 0;
                foreach (var entry in entries.Where(x => !x.Retired && x.QueuedAtUtc != default(DateTime) && x.QueuedAtUtc < cutoff))
                {
                    entry.Retired = true;
                    if (string.IsNullOrWhiteSpace(entry.LastError))
                        entry.LastError = "Retired on upgrade: queued at " + entry.QueuedAtUtc.ToString("u") +
                                          " and never acknowledged by BuildAI.";
                    count++;
                }
                if (count > 0) WriteAtomically(entries);
                return count;
            }
        }

        /// <summary>Retired entries, for a support report or a manual re-queue.</summary>
        public IReadOnlyList<SavedIssueDto> GetRetired(string revitModelUid, string source)
        {
            lock (Gate)
            {
                return Load()
                    .Where(x => x.Retired && Same(x.RevitModelUid, revitModelUid) && Same(x.Source, source))
                    .Select(x => x.Issue)
                    .Where(x => x != null)
                    .ToList();
            }
        }

        private List<PendingIssueEntry> Load()
        {
            if (!File.Exists(_path)) return new List<PendingIssueEntry>();
            try
            {
                return JsonConvert.DeserializeObject<List<PendingIssueEntry>>(File.ReadAllText(_path)) ??
                       new List<PendingIssueEntry>();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "The pending BuildAI Issue synchronization journal is damaged. " +
                    "It was not ignored because doing so could create duplicate Autodesk Issues. File: " + _path, ex);
            }
        }

        private void WriteAtomically(List<PendingIssueEntry> entries)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = _path + ".bak";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonConvert.SerializeObject(entries.OrderBy(x => x.QueuedAtUtc).ToList(), Formatting.Indented));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(_path))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(temp, _path, backup, true);
                }
                else File.Move(temp, _path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static string IssueKey(SavedIssueDto issue)
            => !string.IsNullOrWhiteSpace(issue?.IssueId) ? "id:" + issue.IssueId : "result:" + (issue?.ResultKey ?? "");

        private static bool Same(string left, string right)
            => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

        private static string Normalize(string value) => (value ?? "").Trim().ToLowerInvariant();

        private sealed class PendingIssueEntry
        {
            public PendingIssueEntry() { }
            public string RevitModelUid { get; set; } = "";
            public string Source { get; set; } = "";
            public SavedIssueDto Issue { get; set; }
            public string Fingerprint { get; set; } = "";
            public DateTime QueuedAtUtc { get; set; }

            /// <summary>Number of drain attempts that ended in a server rejection.</summary>
            public int FailedAttempts { get; set; }

            /// <summary>Last rejection text, kept so a retired entry can explain itself.</summary>
            public string LastError { get; set; } = "";

            /// <summary>
            /// Set once the entry has exhausted its attempts. Retired entries are kept
            /// on disk for support but are never returned by GetPending again.
            /// </summary>
            public bool Retired { get; set; }
        }
    }
}
