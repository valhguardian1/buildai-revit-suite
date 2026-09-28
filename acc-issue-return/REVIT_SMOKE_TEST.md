# Revit smoke test

1. Remove the previous ACC Issue Return version and install the new MSI.
2. Start Revit 2023, 2024, 2025 or 2026 and open a host model with linked models.
3. Open the add-in; verify the shared BuildAI tab and 16/32px icon.
4. Select the ACC project region, one model, and load Issues.
5. Start with an Issue created by BuildAI. Verify composite externalId parsing, the correct link, element resolution, preview, and plausible coordinates.
6. Create or update the marker; open BuildAI ACC Issues and verify camera and Section Box.
7. Select a manual ACC Issue whose camera target differs from the pushpin. Verify the mismatch is a warning and successful spatial validation still permits import.
8. Select several Issues from one viewable and then another link/model. Verify logs show context and bounds cache reuse and distinct transforms/offsets.
9. Save the sanitized diagnostic log; do not include access or refresh tokens, cookies, authorization headers, or personal data.