using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BuildAI.AccIssueReturn.Revit;

internal static class IssueMarker
{
    private const string ApplicationId="BuildAI.AccIssueReturn";
    public static ElementId Upsert(Document doc,View3D view,BuildAI.AccIssueReturn.Core.AccIssue issue,XYZ point,BuildAI.AccIssueReturn.Core.CoordinateResult coordinates)
    {
        if(doc==null||view==null||issue==null||point==null)throw new ArgumentNullException();var key=Key(issue);
        using var tx=new Transaction(doc,"BuildAI ACC Issue: update pushpin");tx.Start();
        var marker=new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().FirstOrDefault(x=>x.ApplicationId==ApplicationId&&x.ApplicationDataId==key);
        var created=marker==null;if(marker==null){marker=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));marker.ApplicationId=ApplicationId;marker.ApplicationDataId=key;}
        marker.Name="ACC Issue "+(issue.DisplayId?.ToString()??issue.Id)+" pushpin";marker.SetShape(BuildGeometry(point));
        var graphics=new OverrideGraphicSettings();graphics.SetProjectionLineColor(new Color(220,20,20));graphics.SetProjectionLineWeight(8);try{var fill=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(x=>x.GetFillPattern().IsSolidFill);if(fill!=null){graphics.SetSurfaceForegroundPatternId(fill.Id);graphics.SetSurfaceForegroundPatternColor(new Color(220,20,20));}}catch{}view.SetElementOverrides(marker.Id,graphics);IssueStorage.Write(marker,issue,point,coordinates);tx.Commit();return marker.Id;
    }
    public static string Key(BuildAI.AccIssueReturn.Core.AccIssue i)=>"issue:"+(i.HubId??"")+":"+(i.ProjectId??"")+":"+(i.ContainerId??"")+":"+i.Id;
    private static IList<GeometryObject> BuildGeometry(XYZ tip){const double stemHalfWidth=.045,stemHeight=1.2,headHalfWidth=.32,headHeight=.45;var stem=ExtrudeBox(new XYZ(tip.X-stemHalfWidth,tip.Y-stemHalfWidth,tip.Z),new XYZ(tip.X+stemHalfWidth,tip.Y+stemHalfWidth,tip.Z+stemHeight));var z=tip.Z+stemHeight;var head=ExtrudeBox(new XYZ(tip.X-headHalfWidth,tip.Y-headHalfWidth,z-headHeight*.5),new XYZ(tip.X+headHalfWidth,tip.Y+headHalfWidth,z+headHeight*.5));return new GeometryObject[]{stem,head};}
    private static Solid ExtrudeBox(XYZ min,XYZ max){var loop=new CurveLoop();var a=new XYZ(min.X,min.Y,min.Z);var b=new XYZ(max.X,min.Y,min.Z);var c=new XYZ(max.X,max.Y,min.Z);var d=new XYZ(min.X,max.Y,min.Z);loop.Append(Line.CreateBound(a,b));loop.Append(Line.CreateBound(b,c));loop.Append(Line.CreateBound(c,d));loop.Append(Line.CreateBound(d,a));return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop>{loop},XYZ.BasisZ,max.Z-min.Z);}
}
