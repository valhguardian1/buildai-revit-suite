using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public static class CsvExporter
    {
        public static void Export(string path, IEnumerable<LinkChangeItem> items)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("DetectedAtUtc;Link;Level;Category;ElementId;ElementName;ChangeType");
                foreach (var x in items ?? Enumerable.Empty<LinkChangeItem>())
                {
                    writer.WriteLine(string.Join(";", new[]
                    {
                        Q(x.DetectedAtUtc.ToString("O")), Q(x.LinkInstanceName), Q(x.Level), Q(x.Category),
                        Q(x.ElementId), Q(x.ElementName), Q(x.ChangeType.ToString())
                    }));
                }
            }
        }

        private static string Q(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
