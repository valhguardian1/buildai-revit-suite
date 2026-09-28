using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public sealed class SnapshotStore
    {
        public static string RootDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuildAI", "link-monitor");

        private static string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");
        private static string JournalPath => Path.Combine(RootDirectory, "changes.jsonl");

        public LinkSnapshot Load(string hostProjectId, string linkInstanceUniqueId)
        {
            var path = GetSnapshotPath(hostProjectId, linkInstanceUniqueId);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<LinkSnapshot>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        public void Save(LinkSnapshot snapshot)
        {
            Directory.CreateDirectory(SnapshotsDirectory);
            var path = GetSnapshotPath(snapshot.HostProjectId, snapshot.LinkInstanceUniqueId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(snapshot, Formatting.Indented), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public void AppendJournal(ComparisonResult result)
        {
            if (result?.Changes == null || result.Changes.Count == 0) return;
            Directory.CreateDirectory(RootDirectory);
            using (var writer = new StreamWriter(JournalPath, true, Encoding.UTF8))
            {
                foreach (var item in result.Changes)
                    writer.WriteLine(JsonConvert.SerializeObject(item, Formatting.None));
            }
        }

        public string GetSnapshotPath(string hostProjectId, string linkInstanceUniqueId)
        {
            var safeHost = Safe(hostProjectId);
            var safeLink = Safe(linkInstanceUniqueId);
            return Path.Combine(SnapshotsDirectory, safeHost + "__" + safeLink + ".json");
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
