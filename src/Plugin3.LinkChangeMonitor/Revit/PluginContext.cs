using Autodesk.Revit.UI;
using Plugin3.LinkChangeMonitor.Models;
using Plugin3.LinkChangeMonitor.Monitoring;
using Plugin3.LinkChangeMonitor.UI;
using BuildAI.Core;
using BuildAI.Core.Configuration;

namespace Plugin3.LinkChangeMonitor.Revit
{
    public static class PluginContext
    {
        public static SnapshotStore Store { get; set; }
        public static LinkComparer Comparer { get; set; }
        public static LinkActionHandler ActionHandler { get; set; }
        public static ExternalEvent ActionEvent { get; set; }
        public static UIApplication UiApplication { get; set; }
        public static MonitorWindow Window { get; set; }
        public static bool AutoComparisonPending { get; set; }
        public static bool ComparisonRunning { get; set; }
        public static ComparisonResult LastResult { get; set; }
        public static BuildAiClient Client { get; set; }
        public static BuildAiOptions Options { get; set; }
        public static LinkCheckUploader Uploader { get; set; }
        public static LinkFingerprintService Fingerprints { get; set; }
        public static System.DateTime PendingSinceUtc { get; set; }
    }
}
