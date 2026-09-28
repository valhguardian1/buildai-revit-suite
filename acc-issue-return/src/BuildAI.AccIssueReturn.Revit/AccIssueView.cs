using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Linq;

namespace BuildAI.AccIssueReturn.Revit;

internal static class AccIssueView
{
    public const string Name="BuildAI ACC Issues";
    public static View3D GetOrCreate(Document doc){var v=new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x=>!x.IsTemplate&&x.Name==Name);if(v!=null)return v;var t=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x=>x.ViewFamily==ViewFamily.ThreeDimensional);if(t==null)throw new InvalidOperationException("No 3D view family is available.");using var tx=new Transaction(doc,"BuildAI ACC Issues: create view");tx.Start();v=View3D.CreateIsometric(doc,t.Id);v.Name=Name;tx.Commit();return v;}
    public static void Apply(UIDocument uidoc,View3D view,AccCamera camera,BoundingBoxXYZ? bounds){if(camera==null||camera.Eye.Length!=3||camera.Target.Length!=3||camera.Up.Length!=3)throw new InvalidOperationException("ACC camera context is incomplete; no substitute camera was applied.");var eye=new XYZ(camera.Eye[0],camera.Eye[1],camera.Eye[2]);var target=new XYZ(camera.Target[0],camera.Target[1],camera.Target[2]);var up=new XYZ(camera.Up[0],camera.Up[1],camera.Up[2]);var direction=(target-eye).Normalize();if(direction.IsZeroLength())throw new InvalidOperationException("ACC camera eye equals target.");uidoc.ActiveView=view;using var tx=new Transaction(uidoc.Document,"BuildAI ACC Issues: apply viewpoint");tx.Start();try{if(view.IsLocked)view.Unlock();view.SetOrientation(new ViewOrientation3D(eye,up,direction));if(bounds!=null){view.IsSectionBoxActive=true;view.SetSectionBox(bounds);}}catch{tx.RollBack();throw;}tx.Commit();}
    public static BoundingBoxXYZ Expand(BoundingBoxXYZ box,double metres=1){if(box==null)return null!;var p=metres/0.3048;return new BoundingBoxXYZ{Transform=Transform.Identity,Min=new XYZ(box.Min.X-p,box.Min.Y-p,box.Min.Z-p),Max=new XYZ(box.Max.X+p,box.Max.Y+p,box.Max.Z+p)};}
}
