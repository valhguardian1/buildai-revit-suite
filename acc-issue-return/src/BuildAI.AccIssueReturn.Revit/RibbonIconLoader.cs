using Autodesk.Revit.UI;
using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BuildAI.AccIssueReturn.Revit;

internal static class RibbonIconLoader
{
    public static PushButton AddButton(RibbonPanel panel,PushButtonData data,string iconName)
    {
        var button=panel.AddItem(data) as PushButton??throw new InvalidOperationException("Revit did not create the ACC Issue Return ribbon button.");
        try{button.Image=Load(16,iconName);button.LargeImage=Load(32,iconName);BuildAI.AccIssueReturn.Core.AccIssueReturnLog.Event("ACC_RIBBON_ICON_LOADED",new{sizes="16,32",source="embedded " + iconName});}catch(Exception ex){BuildAI.AccIssueReturn.Core.AccIssueReturnLog.Event("ACC_RIBBON_ICON_WARNING",new{errorType=ex.GetType().Name});}return button;
    }
    public static ImageSource Load(int size,string iconName="BuildAI")
    {
        var assembly=typeof(RibbonIconLoader).Assembly;
        var fileName=iconName=="BuildAI"?"BuildAI.png":iconName+size+".png";
        var resource=assembly.GetManifestResourceNames().FirstOrDefault(name=>name.EndsWith(".Resources."+fileName,StringComparison.OrdinalIgnoreCase));
        if(resource==null)throw new InvalidOperationException("The BuildAI icon resource is missing.");
        using(var stream=assembly.GetManifestResourceStream(resource))
        {
            if(stream==null)throw new InvalidOperationException("The BuildAI icon resource could not be opened.");
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=size;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
        }
    }
}
