using Autodesk.Revit.DB;

namespace Plugin4.LinkComparatorAI.Comparison
{
    internal sealed class ElementProxy
    {
        public Element Element { get; set; }
        public string UniqueId { get; set; }
        public int Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public double LevelElevation { get; set; } = double.NaN;
        public XYZ Min { get; set; }
        public XYZ Max { get; set; }
        public XYZ Center => (Min + Max) * 0.5;
        public double Width => Max.X - Min.X;
        public double Depth => Max.Y - Min.Y;
        public double Height => Max.Z - Min.Z;
    }
}
