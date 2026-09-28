using System;

namespace BuildAI.AccIssueReturn.Core;

public static class MatrixMath
{
    public static double[] TransformPoint(Matrix4 matrix,double[] p)
    {
        if (matrix?.M == null || matrix.M.Length != 16 || p == null || p.Length != 3) throw new ArgumentException("A 4x4 matrix and a 3D point are required.");
        var m=matrix.M; var w=m[3]*p[0]+m[7]*p[1]+m[11]*p[2]+m[15]; if(Math.Abs(w)<1e-12) w=1;
        return new[]{(m[0]*p[0]+m[4]*p[1]+m[8]*p[2]+m[12])/w,(m[1]*p[0]+m[5]*p[1]+m[9]*p[2]+m[13])/w,(m[2]*p[0]+m[6]*p[1]+m[10]*p[2]+m[14])/w};
    }
    public static double[] TransformVector(Matrix4 matrix,double[] direction)
    {
        if (matrix?.M == null || matrix.M.Length != 16 || direction == null || direction.Length != 3) throw new ArgumentException("A 4x4 matrix and a 3D vector are required.");
        var m=matrix.M;
        return new[]{m[0]*direction[0]+m[4]*direction[1]+m[8]*direction[2],m[1]*direction[0]+m[5]*direction[1]+m[9]*direction[2],m[2]*direction[0]+m[6]*direction[1]+m[10]*direction[2]};
    }
    public static double[] TransformPointViewerToModel(Matrix4 viewerToModel,double[] viewerPoint)=>TransformPoint(viewerToModel,viewerPoint);
    public static double[] TransformPointModelToViewer(Matrix4 modelToViewer,double[] modelPoint)=>TransformPoint(modelToViewer,modelPoint);
    public static double[] TransformPointLinkToHost(Matrix4 linkToHost,double[] linkPoint)=>TransformPoint(linkToHost,linkPoint);
    public static double[] TransformPointHostToLink(Matrix4 hostToLink,double[] hostPoint)=>TransformPoint(hostToLink,hostPoint);
    public static double[] TransformVectorLinkToHost(Matrix4 linkToHost,double[] linkVector)=>TransformVector(linkToHost,linkVector);
    public static Matrix4 Multiply(Matrix4 a,Matrix4 b)
    { var r=new double[16]; for(var row=0;row<4;row++) for(var col=0;col<4;col++) for(var k=0;k<4;k++) r[col*4+row]+=a.M[k*4+row]*b.M[col*4+k]; return new Matrix4{M=r}; }
    public static bool IsFinite(double[]? values)=>values!=null&&values.Length>0&&Array.TrueForAll(values,x=>!double.IsNaN(x)&&!double.IsInfinity(x));
    public static bool IsInvertible(Matrix4? x)=>x?.M is {Length:16} && IsFinite(x.M) && Math.Abs(Determinant(x))>1e-12;
    public static bool IsLinearPartInvertible(Matrix4? x)=>x?.M is {Length:16}&&IsFinite(x.M)&&Math.Abs(LinearDeterminant(x))>1e-12;
    public static double LinearDeterminant(Matrix4 x){var m=x.M;return m[0]*(m[5]*m[10]-m[9]*m[6])-m[4]*(m[1]*m[10]-m[9]*m[2])+m[8]*(m[1]*m[6]-m[5]*m[2]);}
    public static double Determinant(Matrix4 x){var m=x.M;return m[0]*(m[5]*(m[10]*m[15]-m[14]*m[11])-m[9]*(m[6]*m[15]-m[14]*m[7])+m[13]*(m[6]*m[11]-m[10]*m[7]))-m[4]*(m[1]*(m[10]*m[15]-m[14]*m[11])-m[9]*(m[2]*m[15]-m[14]*m[3])+m[13]*(m[2]*m[11]-m[10]*m[3]))+m[8]*(m[1]*(m[6]*m[15]-m[14]*m[7])-m[5]*(m[2]*m[15]-m[14]*m[3])+m[13]*(m[2]*m[7]-m[6]*m[3]))-m[12]*(m[1]*(m[6]*m[11]-m[10]*m[7])-m[5]*(m[2]*m[11]-m[10]*m[3])+m[9]*(m[2]*m[7]-m[6]*m[3]));}
    public static double Distance(double[] a,double[] b)=>Math.Sqrt(Math.Pow(a[0]-b[0],2)+Math.Pow(a[1]-b[1],2)+Math.Pow(a[2]-b[2],2));
    public static Matrix4 Inverse(Matrix4 x)
    { var a=(double[])x.M.Clone();var inv=Matrix4.IdentityArray();for(var i=0;i<4;i++){var pivot=i;for(var r=i+1;r<4;r++)if(Math.Abs(a[i*4+r])>Math.Abs(a[i*4+pivot]))pivot=r;if(Math.Abs(a[i*4+pivot])<1e-12)throw new InvalidOperationException("Transform is singular.");for(var c=0;c<4;c++){(a[c*4+i],a[c*4+pivot])=(a[c*4+pivot],a[c*4+i]);(inv[c*4+i],inv[c*4+pivot])=(inv[c*4+pivot],inv[c*4+i]);}var d=a[i*4+i];for(var c=0;c<4;c++){a[c*4+i]/=d;inv[c*4+i]/=d;}for(var r=0;r<4;r++)if(r!=i){var f=a[i*4+r];for(var c=0;c<4;c++){a[c*4+r]-=f*a[c*4+i];inv[c*4+r]-=f*inv[c*4+i];}}}return new Matrix4{M=inv}; }
}
