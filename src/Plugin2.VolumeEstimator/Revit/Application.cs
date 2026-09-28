using System;
using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BuildAI.Core;
using BuildAI.Core.Buffering;
using BuildAI.Core.Configuration;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Security;
using BuildAI.Core.Transport;

namespace Plugin2.VolumeEstimator.Revit
{
    /// <summary>
    /// Entry point for Plugin 2 (Volume Estimator). Adds a "Volumes" panel to the
    /// shared BuildAI ribbon tab and wires the Core client. Uses its own offline
    /// buffer file so it never collides with Plugin 1's queue.
    /// </summary>
    public sealed class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            LanguageSettings.LoadAndApply();
            try
            {
                var options = BuildAiOptions.Load();
                PluginLog.Configure("Plugin2.VolumeEstimator", Assembly.GetExecutingAssembly().GetName().Version?.ToString(), app.ControlledApplication.VersionNumber, options: options);

                var credentials = new WindowsCredentialStore();
                var transport   = new RestBuildAiTransport(credentials);
                // Distinct buffer file: Plugin 1 uses "outbox.jsonl".
                var queue = new OfflineQueue("outbox.materials.jsonl");

                PluginContext.Options = options;
                PluginContext.Client  = new BuildAiClient(options, transport, queue);

                PluginContext.RecalcHandler = new RecalcExternalEventHandler();
                PluginContext.RecalcEvent   = ExternalEvent.Create(PluginContext.RecalcHandler);

                BuildRibbon(app);

                if (options.VolumeAutoCalcOnSync)
                    app.ControlledApplication.DocumentSynchronizedWithCentral += OnSynced;

                PluginLog.Info("Plugin2.VolumeEstimator started.");
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
                try { app.ControlledApplication.DocumentSynchronizedWithCentral -= OnSynced; } catch { }
                PluginContext.RecalcEvent?.Dispose();
                PluginContext.Client?.Dispose();
            }
            catch (Exception ex) { PluginLog.Error("OnShutdown failed", ex); }
            return Result.Succeeded;
        }

        private static void OnSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            if (AccViewPreparation.Synchronizing) return;
            // Event context is read-only; hop to a valid API context to estimate.
            try { PluginContext.RecalcEvent?.Raise(); }
            catch (Exception ex) { PluginLog.Error("OnSynced raise failed", ex); }
        }

        private static void BuildRibbon(UIControlledApplication app)
        {
            const string tab = "BuildAI";
            try { app.CreateRibbonTab(tab); } catch { /* another BuildAI plugin already created it */ }

            var panel = app.CreateRibbonPanel(tab, "Material Volumes");
            var asm = Assembly.GetExecutingAssembly().Location;

            RibbonIconLoader.AddButton(panel, new PushButtonData(
                "BuildAI_Vol_Calc", "Recalculate", asm,
                "Plugin2.VolumeEstimator.Revit.CalculateCommand"), "recalculate");

            RibbonIconLoader.AddButton(panel, new PushButtonData(
                "BuildAI_Vol_Open", "Open in BuildAI", asm,
                "Plugin2.VolumeEstimator.Revit.OpenInBuildAiCommand"), "open");

        }
    }
}
