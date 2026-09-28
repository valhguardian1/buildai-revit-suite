using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public sealed class LinkComparer
    {
        private readonly SnapshotStore _store;
        public LinkComparer(SnapshotStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }

        public ComparisonResult CompareAndSave(Document host)
        {
            var result = new ComparisonResult();
            if (host == null) return result;

            var links = new FilteredElementCollector(host)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                var current = LinkSnapshotBuilder.Build(host, link);
                if (current == null)
                {
                    result.Warnings.Add("Link is unloaded or unavailable: " + SafeName(link));
                    continue;
                }

                result.LinksScanned++;
                var previous = _store.Load(current.HostProjectId, current.LinkInstanceUniqueId);
                if (previous == null)
                {
                    _store.Save(current);
                    result.BaselinesCreated++;
                    continue;
                }

                Compare(previous, current, result.Changes);
                _store.Save(current);
            }

            _store.AppendJournal(result);
            return result;
        }

        private static void Compare(LinkSnapshot before, LinkSnapshot after, IList<LinkChangeItem> changes)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in after.Elements)
            {
                if (!before.Elements.TryGetValue(kv.Key, out var oldItem))
                {
                    changes.Add(Create(after, kv.Value, LinkChangeType.Added, null, kv.Value.Parameters, now));
                    continue;
                }
                if (!string.Equals(oldItem.Fingerprint, kv.Value.Fingerprint, StringComparison.Ordinal))
                    changes.Add(Create(after, kv.Value, LinkChangeType.Modified, oldItem.Parameters, kv.Value.Parameters, now));
            }

            foreach (var kv in before.Elements)
            {
                if (after.Elements.ContainsKey(kv.Key)) continue;
                changes.Add(Create(after, kv.Value, LinkChangeType.Deleted, kv.Value.Parameters, null, now));
            }
        }

        private static LinkChangeItem Create(LinkSnapshot link, LinkElementSnapshot element,
            LinkChangeType type, Dictionary<string, string> before, Dictionary<string, string> after, DateTime now)
        {
            return new LinkChangeItem
            {
                DetectedAtUtc = now,
                HostProjectId = link.HostProjectId,
                LinkInstanceUniqueId = link.LinkInstanceUniqueId,
                LinkInstanceName = link.LinkInstanceName,
                LinkDocumentTitle = link.LinkDocumentTitle,
                ElementUniqueId = element.UniqueId,
                ElementId = element.ElementId,
                ElementName = element.Name,
                Category = element.Category,
                Level = string.IsNullOrWhiteSpace(element.Level) ? (link.LinkedLevelRange ?? "") : element.Level,
                LevelElevation = element.LevelElevation,
                TypeName = element.TypeName,
                ChangeType = type,
                Before = before,
                After = after
            };
        }

        private static string SafeName(Element e) { try { return e?.Name ?? ""; } catch { return ""; } }
    }
}
