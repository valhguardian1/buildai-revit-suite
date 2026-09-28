using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildAI.Core.AI;
using BuildAI.Core.Logging;
using Newtonsoft.Json;
using Plugin5.ClashFormaIntegration.Models;

namespace Plugin5.ClashFormaIntegration.AI
{
    public sealed class ClashAiConnector
    {
        private readonly AiClient _client = new AiClient();
        private readonly AiCache _cache = new AiCache("clash");

        public async Task AnalyzeAsync(IList<ClashItem> items, Plugin5Settings settings)
        {
            if (items == null || items.Count == 0) return;
            if (string.IsNullOrWhiteSpace(settings.AiEndpoint) || string.IsNullOrWhiteSpace(settings.AiModel)) throw new InvalidOperationException("AI endpoint and model are not configured.");

            var compact = items.Select(x => new
            {
                result_key=x.ResultKey, kind=x.Kind.ToString(), category_a=x.CategoryA, category_b=x.CategoryB,
                element_a=x.ElementA, element_b=x.ElementB, source_a=x.SourceA, source_b=x.SourceB, level=x.Level,
                distance_mm=x.DistanceMm, intersection_volume_mm3=x.IntersectionVolumeMm3,
                location=new{x=x.X,y=x.Y,z=x.Z}, existing_recommendation=x.Recommendation
            }).ToArray();
            var inputJson = JsonConvert.SerializeObject(new { analysis_type="clash", results=compact });
            var hash = AiCache.ComputeHash(settings.AiModel + "|v2|" + inputJson);
            AiAnalysisResult cached;
            if (_cache.TryGet(hash, out cached)) { Apply(items, cached); PluginLog.Info("Clash AI cache hit", new { hash, count=items.Count }); return; }

            var analysisText = await _client.CompleteJsonAsync(settings.AiEndpoint, settings.AiApiKey, settings.AiModel, AiPromptBuilder.AnalysisSystemPrompt, new { analysis_type="clash", results=compact }, "Clash AI analysis").ConfigureAwait(false);
            var recommendations = AiResponseValidator.ParseRecommendations(analysisText, items.Select(x=>x.ResultKey));
            var counts = recommendations.GroupBy(x=>x.Severity).ToDictionary(g=>g.Key,g=>g.Count());
            var summaryPayload = new { analysis_type="clash", totals=new { analyzed=recommendations.Count, recommended=recommendations.Count(x=>x.ShouldCreateIssue), likely_noise=recommendations.Count(x=>x.Assessment=="likely_noise") }, severity_counts=counts, recommendations=recommendations.Select(x=>new{x.ResultKey,x.ShouldCreateIssue,x.Assessment,x.Severity,x.Confidence,x.ResponsibleDiscipline,x.RecommendedAction}), source_results=compact };
            var summaryText = await _client.CompleteJsonAsync(settings.AiEndpoint, settings.AiApiKey, settings.AiModel, AiPromptBuilder.SummarySystemPrompt, summaryPayload, "Clash AI summary").ConfigureAwait(false);
            var result = new AiAnalysisResult { Issues=recommendations, Summary=AiResponseValidator.ParseSummary(summaryText), InputHash=hash };
            _cache.Put(hash,result); Apply(items,result);
        }

        private static void Apply(IList<ClashItem> items, AiAnalysisResult result)
        {
            var map=result.Issues.ToDictionary(x=>x.ResultKey,StringComparer.Ordinal);
            foreach(var item in items)
            {
                AiRecommendation row; if(!map.TryGetValue(item.ResultKey,out row)) continue;
                item.AiIsRealIssue=row.IsRealIssue; item.AiAssessment=row.Assessment; item.AiSeverity=row.Severity;
                item.AiComment=row.Comment; item.AiReason=row.Reason; item.AiConfidence=row.Confidence;
                item.AiRecommendedAction=row.RecommendedAction; item.ResponsibleDiscipline=row.ResponsibleDiscipline;
                item.AiExplanation=row.Comment; item.IsSelectedForIssueCreation=!item.HasApsIssue && row.ShouldCreateIssue;
                ClashSeverity severity; if(Enum.TryParse(row.Severity,true,out severity)) item.Severity=severity;
            }
            var report=Revit.PluginContext.Report; if(report!=null) report.AiSummary=result.Summary+(result.FromCache?"\nLoaded from unchanged-results cache.":"");
        }
    }
}
