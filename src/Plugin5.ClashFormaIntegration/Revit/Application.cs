using System;
using System.Reflection;
using System.Linq;
using Autodesk.Revit.UI;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Presentation;
using BuildAI.Core.Security;
using Plugin5.ClashFormaIntegration.AI;
using Plugin5.ClashFormaIntegration.Clash;
using Plugin5.ClashFormaIntegration.Models;
using BuildAI.Core.Issues;
using Plugin5.ClashFormaIntegration.Issues;

namespace Plugin5.ClashFormaIntegration.Revit
{
    public sealed class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            LanguageSettings.LoadAndApply();
            try
            {
                PluginLog.Configure("Plugin5.ClashFormaIntegration", Assembly.GetExecutingAssembly().GetName().Version?.ToString(), app.ControlledApplication.VersionNumber);
                PluginContext.Settings = Plugin5Settings.Load();
                PluginContext.CredentialStore = new WindowsCredentialStore();
                PluginContext.Engine = new ClashEngine();
                PluginContext.Ai = new ClashAiConnector();
                PluginContext.Issues = new ClashIssueWorkflow(new IssueIntegrationClient(PluginContext.Settings.BuildAiBaseUrl, PluginContext.CredentialStore));
                PluginContext.Handler = new ModelActionHandler();
                PluginContext.Event = ExternalEvent.Create(PluginContext.Handler);
                const string tab = "BuildAI"; try { app.CreateRibbonTab(tab); } catch { }
                var panel = app.GetRibbonPanels(tab).FirstOrDefault(x=>x.Name=="MEP Comparation")??app.CreateRibbonPanel(tab,"MEP Comparation");
                var asm = Assembly.GetExecutingAssembly().Location;
                RibbonIconLoader.AddButton(panel, new PushButtonData("P5_Check", "Find clashes", asm, "Plugin5.ClashFormaIntegration.Revit.RunClashCommand"), "clash");
                RibbonIconLoader.AddButton(panel, new PushButtonData("P5_Results", "Results", asm, "Plugin5.ClashFormaIntegration.Revit.OpenResultsCommand"), "results");
                RibbonIconLoader.AddButton(panel, new PushButtonData("P5_Create", "Create views", asm, "Plugin5.ClashFormaIntegration.Revit.CreateViewsCommand"), "createview");
                var settingsPanel=app.GetRibbonPanels(tab).FirstOrDefault(x=>x.Name=="Settings")??app.CreateRibbonPanel(tab,"Settings");
                if(SettingsRibbonPolicy.ShouldCreate(settingsPanel.GetItems().Select(x=>x.Name))){RibbonIconLoader.AddButton(settingsPanel,new PushButtonData("BuildAI_Settings","Settings",asm,"Plugin5.ClashFormaIntegration.Revit.SettingsCommand"),"settings");PluginLog.Info("ACC_SETTINGS_BUTTON_CREATED host=Clash");}
                else PluginLog.Info("ACC_SETTINGS_BUTTON_REUSED host=Clash");
                return Result.Succeeded;
            }
            catch (Exception ex) { PluginLog.Error("Plugin5 startup", ex); return Result.Failed; }
        }
        public Result OnShutdown(UIControlledApplication app)
        {
            try { PluginContext.Window?.Close(); PluginContext.Event?.Dispose(); } catch { }
            return Result.Succeeded;
        }
    }
}
