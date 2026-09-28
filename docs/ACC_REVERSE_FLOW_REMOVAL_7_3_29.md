# ACC Issues to Revit removal map

Audited the supplied current source before edits; no archived implementation was used.

| Layer | Before removal | Decision |
|---|---|---|
| Ribbon/UI | Open Issues; P5_Pushpins; Btn_CorrectPushpins | Removed button and localized labels |
| ExternalCommand | CorrectPushpinsCommand in PushpinCorrectionCommand.cs | Deleted |
| Availability | No dedicated availability class/property | Nothing separate to remove |
| Handler | Execute and Calibrate directly inside IExternalCommand; window GoToSelected | Deleted; shared ModelActionHandler retained |
| Services | PushpinCorrectionService, calibration/store; reverse-only PushpinLocator resolver helpers | Deleted exclusive code |
| DTO/models | ApsIssuePushpin, PushpinCorrectionEntry/Outcome, PushpinOffsetSample/Calibration, IssueElementRef, ResolvedIssueElement | Deleted |
| API | GetApsIssuePushpinsAsync, ParseIssuePushpin, ReadCoordinate, GetIssuesByAutodeskIdAsync in IssueIntegrationClient.Pushpins.cs | Deleted partial file; main client retained |
| Coordinates | Unit detection, reverse calibration delta, host resolving/centroid/bounds helpers | Deleted exclusive reverse conversions; outgoing frame and anchor unchanged |
| Localization | Btn_CorrectPushpins; P5_Pushpin*; exclusive P5_ColIssueTitle/Status/Correction/Location | Removed in both resx; shared P5_ColIssueNumber and Btn_Close retained |
| Icon | results | Retained: used by ordinary Results buttons |
| Registration | Plugin5 Application.OnStartup; linked Compile items in Plugin4/Plugin5 projects | Removed button and exclusive shared compile entries |
| Tests | Test-IssueNavigator.ps1 tested incoming navigation | Replaced with absence check; retained in build.ps1 |
| Addin/manifest | Only Application entries, no independent CorrectPushpins entry | Existing application manifests retained; staged output checked |
| Logging | Pushpin correction, AUTODESK ISSUES READ, PUSHPIN CALIBRATION, PUSHPIN OFFSET IS NOT CONSTANT | Removed with exclusive methods |

Shared IssueIntegrationClient, ExistingRevitIssueDto/RevitIssueElementDto, APS clients, PushpinFrame, object resolver and Viewer probe remain for outgoing creation, metadata/deduplication, publication and selection. PushpinLocator.FocusOn and EnsureNamed3D remain byte-identical for local result navigation.

Search covered ACC, Issue, Pushpin, Import, Pull, Sync, Load, Download and Revit in current product source, tools, installer and harness. Remaining reads load outgoing metadata, deduplication records, APS catalogs, geometry and Viewer state. Remaining synchronization publishes Revit data or acknowledges created Issues in BuildAI. No user-accessible inbound ACC pushpin route remains.

Primary camera search remains the original direction sequence at 0.70 m (within the 0.60–0.80 m contract). Fallback uses the same helper at 0.55, 0.50, 0.45, 0.40, 0.35, 0.30, 0.25 m. No target/anchor mutation, fit-to-view, identity, objectSet, globalOffset or AR-ST/MEP changes were introduced.

Known supplied-source limits: Test-ArStIssuePayload.ps1 expects an older runtime-id contract and fails unchanged; its four source inputs match supplied SHA-256 hashes. Clash creation accepts a successful 2xx response with an ID, not exclusively HTTP 201. These pre-existing behaviors were preserved. Real Revit/ACC HTTP 201 and visual checks require a live model session.
