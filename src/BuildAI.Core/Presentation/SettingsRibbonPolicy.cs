using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildAI.Core.Presentation
{
    public static class SettingsRibbonPolicy
    {
        public const string ButtonId="BuildAI_Settings";
        public static bool ShouldCreate(IEnumerable<string> existingItemIds) =>
            !(existingItemIds??Enumerable.Empty<string>()).Any(id=>string.Equals(id,ButtonId,StringComparison.Ordinal));
    }
}
