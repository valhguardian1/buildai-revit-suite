using System;
using Autodesk.Revit.DB;

namespace BuildAI.RevitCompatibility
{
    /// <summary>
    /// Bridges the ElementId API change in Revit 2026 while keeping the
    /// existing BuildAI/backend integer contract unchanged for Revit 2023-2025.
    /// </summary>
    internal static class ElementIdCompatibility
    {
        public static int ToInt32(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2026_OR_GREATER
            return checked((int)id.Value);
#else
            return id.IntegerValue;
#endif
        }
    }
}
