using System;
using System.Reflection;
using Autodesk.Revit.UI;
using BuildAI.Core;
using BuildAI.Core.Buffering;
using BuildAI.Core.Configuration;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using BuildAI.Core.Transport;
using Plugin1.TimeModelAnalytics.Tracking;

namespace Plugin1.TimeModelAnalytics.Revit
{
    /// <summary>
    /// Entry point for Plugin 1 (Time &amp; Model Analytics). Builds the ribbon,
    /// wires the Core client, and starts the tracking session.
    /// </summary>
    public sealed class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            LanguageSettings.LoadAndApply();
            try
            {
                var options = BuildAiOptions.Load();
                PluginLog.Configure("Plugin1.TimeModelAnalytics", Assembly.GetExecutingAssembly().GetName().Version?.ToString(), app.ControlledApplication.VersionNumber, options: options);

                var credentials = new WindowsCredentialStore();
                var transport   = new RestBuildAiTransport(credentials);
                PluginContext.Client = new BuildAiClient(options, transport, new OfflineQueue());

                BuildRibbon(app);

                PluginContext.Session = new SessionManager(app, PluginContext.Client, options);

                if (string.IsNullOrEmpty(credentials.GetApiToken()))
                    PluginLog.Info(Loc.T("Token_Missing"));

                PluginLog.Info("Plugin1.TimeModelAnalytics started.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("OnStartup failed", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            try
            {
                PluginContext.Session?.Dispose();
                PluginContext.Client?.Dispose();
            }
            catch (Exception ex) { PluginLog.Error("OnShutdown failed", ex); }
            return Result.Succeeded;
        }

        private static void BuildRibbon(UIControlledApplication app)
        {
            // Session management is intentionally headless in Iteration 3.
            // Session data is created, updated and finished automatically and is viewed in BuildAI.
        }
    }
}
