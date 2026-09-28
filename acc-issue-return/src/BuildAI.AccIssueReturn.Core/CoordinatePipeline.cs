using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BuildAI.AccIssueReturn.Core;

public static class CoordinatePipeline
{
    public const double DefaultRoundTripThresholdMm=10.0; public const double MaxRoundTripErrorMm=DefaultRoundTripThresholdMm;
    public const double ModelBoundsMarginFeet=3.280839895013123;
    public const double GeometryProximityThresholdFeet=3.280839895013123;

    public static bool IsRoundTripAcceptable(double errorMm)=>IsFinite(errorMm)&&errorMm<=MaxRoundTripErrorMm;

    public static CoordinateResult ConvertViewerPointToRevitHost(double[] viewerPoint,IssueCoordinateContext context,string linkName,IReadOnlyList<string>? stages=null)
    {
        var result=new CoordinateResult{SourcePoint=viewerPoint??Array.Empty<double>(),MatchedLink=linkName??""};
        if(viewerPoint==null||viewerPoint.Length!=3||!MatrixMath.IsFinite(viewerPoint))return Reject(result,CoordinateQuality.InvalidCoordinates,"ACC pushpin has non-finite coordinates.");
        if(context==null||!context.IsComplete)return Reject(result,CoordinateQuality.MissingContext,context?.IncompleteReason??"Model/version/viewable coordinate context is incomplete.");

        double[] linkFeet;
        if(context.Contract==CoordinateContract.ViewerLocalFeetPlusGlobalOffset)
        {
            linkFeet=Add(viewerPoint,context.GlobalOffset);
            result.Diagnostics.Add("unitConversion=none; contract already guarantees Revit link internal feet");
        }
        else if(context.Contract==CoordinateContract.ViewerLocalModelUnits)
        {
            if(!context.MetresPerUnit.HasValue)return Reject(result,CoordinateQuality.MissingContext,"Model-unit payload has no known unit scale.");
            var withOffset=Add(viewerPoint,context.GlobalOffset);
            double[] modelUnits;
            if(context.PlacementDirection==CoordinateTransformDirection.ViewerToModel)
                modelUnits=MatrixMath.TransformPointViewerToModel(context.PlacementTransform,withOffset);
            else if(context.PlacementDirection==CoordinateTransformDirection.ModelToViewer)
                modelUnits=MatrixMath.TransformPointViewerToModel(MatrixMath.Inverse(context.PlacementTransform),withOffset);
            else return Reject(result,CoordinateQuality.MissingContext,"Placement transform direction is unverified.");
            var feetPerSourceUnit=context.MetresPerUnit.Value*LengthUnits.FeetPerMeter;
            linkFeet=Scale(modelUnits,feetPerSourceUnit);
            result.Diagnostics.Add("unitConversion=modelUnitsToFeetOnce");
        }
        else return Reject(result,CoordinateQuality.MissingContext,"Coordinate contract is unverified; use element recovery or export a sanitized fixture.");

        var hostFeet=MatrixMath.TransformPointLinkToHost(context.LinkToHost,linkFeet);
        result.RevitPointFeet=hostFeet;
        var linkBack=MatrixMath.TransformPointHostToLink(MatrixMath.Inverse(context.LinkToHost),hostFeet);
        double[] viewerBack;
        if(context.Contract==CoordinateContract.ViewerLocalFeetPlusGlobalOffset)viewerBack=Subtract(linkBack,context.GlobalOffset);
        else
        {
            var sourceUnits=Scale(linkBack,1d/(context.MetresPerUnit!.Value*LengthUnits.FeetPerMeter));
            var beforePlacement=context.PlacementDirection==CoordinateTransformDirection.ViewerToModel
                ? MatrixMath.TransformPointModelToViewer(MatrixMath.Inverse(context.PlacementTransform),sourceUnits)
                : MatrixMath.TransformPointModelToViewer(context.PlacementTransform,sourceUnits);
            viewerBack=Subtract(beforePlacement,context.GlobalOffset);
        }
        result.RoundTripPoint=viewerBack;
        var errorInSourceUnits=MatrixMath.Distance(viewerPoint,viewerBack);
        var mmPerSourceUnit=context.Contract==CoordinateContract.ViewerLocalFeetPlusGlobalOffset?LengthUnits.MillimetersPerFoot:context.MetresPerUnit!.Value*1000d;
        result.RoundTripErrorMm=errorInSourceUnits*mmPerSourceUnit;
        result.MatrixRoundTripPassed=IsRoundTripAcceptable(result.RoundTripErrorMm);
        result.ScaleSanityPassed=ScaleIsSane(context);
        result.Quality=result.MatrixRoundTripPassed?CoordinateQuality.ExactRoundTrip:CoordinateQuality.Rejected;
        result.Diagnostics.Add("roundTripThresholdMm="+DefaultRoundTripThresholdMm.ToString(CultureInfo.InvariantCulture));
        result.Diagnostics.Add("roundTripErrorMm="+result.RoundTripErrorMm.ToString("0.###",CultureInfo.InvariantCulture));
        if(stages!=null)foreach(var stage in stages)result.Diagnostics.Add(stage);
        return result;
    }

    [Obsolete("Use ConvertViewerPointToRevitHost so the source and destination spaces are explicit.")]
    public static CoordinateResult Calculate(double[] source,IssueCoordinateContext context,string linkName,IReadOnlyList<string>? stages=null)=>ConvertViewerPointToRevitHost(source,context,linkName,stages);

    public static void ValidateSpatially(CoordinateResult result,IssueCoordinateContext context,CoordinateValidationEvidence evidence)
    {
        if(result==null)throw new ArgumentNullException(nameof(result));if(context==null)throw new ArgumentNullException(nameof(context));evidence=evidence??new CoordinateValidationEvidence();
        result.ScaleSanityPassed=ScaleIsSane(context)&&MagnitudeIsSane(result.RevitPointFeet);
        result.ModelBoundsPassed=evidence.ModelBoundsFeet!=null&&Inside(result.RevitPointFeet,evidence.ModelBoundsFeet,ModelBoundsMarginFeet);
        var elementDistances=evidence.GeometryElementBoundsFeet?.Where(x=>x!=null).Select(x=>DistanceToBounds(result.RevitPointFeet,x)).ToList();
        result.GeometryProximityPassed=elementDistances!=null&&elementDistances.Count>0?elementDistances.Min()<=GeometryProximityThresholdFeet:evidence.GeometryBoundsFeet!=null&&DistanceToBounds(result.RevitPointFeet,evidence.GeometryBoundsFeet)<=GeometryProximityThresholdFeet;
        result.ExternalIdMatched=evidence.ExternalIdMatched;
        result.ExpectedFixturePassed=evidence.ExpectedRevitPointFeet!=null&&MatrixMath.Distance(result.RevitPointFeet,evidence.ExpectedRevitPointFeet)*LengthUnits.MillimetersPerFoot<=evidence.ExpectedToleranceMm;
        var spatial=result.ExpectedFixturePassed||(result.ModelBoundsPassed&&result.GeometryProximityPassed&&result.ExternalIdMatched);
        result.CoordinateValidated=result.MatrixRoundTripPassed&&result.ScaleSanityPassed&&spatial;
        result.Quality=result.CoordinateValidated?CoordinateQuality.ExactContext:CoordinateQuality.Rejected;
        if(!result.CoordinateValidated)result.Warning="Coordinate validation failed: a reversible matrix pipeline alone is insufficient. Check scale, model bounds, element proximity, externalId, and context provenance.";
    }

    public static CoordinateResult RecoverFromElement(double[] hostPointFeet,string reason)
    {
        return new CoordinateResult{Quality=CoordinateQuality.RecoveredFromElement,RevitPointFeet=(double[])hostPointFeet.Clone(),CoordinateValidated=false,Warning=reason,ScaleSanityPassed=true,ModelBoundsPassed=true,GeometryProximityPassed=true,ExternalIdMatched=true};
    }

    private static bool ScaleIsSane(IssueCoordinateContext context)
    {
        if(context.Contract==CoordinateContract.ViewerLocalFeetPlusGlobalOffset)return context.ViewerCoordinateUnit==LengthUnit.Foot&&context.RevitInternalUnit==LengthUnit.Foot;
        return context.Contract==CoordinateContract.ViewerLocalModelUnits&&context.HasKnownUnits&&context.MetresPerUnit!.Value>0&&context.MetresPerUnit.Value<=1;
    }
    private static bool MagnitudeIsSane(double[] p)=>MatrixMath.IsFinite(p)&&Math.Max(Math.Abs(p[0]),Math.Max(Math.Abs(p[1]),Math.Abs(p[2])))<100000000d;
    private static bool Inside(double[] p,CoordinateBounds b,double margin)=>p[0]>=b.Min[0]-margin&&p[0]<=b.Max[0]+margin&&p[1]>=b.Min[1]-margin&&p[1]<=b.Max[1]+margin&&p[2]>=b.Min[2]-margin&&p[2]<=b.Max[2]+margin;
    private static double DistanceToBounds(double[] p,CoordinateBounds b){var dx=Math.Max(Math.Max(b.Min[0]-p[0],0),p[0]-b.Max[0]);var dy=Math.Max(Math.Max(b.Min[1]-p[1],0),p[1]-b.Max[1]);var dz=Math.Max(Math.Max(b.Min[2]-p[2],0),p[2]-b.Max[2]);return Math.Sqrt(dx*dx+dy*dy+dz*dz);}
    private static CoordinateResult Reject(CoordinateResult r,CoordinateQuality q,string warning){r.Quality=q;r.Warning=warning;r.RoundTripErrorMm=double.NaN;return r;}
    private static double[] Add(double[] a,double[] b)=>new[]{a[0]+b[0],a[1]+b[1],a[2]+b[2]};
    private static double[] Subtract(double[] a,double[] b)=>new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
    private static double[] Scale(double[] a,double s)=>new[]{a[0]*s,a[1]*s,a[2]*s};
    private static bool IsFinite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
}
