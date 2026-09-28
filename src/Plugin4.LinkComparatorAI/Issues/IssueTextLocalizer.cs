using System;
using System.Globalization;
using BuildAI.Core.Localization;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.Issues
{
    /// <summary>
    /// Builds Issue titles and descriptions from resource templates.
    ///
    /// Two rules shape this class. The text follows the pattern
    /// "[category of element 1] in [model of element 1] does not match
    /// [category of element 2] in [model of element 2]", and it exists only in
    /// English and Hebrew, whatever language Revit itself is running in. Nothing here
    /// interpolates a value that came from Revit's UI: categories arrive through
    /// CategoryNaming and source names are filtered at the engine.
    ///
    /// The previous implementation returned row.Description as the title, which in
    /// English produced "Position: 1234 mm; elevation: 56 mm; size: 200 mm." - a
    /// measurement dump rather than a statement of what is wrong.
    /// </summary>
    internal static class IssueTextLocalizer
    {
        // Unicode isolates. Without them, a Latin model name or an integer embedded in
        // a Hebrew sentence is reordered by the bidi algorithm and renders detached
        // from the phrase it belongs to - "ID 1234567" appears reversed. The isolate
        // pins each substituted value inside its own direction run.
        private const string FirstStrongIsolate = "\u2068";
        private const string PopDirectionalIsolate = "\u2069";

        private static string Iso(string value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) return Loc.T("Issue_UnknownValue");
            return Loc.IsRightToLeft ? FirstStrongIsolate + text + PopDirectionalIsolate : text;
        }

        public static string IssueTitle(ComparisonIssue row)
        {
            if (row == null) return "";

            if (row.CheckType == ComparatorCheckType.RoomHeight)
                return string.Format(Loc.T("Issue_Title_RoomHeight"),
                    Iso(row.ArchitecturalCategory), Iso(row.ArchitecturalSourceName), Iso(row.Level));

            if (!row.HasStructuralElement && row.HasArchitecturalElement)
                return string.Format(Loc.T("Issue_Title_NoStructural"),
                    Iso(row.ArchitecturalCategory), Iso(row.ArchitecturalSourceName),
                    Iso(row.StructuralSourceName));

            if (!row.HasArchitecturalElement && row.HasStructuralElement)
                return string.Format(Loc.T("Issue_Title_NoArchitectural"),
                    Iso(row.StructuralCategory), Iso(row.StructuralSourceName),
                    Iso(row.ArchitecturalSourceName));

            return string.Format(Loc.T("Issue_Title_ArSt"),
                Iso(row.ArchitecturalCategory), Iso(row.ArchitecturalSourceName),
                Iso(row.StructuralCategory), Iso(row.StructuralSourceName));
        }

        /// <summary>
        /// The description restates the finding and adds only what a coordinator acts
        /// on: level and the size of the discrepancy. Run ids and timestamps are
        /// appended by the workflow, not mixed in here.
        /// </summary>
        public static string Details(ComparisonIssue row)
        {
            if (row == null) return "";
            var title = IssueTitle(row);

            if (row.CheckType == ComparatorCheckType.RoomHeight)
                return title + " " + string.Format(Loc.T("Issue_Body_RoomHeight"),
                    Iso(Level(row)), Number(row.DeltaMm));

            if (!row.HasStructuralElement || !row.HasArchitecturalElement)
                return title + " " + string.Format(Loc.T("Issue_Body_Missing"), Iso(Level(row)));

            return title + " " + string.Format(Loc.T("Issue_Body_ArSt"),
                Iso(Level(row)), Number(row.DeltaMm));
        }

        /// <summary>
        /// Numbers are formatted invariantly on purpose. These strings go to Autodesk
        /// and to the BuildAI backend, where a comma decimal separator from a European
        /// regional format would be read as a different value - the same class of
        /// defect as the refPointTransformation parse fixed in 7.0.5.
        /// </summary>
        private static string Number(double value)
        {
            var text = Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
            return Loc.IsRightToLeft ? FirstStrongIsolate + text + PopDirectionalIsolate : text;
        }

        /// <summary>
        /// Root cause is matched against the project's ACC catalogue, so it must stay
        /// a stable identifier rather than a translated sentence: translating it would
        /// mean the same finding resolves to a different rootCauseId depending on the
        /// operator's language, or to none at all.
        /// </summary>
        public static string RootCause(ComparisonIssue row)
        {
            if (row == null) return "AR-ST: model discrepancy";
            if (!row.HasStructuralElement && row.HasArchitecturalElement) return "AR-ST: no structural match";
            if (!row.HasArchitecturalElement && row.HasStructuralElement) return "AR-ST: no architectural match";
            if (row.CheckType == ComparatorCheckType.RoomHeight) return "AR-ST: room height out of range";
            return "AR-ST: model discrepancy";
        }

        public static string Level(ComparisonIssue row)
            => string.IsNullOrWhiteSpace(row?.Level) ? Loc.T("Issue_LevelUnknown") : row.Level;
    }
}
