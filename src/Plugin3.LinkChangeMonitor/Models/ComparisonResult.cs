using System;
using System.Collections.Generic;

namespace Plugin3.LinkChangeMonitor.Models
{
    public sealed class ComparisonResult
    {
        public DateTime ComparedAtUtc { get; set; } = DateTime.UtcNow;
        public int LinksScanned { get; set; }
        public int BaselinesCreated { get; set; }
        public List<LinkChangeItem> Changes { get; set; } = new List<LinkChangeItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
