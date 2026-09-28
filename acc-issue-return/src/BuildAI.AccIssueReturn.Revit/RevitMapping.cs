using Autodesk.Revit.DB;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Collections.Generic;
using System.Linq;
namespace BuildAI.AccIssueReturn.Revit;
internal sealed class LinkMatch { public RevitLinkInstance Link {get;set;}=null!; public Document Document=>Link.GetLinkDocument()!; public string Reason {get;set;}=""; }
internal static class RevitMapping
{
 public static IReadOnlyList<LinkMatch> FindCandidates(Document host,AccModel model,AccIssue issue)
 {
  var links=new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(x=>x.GetLinkDocument()!=null).ToList();
  var heads=issue.ObjectSet.Select(x=>x.Composite?.LinkDocumentUniqueId).Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();
  var exact=links.Where(x=>heads.Any(h=>string.Equals(h,x.UniqueId,StringComparison.OrdinalIgnoreCase))).ToList();
  if(exact.Count>0)return exact.Select(x=>new LinkMatch{Link=x,Reason="composite link head"}).ToList();
  var tokens=new[]{model.Name,model.ItemUrn,model.VersionUrn,issue.ModelUrn,issue.VersionUrn}.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(Normalize).ToList();var scored=links.Select(x=>new LinkMatch{Link=x,Reason=x.GetLinkDocument()?.Title??""}).Select(x=>new{Match=x,Score=tokens.Any(t=>Normalize(x.Reason).IndexOf(t,StringComparison.OrdinalIgnoreCase)>=0)?1:0}).OrderByDescending(x=>x.Score).ToList();var best=scored.Where(x=>x.Score>0).Select(x=>x.Match).ToList();if(best.Count==0&&links.Count==1)return new[]{new LinkMatch{Link=links[0],Reason="single-loaded-link"}};return best;
 }
 public static LinkMatch? FindByUniqueId(Document host,string uniqueId){if(string.IsNullOrWhiteSpace(uniqueId))return null;var link=new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().FirstOrDefault(x=>string.Equals(x.UniqueId,uniqueId,StringComparison.OrdinalIgnoreCase)&&x.GetLinkDocument()!=null);return link==null?null:new LinkMatch{Link=link,Reason=link.GetLinkDocument()?.Title??""};}
 public static Element? ResolveBuildAiElement(LinkMatch? match,BuildAiRevitElementReference? reference,out string method,IReadOnlyDictionary<string,ElementId>? uniqueIdIndex=null){
  method="None";
  if(match==null||reference==null||!string.Equals(match.Link.UniqueId,reference.LinkInstanceUid,StringComparison.OrdinalIgnoreCase))return null;
  var document=match.Link.GetLinkDocument();if(document==null)return null;
  if(!string.IsNullOrWhiteSpace(reference.ElementUniqueId)){
   if(uniqueIdIndex!=null&&uniqueIdIndex.TryGetValue(reference.ElementUniqueId,out var indexedId)){
    var indexed=document.GetElement(indexedId);
    if(indexed!=null&&string.Equals(indexed.UniqueId,reference.ElementUniqueId,StringComparison.OrdinalIgnoreCase)){method="UniqueIdIndex";return indexed;}
   }
   var exact=document.GetElement(reference.ElementUniqueId);
   if(exact!=null){method="UniqueId";return exact;}
  }
  if(reference.ElementId.HasValue){
#if NET8_0_OR_GREATER
   var fallback=document.GetElement(new ElementId((long)reference.ElementId.Value));
#else
   var fallback=document.GetElement(new ElementId(reference.ElementId.Value));
#endif
   if(fallback!=null){method="ElementIdFallback";return fallback;}
  }
  return null;
 }
 public static void AttachTransform(IssueCoordinateContext context,LinkMatch match){context.LinkToHost=ToMatrix(match.Link.GetTotalTransform());context.TransformSource="RevitLinkInstance.GetTotalTransform (LinkToHost)";}
 public static Element? ResolveElement(LinkMatch match,AccObjectRef reference){if(match?.Document==null||reference==null)return null;try{var uid=reference.Composite?.ElementUniqueId;if(reference.Composite?.IsComposite==true){if(!string.Equals(match.Link.UniqueId,reference.Composite.LinkDocumentUniqueId,StringComparison.OrdinalIgnoreCase))return null;var e=match.Document.GetElement(uid);if(e!=null){reference.MatchedRevitUniqueId=e.UniqueId;reference.MatchedDocument=match.Document.Title;}return e;}uid=string.IsNullOrWhiteSpace(reference.RevitUniqueId)?reference.ExternalId:reference.RevitUniqueId;if(!string.IsNullOrWhiteSpace(uid)){var e=match.Document.GetElement(uid);if(e!=null){reference.MatchedRevitUniqueId=e.UniqueId;reference.MatchedDocument=match.Document.Title;return e;}}if(!string.IsNullOrWhiteSpace(reference.ExternalId))return new FilteredElementCollector(match.Document).WhereElementIsNotElementType().FirstOrDefault(e=>string.Equals(e?.UniqueId,reference.ExternalId,StringComparison.OrdinalIgnoreCase));}catch{}return null;}
 public static Matrix4 ToMatrix(Transform t){return new Matrix4{M=new[]{t.BasisX.X,t.BasisX.Y,t.BasisX.Z,0d,t.BasisY.X,t.BasisY.Y,t.BasisY.Z,0d,t.BasisZ.X,t.BasisZ.Y,t.BasisZ.Z,0d,t.Origin.X,t.Origin.Y,t.Origin.Z,1d}};}
 public static BoundingBoxXYZ HostBounds(LinkMatch match,IEnumerable<Element> elements,Transform? cachedTransform=null){var points=new List<XYZ>();var tr=cachedTransform??match.Link.GetTotalTransform();foreach(var e in elements){var b=e?.get_BoundingBox(null);if(b==null)continue;for(var x=0;x<2;x++)for(var y=0;y<2;y++)for(var z=0;z<2;z++)points.Add(tr.OfPoint(b.Transform.OfPoint(new XYZ(x==0?b.Min.X:b.Max.X,y==0?b.Min.Y:b.Max.Y,z==0?b.Min.Z:b.Max.Z))));}if(points.Count==0)return null!;return new BoundingBoxXYZ{Transform=Transform.Identity,Min=new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),Max=new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z))};}
 public static BoundingBoxXYZ ModelHostBounds(LinkMatch match)=>HostBounds(match,new FilteredElementCollector(match.Document).WhereElementIsNotElementType().ToElements());
 private static string Normalize(string s)=>new string((s??"").Where(char.IsLetterOrDigit).ToArray());
}
