using System;
using System.Linq;

namespace BuildAI.AccIssueReturn.Core;

public enum MatrixSourceFormat { None, ViewerColumnMajor4x4, AecRefPointColumnMajor4x3, RevitColumnMajor4x4 }
public enum CoordinateTransformDirection { Unverified, ModelToViewer, ViewerToModel, LinkToHost, HostToLink }

public static class MatrixFormats
{
    public static Matrix4 Parse(double[] values,MatrixSourceFormat format)
    {
        if(values==null)throw new ArgumentNullException(nameof(values));
        double[] matrix;
        switch(format)
        {
            case MatrixSourceFormat.AecRefPointColumnMajor4x3:
                if(values.Length!=12)throw new FormatException("AEC refPointTransformation must contain exactly 12 values.");
                matrix=new[]{values[0],values[1],values[2],0d,values[3],values[4],values[5],0d,values[6],values[7],values[8],0d,values[9],values[10],values[11],1d};
                break;
            case MatrixSourceFormat.ViewerColumnMajor4x4:
            case MatrixSourceFormat.RevitColumnMajor4x4:
                if(values.Length!=16)throw new FormatException("The 4x4 transform must contain exactly 16 values.");
                matrix=(double[])values.Clone();
                break;
            default: throw new ArgumentException("A concrete matrix source format is required.",nameof(format));
        }
        if(!MatrixMath.IsFinite(matrix))throw new FormatException("Transform contains a non-finite value.");
        if(Math.Abs(matrix[3])>1e-10||Math.Abs(matrix[7])>1e-10||Math.Abs(matrix[11])>1e-10||Math.Abs(matrix[15]-1d)>1e-10)
            throw new FormatException("Transform is not affine: expected homogeneous bottom row [0,0,0,1].");
        var result=new Matrix4{M=matrix};
        if(!MatrixMath.IsLinearPartInvertible(result))throw new FormatException("Transform linear part is singular.");
        return result;
    }

    public static string DeterminantText(Matrix4 matrix)=>MatrixMath.LinearDeterminant(matrix).ToString("0.############",System.Globalization.CultureInfo.InvariantCulture);
}
