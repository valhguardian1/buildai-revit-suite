using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Comparison
{
    internal sealed class ModelSource
    {
        public Document Document { get; set; }
        public Transform Transform { get; set; } = Transform.Identity;
        public RevitLinkInstance LinkInstance { get; set; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsHost => LinkInstance == null;
    }

    internal static class LinkResolver
    {
        public static List<RevitLinkInstance> GetLoadedLinks(Document doc) => new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(x => x.GetLinkDocument() != null).ToList();

        public static ModelSource ResolveArchitectural(Document doc, ComparatorSettings settings)
            => Resolve(doc, settings.ArchitecturalLinkUniqueId, new[] { "_ar", " ar", "arch", "architecture", "architectural", "ap" });

        public static ModelSource ResolveStructural(Document doc, ComparatorSettings settings)
            => Resolve(doc, settings.StructuralLinkUniqueId, new[] { "_kr", " kr", "kr", "struct", "structure", "structural", "struct" });

        public static ModelSource Resolve(Document host, string sourceId, string[] hints)
        {
            if (string.Equals(sourceId, ComparatorSettings.HostSourceId, StringComparison.OrdinalIgnoreCase))
                return new ModelSource { Document = host, Transform = Transform.Identity, Id = ComparatorSettings.HostSourceId, Name = host.Title };

            var links = GetLoadedLinks(host);
            var exact = links.FirstOrDefault(x => string.Equals(x.UniqueId, sourceId, StringComparison.OrdinalIgnoreCase));
            // A saved selection must never silently switch to another similarly
            // named link. Name hints are only a first-run fallback before a source
            // has been selected and persisted in Settings.
            var found = exact;
            if (found == null && string.IsNullOrWhiteSpace(sourceId))
                found = links.FirstOrDefault(x => hints.Any(h => (x.Name ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0));
            if (found == null) return null;
            return new ModelSource
            {
                Document = found.GetLinkDocument(),
                Transform = found.GetTotalTransform() ?? Transform.Identity,
                LinkInstance = found,
                Id = found.UniqueId,
                Name = found.Name
            };
        }
    }
}
