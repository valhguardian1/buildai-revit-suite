using System;
using Newtonsoft.Json;

namespace BuildAI.Core.Models
{
    public sealed class RevitSessionStart
    {
        [JsonProperty("client_session_id")] public string ClientSessionId { get; set; }
        [JsonProperty("revit_model_uid")] public string RevitModelUid { get; set; }
        [JsonProperty("project_id")] public string ProjectId { get; set; }
        [JsonProperty("project_name")] public string ProjectName { get; set; }
        [JsonProperty("project_number")] public string ProjectNumber { get; set; }
        [JsonProperty("model_title")] public string ModelTitle { get; set; }
        [JsonProperty("model_path")] public string ModelPath { get; set; }
        [JsonProperty("is_workshared")] public bool IsWorkshared { get; set; }
        [JsonProperty("central_model_path")] public string CentralModelPath { get; set; }
        [JsonProperty("revit_version")] public string RevitVersion { get; set; }
        [JsonProperty("plugin_version")] public string PluginVersion { get; set; }
        [JsonProperty("username")] public string Username { get; set; }
        [JsonProperty("machine_name")] public string MachineName { get; set; }
        [JsonProperty("started_at_utc")] public DateTime StartedAtUtc { get; set; }
    }

    public sealed class RevitSessionHeartbeat
    {
        [JsonProperty("client_session_id")] public string ClientSessionId { get; set; }
        [JsonProperty("sequence")] public long Sequence { get; set; }
        [JsonProperty("sent_at_utc")] public DateTime SentAtUtc { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("active_view")] public string ActiveView { get; set; }
        [JsonProperty("active_view_type")] public string ActiveViewType { get; set; }
        [JsonProperty("active_seconds_delta")] public int ActiveSecondsDelta { get; set; }
        [JsonProperty("idle_seconds_delta")] public int IdleSecondsDelta { get; set; }
        [JsonProperty("added_elements_delta")] public int AddedElementsDelta { get; set; }
        [JsonProperty("modified_elements_delta")] public int ModifiedElementsDelta { get; set; }
        [JsonProperty("deleted_elements_delta")] public int DeletedElementsDelta { get; set; }
        [JsonProperty("sync_count_delta")] public int SyncCountDelta { get; set; }
        [JsonProperty("save_count_delta")] public int SaveCountDelta { get; set; }
        [JsonProperty("pending_events")] public int PendingEvents { get; set; }
        [JsonProperty("memory_used_mb")] public long MemoryUsedMb { get; set; }
    }

    public sealed class RevitSessionFinish
    {
        [JsonProperty("client_session_id")] public string ClientSessionId { get; set; }
        [JsonProperty("finished_at_utc")] public DateTime FinishedAtUtc { get; set; }
        [JsonProperty("finish_reason")] public string FinishReason { get; set; }
        [JsonProperty("total_active_seconds")] public long TotalActiveSeconds { get; set; }
        [JsonProperty("total_idle_seconds")] public long TotalIdleSeconds { get; set; }
        [JsonProperty("total_added_elements")] public long TotalAddedElements { get; set; }
        [JsonProperty("total_modified_elements")] public long TotalModifiedElements { get; set; }
        [JsonProperty("total_deleted_elements")] public long TotalDeletedElements { get; set; }
        [JsonProperty("total_sync_count")] public long TotalSyncCount { get; set; }
        [JsonProperty("total_save_count")] public long TotalSaveCount { get; set; }
    }
}
