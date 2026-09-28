using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildAI.AccIssueReturn.Core;

public sealed class IssueFilterState { public string AssignedUser {get;set;}=""; public string Type {get;set;}=""; public string Model {get;set;}=""; public string Status {get;set;}=""; public string Search {get;set;}=""; }
public static class IssueFiltering
{
    public static IReadOnlyList<AccIssue> Apply(IEnumerable<AccIssue> issues,IssueFilterState filter)
    {
        var q=issues??Enumerable.Empty<AccIssue>(); filter??=new IssueFilterState();
        if(!string.IsNullOrWhiteSpace(filter.AssignedUser)) q=q.Where(x=>filter.AssignedUser=="<unassigned>"?string.IsNullOrWhiteSpace(x.AssignedUserId):string.Equals(x.AssignedUserId,filter.AssignedUser,StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(filter.Type)) q=q.Where(x=>string.Equals(x.Type,filter.Type,StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(filter.Model)) q=q.Where(x=>string.Equals(x.ModelUrn,filter.Model,StringComparison.OrdinalIgnoreCase)||string.Equals(x.LineageUrn,filter.Model,StringComparison.OrdinalIgnoreCase)||string.Equals(x.VersionUrn,filter.Model,StringComparison.OrdinalIgnoreCase)||string.Equals(x.ViewableGuid,filter.Model,StringComparison.OrdinalIgnoreCase)||string.Equals(x.SeedUrn,filter.Model,StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(filter.Status)) q=q.Where(x=>string.Equals(x.Status,filter.Status,StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(filter.Search)) q=q.Where(x=>(x.Title+" "+x.Description+" "+x.Id).IndexOf(filter.Search,StringComparison.OrdinalIgnoreCase)>=0);
        return q.ToList();
    }
}
