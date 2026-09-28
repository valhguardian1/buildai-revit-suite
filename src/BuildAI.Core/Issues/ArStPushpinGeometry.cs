using System;

namespace BuildAI.Core.Issues
{
    /// <summary>AR-ST Viewer-local pin distance guard, in Viewer feet.</summary>
    public static class ArStPushpinGeometry
    {
        public const double SurfaceOffsetFeet = 0.25;
        public const double NumericalToleranceFeet = 0.01;

        public static bool IsNearFragmentBounds(double x, double y, double z,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            var values = new[] { x, y, z, minX, minY, minZ, maxX, maxY, maxZ };
            foreach (var value in values)
                if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (minX > maxX || minY > maxY || minZ > maxZ) return false;
            double Axis(double value, double min, double max) =>
                value < min ? min - value : value > max ? value - max : 0;
            var dx = Axis(x, minX, maxX);
            var dy = Axis(y, minY, maxY);
            var dz = Axis(z, minZ, maxZ);
            return dx * dx + dy * dy + dz * dz <=
                   (SurfaceOffsetFeet + NumericalToleranceFeet) *
                   (SurfaceOffsetFeet + NumericalToleranceFeet);
        }
    }
}
