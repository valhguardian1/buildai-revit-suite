using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Export
{
    public static class ReportExporter
    {
        public static void ExportCsv(string path, IEnumerable<ComparisonIssue> issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Kritichnost;Tip proverki;Uroven;Kategoriya;Zagolovok;Opisanie;Otklonenie, mm;Rekomendatsiya;AR ID;ST ID;AI opisanie");
            foreach (var x in issues ?? Enumerable.Empty<ComparisonIssue>())
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    Esc(x.SeverityRu), Esc(x.CheckTypeRu), Esc(x.Level), Esc(x.Category), Esc(x.Title), Esc(x.Description),
                    x.DeltaMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Esc(x.Recommendation),
                    x.ArchitecturalElementId.ToString(), x.StructuralElementId.ToString(), Esc(x.AiExplanation)
                }));
            }
            File.WriteAllText(path, "\uFEFF" + sb, Encoding.UTF8);
        }
        private static string Esc(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
