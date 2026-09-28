# BuildAI ACC Issue Return

This is a separate Revit add-in and installer. It adds its own **ACC Issues** panel to the shared **BuildAI** tab and is not included by the main `BuildAI.RevitSuite` MSI. It supports Revit 2023–2026.

The recovered marker contract is the former BuildAI mechanism: a `DirectShape` in `OST_GenericModel` made from two extruded boxes, with red view overrides. The separate add-in uses one persistent ApplicationDataId per ACC hub/project/container/Issue and stores the original context in Extensible Storage, so a later run updates the same marker.

When an incoming payload explicitly declares feet and proves `viewport.target` or `pivotPoint = position + globalOffset`, `viewer-local position (ft) + per-viewable globalOffset (ft) = Revit link internal point (ft)`; the result then receives exactly one `RevitLinkInstance.GetTotalTransform()` to host coordinates. No unit conversion or ref-point transform is repeated for that contract. Other APS payloads remain unverified until their source contract is proven and can recover through a user-picked linked Revit element after Preview.

`IntegratedWithBuildAI` reads the existing BuildAI configuration and `BuildAI:ApiToken` credential when the main add-in is installed. `StandAlone` uses the same configuration location and its own `BuildAI.AccIssueReturn:ApiToken` credential, so removing the main add-in does not remove or overwrite its credentials. Tokens are never written to the Revit model.

Build on Windows with `pwsh -NoProfile -File .\build.ps1`. The output is `installer\output\BuildAI_AccIssueReturn_1.1_Setup.msi`.
