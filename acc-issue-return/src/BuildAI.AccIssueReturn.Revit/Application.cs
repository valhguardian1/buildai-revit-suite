using Autodesk.Revit.UI;
using System;
using System.Reflection;
using System.Linq;

namespace BuildAI.AccIssueReturn.Revit;

public sealed class Application : IExternalApplication
{
    private EventHandler<Autodesk.Revit.DB.Events.DocumentClosingEventArgs>? closingHandler;
    private EventHandler<Autodesk.Revit.UI.Events.ViewActivatedEventArgs>? viewHandler;
    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            const string tab="BuildAI";try{app.CreateRibbonTab(tab);}catch(ArgumentException){app.GetRibbonPanels(tab);}
            var panel=System.Linq.Enumerable.FirstOrDefault(app.GetRibbonPanels(tab),x=>string.Equals(x.Name,"ACC Issues",StringComparison.Ordinal))??app.CreateRibbonPanel(tab,"ACC Issues");var asm=Assembly.GetExecutingAssembly().Location;
            var data=new PushButtonData("BuildAI_AccIssueReturn_Open","Return ACC Issues",asm,typeof(ReturnIssuesCommand).FullName);RibbonIconLoader.AddButton(panel,data,"syncback");
            var settingsPanel=app.GetRibbonPanels(tab).FirstOrDefault(x=>x.Name=="Settings")??app.CreateRibbonPanel(tab,"Settings");
            if(!settingsPanel.GetItems().Any(x=>x.Name=="BuildAI_Settings"))
            {
                RibbonIconLoader.AddButton(settingsPanel,new PushButtonData("BuildAI_Settings","Settings",asm,typeof(SettingsCommand).FullName),"settings");
                BuildAI.AccIssueReturn.Core.AccIssueReturnLog.Event("ACC_SETTINGS_BUTTON_CREATED",new{host="ReturnPlugin"});
            }
            else BuildAI.AccIssueReturn.Core.AccIssueReturnLog.Event("ACC_SETTINGS_BUTTON_REUSED",new{host="ReturnPlugin"});
            closingHandler=(_,e)=>ReturnIssuesCommand.CloseForDocument(e.Document);
            viewHandler=(_,e)=>ReturnIssuesCommand.NotifyActiveDocument(e.CurrentActiveView?.Document);
            app.ControlledApplication.DocumentClosing+=closingHandler;
            app.ViewActivated+=viewHandler;
            return Result.Succeeded;
        }
        catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex);return Result.Failed;}
    }
    public Result OnShutdown(UIControlledApplication app){if(closingHandler!=null)app.ControlledApplication.DocumentClosing-=closingHandler;if(viewHandler!=null)app.ViewActivated-=viewHandler;ReturnIssuesCommand.CloseActiveWindow();return Result.Succeeded;}
}
