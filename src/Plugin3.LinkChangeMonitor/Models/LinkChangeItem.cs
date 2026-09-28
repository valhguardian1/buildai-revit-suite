using System;
using System.Collections.Generic;
using System.Linq;

namespace Plugin3.LinkChangeMonitor.Models
{
    public enum LinkChangeType
    {
        Added,
        Modified,
        Deleted
    }

    public sealed class LinkChangeItem
    {
        public string ChangeId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime DetectedAtUtc { get; set; }
        public string HostProjectId { get; set; }
        public string LinkInstanceUniqueId { get; set; }
        public string LinkInstanceName { get; set; }
        public string LinkDocumentTitle { get; set; }
        public string ElementUniqueId { get; set; }
        public string ElementId { get; set; }
        public string ElementName { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public double LevelElevation { get; set; }
        public string TypeName { get; set; }
        public LinkChangeType ChangeType { get; set; }
        public Dictionary<string, string> Before { get; set; }
        public Dictionary<string, string> After { get; set; }


        public int CategorySortOrder
        {
            get
            {
                var c=(Category??"").ToLowerInvariant();
                if(c.Contains("floor")||c.Contains("רצפ")) return 0;
                if(c.Contains("column")||c.Contains("עמוד")) return 1;
                if(c.Contains("beam")||c.Contains("קורה")||c.Contains("קורות")) return 2;
                if(c.Contains("wall")||c.Contains("קיר")) return 3;
                return 100;
            }
        }
        public int ChangeTypeSortOrder => ChangeType == LinkChangeType.Modified ? 0 : ChangeType == LinkChangeType.Added ? 1 : 2;
        public string NaturalSortName => string.IsNullOrWhiteSpace(TypeName) ? (ElementName ?? "") : TypeName;
        public string ChangeTypeText => ChangeType == LinkChangeType.Added ? "Added" :
                                        ChangeType == LinkChangeType.Modified ? "Modified" : "Deleted";
        public bool CanHighlight => ChangeType != LinkChangeType.Deleted;

        public string PositionCode => string.IsNullOrWhiteSpace(ElementId) ? ElementUniqueId : ElementId;

        public string ChangedParameters
        {
            get
            {
                if (ChangeType == LinkChangeType.Added) return "New element";
                if (ChangeType == LinkChangeType.Deleted) return "Element deleted";
                return string.Join(", ", GetChangedKeys());
            }
        }

        public string BeforeSummary => BuildSummary(Before, GetChangedKeys());
        public string AfterSummary => BuildSummary(After, GetChangedKeys());

        private IEnumerable<string> GetChangedKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Before != null) foreach (var key in Before.Keys) keys.Add(key);
            if (After != null) foreach (var key in After.Keys) keys.Add(key);
            return keys.Where(key => !Same(Value(Before, key), Value(After, key)))
                       .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        private static string BuildSummary(Dictionary<string, string> values, IEnumerable<string> keys)
        {
            if (values == null) return "—";
            var lines = new List<string>();
            foreach (var key in keys ?? Enumerable.Empty<string>())
            {
                if (values.TryGetValue(key, out var value))
                    lines.Add(key + ": " + (string.IsNullOrWhiteSpace(value) ? "—" : value));
            }
            return lines.Count == 0 ? "—" : string.Join("; ", lines);
        }

        private static string Value(Dictionary<string, string> values, string key)
        {
            if (values == null || key == null) return null;
            return values.TryGetValue(key, out var value) ? value : null;
        }

        private static bool Same(string left, string right) =>
            string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    }
}
