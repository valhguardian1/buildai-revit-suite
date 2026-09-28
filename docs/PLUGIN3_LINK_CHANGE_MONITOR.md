# Plugin 3 — Link Change Monitor

## Implemented scope

- Snapshot all non-type elements of every loaded `RevitLinkInstance`.
- Persist one snapshot per host project + link instance.
- Compare by stable linked-element `UniqueId`.
- Classify changes as `Added`, `Modified`, or `Deleted`.
- Modified fingerprint includes category, name, type, level, all readable
  parameters, and the bounding box transformed into host coordinates.
- Trigger checks manually, on document open, and after a Revit link instance/type
  is modified in the host document.
- Modeless results UI with filtering, search, linked-element selection and reset.
- Styled Excel export modelled after the supplied estimate workbook.
- Local JSONL audit journal.

## Results layout

The Revit window and Excel report use a bill-of-quantities style layout:

- blue table header;
- summary counters for added, modified and deleted elements;
- grouping by linked model, level and category;
- element ID and description columns;
- element type and changed parameter list;
- side-by-side `Before` / `After` values;
- green/yellow/red row colours for added/modified/deleted changes;
- model subtotals and a grand total.

The Excel report is generated directly as `.xlsx` without Microsoft Excel and without
an additional third-party spreadsheet dependency.

## First-run behavior

The first comparison creates a baseline and reports no changes. A later comparison
reports the delta and replaces the baseline with the current snapshot.

## Storage

`%LOCALAPPDATA%\\BuildAI\\link-monitor\\snapshots\\*.json`

`%LOCALAPPDATA%\\BuildAI\\link-monitor\\changes.jsonl`

## Known limitation

A deleted linked element cannot be highlighted because it no longer exists in the
current linked document. It remains in the results and export with the last known
name, category, level, type, parameters and element id.
