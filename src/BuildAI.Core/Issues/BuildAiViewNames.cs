namespace BuildAI.Core.Issues
{
    /// <summary>
    /// Canonical names of the Revit 3D views used for APS publication and Issue pushpins.
    /// Keep all publication/viewable matching code aligned with these constants.
    /// </summary>
    public static class BuildAiViewNames
    {
        public const string Coordination = "BuildAI Coordination";
        public const string ArSt = "BuildAI AR-ST";

        public static string ForSource(string source)
        {
            return string.Equals(source, "ar-st", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "arst", System.StringComparison.OrdinalIgnoreCase)
                ? ArSt
                : Coordination;
        }
    }
}
