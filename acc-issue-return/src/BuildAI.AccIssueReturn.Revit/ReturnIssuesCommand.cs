using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace BuildAI.AccIssueReturn.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ReturnIssuesCommand : IExternalCommand
{
    private static ImportWindow? activeWindow;
    internal static void CloseActiveWindow() { if(activeWindow?.IsVisible==true) activeWindow.Close(); activeWindow=null; }
    internal static void CloseForDocument(Document document){if(activeWindow?.IsVisible==true&&activeWindow.HostDocument.Equals(document))CloseActiveWindow();}
    internal static void NotifyActiveDocument(Document? document){if(activeWindow?.IsVisible==true&&document!=null&&!activeWindow.HostDocument.Equals(document))activeWindow.NotifyDocumentChanged();}
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        try
        {
            var assembly=typeof(ReturnIssuesCommand).Assembly;var path=assembly.Location;
            AccIssueReturnLog.Event("ACC_PLUGIN_BUILD_INFO",new{assemblyVersion=assembly.GetName().Version?.ToString(),
                fileVersion=FileVersionInfo.GetVersionInfo(path).FileVersion,
                buildTimestamp=File.GetLastWriteTimeUtc(path).ToString("O"),revitVersion=data.Application.Application.VersionNumber,
                targetFramework=Attribute.GetCustomAttribute(assembly,typeof(TargetFrameworkAttribute)) is TargetFrameworkAttribute framework?framework.FrameworkName:"unknown",
                assemblyPath=path});
            var uidoc=data.Application.ActiveUIDocument;
            if(uidoc==null)throw new InvalidOperationException("Open a Revit document before starting ACC Issue Return.");
            if(activeWindow?.IsVisible==true)
            {
                if(activeWindow.HostDocument.Equals(uidoc.Document)){activeWindow.Activate();return Result.Succeeded;}
                activeWindow.Close();
            }
            var integrated=WindowsCredentialTokenProvider.DetectBuildAiInstall();var auth=new WindowsCredentialTokenProvider(integrated);
            var client=new AccIssueClient(auth);var actions=new RevitActionQueue(uidoc.Document);
            var window=new ImportWindow(uidoc,client,auth,actions);
            activeWindow=window;
            window.Closed+=(_,__)=>{if(ReferenceEquals(activeWindow,window))activeWindow=null;client.Dispose();};
            window.Show();return Result.Succeeded;
        }
        catch(Exception ex){message=ex.Message;TaskDialog.Show("BuildAI ACC Issue Return",ex.Message);return Result.Failed;}
    }
}
