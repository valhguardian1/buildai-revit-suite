# Autodesk Forma integration requirements

This document defines what BuildAI needs before implementing bidirectional synchronization with Autodesk Forma Issues.

## 1. Autodesk-side prerequisites

1. An Autodesk Forma/ACC account and a target hub/project where Issues is enabled.
2. An APS application in Autodesk Developer Hub.
3. The APS application must be added in the hub administration under Custom Integrations so it is allowed to access Forma services.
4. Decide authentication mode:
   - three-legged OAuth for actions performed as the signed-in Autodesk user;
   - two-legged OAuth with the integration provisioned by an administrator for server-side automation.
5. A public HTTPS callback URL for OAuth, when three-legged OAuth is used.
6. A public HTTPS webhook endpoint for issue events.

## 2. Identifiers and settings BuildAI must store

- Autodesk account/hub ID;
- Autodesk project ID;
- BuildAI project ↔ Autodesk project mapping;
- issue type/subtype IDs;
- permitted statuses;
- root-cause category IDs, if used;
- custom attribute definition IDs and allowed values;
- project user IDs for assignment;
- default assignee and due-date policy;
- BuildAI clash ID ↔ Forma issue ID mapping;
- source model/version/element identifiers used to locate the problem.

These values must be read from the project APIs rather than hard-coded because issue types, statuses and custom fields vary by project.

## 3. Minimal API operations

BuildAI backend needs to:

- read project issue settings and user permissions;
- list issue types and custom attributes;
- list project users for assignment;
- create an issue from a BuildAI clash;
- read one or many issues;
- update status, assignee, due date, description and supported attributes;
- create and read comments;
- optionally attach or link a snapshot/report;
- subscribe to issue webhooks;
- process create/update/delete/comment events idempotently.

## 4. Data mapping to agree before coding

For every clash/issue define:

- title template;
- description template;
- severity → Forma issue type/status mapping;
- responsible discipline → assignee/company mapping;
- Revit element IDs, model URN and viewer location payload;
- whether one issue represents one clash or a grouped set;
- rules for reopening a resolved issue when the clash reappears;
- conflict resolution when BuildAI and Forma are edited at the same time.

## 5. Security and operations

- APS client secret and refresh tokens stay only on the BuildAI backend, never in the Revit DLL.
- Encrypt tokens at rest and support token revocation.
- Verify webhook authenticity according to Autodesk's current webhook requirements and reject replayed/duplicate events.
- Keep an idempotency key and event log for every sync operation.
- Add retry with exponential backoff and a dead-letter queue.
- Log request correlation IDs, HTTP status and Autodesk request IDs without logging access tokens or file contents.

## 6. Decisions required from the product owner

Before implementation we need:

1. Which Autodesk product/project will be the pilot.
2. Whether users should sign in with Autodesk or all actions should run under a service integration.
3. Which Forma issue type and custom fields BuildAI will use.
4. Who can create, assign, close and reopen issues.
5. Whether synchronization is one-way or bidirectional.
6. Whether comments and attachments are included in the first release.
7. Whether issues must open at the exact model location in Autodesk/BuildAI Viewer.

## 7. Recommended first release

Phase 1:

- server-side Autodesk connection;
- project selection and mapping;
- creation of one Forma issue from one selected BuildAI clash;
- storage of the returned issue ID;
- status and assignee synchronization;
- webhook handling for issue updates;
- comments in the second iteration.

The Revit plugin should call BuildAI only. BuildAI backend should call APS/Forma, ensuring that Autodesk credentials and integration logic remain centralized.
