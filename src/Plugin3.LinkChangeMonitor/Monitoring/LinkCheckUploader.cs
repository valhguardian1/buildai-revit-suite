using System;
using System.Collections.Generic;
using System.Linq;
using BuildAI.Core;
using BuildAI.Core.Configuration;
using Plugin3.LinkChangeMonitor.Models;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public sealed class LinkCheckUploader
    {
        private readonly BuildAiClient _client; private readonly BuildAiOptions _options;
        public LinkCheckUploader(BuildAiClient client, BuildAiOptions options){_client=client;_options=options;}
        public void Enqueue(ComparisonResult result,string modelUid,string projectId,string trigger)
        {
            if(result==null)return;
            var checkId=Guid.NewGuid().ToString("D");
            _client.EnqueueJson(_options.ResolveLinkChecksUrl(),new
            {
                client_check_id=checkId,revit_model_uid=modelUid,project_id=projectId,session_id=(string)null,
                started_at_utc=result.ComparedAtUtc,finished_at_utc=DateTime.UtcNow,trigger=trigger,
                links_scanned=result.LinksScanned,baselines_created=result.BaselinesCreated,changes_count=result.Changes.Count,warnings=result.Warnings
            });
            const int size=200;
            for(int i=0;i<result.Changes.Count;i+=size)
            {
                var batch=result.Changes.Skip(i).Take(size).Select(x=>new
                {
                    change_id=x.ChangeId,detected_at_utc=x.DetectedAtUtc,change_type=x.ChangeType.ToString().ToLowerInvariant(),
                    link_instance_uid=x.LinkInstanceUniqueId,link_instance_name=x.LinkInstanceName,link_document_title=x.LinkDocumentTitle,
                    element_uid=x.ElementUniqueId,element_id=x.ElementId,element_name=x.ElementName,category=x.Category,level=x.Level,type_name=x.TypeName,
                    before=x.Before,after=x.After
                }).ToList();
                _client.EnqueueJson(_options.ResolveLinkChecksUrl().TrimEnd('/')+"/"+checkId+"/changes/batch",new{batch_number=i/size+1,is_last_batch=i+size>=result.Changes.Count,items=batch});
            }
        }
    }
}
