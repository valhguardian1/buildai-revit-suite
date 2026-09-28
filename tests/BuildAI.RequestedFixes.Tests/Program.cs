using BuildAI.Core.Issues;
using Plugin4.LinkComparatorAI.Issues;
using BuildAI.Core.ViewerProbe;
using BuildAI.Core.Presentation;

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Test("summary shows three factual lines and toggles only when needed", () =>
        {
            Assert(SummaryLines.Visible("one\ntwo\nthree\nfour",false)=="one"+Environment.NewLine+"two"+Environment.NewLine+"three");
            Assert(SummaryLines.HasMore("one\ntwo\nthree\nfour"));
            Assert(!SummaryLines.HasMore("one\ntwo\nthree"));
            Assert(SummaryLines.Visible("",false)=="");
        });
        Test("export format follows a manually entered extension", () =>
        {
            Assert(TableExport.Resolve("results.csv",1).Format==TableExportFormat.Csv);
            Assert(TableExport.Resolve("results.xlsx",2).Format==TableExportFormat.Xlsx);
            Assert(TableExport.Resolve("results",2).Path.EndsWith(".csv",StringComparison.OrdinalIgnoreCase));
        });
        Test("settings button is created once for either load order", () =>
        {
            Assert(SettingsRibbonPolicy.ShouldCreate(Array.Empty<string>()));
            Assert(!SettingsRibbonPolicy.ShouldCreate(new[]{"Other","BuildAI_Settings"}));
        });
        Test("XLSX and CSV preserve the same Unicode rows and text identifiers", () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"buildai-export-test-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var headers=new[]{"Issue ID","Описание","תיאור"};
                IReadOnlyList<string>[] rows={new[]{"12345678901234567890","текст, \"пример\"\nстрока","בדיקה"}};
                var csv=Path.Combine(folder,"sample.csv");var xlsx=Path.Combine(folder,"sample.xlsx");
                TableExport.Write(csv,TableExportFormat.Csv,headers,rows);
                TableExport.Write(xlsx,TableExportFormat.Xlsx,headers,rows);
                var bytes=File.ReadAllBytes(csv);Assert(bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF);
                var csvText=File.ReadAllText(csv);Assert(csvText.Contains("12345678901234567890")&&csvText.Contains("текст, \"\"пример\"\""));
                using var archive=System.IO.Compression.ZipFile.OpenRead(xlsx);
                var sheet=archive.GetEntry("xl/worksheets/sheet1.xml");Assert(sheet!=null);
                using var reader=new StreamReader(sheet.Open());var xml=reader.ReadToEnd();
                Assert(xml.Contains("12345678901234567890")&&xml.Contains("בדיקה")&&xml.Contains("autoFilter")&&xml.Contains("state=\"frozen\""));
            }
            finally{Directory.Delete(folder,true);}
        });
        Test("new and reopened windows start disabled", () =>
        {
            Assert(!new ChangeCalculationModePolicy().Enabled);
            Assert(!new ChangeCalculationModePolicy().Enabled);
        });
        Test("only user action enables and operation consumes the value", () =>
        {
            var policy = new ChangeCalculationModePolicy();
            policy.ApplySettings(true);
            Assert(!policy.Enabled);
            policy.SetByUser(true);
            Assert(policy.Enabled && policy.ConsumeForOperation());
            Assert(!policy.Enabled && !policy.ConsumeForOperation());
            policy.SetByUser(true);
            policy.ResetOperation();
            Assert(!policy.Enabled);
        });
        Test("Location Details priority and Unicode are preserved", () =>
        {
            const string hebrew = "\u200Fמרתף 2 מעודכן";
            Assert(IssueLocationDetailsResolver.Resolve(hebrew, "P", "S", "T — F").Value == hebrew);
            Assert(IssueLocationDetailsResolver.Resolve(null, "Этаж -2", "S", "T — F").Value == "Этаж -2");
            Assert(IssueLocationDetailsResolver.Resolve(null, null, "Level 03", "T — F").Value == "Level 03");
            var fallback = IssueLocationDetailsResolver.Resolve(null, null, null, "Pipes — \u200Fמרתף 2 מעודכן");
            Assert(fallback.Source == IssueLocationDetailsSource.TitleFallback && fallback.Value == hebrew);
            Assert(IssueLocationDetailsResolver.Resolve(null, null, null, "BuildAI Coordination").Value == null);
        });
        Test("existing Issues merge by exact result_key and retain history", () =>
        {
            var rows = new List<TestRow>
            {
                new() { ResultKey = "same-title-key-A", IsSelectedForIssueCreation = true },
                new() { ResultKey = "same-title-key-B", IsSelectedForIssueCreation = false }
            };
            var existing = new List<ExistingRevitIssueDto>
            {
                new() { ResultKey="same-title-key-A", IssueId="i1", DisplayId=17, IssueStatus="open", IssueUrl="https://acc/i1", CreatedAt=DateTime.UtcNow },
                new() { ResultKey="historical-key", IssueId="i2", DisplayId=18, IssueStatus="open", IssueUrl="https://acc/i2", CreatedAt=DateTime.UtcNow }
            };
            ExistingIssueMergeService.Merge(rows, existing, x => new TestRow { ResultKey=x.ResultKey });
            Assert(rows.Count == 3);
            Assert(rows.Single(x => x.ResultKey == "same-title-key-A").CreationState == IssueCreationState.AlreadyCreated);
            Assert(!rows.Single(x => x.ResultKey == "same-title-key-A").IsSelectedForIssueCreation);
            Assert(rows.Single(x => x.ResultKey == "same-title-key-B").CreationState == IssueCreationState.New);
            Assert(!rows.Single(x => x.ResultKey == "same-title-key-B").IsSelectedForIssueCreation);
            var historical = rows.Single(x => x.ResultKey == "historical-key");
            Assert(historical.CreationState == IssueCreationState.PreviouslyCreatedNotDetected);
            Assert(historical.ApsIssueUrl == "https://acc/i2");
            ExistingIssueMergeService.Merge(rows, existing, x => new TestRow { ResultKey=x.ResultKey });
            Assert(rows.Count == 3 && rows.Count(x => x.ResultKey == "historical-key") == 1);
        });
        Test("Autodesk REST/CDN host difference accepts exact derivative identity", () =>
        {
            var (probe, context) = MatchingDerivative();
            probe.DerivativeEndpoint = "https://cdn.derivative.autodesk.com";
            context.DerivativeRegion = "us";
            ArStProbeDerivativeGuard.Validate(probe, context, "key", true, null);
        });
        Test("different derivative URN rejects one row", () =>
        {
            var (probe, context) = MatchingDerivative();
            probe.LoadedModelUrn = "urn:other-version";
            AssertThrows(() => ArStProbeDerivativeGuard.Validate(probe, context, "key", true, null));
        });
        Test("different viewable GUID rejects one row", () =>
        {
            var (probe, context) = MatchingDerivative();
            probe.LoadedViewableGuid = "other-guid";
            AssertThrows(() => ArStProbeDerivativeGuard.Validate(probe, context, "key", true, null));
        });
        Test("legacy SVF without OTG accepts verified externalId and fragment", () =>
        {
            var (probe, context) = MatchingDerivative();
            probe.IsOtg = false;
            probe.OtgNodeFound = false;
            ArStProbeDerivativeGuard.Validate(probe, context, "key", true, null);
        });
        Test("missing fragment or reverse externalId rejects derivative", () =>
        {
            var (probe, context) = MatchingDerivative();
            probe.FragmentIds = Array.Empty<int>();
            AssertThrows(() => ArStProbeDerivativeGuard.Validate(probe, context, "key", true, null));
            probe.FragmentIds = new[] { 4 };
            AssertThrows(() => ArStProbeDerivativeGuard.Validate(probe, context, "key", false, null));
        });
        Test("38–65 mm surface offsets pass; metre scale offsets fail", () =>
        {
            static bool Near(double x) => ArStPushpinGeometry.IsNearFragmentBounds(
                x, 0.5, 0.5, 0, 0, 0, 1, 1, 1);
            Assert(Near(1.125));
            Assert(Near(1.212));
            Assert(!Near(13.3124));
            Assert(!Near(17.0989));
            Assert(!Near(19.4158));
            Assert(!Near(double.NaN) && !Near(double.PositiveInfinity));
        });
        Console.WriteLine($"Requested C# policies: {_passed} tests passed.");
    }

    private static (ViewerCoordinateProbeResult, ApsPushpinContext) MatchingDerivative() =>
        (new ViewerCoordinateProbeResult {
            DbId = 123, ResolvedExternalId = "link/element", FragmentIds = new[] { 4 },
            LoadedModelUrn = "urn:exact-derivative", LoadedViewableGuid = "view-guid",
            LoadedViewableId = "view-id", LoadedViewableName = "BuildAI AR-ST"
        }, new ApsPushpinContext {
            DerivativeUrn = "exact-derivative", VersionUrn = "urn:version:31",
            DocumentUrn = "urn:lineage", ViewableGeometryGuid = "view-guid",
            ViewableId = "view-id", ViewableName = "BuildAI AR-ST", DerivativeRegion = "us"
        });
    private static void AssertThrows(Action body) {
        try { body(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected identity rejection.");
    }

    private static void Test(string name, Action body) { body(); _passed++; Console.WriteLine("[OK] " + name); }
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }

    private sealed class TestRow : IIssueCreationRow
    {
        public string ResultKey { get; set; } = "";
        public string ApsIssueId { get; set; } = "";
        public int? ApsIssueDisplayId { get; set; }
        public string ApsIssueStatus { get; set; } = "";
        public string ApsIssueUrl { get; set; } = "";
        public DateTime? ApsIssueCreatedAt { get; set; }
        public bool IsSelectedForIssueCreation { get; set; }
        public IssueCreationState CreationState { get; set; }
    }
}

namespace BuildAI.Core.Issues
{
    public sealed class ApsPushpinContext {
        public string DerivativeUrn { get; set; } = "";
        public string VersionUrn { get; set; } = "";
        public string DocumentUrn { get; set; } = "";
        public string ViewableGeometryGuid { get; set; } = "";
        public string ViewableId { get; set; } = "";
        public string ViewableName { get; set; } = "";
        public string DerivativeRegion { get; set; } = "";
    }
    public sealed class ExistingRevitIssueDto
    {
        public string ResultKey { get; set; } = "";
        public string IssueId { get; set; } = "";
        public int? DisplayId { get; set; }
        public string IssueStatus { get; set; } = "";
        public string IssueUrl { get; set; } = "";
        public DateTime? CreatedAt { get; set; }
    }
}

namespace BuildAI.Core.ViewerProbe
{
    public sealed class ViewerCoordinateProbeResult {
        public int DbId { get; set; }
        public string ResolvedExternalId { get; set; } = "";
        public int[] FragmentIds { get; set; } = Array.Empty<int>();
        public string LoadedModelUrn { get; set; } = "";
        public string LoadedViewableGuid { get; set; } = "";
        public string LoadedViewableId { get; set; } = "";
        public string LoadedViewableName { get; set; } = "";
        public string DerivativeEndpoint { get; set; } = "";
        public string ApiFlavor { get; set; } = "";
        public bool IsOtg { get; set; }
        public bool OtgNodeFound { get; set; }
        public string OtgStatus { get; set; } = "";
        public int SceneFragmentCount { get; set; }
        public int SceneNodeCount { get; set; }
    }
}
