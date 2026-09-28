using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BuildAI.Core.Issues
{
    /// <summary>
    /// Local, privacy-preserving journal of APS Issues that were confirmed as
    /// created. Failed attempts are deliberately never recorded.
    /// </summary>
    public sealed class IssueFingerprintStore
    {
        private static readonly object Gate = new object();
        private readonly string _path;

        public IssueFingerprintStore(string path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BuildAI", "issues", "created-fingerprints.json");
        }

        public string Build(
            string source,
            string projectId,
            string containerId,
            string hostVersionId,
            string firstModelVersion,
            string secondModelVersion,
            string rule,
            string firstElementId,
            string secondElementId,
            string direction = null)
        {
            var canonical = string.Join("\n", new[]
            {
                source, projectId, containerId, hostVersionId,
                firstModelVersion, secondModelVersion, rule,
                firstElementId, secondElementId, direction
            }.Select(Normalize));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", "").ToLowerInvariant();
        }

        public bool Contains(string fingerprint)
        {
            lock (Gate) return Load().Contains(fingerprint ?? "");
        }

        public void MarkCreated(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return;
            lock (Gate)
            {
                var values = Load();
                if (!values.Add(fingerprint)) return;
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                WriteAtomically(JsonConvert.SerializeObject(values.OrderBy(x => x).ToArray(), Formatting.Indented));
            }
        }

        private HashSet<string> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new HashSet<string>(StringComparer.Ordinal);
                var values = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(_path)) ?? Array.Empty<string>();
                return new HashSet<string>(values.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "The local Autodesk Issue fingerprint journal is damaged. " +
                    "It was not ignored because doing so could create duplicate Issues. File: " + _path, ex);
            }
        }

        private void WriteAtomically(string json)
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
                    writer.Write(json);
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

        private static string Normalize(string value)
            => (value ?? "").Trim().ToLowerInvariant();
    }
}
