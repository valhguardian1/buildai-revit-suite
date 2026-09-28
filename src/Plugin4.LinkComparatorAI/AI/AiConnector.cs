using System;
using System.Linq;
using System.Threading.Tasks;
using BuildAI.Core.AI;
using BuildAI.Core.Logging;
using BuildAI.Core.Localization;
using Newtonsoft.Json;
using Plugin4.LinkComparatorAI.Models;

namespace Plugin4.LinkComparatorAI.AI
{
    public sealed class AiConnector
    {
        private readonly AiClient _client=new AiClient();
        private readonly AiCache _cache=new AiCache("ar-st");

        public async Task AnalyzeAsync(ComparisonReport report, ComparatorSettings settings)
        {
            if(!settings.AiEnabled) throw new InvalidOperationException("AI endpoint and model are not configured.");
            var compact=report.Issues.Select(x=>new{result_key=x.ResultKey,check_type=x.CheckType.ToString(),level=x.Level,category=x.Category,title=x.Title,description=x.Description,delta_mm=x.DeltaMm,ar_element_id=x.ArchitecturalElementId,st_element_id=x.StructuralElementId,existing_recommendation=x.Recommendation}).ToArray();
            var inputJson=JsonConvert.SerializeObject(new{analysis_type="ar-st",architectural_model=report.ArchitecturalModel,structural_model=report.StructuralModel,results=compact});
            var languageInstruction = LanguageSettings.CurrentLanguage == "he"
                ? "\nAll human-readable values (reason, comment, recommended_action and summary) must be written in Hebrew. Keep JSON property names and allowed enum values unchanged."
                : "\nAll human-readable values must be written in English.";
            var hash=AiCache.ComputeHash(settings.AiModel+"|v3|"+LanguageSettings.CurrentLanguage+"|"+inputJson); AiAnalysisResult cached;
            if(_cache.TryGet(hash,out cached)){Apply(report,cached);PluginLog.Info("AR-ST AI cache hit",new{hash,count=compact.Length});return;}
            var text=await _client.CompleteJsonAsync(settings.AiEndpoint,settings.AiApiKey,settings.AiModel,AiPromptBuilder.AnalysisSystemPrompt+languageInstruction,new{analysis_type="ar-st",architectural_model=report.ArchitecturalModel,structural_model=report.StructuralModel,results=compact},"AR-ST AI analysis").ConfigureAwait(false);
            var recommendations=AiResponseValidator.ParseRecommendations(text,report.Issues.Select(x=>x.ResultKey));
            var counts=recommendations.GroupBy(x=>x.Severity).ToDictionary(g=>g.Key,g=>g.Count());
            var summaryText=await _client.CompleteJsonAsync(settings.AiEndpoint,settings.AiApiKey,settings.AiModel,AiPromptBuilder.SummarySystemPrompt+languageInstruction,new{analysis_type="ar-st",totals=new{analyzed=recommendations.Count,recommended=recommendations.Count(x=>x.ShouldCreateIssue),likely_noise=recommendations.Count(x=>x.Assessment=="likely_noise")},severity_counts=counts,recommendations=recommendations.Select(x=>new{x.ResultKey,x.ShouldCreateIssue,x.Assessment,x.Severity,x.Confidence,x.ResponsibleDiscipline,x.RecommendedAction}),source_results=compact},"AR-ST AI summary").ConfigureAwait(false);
            var result=new AiAnalysisResult{Issues=recommendations,Summary=AiResponseValidator.ParseSummary(summaryText),InputHash=hash};_cache.Put(hash,result);Apply(report,result);
        }
        private static void Apply(ComparisonReport report,AiAnalysisResult result)
        {
            var map=result.Issues.ToDictionary(x=>x.ResultKey,StringComparer.Ordinal);
            foreach(var item in report.Issues){AiRecommendation row;if(!map.TryGetValue(item.ResultKey,out row))continue;item.AiIsRealIssue=row.IsRealIssue;item.AiAssessment=row.Assessment;item.AiSeverity=row.Severity;item.AiComment=row.Comment;item.AiReason=row.Reason;item.AiConfidence=row.Confidence;item.AiRecommendedAction=row.RecommendedAction;item.AiExplanation=row.Comment;item.Recommendation=row.RecommendedAction;item.IsSelectedForIssueCreation=!item.HasApsIssue&&row.ShouldCreateIssue;}
            report.AiSummary=result.Summary+(result.FromCache?"\nLoaded from unchanged-results cache.":"");
        }
    }
}
