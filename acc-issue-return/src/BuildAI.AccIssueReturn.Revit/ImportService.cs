using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BuildAI.AccIssueReturn.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BuildAI.AccIssueReturn.Revit;

internal sealed class ImportService
{
    private readonly UIDocument uidoc; private readonly IAccIssueClient client; private readonly Dictionary<string,BoundingBoxXYZ?> modelBoundsCache=new(StringComparer.OrdinalIgnoreCase); private int boundsCalculated;
    public ImportService(UIDocument document,IAccIssueClient issueClient){uidoc=document;client=issueClient;}
    public bool HasExactBuildAiElement(AccIssue issue){
        if(issue.BuildAiMappingStatus!=BuildAiMappingStatus.Exact)return false;
        var found=false;
        foreach(var reference in new[]{issue.BuildAiRecord?.PrimaryElement,issue.BuildAiRecord?.SecondaryElement}){
            if(reference==null)continue;
            if(string.IsNullOrWhiteSpace(reference.LinkInstanceUid)||string.IsNullOrWhiteSpace(reference.ElementUniqueId))return false;
            var link=RevitMapping.FindByUniqueId(uidoc.Document,reference.LinkInstanceUid);
            if(link?.Link.GetLinkDocument()?.GetElement(reference.ElementUniqueId)==null)return false;
            found=true;
        }
        return found;
    }
    public async Task<PreviewRow> PreviewAsync(AccIssue issue,AccModel model,CancellationToken ct,IssueCoordinateContext? preloadedContext=null)
    {
        var row=new PreviewRow{Issue=issue,ModelVersionSource=model.Source+" "+(model.VersionNumber?.ToString()??"unknown"),ElementMappingSource=issue.BuildAiMappingStatus==BuildAiMappingStatus.Exact?"Exact BuildAI mapping":"ACC objectSet"};
        var buildAiLinks=new List<(string Role,LinkMatch Match,Element Element)>();
        var exactBuildAi=issue.BuildAiMappingStatus==BuildAiMappingStatus.Exact&&issue.BuildAiRecord!=null&&(issue.BuildAiRecord.PrimaryElement!=null||issue.BuildAiRecord.SecondaryElement!=null);
        foreach(var item in new[]{("Primary",issue.BuildAiRecord?.PrimaryElement),("Secondary",issue.BuildAiRecord?.SecondaryElement)}){
            var reference=item.Item2;
            if(reference==null||string.IsNullOrWhiteSpace(reference.LinkInstanceUid))continue;
            var link=RevitMapping.FindByUniqueId(uidoc.Document,reference.LinkInstanceUid);
            var element=RevitMapping.ResolveBuildAiElement(link,reference,out var resolutionMethod);
            var exactElement=resolutionMethod=="UniqueId"?element:null;
            AccIssueReturnLog.Event("BUILDAI_REVIT_LINK_RESOLUTION",new{issueId=ShortIssueId(issue.Id),role=item.Item1,exactInstanceMatch=link!=null,elementUniqueIdMatch=exactElement!=null,elementIdFallbackMatch=element!=null&&exactElement==null,modelUidFallback=false,linksChecked=1,status=link==null?"LinkNotFound":element==null?"ElementNotFound":exactElement!=null?"Exact":"ElementIdFallback"});
            AccIssueReturnLog.Event("ACC_PREVIEW_ELEMENT_EVIDENCE",new{issueIdSuffix=ShortIssueId(issue.Id),role=item.Item1,buildAiMappingFound=issue.BuildAiRecord!=null,requestedLinkInstanceUid=ShortIssueId(reference.LinkInstanceUid),matchedLinkInstanceUid=link==null?"":ShortIssueId(link.Link.UniqueId),requestedElementUniqueId=ShortIssueId(reference.ElementUniqueId),matchedElementUniqueId=exactElement==null?"":ShortIssueId(exactElement.UniqueId),exactLinkMatch=link!=null,exactElementMatch=exactElement!=null,fallbackUsed=element!=null&&exactElement==null});
            if(link==null||exactElement==null)exactBuildAi=false;
            if(link!=null&&element!=null)buildAiLinks.Add((item.Item1,link,element));
        }
        var candidates=buildAiLinks.Count>0?new[]{buildAiLinks[0].Match}:RevitMapping.FindCandidates(uidoc.Document,model,issue);
        if(candidates.Count!=1 && buildAiLinks.Count==0){issue.CoordinateQuality=CoordinateQuality.AmbiguousModel;issue.ImportState=ImportState.Blocked;issue.Warning=candidates.Count==0?"No Revit link matched the ACC model.":"Multiple Revit links matched; select one explicitly before import.";row.BlockingReasons.Add(candidates.Count==0?"Revit link could not be resolved.":"Multiple Revit links matched.");row.Coordinates=new CoordinateResult{Quality=issue.CoordinateQuality,Warning=issue.Warning};return row;}
        var match=candidates[0];IssueCoordinateContext context;
        if(preloadedContext!=null)context=preloadedContext;
        else try{context=await client.GetContextAsync(issue,model,ct);}catch(OperationCanceledException){throw;}catch(Exception ex){context=new IssueCoordinateContext{ModelUrn=model.ItemUrn,VersionUrn=model.VersionUrn,ViewableGuid=string.IsNullOrWhiteSpace(issue.ViewableGuid)?model.ViewableGuid:issue.ViewableGuid,IncompleteReason="Model Derivative context unavailable: "+ex.GetType().Name};AccIssueReturnLog.Event("ACC_COORDINATE_CONTEXT_UNAVAILABLE",new{issueIdSuffix=ShortIssueId(issue.Id),versionUrnPresent=!string.IsNullOrWhiteSpace(model.VersionUrn),errorType=ex.GetType().Name});}
        RevitMapping.AttachTransform(context,match);issue.Context=context;row.RevitDocument=match.Document.Title;row.RevitLink=match.Reason;row.RevitLinkInstanceUniqueId=match.Link.UniqueId;
        AccIssueReturnLog.Event("ACC_OBJECTSET",new{issueId=ShortIssueId(issue.Id),entries=issue.ObjectSet.Count,dbIds=issue.ObjectSet.Count(x=>x.DbId.HasValue||x.DbIds.Count>0),externalIds=issue.ObjectSet.Count(x=>!string.IsNullOrWhiteSpace(x.ExternalId)||x.ExternalIds.Count>0),compositeIds=issue.ObjectSet.Count(x=>x.Composite!=null||x.ExternalId.Contains("/"))}); var elements=buildAiLinks.Where(x=>x.Match.Link.UniqueId==match.Link.UniqueId).Select(x=>x.Element).GroupBy(x=>x.UniqueId).Select(x=>x.First()).ToList();
        if(elements.Count==0)elements=issue.ObjectSet.Select(x=>RevitMapping.ResolveElement(match,x)).Where(x=>x!=null).Cast<Element>().ToList();
        row.Elements.AddRange(buildAiLinks.Count>0?buildAiLinks.Select(x=>x.Element.UniqueId).Distinct(StringComparer.OrdinalIgnoreCase):elements.Select(x=>x.UniqueId)); AccIssueReturnLog.Event("ACC_EXTERNAL_ELEMENT_MAPPING",new{issueId=ShortIssueId(issue.Id),status=elements.Count>0?"Passed":"Failed",linksChecked=buildAiLinks.Select(x=>x.Match.Link.UniqueId).Distinct().Count(),elementsResolved=row.Elements.Count,reason=elements.Count>0?"":"No matching linked element"});
        var geometryBounds=RevitMapping.HostBounds(match,elements);var elementBounds=elements.Select(x=>ToBounds(RevitMapping.HostBounds(match,new[]{x}))).Where(x=>x!=null).Cast<CoordinateBounds>().ToList();var cacheKey=match.Link.UniqueId; if(!modelBoundsCache.TryGetValue(cacheKey,out var modelBounds)){modelBounds=RevitMapping.ModelHostBounds(match);modelBoundsCache[cacheKey]=modelBounds;boundsCalculated++;}
        var accExternalIdMatched=issue.ObjectSet.Any(x=>new[]{x.ExternalId}.Concat(x.ExternalIds).Where(id=>!string.IsNullOrWhiteSpace(id)).Any(id=>elements.Any(e=>string.Equals(e.UniqueId,id,StringComparison.OrdinalIgnoreCase))));
        var externalElementMatched=accExternalIdMatched||exactBuildAi;
        row.AccExternalIdMatched=accExternalIdMatched;row.BuildAiExactElementMatched=exactBuildAi;
        if(!context.IsComplete){row.Coordinates=new CoordinateResult{Quality=CoordinateQuality.MissingContext,Warning=context.IncompleteReason};}
        else
        {
            row.Coordinates=CoordinatePipeline.ConvertViewerPointToRevitHost(issue.PushpinPosition??Array.Empty<double>(),context,match.Reason);
            CoordinatePipeline.ValidateSpatially(row.Coordinates,context,new CoordinateValidationEvidence{ModelBoundsFeet=ToBounds(modelBounds),GeometryBoundsFeet=ToBounds(geometryBounds),GeometryElementBoundsFeet=elementBounds,ExternalIdMatched=externalElementMatched}); if(issue.Camera?.Target is { Length:3 } target && issue.PushpinPosition is { Length:3 } push){var expected=new[]{push[0]+context.GlobalOffset[0],push[1]+context.GlobalOffset[1],push[2]+context.GlobalOffset[2]};row.Coordinates.CameraTargetDeltaFt=Math.Sqrt(Enumerable.Range(0,3).Sum(i=>(target[i]-expected[i])*(target[i]-expected[i])));row.Coordinates.CameraTargetMatchesPushpin=row.Coordinates.CameraTargetDeltaFt<=10d/304.8d;if(!row.Coordinates.CameraTargetMatchesPushpin)row.Coordinates.Warning=(row.Coordinates.Warning+" Camera target differs from pushpin; diagnostic only.").Trim();}
        }
        if(!row.Coordinates.IsImportable&&exactBuildAi&&buildAiLinks.Count>=2)TryRecoverGeometry(row,buildAiLinks,modelBounds,context);
        else if(!row.Coordinates.IsImportable)AccIssueReturnLog.Event("ACC_PREVIEW_COORDINATE_RECOVERY",new{issueIdSuffix=ShortIssueId(issue.Id),originalPoint=row.Coordinates.RevitPointFeet,originalPointValid=false,contextComplete=context.IsComplete,primaryResolved=buildAiLinks.Any(x=>x.Role=="Primary"),secondaryResolved=buildAiLinks.Any(x=>x.Role=="Secondary"),recoveryAttempted=false,recoveryMethod="None",recoveredPoint=(double[]?)null,primaryDistanceMm=(double?)null,secondaryDistanceMm=(double?)null,boundsPassed=false,proximityPassed=false,accepted=false,rejectionReason=exactBuildAi?"Both exact element geometries are required.":"Exact BuildAI element mapping is unavailable."});
        row.Coordinates.ExternalIdMatched=externalElementMatched;
        if(issue.BuildAiRecord!=null&&!exactBuildAi){row.Coordinates.CoordinateValidated=false;row.BlockingReasons.Add("BuildAI link or element could not be resolved exactly for every required role.");}
        if(!externalElementMatched)row.BlockingReasons.Add("External element mapping did not match a Revit element.");
        if(!row.Coordinates.IsImportable)row.BlockingReasons.Add(row.Coordinates.Warning.Length>0?row.Coordinates.Warning:"Coordinate could not be validated.");
        issue.CoordinateQuality=row.Coordinates.Quality;issue.ImportState=row.Coordinates.IsImportable?(row.Coordinates.Quality==CoordinateQuality.RecoveredFromElement?ImportState.Warning:ImportState.PreviewReady):ImportState.Blocked;issue.Warning=row.Coordinates.Warning;
        AccIssueReturnLog.Event("ACC_COORDINATE_CONTEXT",new{issueId=issue.Id,versionUrn=context.VersionUrn.Length>12?"..."+context.VersionUrn.Substring(context.VersionUrn.Length-12):context.VersionUrn,context.ViewableGuid,rawUnits=context.SourceUnits,normalizedUnits=context.Unit?.ToString()??context.UnitStatus.ToString(),pushpinSpace=context.PushpinSpace.ToString(),globalOffsetSpace=context.GlobalOffsetSpace.ToString(),placementDirection=context.PlacementDirection.ToString(),matrixFormat=context.MatrixFormat.ToString(),contextSources=context.ContextSources,linkTransformApplied=true});
        AccIssueReturnLog.Event("ACC_COORDINATE_VALIDATION",new{issueId=issue.Id,revitPointFt=row.Coordinates.RevitPointFeet,roundTripErrorMm=row.Coordinates.RoundTripErrorMm,scaleSanity=Pass(row.Coordinates.ScaleSanityPassed),modelBounds=Pass(row.Coordinates.ModelBoundsPassed),geometryProximity=Pass(row.Coordinates.GeometryProximityPassed),externalIdMatch=Pass(row.Coordinates.ExternalIdMatched),coordinateValidated=row.Coordinates.CoordinateValidated,quality=row.Coordinates.Quality.ToString(),cameraTargetDeltaFt=row.Coordinates.CameraTargetDeltaFt,cameraTargetMismatchIsBlocking=false});AccIssueReturnLog.Event("ACC_PREVIEW_PERFORMANCE",new{issues=1,links=1,boundsCalculated,elementIndexesBuilt=0,contextCacheHits=0,contextCacheMisses=0});
        row.ExistingMarker=new FilteredElementCollector(uidoc.Document).OfClass(typeof(DirectShape)).Cast<DirectShape>().Any(x=>x.ApplicationId=="BuildAI.AccIssueReturn"&&x.ApplicationDataId==IssueMarker.Key(issue));return row;
    }
    public void Import(PreviewRow row)
    {
        if(row?.Issue==null||row.Coordinates==null||!row.Coordinates.IsImportable||(row.Issue.ImportState!=ImportState.PreviewReady&&row.Issue.ImportState!=ImportState.Warning))throw new InvalidOperationException("Only a validated Ready or Warning preview row can be imported.");var bounds=FindBounds(row);AccCamera? camera=null;if(row.Issue.Camera!=null){if(row.Issue.Context?.CameraContract==CameraPointContract.RevitLinkInternalFeet)camera=ConvertCamera(row.Issue.Id,row.Issue.Camera,row.Issue.Context);else if(row.Coordinates.Quality==CoordinateQuality.RecoveredFromElement){row.Issue.Warning=(row.Issue.Warning+" ACC camera was not applied because its coordinate space is unverified.").Trim();AccIssueReturnLog.Event("ACC_CAMERA_VALIDATION",new{issueId=row.Issue.Id,eyeFinite=MatrixMath.IsFinite(row.Issue.Camera.Eye),targetFinite=MatrixMath.IsFinite(row.Issue.Camera.Target),upTransformed=false,basisOrthonormal=false,status="skipped-unverified-recovery"});}else throw new InvalidOperationException("ACC camera coordinate space is unverified; no substitute camera was applied.");}var view=AccIssueView.GetOrCreate(uidoc.Document);var point=new XYZ(row.Coordinates.RevitPointFeet[0],row.Coordinates.RevitPointFeet[1],row.Coordinates.RevitPointFeet[2]);if(camera!=null)AccIssueView.Apply(uidoc,view,camera,bounds);else{uidoc.ActiveView=view;if(bounds!=null){using var tx=new Transaction(uidoc.Document,"BuildAI ACC Issues: apply section box");tx.Start();view.IsSectionBoxActive=true;view.SetSectionBox(bounds);tx.Commit();}}IssueMarker.Upsert(uidoc.Document,view,row.Issue,point,row.Coordinates);row.Issue.ImportState=row.ExistingMarker?ImportState.Updated:ImportState.Imported;
    }
    public void RecoverFromLinkedElement(PreviewRow row,ElementId selectedLinkInstanceId,ElementId linkedElementId)
    {
        if(row==null||selectedLinkInstanceId==null||linkedElementId==null)throw new ArgumentNullException();var match=RevitMapping.FindByUniqueId(uidoc.Document,row.RevitLinkInstanceUniqueId)??throw new InvalidOperationException("The previewed Revit link is no longer loaded.");if(!match.Link.Id.Equals(selectedLinkInstanceId))throw new InvalidOperationException("The selected element belongs to a different Revit link than the previewed Issue.");var element=match.Document.GetElement(linkedElementId)??throw new InvalidOperationException("The selected linked element is unavailable.");var bounds=RevitMapping.HostBounds(match,new[]{element});if(bounds==null)throw new InvalidOperationException("The selected element has no bounding box.");var center=new[]{(bounds.Min.X+bounds.Max.X)/2,(bounds.Min.Y+bounds.Max.Y)/2,(bounds.Min.Z+bounds.Max.Z)/2};row.Coordinates=CoordinatePipeline.RecoverFromElement(center,"Explicit recovery from user-selected linked element; spatial review is required.");row.Coordinates.CoordinateValidated=false;row.Elements.Clear();row.Elements.Add(element.UniqueId);row.Issue.CoordinateQuality=row.Coordinates.Quality;row.Issue.ImportState=ImportState.Blocked;row.Issue.Warning=row.Coordinates.Warning;
    }
    private static void TryRecoverGeometry(PreviewRow row,List<(string Role,LinkMatch Match,Element Element)> links,BoundingBoxXYZ? modelBounds,IssueCoordinateContext context)
    {
        var original=row.Coordinates.RevitPointFeet;
        var originalValid=row.Coordinates.CoordinateValidated;
        var primary=links.FirstOrDefault(x=>x.Role=="Primary");var secondary=links.FirstOrDefault(x=>x.Role=="Secondary");
        var a=primary.Element==null?null:RevitMapping.HostBounds(primary.Match,new[]{primary.Element});
        var b=secondary.Element==null?null:RevitMapping.HostBounds(secondary.Match,new[]{secondary.Element});
        var secondaryModelBounds=secondary.Element==null?null:RevitMapping.ModelHostBounds(secondary.Match);
        var recoveryBounds=modelBounds;
        if(modelBounds!=null&&secondaryModelBounds!=null)recoveryBounds=new BoundingBoxXYZ{Transform=Transform.Identity,Min=new XYZ(Math.Min(modelBounds.Min.X,secondaryModelBounds.Min.X),Math.Min(modelBounds.Min.Y,secondaryModelBounds.Min.Y),Math.Min(modelBounds.Min.Z,secondaryModelBounds.Min.Z)),Max=new XYZ(Math.Max(modelBounds.Max.X,secondaryModelBounds.Max.X),Math.Max(modelBounds.Max.Y,secondaryModelBounds.Max.Y),Math.Max(modelBounds.Max.Z,secondaryModelBounds.Max.Z))};
        var accepted=false;var reason="Element geometry has no usable host bounds.";double[]? point=null;double da=double.NaN,db=double.NaN;var method="HostBoundingBoxes";
        if(a!=null&&b!=null){
            var p1=new[]{a.Min.X,a.Min.Y,a.Min.Z};var p2=new[]{a.Max.X,a.Max.Y,a.Max.Z};var q1=new[]{b.Min.X,b.Min.Y,b.Min.Z};var q2=new[]{b.Max.X,b.Max.Y,b.Max.Z};
            var nearA=new double[3];var nearB=new double[3];
            for(var i=0;i<3;i++){if(p2[i]<q1[i]){nearA[i]=p2[i];nearB[i]=q1[i];}else if(q2[i]<p1[i]){nearA[i]=p1[i];nearB[i]=q2[i];}else{nearA[i]=nearB[i]=(Math.Max(p1[i],q1[i])+Math.Min(p2[i],q2[i]))/2;}}
            point=Enumerable.Range(0,3).Select(i=>(nearA[i]+nearB[i])/2).ToArray();
            da=DistanceToBox(point,a);db=DistanceToBox(point,b);
            try{if(TrySolidPoint(primary.Match,primary.Element!,secondary.Match,secondary.Element!,out var solidPoint,out var solidDistance,out var solidMethod)){
                point=solidPoint;da=db=solidDistance/2;method=solidMethod;
            }}catch(Autodesk.Revit.Exceptions.InvalidOperationException){method="HostBoundingBoxes";}
            var boundsPassed=recoveryBounds!=null&&InsideBox(point,recoveryBounds,CoordinatePipeline.ModelBoundsMarginFeet);
            var proximityPassed=da<=CoordinatePipeline.GeometryProximityThresholdFeet&&db<=CoordinatePipeline.GeometryProximityThresholdFeet;
            accepted=MatrixMath.IsFinite(point)&&boundsPassed&&proximityPassed;
            reason=accepted?"":"Recovered point failed model bounds or proximity to both linked elements.";
            if(accepted){row.Coordinates=CoordinatePipeline.RecoverFromElement(point,"Recovered from element geometry ("+method+"); inspect placement before import.");row.Coordinates.CoordinateValidated=true;row.Coordinates.ModelBoundsPassed=true;row.Coordinates.GeometryProximityPassed=true;row.Coordinates.ExternalIdMatched=true;}
        }
        AccIssueReturnLog.Event("ACC_PREVIEW_COORDINATE_RECOVERY",new{issueIdSuffix=ShortIssueId(row.Issue.Id),originalPoint=original,originalPointValid=originalValid,contextComplete=context.IsComplete,primaryResolved=primary.Element!=null,secondaryResolved=secondary.Element!=null,recoveryAttempted=true,recoveryMethod=method,recoveredPoint=point,primaryDistanceMm=da*304.8,secondaryDistanceMm=db*304.8,boundsPassed=point!=null&&recoveryBounds!=null&&InsideBox(point,recoveryBounds,CoordinatePipeline.ModelBoundsMarginFeet),proximityPassed=point!=null&&da<=CoordinatePipeline.GeometryProximityThresholdFeet&&db<=CoordinatePipeline.GeometryProximityThresholdFeet,accepted,rejectionReason=reason});
    }
    internal static bool TrySolidPoint(LinkMatch first,Element firstElement,LinkMatch second,Element secondElement,out double[] point,out double distance,out string method,int budgetMs=0)
    {
        point=Array.Empty<double>();distance=double.NaN;method="";
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var left=HostSolids(first,firstElement).Take(budgetMs>0?4:int.MaxValue).ToList();
        if(budgetMs>0&&timer.ElapsedMilliseconds>budgetMs)return false;
        var right=HostSolids(second,secondElement).Take(budgetMs>0?4:int.MaxValue).ToList();
        if(budgetMs>0&&timer.ElapsedMilliseconds>budgetMs)return false;
        if(left.Count==0||right.Count==0)return false;
        foreach(var a in left)foreach(var b in right){
            if(budgetMs>0&&timer.ElapsedMilliseconds>budgetMs)return false;
            try{var overlap=BooleanOperationsUtils.ExecuteBooleanOperation(a,b,BooleanOperationsType.Intersect);if(overlap!=null&&overlap.Volume>1e-9){var c=overlap.ComputeCentroid();point=new[]{c.X,c.Y,c.Z};distance=0;method="SolidIntersection";return MatrixMath.IsFinite(point);}}
            catch(Autodesk.Revit.Exceptions.InvalidOperationException){}
        }
        var best=double.PositiveInfinity;XYZ? pa=null,pb=null;
        foreach(var a in left)foreach(var b in right){
            if(budgetMs>0&&timer.ElapsedMilliseconds>budgetMs)return false;
            ProjectVertices(a,b,ref best,ref pa,ref pb,timer,budgetMs);
            ProjectVertices(b,a,ref best,ref pb,ref pa,timer,budgetMs);
        }
        if(pa==null||pb==null||double.IsInfinity(best))return false;
        point=new[]{(pa.X+pb.X)/2,(pa.Y+pb.Y)/2,(pa.Z+pb.Z)/2};distance=best;method="SolidFaceProjection";return MatrixMath.IsFinite(point);
    }
    private static IEnumerable<Solid> HostSolids(LinkMatch match,Element element)
    {
        var geometry=element.get_Geometry(new Options{ComputeReferences=false,IncludeNonVisibleObjects=false});
        if(geometry==null)yield break;
        foreach(var solid in ReadSolids(geometry)){
            Solid transformed;
            try{transformed=SolidUtils.CreateTransformed(solid,match.Link.GetTotalTransform());}catch(Autodesk.Revit.Exceptions.InvalidOperationException){continue;}
            if(transformed.Volume>1e-9)yield return transformed;
        }
    }
    private static IEnumerable<Solid> ReadSolids(GeometryElement geometry)
    {
        foreach(GeometryObject obj in geometry){
            if(obj is Solid s&&s.Volume>1e-9)yield return s;
            if(obj is GeometryInstance instance){var nested=instance.GetInstanceGeometry();foreach(var child in ReadSolids(nested))yield return child;}
        }
    }
    private static void ProjectVertices(Solid from,Solid to,ref double best,ref XYZ? fromPoint,ref XYZ? toPoint,System.Diagnostics.Stopwatch? timer=null,int budgetMs=0)
    {
        foreach(Face face in from.Faces){
            if(budgetMs>0&&timer!.ElapsedMilliseconds>budgetMs)return;
            var mesh=face.Triangulate();
            foreach(var vertex in mesh.Vertices){
                if(budgetMs>0&&timer!.ElapsedMilliseconds>budgetMs)return;
                foreach(Face target in to.Faces){
                    var projection=target.Project(vertex);if(projection==null)continue;
                    var d=vertex.DistanceTo(projection.XYZPoint);
                    if(d<best){best=d;fromPoint=vertex;toPoint=projection.XYZPoint;}
                }
            }
        }
    }
    private static double DistanceToBox(double[] p,BoundingBoxXYZ b){var min=new[]{b.Min.X,b.Min.Y,b.Min.Z};var max=new[]{b.Max.X,b.Max.Y,b.Max.Z};return Math.Sqrt(Enumerable.Range(0,3).Sum(i=>Math.Pow(Math.Max(Math.Max(min[i]-p[i],0),p[i]-max[i]),2)));}
    private static bool InsideBox(double[] p,BoundingBoxXYZ b,double margin)=>p[0]>=b.Min.X-margin&&p[0]<=b.Max.X+margin&&p[1]>=b.Min.Y-margin&&p[1]<=b.Max.Y+margin&&p[2]>=b.Min.Z-margin&&p[2]<=b.Max.Z+margin;
    private BoundingBoxXYZ? FindBounds(PreviewRow row){
        var boxes=new List<BoundingBoxXYZ>();
        foreach(var reference in new[]{row.Issue.BuildAiRecord?.PrimaryElement,row.Issue.BuildAiRecord?.SecondaryElement}){
            if(reference==null)continue;
            var link=RevitMapping.FindByUniqueId(uidoc.Document,reference.LinkInstanceUid);
            var element=link?.Document.GetElement(reference.ElementUniqueId);
            if(link!=null&&element!=null){var box=RevitMapping.HostBounds(link,new[]{element});if(box!=null)boxes.Add(box);}
        }
        if(boxes.Count==0){var match=RevitMapping.FindByUniqueId(uidoc.Document,row.RevitLinkInstanceUniqueId);if(match==null)return null;var elements=row.Elements.Select(x=>match.Document.GetElement(x)).Where(x=>x!=null).Cast<Element>();var box=RevitMapping.HostBounds(match,elements);if(box!=null)boxes.Add(box);}
        if(boxes.Count==0)return null;
        var combined=new BoundingBoxXYZ{Transform=Transform.Identity,Min=new XYZ(boxes.Min(x=>x.Min.X),boxes.Min(x=>x.Min.Y),boxes.Min(x=>x.Min.Z)),Max=new XYZ(boxes.Max(x=>x.Max.X),boxes.Max(x=>x.Max.Y),boxes.Max(x=>x.Max.Z))};
        return AccIssueView.Expand(combined);
    }
    private static AccCamera ConvertCamera(string issueId,AccCamera source,IssueCoordinateContext c)
    {
        if(c.CameraContract!=CameraPointContract.RevitLinkInternalFeet)throw new InvalidOperationException("ACC camera coordinate space is unverified; no substitute camera was applied.");
        try{var camera=CameraPipeline.ConvertLinkCameraToHost(source,c.LinkToHost);AccIssueReturnLog.Event("ACC_CAMERA_VALIDATION",new{issueId,eyeFinite=MatrixMath.IsFinite(camera.Eye),targetFinite=MatrixMath.IsFinite(camera.Target),upTransformed=true,basisOrthonormal=CameraPipeline.IsOrthonormal(camera),status="pass"});return camera;}
        catch(Exception ex){AccIssueReturnLog.Event("ACC_CAMERA_VALIDATION",new{issueId,eyeFinite=MatrixMath.IsFinite(source.Eye),targetFinite=MatrixMath.IsFinite(source.Target),upTransformed=false,basisOrthonormal=false,status=ex.Message});throw;}
    }
    private static CoordinateBounds? ToBounds(BoundingBoxXYZ? box)=>box==null?null:new CoordinateBounds{Min=new[]{box.Min.X,box.Min.Y,box.Min.Z},Max=new[]{box.Max.X,box.Max.Y,box.Max.Z}};
    private static ElementId BuildElementId(int value){
#if NET8_0_OR_GREATER
        return new ElementId((long)value);
#else
        return new ElementId(value);
#endif
    }
    private static string ShortIssueId(string value)=>string.IsNullOrWhiteSpace(value)?"":value.Length<=8?value:value.Substring(value.Length-8);
    private static string Pass(bool value)=>value?"pass":"fail";
}


