# BuildAI — Recovered Backend Contract & Reconciliation

Source of truth: extracted from the shipped legacy installer
(`BuildAI_2024.exe`, Inno Setup 6.5.2 → LZMA2 data block → `RevitPluginsOld.dll`),
cross-checked against the contract supplied by the client.

## Endpoints (live)
| Purpose            | Method | URL                                            |
|--------------------|--------|------------------------------------------------|
| Work / elements    | POST   | `https://app.buildai.me/api/revit_work/`       |
| Materials (legacy) | POST   | `https://app.buildai.me/api/revit_materials`   |
| Elements (client)  | POST   | `https://app.buildai.me/api/revit_elems/{model_id}` |
| Local companion    | POST   | `http://127.0.0.1:5000/element`                |

- Auth: `Authorization: Bearer <API_TOKEN>` + `Content-Type: application/json`.
- `{model_id}` for the materials/elements endpoint = `ProjectId` from `revit_work`.

## `/api/revit_work/` body (flat — confirmed by client)
```json
{
  "ItemName": "", "ItemUniqueId": "", "ProjectId": "", "ItemCategory": "",
  "ProjectTitle": "", "ProjectBuildingName": "", "ProjectName": "",
  "ProjectNumber": "", "Method": "", "Level": "", "Time": null, "Username": ""
}
```
`Method` values seen in the legacy plugin: `add_element`, `modify_element`.

## Where the original specification and the live backend disagree
1. **Auth** — specification §3.2 specifies OAuth 2.0 + refresh. Live backend uses a static
   Bearer token. → Built the static-token path (works today) behind
   `ICredentialStore`; OAuth can replace it without touching call sites.
2. **Transport** — specification §4.4 specifies WebSocket. Legacy plugin is REST. → Built
   REST behind `IBuildAiTransport`; a WebSocket transport drops in later.
3. **Elements endpoint** — **RESOLVED (2026-07-10):** current endpoint is
   `POST /api/revit_elems/{model_id}` with `{model_id}` = `ProjectId` in the URL.
   Set as the default in `BuildAiOptions.MaterialsPath`; still configurable so the
   legacy `revit_materials` (or a mock) can be selected. Plugin 2 sends here.
4. **Payload** — using the flat `revit_work` object, not the §3.4 meta-envelope.
   **RESOLVED (2026-07-10):** `Time` is an event timestamp (UTC ISO-8601), not a
   duration.

## Open inputs needed from client
- Is the local `127.0.0.1:5000/element` companion still relevant?
- For Plugin 5 (Forma/APS): Client ID + Secret + Callback URL, scopes
  `data:read data:write data:create account:read`, an Autodesk account with
  project access, the Forma/ACC project + hub/account id, and region (US/EU).
