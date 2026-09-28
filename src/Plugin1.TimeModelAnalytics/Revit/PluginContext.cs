using BuildAI.Core;
using Plugin1.TimeModelAnalytics.Tracking;

namespace Plugin1.TimeModelAnalytics.Revit
{
    /// <summary>
    /// Process-wide handles created once in OnStartup and used by the ribbon
    /// commands. Kept tiny on purpose — no Revit API objects are cached here.
    /// </summary>
    public static class PluginContext
    {
        public static BuildAiClient Client { get; set; }
        public static SessionManager Session { get; set; }
    }
}
