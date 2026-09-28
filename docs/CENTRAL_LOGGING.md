# Central logging

BuildAI.Core now contains one logger shared by all five Revit plugins.

## What is recorded

Each entry contains timestamp, severity, plugin and assembly version, Revit version, session ID, correlation ID, operation, project/document context, machine/user, structured data and exception details.

Local files:

- `%LOCALAPPDATA%\BuildAI\logs\buildai-yyyyMMdd.log` — readable log;
- `%LOCALAPPDATA%\BuildAI\logs\buildai-yyyyMMdd.jsonl` — structured log;
- `%LOCALAPPDATA%\BuildAI\logs\remote-spool.jsonl` — undelivered entries.

Logging must never interrupt Revit. Network delivery is asynchronous, batched and retried from the spool.

## Configuration

In `%LOCALAPPDATA%\BuildAI\config.json`:

```json
{
  "BaseUrl": "https://app.buildai.me",
  "RemoteLoggingEnabled": true,
  "LogsPath": "/api/revit-logs/batch",
  "LogBatchSize": 50,
  "LogFlushSeconds": 30,
  "LogRetentionDays": 14
}
```

Authentication uses the existing BuildAI bearer token from Windows Credential Manager. The reference gateway includes `POST /api/revit-logs/batch`.

## Backend contract

Request:

```json
{
  "entries": [
    {
      "timestampUtc": "2026-07-22T08:30:00Z",
      "level": "ERROR",
      "plugin": "Plugin5.ClashFormaIntegration",
      "pluginVersion": "1.0.0.0",
      "revitVersion": "2025",
      "sessionId": "...",
      "correlationId": "...",
      "operation": "PublishModel",
      "projectId": "...",
      "message": "APS translation failed",
      "exceptionType": "System.Net.Http.HttpRequestException"
    }
  ]
}
```

Recommended production behavior: validate token, limit payload size, redact secrets, store logs in a searchable system, and define alerts for `CRITICAL` and repeated `ERROR` events.
