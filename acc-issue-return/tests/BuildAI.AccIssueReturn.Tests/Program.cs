using BuildAI.AccIssueReturn.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

static class Program
{
    private static int passed;
    static int Main()
    {
        try
        {
            Run("feet payload is not rescaled",FeetPayloadIsNotRescaled);
            Run("BuildAI array response parses",BuildAiArrayResponseParses);
            Run("BuildAI wrapped response parses",BuildAiWrappedResponseParses);
            Run("BuildAI issue ids normalize exactly",BuildAiIssueIdsNormalize);
            Run("millimetres convert once",MillimetresConvertOnce);
            Run("unit variants",UnitVariants);
            Run("unknown and missing differ",UnknownAndMissingDiffer);
            Run("unknown unit recovery reason",UnknownUnitRecoveryReason);
            Run("missing units activate verified viewer feet contract",MissingUnitsActivateFeetContract);
            Run("camera target mismatch is diagnostic",CameraTargetMismatchIsDiagnostic);
            Run("cache merge preserves issue offset",CacheMergePreservesIssueOffset);
            Run("cache separates viewables and regions",CacheSeparatesViewables);
            Run("linked document mismatch does not fall back",LinkedDocumentMismatchDoesNotFallBack);
            Run("metadata mismatch does not fall back",MetadataMismatchDoesNotFallBack);
            Run("AEC resource mismatch does not fall back",AecResourceMismatchDoesNotFallBack);
            Run("AEC 12 identity",Aec12Identity);
            Run("AEC 12 translation",Aec12Translation);
            Run("Viewer 16 rotation and translation",Viewer16RotationTranslation);
            Run("uniform scale",UniformScale);
            Run("invalid matrix count",InvalidMatrixCount);
            Run("singular matrix",SingularMatrix);
            Run("non-finite matrix",NonFiniteMatrix);
            Run("transform direction fixture",TransformDirectionFixture);
            Run("wrong transform direction fails fixture",WrongDirectionFailsFixture);
            Run("scale times 304.8 fails",ScaleTimes3048Fails);
            Run("scale divided by 304.8 fails",ScaleDivided3048Fails);
            Run("wrong offset fails spatial validation",WrongOffsetFailsSpatialValidation);
            Run("round-trip alone is insufficient",RoundTripAloneIsInsufficient);
            Run("external id without proximity is insufficient",ExternalIdWithoutProximityIsInsufficient);
            Run("correct runtime spatial evidence validates",CorrectRuntimeSpatialEvidenceValidates);
            Run("union bounds cannot hide distant element",UnionBoundsCannotHideDistantElement);
            Run("camera identity",CameraIdentity);
            Run("camera rotation Z",CameraRotationZ);
            Run("camera rotation X",CameraRotationX);
            Run("camera translated link",CameraTranslatedLink);
            Run("camera mirrored transform",CameraMirroredTransform);
            Run("camera parallel up fallback",CameraParallelUpFallback);
            Run("camera non-finite rejected",CameraNonFiniteRejected);
            Run("camera conversion does not mutate source",CameraConversionDoesNotMutateSource);
            Run("sanitized diagnostic",SanitizedDiagnostic);
            Run("filters",FiltersUseAssignedUsers);
            Run("dbId is optional",NoStableDbIdRequirement);Run("composite id parses by last slash",CompositeIdParsesByLastSlash);Run("regional routes",RegionalRoutes);Run("diagnostic export",DiagnosticExport);Run("round trip threshold",RoundTripThreshold);Run("URN normalization",UrnNormalization);Run("model identity matching",ModelIdentityMatching);Run("model mismatch has no fallback",ModelMismatchHasNoFallback);Run("all issues remain visible",AllIssuesRemainVisible);Run("viewable filters are independent",ViewableFiltersAreIndependent);Run("blocked preview row remains previewable",BlockedPreviewRowRemainsPreviewable);Run("recovered point requires spatial validation",RecoveredPointRequiresValidation);Run("version number 57 parses",VersionNumber57Parses);Run("unknown version is not V0",UnknownVersionIsNotV0);Run("invalid candidate is blocked",InvalidCandidateIsBlocked);Run("missing issue version remains unconfirmed",MissingIssueVersionRemainsUnconfirmed);
            Console.WriteLine("ACC Issue Return tests: "+passed+" passed.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }

    static IssueCoordinateContext Feet(double offset=0)=>new(){ModelUrn="m",VersionUrn="v",ViewableGuid="g",SourceUnits="feet",UnitStatus=UnitParseStatus.Known,Unit=LengthUnit.Foot,ViewerCoordinateUnit=LengthUnit.Foot,MetresPerUnit=.3048,GlobalOffset=new[]{offset,0d,0d},Contract=CoordinateContract.ViewerLocalFeetPlusGlobalOffset,PushpinSpace=CoordinateSpace.ViewerLocal,GlobalOffsetSpace=CoordinateSpace.RevitLinkInternalFeet,CameraContract=CameraPointContract.RevitLinkInternalFeet,LinkToHost=Matrix4.Identity()};
    static IssueCoordinateContext Mm()=>new(){ModelUrn="m",VersionUrn="v",ViewableGuid="g",SourceUnits="millimeters",UnitStatus=UnitParseStatus.Known,Unit=LengthUnit.Millimetre,MetresPerUnit=.001,GlobalOffset=new[]{0d,0d,0d},Contract=CoordinateContract.ViewerLocalModelUnits,PushpinSpace=CoordinateSpace.ViewerLocal,GlobalOffsetSpace=CoordinateSpace.Model,PlacementTransform=Matrix4.Identity(),PlacementDirection=CoordinateTransformDirection.ViewerToModel,LinkToHost=Matrix4.Identity()};
    static void FeetPayloadIsNotRescaled(){var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{10d,2d,3d},Feet(5),"link");Eq(15,r.RevitPointFeet[0]);}
    static void MillimetresConvertOnce(){var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{1000d,0d,0d},Mm(),"link");Eq(LengthUnits.FeetPerMeter,r.RevitPointFeet[0]);}
    static void UnitVariants(){foreach(var x in new[]{"m","meter","meters","metre","metres","mm","millimeter","millimeters","millimetre","millimetres","cm","centimeter","centimeters","centimetre","centimetres","ft","foot","feet","decimal-foot","decimal-feet","decimal foot","decimal feet","in","inch","inches"})if(!LengthUnits.Parse(x).IsKnown)throw new Exception("Unit was not parsed: "+x);}
    static void UnknownAndMissingDiffer(){var a=LengthUnits.Parse("cubits");var b=LengthUnits.Parse(null);if(a.Status!=UnitParseStatus.Unknown||a.MetresPerUnit.HasValue||b.Status!=UnitParseStatus.Missing)throw new Exception("Unknown/missing unit contract failed.");}
    static void UnknownUnitRecoveryReason(){var c=Feet();c.ViewerCoordinateUnit=null;c.Contract=CoordinateContract.Unverified;c.UnitStatus=UnitParseStatus.Unknown;c.Unit=null;c.MetresPerUnit=null;c.IncompleteReason="Unknown unit 'cubits'; recover from a matched Revit element.";var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{1d,2d,3d},c,"link");if(r.Quality!=CoordinateQuality.MissingContext||!r.Warning.Contains("recover",StringComparison.OrdinalIgnoreCase))throw new Exception("Recovery reason is not actionable.");}
    static void MissingUnitsActivateFeetContract(){var c=new IssueCoordinateContext{ViewableGuid="g"};if(!AccIssueClient.TrySetViewerFeetContract(c,null,null,new[]{10d,20d,30d})||c.ViewerCoordinateUnit!=LengthUnit.Foot)throw new Exception("Viewer feet contract was not independent of display units.");}
    static void CameraTargetMismatchIsDiagnostic(){var c=Feet(10);var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{1d,0d,0d},c,"link");r.CameraTargetDeltaFt=.44;r.CameraTargetMatchesPushpin=false;CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=Bounds(0,20),GeometryBoundsFeet=BoundsX(10,12),ExternalIdMatched=true});if(!r.CoordinateValidated)throw new Exception("Camera mismatch blocked valid spatial evidence.");}
    static void CacheMergePreservesIssueOffset(){var first=Feet(10);first.UnitsSource="Issue/viewerState";first.GlobalOffsetSource="Issue.linkedDocuments.details.viewerState.globalOffset";first.PlacementTransformSource="Issue.viewerState.placementTransform";var cached=AccIssueClient.CloneContext(first);if(MatrixMath.IsFinite(cached.GlobalOffset)||cached.Contract!=CoordinateContract.Unverified||cached.UnitStatus!=UnitParseStatus.Missing)throw new Exception("Issue-specific coordinate context entered the viewable cache.");var current=Feet(20);AccIssueClient.MergeMissing(current,cached);Eq(20,current.GlobalOffset[0]);}
    static void CacheSeparatesViewables(){var a=AccIssueClient.BuildCacheKey("p","US","v","g1");var b=AccIssueClient.BuildCacheKey("p","US","v","g2");if(a.Equals(b))throw new Exception("Viewables shared a cache key.");}
    static void LinkedDocumentMismatchDoesNotFallBack(){var raw=JObject.Parse(@"{""linkedDocuments"":[{""details"":{""viewable"":{""guid"":""g1""}}},{""details"":{""viewable"":{""guid"":""g2""}}}]}");if(AccIssueClient.SelectLinkedDetails(raw,"missing")!=null)throw new Exception("A different linked document was used.");if((string?)AccIssueClient.SelectLinkedDetails(raw,"g2")?["viewable"]?["guid"]!="g2")throw new Exception("Exact linked document was not selected.");}
    static void MetadataMismatchDoesNotFallBack(){var metadata=JObject.Parse(@"{""data"":{""metadata"":[{""guid"":""g1""}]}}");if(AccIssueClient.SelectMetadataViewable(metadata,null,"missing")!=null)throw new Exception("A different metadata row was used.");}
    static void AecResourceMismatchDoesNotFallBack(){var manifest=JObject.Parse(@"{""derivatives"":[{""children"":[{""guid"":""g1"",""type"":""geometry"",""children"":[{""role"":""Autodesk.AEC.ModelData"",""urn"":""u1""}]}]}]}");if(AccIssueClient.FindAecModelDataResource(manifest,"missing")!=null)throw new Exception("AECModelData from a different viewable was used.");}
    static void Aec12Identity(){var m=MatrixFormats.Parse(new[]{1d,0,0,0,1,0,0,0,1,0,0,0},MatrixSourceFormat.AecRefPointColumnMajor4x3);Point(new[]{2d,3d,4d},MatrixMath.TransformPointModelToViewer(m,new[]{2d,3d,4d}));}
    static void Aec12Translation(){var m=MatrixFormats.Parse(new[]{1d,0,0,0,1,0,0,0,1,10,20,30},MatrixSourceFormat.AecRefPointColumnMajor4x3);Point(new[]{11d,22d,33d},MatrixMath.TransformPointModelToViewer(m,new[]{1d,2d,3d}));}
    static void Viewer16RotationTranslation(){var m=MatrixFormats.Parse(new[]{0d,1,0,0,-1,0,0,0,0,0,1,0,10,20,30,1},MatrixSourceFormat.ViewerColumnMajor4x4);Point(new[]{8d,21d,33d},MatrixMath.TransformPointModelToViewer(m,new[]{1d,2d,3d}));}
    static void UniformScale(){var m=MatrixFormats.Parse(new[]{2d,0,0,0,0,2,0,0,0,0,2,0,0,0,0,1},MatrixSourceFormat.ViewerColumnMajor4x4);Point(new[]{2d,4d,6d},MatrixMath.TransformPointModelToViewer(m,new[]{1d,2d,3d}));}
    static void InvalidMatrixCount()=>Throws<FormatException>(()=>MatrixFormats.Parse(new double[15],MatrixSourceFormat.ViewerColumnMajor4x4));
    static void SingularMatrix()=>Throws<FormatException>(()=>MatrixFormats.Parse(new double[16],MatrixSourceFormat.ViewerColumnMajor4x4));
    static void NonFiniteMatrix(){var x=Matrix4.IdentityArray();x[4]=double.NaN;Throws<FormatException>(()=>MatrixFormats.Parse(x,MatrixSourceFormat.ViewerColumnMajor4x4));}
    static void TransformDirectionFixture(){var c=Mm();c.PlacementDirection=CoordinateTransformDirection.ModelToViewer;c.PlacementTransform=MatrixFormats.Parse(new[]{1d,0,0,0,1,0,0,0,1,1000,0,0},MatrixSourceFormat.AecRefPointColumnMajor4x3);var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{2000d,0,0},c,"link");Eq(LengthUnits.FeetPerMeter,r.RevitPointFeet[0]);}
    static void WrongDirectionFailsFixture(){var c=Mm();c.PlacementDirection=CoordinateTransformDirection.ViewerToModel;c.PlacementTransform=MatrixFormats.Parse(new[]{1d,0,0,0,1,0,0,0,1,1000,0,0},MatrixSourceFormat.AecRefPointColumnMajor4x3);var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{2000d,0,0},c,"link");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ExpectedRevitPointFeet=new[]{LengthUnits.FeetPerMeter,0d,0d}});if(r.CoordinateValidated)throw new Exception("Wrong direction passed an independent expected point.");}
    static void ScaleTimes3048Fails(){var c=Mm();var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{30480d,0,0},c,"link");r.RevitPointFeet[0]*=304.8;CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=BoundsX(90,110),GeometryBoundsFeet=BoundsX(99,101),ExternalIdMatched=true});if(r.CoordinateValidated)throw new Exception("x304.8 scale passed runtime spatial evidence.");}
    static void ScaleDivided3048Fails(){var c=Mm();var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{30480d,0,0},c,"link");r.RevitPointFeet[0]/=304.8;CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=BoundsX(90,110),GeometryBoundsFeet=BoundsX(99,101),ExternalIdMatched=true});if(r.CoordinateValidated)throw new Exception("/304.8 scale passed runtime spatial evidence.");}
    static void WrongOffsetFailsSpatialValidation(){var c=Feet(100);var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{1d,0,0},c,"link");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=Bounds(0,10),GeometryBoundsFeet=Bounds(0,2),ExternalIdMatched=true});if(r.CoordinateValidated)throw new Exception("Wrong offset passed bounds validation.");}
    static void RoundTripAloneIsInsufficient(){var c=Feet(100);var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{1d,0,0},c,"link");if(!r.MatrixRoundTripPassed)throw new Exception("Setup round-trip failed.");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence());if(r.CoordinateValidated)throw new Exception("Round-trip alone validated coordinates.");}
    static void ExternalIdWithoutProximityIsInsufficient(){var c=Feet();var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{50d,50d,50d},c,"link");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=Bounds(0,100),GeometryBoundsFeet=Bounds(0,1),ExternalIdMatched=true});if(r.CoordinateValidated)throw new Exception("ExternalId bypassed geometry proximity.");}
    static void CorrectRuntimeSpatialEvidenceValidates(){var c=Mm();var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{30480d,0,0},c,"link");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=BoundsX(90,110),GeometryBoundsFeet=BoundsX(99,101),ExternalIdMatched=true});if(!r.CoordinateValidated)throw new Exception("Correct scale and element evidence did not validate.");}
    static void UnionBoundsCannotHideDistantElement(){var c=Feet();var r=CoordinatePipeline.ConvertViewerPointToRevitHost(new[]{50d,0,0},c,"link");CoordinatePipeline.ValidateSpatially(r,c,new CoordinateValidationEvidence{ModelBoundsFeet=BoundsX(0,100),GeometryBoundsFeet=BoundsX(0,100),GeometryElementBoundsFeet=new[]{BoundsX(0,1),BoundsX(99,100)},ExternalIdMatched=true});if(r.CoordinateValidated)throw new Exception("Union bounds hid the distant point from both elements.");}
    static void CameraIdentity(){var c=CameraPipeline.ConvertLinkCameraToHost(Camera(),Matrix4.Identity());Point(new[]{1d,2d,3d},c.Eye);if(!CameraPipeline.IsOrthonormal(c))throw new Exception("Identity camera basis invalid.");}
    static void CameraRotationZ(){var m=MatrixFormats.Parse(new[]{0d,1,0,0,-1,0,0,0,0,0,1,0,0,0,0,1},MatrixSourceFormat.RevitColumnMajor4x4);var c=CameraPipeline.ConvertLinkCameraToHost(Camera(),m);Point(new[]{-2d,1d,3d},c.Eye);Point(new[]{-1d,0d,0d},c.Up);}
    static void CameraRotationX(){var m=MatrixFormats.Parse(new[]{1d,0,0,0,0,0,1,0,0,-1,0,0,0,0,0,1},MatrixSourceFormat.RevitColumnMajor4x4);if(!CameraPipeline.IsOrthonormal(CameraPipeline.ConvertLinkCameraToHost(Camera(),m)))throw new Exception("X rotation failed.");}
    static void CameraTranslatedLink(){var m=MatrixFormats.Parse(new[]{1d,0,0,0,0,1,0,0,0,0,1,0,10,20,30,1},MatrixSourceFormat.RevitColumnMajor4x4);var c=CameraPipeline.ConvertLinkCameraToHost(Camera(),m);Point(new[]{11d,22d,33d},c.Eye);Point(new[]{0d,1d,0d},c.Up);}
    static void CameraMirroredTransform(){var m=MatrixFormats.Parse(new[]{-1d,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},MatrixSourceFormat.RevitColumnMajor4x4);if(!CameraPipeline.IsOrthonormal(CameraPipeline.ConvertLinkCameraToHost(Camera(),m)))throw new Exception("Mirrored camera basis invalid.");}
    static void CameraParallelUpFallback(){var c=Camera();c.Up=new[]{1d,0,0};if(!CameraPipeline.IsOrthonormal(CameraPipeline.ConvertLinkCameraToHost(c,Matrix4.Identity())))throw new Exception("Parallel-up fallback failed.");}
    static void CameraNonFiniteRejected(){var c=Camera();c.Eye[0]=double.NaN;Throws<ArgumentException>(()=>CameraPipeline.ConvertLinkCameraToHost(c,Matrix4.Identity()));}
    static void CameraConversionDoesNotMutateSource(){var c=Camera();var before=(double[])c.Eye.Clone();var m=MatrixFormats.Parse(new[]{1d,0,0,0,0,1,0,0,0,0,1,0,10,20,30,1},MatrixSourceFormat.RevitColumnMajor4x4);CameraPipeline.ConvertLinkCameraToHost(c,m);Point(before,c.Eye);}
    static void VersionNumber57Parses(){var x=JObject.Parse(@"{""attributes"":{""versionNumber"":57}}");if(AccIssueClient.ParseVersionNumber(x)!=57)throw new Exception("V57 was not parsed from ACC version payload.");var m=new AccModel{Name="Eilat Hotel_207_SP_R25.rvt",VersionNumber=57,IsLatest=true,VersionStatus=VersionResolutionStatus.LatestResolved};if(!m.DisplayLabel.Contains("V57 (latest)"))throw new Exception("Latest label is incorrect.");}
    static void UnknownVersionIsNotV0(){var m=new AccModel{Name="Model",VersionStatus=VersionResolutionStatus.Unknown};if(m.DisplayLabel.Contains("V0")||!m.DisplayLabel.Contains("Version unknown"))throw new Exception("Unknown version was displayed as V0.");}
    static void InvalidCandidateIsBlocked(){var issue=new AccIssue{ModelUrn="item-1",ViewableGuid="g"};var m=new AccModel{ItemUrn="item-1",LineageUrn="item-1",ViewableGuid="g",VersionNumber=59,VersionStatus=VersionResolutionStatus.InvalidCandidate};var r=AccModelMatcher.Match(issue,m);if(!r.Matches||r.ImportIdentityConfirmed||r.Reason!="CandidateVersionDoesNotExistForItem")throw new Exception("Invalid V59 candidate was accepted.");}
    static void MissingIssueVersionRemainsUnconfirmed(){var issue=new AccIssue{ModelUrn="item-1",ViewableGuid="g"};var m=new AccModel{ItemUrn="item-1",LineageUrn="item-1",ViewableGuid="g",VersionNumber=57,VersionStatus=VersionResolutionStatus.Unknown};var r=AccModelMatcher.Match(issue,m);if(!r.Matches||r.ImportIdentityConfirmed||r.Reason!="MissingIssueVersion")throw new Exception("Missing issue version was silently accepted.");}    static void SanitizedDiagnostic(){var issue=new AccIssue{Id="i",VersionUrn="v",ViewableGuid="g",PushpinPosition=new[]{1d,2d,3d},Context=Feet()};issue.RawJson=@"{""access_token"":""secret"",""email"":""x@y""}";var json=CoordinateDiagnosticExporter.CreateSanitizedJson(issue);if(json.Contains("secret")||json.Contains("x@y")||!json.Contains("provenance"))throw new Exception("Diagnostic export is not sanitized.");}
    static void FiltersUseAssignedUsers(){var issues=new[]{new AccIssue{AssignedUserId="u1",Status="open"},new AccIssue{AssignedUserId="",Status="open"}};var r=IssueFiltering.Apply(issues,new IssueFilterState{AssignedUser="<unassigned>"});if(r.Count!=1||r[0].AssignedUserId!="")throw new Exception("Unassigned filter failed.");}
    static void CompositeIdParsesByLastSlash(){var x=CompositeExternalId.Parse("link/a/b/element");if(!x.IsComposite||x.LinkDocumentUniqueId!="link/a/b"||x.ElementUniqueId!="element")throw new Exception("Composite ID was not split by last slash.");if(CompositeExternalId.Parse("plain").IsComposite)throw new Exception("Plain ID became composite.");}
    static void RegionalRoutes(){var u=ApsRegionResolver.BuildModelDerivativeUrl("https://aps","EMEA","urn:x","manifest");if(!u.Contains("/regions/emea/designdata/"))throw new Exception("EMEA route missing.");var us=ApsRegionResolver.BuildModelDerivativeUrl("https://aps","US","urn:x","manifest");if(us.Contains("/regions/"))throw new Exception("US route was regional.");}
    static void DiagnosticExport(){var path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"acc-diagnostic-test.json");new DiagnosticExportService().Export(path,new AccIssue{Id="x",Context=Feet(),PushpinPosition=new[]{1d,2d,3d}});if(!System.IO.File.Exists(path)||!System.IO.File.ReadAllText(path).Contains("provenance"))throw new Exception("Diagnostic export failed.");try{System.IO.File.Delete(path);}catch{}}
    static void RoundTripThreshold(){if(CoordinatePipeline.DefaultRoundTripThresholdMm!=10.0)throw new Exception("Product threshold changed.");}
    static void NoStableDbIdRequirement(){var i=new AccIssue{ObjectSet=new List<AccObjectRef>{new(){ExternalId="revit-unique"}}};if(i.ObjectSet[0].DbId!=null)throw new Exception("dbId became required.");}
    static void UrnNormalization(){if(!ApsUrnNormalizer.Same("b.urn:adsk.objects:example","urn:adsk.objects:example"))throw new Exception("b prefix failed");if(ApsUrnNormalizer.Normalize("%%%")!="%%%")throw new Exception("invalid URN changed");}
    static void ModelIdentityMatching(){var issue=new AccIssue{VersionUrn="b.urn:version:42",ViewableGuid="view-1"};var model=new AccModel{VersionUrn="urn:version:42",ViewableGuid="view-1"};if(!AccModelMatcher.Matches(issue,model))throw new Exception("identity did not match");}
    static void ModelMismatchHasNoFallback(){var issue=new AccIssue{VersionUrn="urn:version:old",ViewableGuid="view-1"};var model=new AccModel{VersionUrn="urn:version:new",ViewableGuid="view-1"};if(AccModelMatcher.Matches(issue,model))throw new Exception("different version matched");}
    static void AllIssuesRemainVisible(){var issues=Enumerable.Range(0,90).Select(i=>new AccIssue{Id="i"+i,ModelUrn=i<39?"model": "other",ViewableGuid=i<39?"view-a":"view-b"}).ToList();var visible=IssueFiltering.Apply(issues,new IssueFilterState());if(visible.Count!=90)throw new Exception("AllIssues was destructively filtered.");}
    static void ViewableFiltersAreIndependent(){var issues=Enumerable.Range(0,90).Select(i=>new AccIssue{Id="i"+i,ModelUrn="model",ViewableGuid=i<39?"view-a":"view-b"}).ToList();if(IssueFiltering.Apply(issues,new IssueFilterState{Model="view-a"}).Count!=39||IssueFiltering.Apply(issues,new IssueFilterState{Model="view-b"}).Count!=51)throw new Exception("Viewable filter counts failed.");}
    static void BlockedPreviewRowRemainsPreviewable(){var row=new PreviewRow{Issue=new AccIssue(),Coordinates=new CoordinateResult{Quality=CoordinateQuality.AmbiguousModel,CoordinateValidated=false}};if(!row.CanPreview||row.CanImport)throw new Exception("Blocked preview row policy failed.");}
    static void RecoveredPointRequiresValidation(){var result=CoordinatePipeline.RecoverFromElement(new[]{0d,0d,0d},"unverified");if(result.IsImportable)throw new Exception("Unvalidated origin was importable.");result.CoordinateValidated=true;if(!result.IsImportable)throw new Exception("Validated finite point was blocked.");}
    static CoordinateBounds Bounds(double min,double max)=>new(){Min=new[]{min,min,min},Max=new[]{max,max,max}};
    static AccCamera Camera()=>new(){Eye=new[]{1d,2d,3d},Target=new[]{2d,2d,3d},Up=new[]{0d,1d,0d}};
    static CoordinateBounds BoundsX(double min,double max)=>new(){Min=new[]{min,-1d,-1d},Max=new[]{max,1d,1d}};
    static void Run(string name,Action test){test();passed++;Console.WriteLine("PASS "+name);}
    static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    static void Point(double[] expected,double[] actual){for(var i=0;i<3;i++)Eq(expected[i],actual[i]);}
    static void Eq(double expected,double actual,double tolerance=1e-8){if(Math.Abs(expected-actual)>tolerance)throw new Exception($"Expected {expected}, got {actual}.");}

    static void BuildAiArrayResponseParses(){var rows=BuildAiRevitIssuesClient.ParseResponse("[{\"issue_id\":\" ABC \",\"primary_element\":{\"element_unique_id\":\"e\"}}]");if(rows.Count!=1||rows[0].IssueId!=" ABC ")throw new Exception("Array response parse failed.");}
    static void BuildAiWrappedResponseParses(){var rows=BuildAiRevitIssuesClient.ParseResponse("{\"issues\":[{\"issue_id\":\"i\"}]}");if(rows.Count!=1||rows[0].IssueId!="i")throw new Exception("Wrapped response parse failed.");}
    static void BuildAiIssueIdsNormalize(){if(BuildAiRevitIssuesClient.NormalizeIssueId("  AbC ")!="ABC")throw new Exception("Issue id normalization failed.");}
}

