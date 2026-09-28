using System;

namespace BuildAI.AccIssueReturn.Core;

public static class CameraPipeline
{
    public static AccCamera ConvertLinkCameraToHost(AccCamera source,Matrix4 linkToHost)
    {
        if(source==null)throw new ArgumentNullException(nameof(source));
        Validate(source.Eye,"eye");Validate(source.Target,"target");Validate(source.Up,"up");
        var eye=MatrixMath.TransformPointLinkToHost(linkToHost,source.Eye);
        var target=MatrixMath.TransformPointLinkToHost(linkToHost,source.Target);
        var upCandidate=MatrixMath.TransformVectorLinkToHost(linkToHost,source.Up);
        var forward=Normalize(Subtract(target,eye),"Camera eye equals target.");
        if(Length(upCandidate)<=1e-12)upCandidate=FallbackAxis(forward);
        var right=Cross(forward,upCandidate);
        if(Length(right)<=1e-10){upCandidate=FallbackAxis(forward);right=Cross(forward,upCandidate);}
        right=Normalize(right,"Camera right vector is not normalizable.");
        var correctedUp=Normalize(Cross(right,forward),"Camera up vector is not normalizable.");
        if(Math.Abs(Dot(forward,correctedUp))>1e-9)throw new InvalidOperationException("Camera basis is not orthogonal.");
        return new AccCamera{Eye=eye,Target=target,Up=correctedUp,FieldOfView=source.FieldOfView,IsPerspective=source.IsPerspective};
    }
    public static bool IsOrthonormal(AccCamera camera,double tolerance=1e-9){try{var f=Normalize(Subtract(camera.Target,camera.Eye),"invalid");var u=Normalize(camera.Up,"invalid");return Math.Abs(Dot(f,u))<=tolerance&&Math.Abs(Length(u)-1)<=tolerance;}catch{return false;}}
    private static void Validate(double[] value,string name){if(value==null||value.Length!=3||!MatrixMath.IsFinite(value))throw new ArgumentException("Camera "+name+" contains non-finite coordinates.");}
    private static double[] FallbackAxis(double[] f){var x=Math.Abs(f[0]);var y=Math.Abs(f[1]);var z=Math.Abs(f[2]);return x<=y&&x<=z?new[]{1d,0d,0d}:y<=z?new[]{0d,1d,0d}:new[]{0d,0d,1d};}
    private static double[] Subtract(double[] a,double[] b)=>new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
    private static double[] Cross(double[] a,double[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    private static double Dot(double[] a,double[] b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
    private static double Length(double[] a)=>Math.Sqrt(Dot(a,a));
    private static double[] Normalize(double[] a,string error){var l=Length(a);if(l<=1e-12||double.IsNaN(l)||double.IsInfinity(l))throw new InvalidOperationException(error);return new[]{a[0]/l,a[1]/l,a[2]/l};}
}
