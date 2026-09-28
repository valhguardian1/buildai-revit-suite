using System.Collections.Generic;

namespace Plugin3.LinkChangeMonitor.Models
{
    public sealed class LinkElementSnapshot
    {
        public string UniqueId { get; set; }
        public string ElementId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public double LevelElevation { get; set; }
        public string TypeName { get; set; }
        public string Fingerprint { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }
}
