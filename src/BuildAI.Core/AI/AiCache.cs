using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BuildAI.Core.AI
{
    public sealed class AiCache
    {
        private readonly string _directory;
        public AiCache(string namespaceName)
        {
            _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuildAI", "ai-cache", Sanitize(namespaceName));
        }
        public static string ComputeHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                var sb = new StringBuilder(bytes.Length * 2); foreach (var b in bytes) sb.Append(b.ToString("x2")); return sb.ToString();
            }
        }
        public bool TryGet(string hash, out AiAnalysisResult result)
        {
            result = null;
            try { var path = Path.Combine(_directory, hash + ".json"); if (!File.Exists(path)) return false; result = JsonConvert.DeserializeObject<AiAnalysisResult>(File.ReadAllText(path)); if (result != null) result.FromCache = true; return result != null; } catch { return false; }
        }
        public void Put(string hash, AiAnalysisResult result)
        {
            try { Directory.CreateDirectory(_directory); result.InputHash = hash; File.WriteAllText(Path.Combine(_directory, hash + ".json"), JsonConvert.SerializeObject(result, Formatting.Indented)); } catch { }
        }
        private static string Sanitize(string value) { foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return value; }
    }
}
