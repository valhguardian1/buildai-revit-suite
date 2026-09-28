using System;
using System.Collections.Generic;

namespace Plugin3.LinkChangeMonitor.Models
{
    public sealed class LinkSnapshot
    {
        public int SchemaVersion { get; set; } = 2;
        public string HostProjectId { get; set; }
        public string HostDocumentTitle { get; set; }
        public string LinkInstanceUniqueId { get; set; }
        public string LinkInstanceName { get; set; }
        public string LinkDocumentTitle { get; set; }
        public string LinkPath { get; set; }
        public string LinkedLevelRange { get; set; }
        public DateTime CapturedAtUtc { get; set; }
        public Dictionary<string, LinkElementSnapshot> Elements { get; set; }
            = new Dictionary<string, LinkElementSnapshot>();
    }
}
