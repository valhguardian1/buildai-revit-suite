using Autodesk.Revit.UI;
using BuildAI.Core;
using BuildAI.Core.Configuration;

namespace Plugin2.VolumeEstimator.Revit
{
    /// <summary>
    /// Process-wide handles created once in OnStartup. Kept tiny on purpose — no
    /// Revit document/element objects are cached here.
    /// </summary>
    public static class PluginContext
    {
        public static BuildAiClient Client { get; set; }
        public static BuildAiOptions Options { get; set; }

        /// <summary>ExternalEvent used to run estimation from a valid API context
        /// (needed when triggered from the Sync-with-Central event, specification §5.2.2).</summary>
        public static ExternalEvent RecalcEvent { get; set; }
        public static RecalcExternalEventHandler RecalcHandler { get; set; }
    }
}
