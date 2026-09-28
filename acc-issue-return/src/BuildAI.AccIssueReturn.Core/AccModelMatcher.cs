namespace BuildAI.AccIssueReturn.Core;

public sealed class AccModelMatchResult { public bool Matches { get; set; } public bool ImportIdentityConfirmed { get; set; } public string Reason { get; set; }=""; }
public static class AccModelMatcher
{
    public static bool Matches(AccIssue issue, AccModel model)
    {
        if (issue == null || model == null) return false;
        if (!string.IsNullOrWhiteSpace(issue.ViewableGuid) &&
            !string.Equals(issue.ViewableGuid, model.ViewableGuid, System.StringComparison.OrdinalIgnoreCase))
            return false;

        if (Has(issue.VersionUrn))
            return ApsUrnNormalizer.Same(issue.VersionUrn, model.VersionUrn) ||
                   ApsUrnNormalizer.Same(issue.VersionUrn, model.DerivativeUrn);
        if (Has(issue.DerivativeUrn) && ApsUrnNormalizer.Same(issue.DerivativeUrn, model.DerivativeUrn)) return true;
        if (Has(issue.SeedUrn) && (ApsUrnNormalizer.Same(issue.SeedUrn, model.SeedUrn) || ApsUrnNormalizer.Same(issue.SeedUrn, model.VersionUrn))) return true;
        if (Has(issue.LineageUrn) && ApsUrnNormalizer.Same(issue.LineageUrn, model.LineageUrn)) return true;
        return Has(issue.ModelUrn) && (ApsUrnNormalizer.Same(issue.ModelUrn, model.ItemUrn) || ApsUrnNormalizer.Same(issue.ModelUrn, model.LineageUrn));
    }

    public static AccModelMatchResult Match(AccIssue issue, AccModel model)
    {
        if (issue == null || model == null) return new AccModelMatchResult{Reason="MissingIssueOrModel"};
        if (!string.IsNullOrWhiteSpace(issue.ViewableGuid) && !string.Equals(issue.ViewableGuid,model.ViewableGuid,System.StringComparison.OrdinalIgnoreCase)) return new AccModelMatchResult{Reason="ViewableMismatch"};
        var identity=Matches(issue,model); if(!identity) return new AccModelMatchResult{Reason="ModelIdentityMismatch"}; if(model.VersionStatus==VersionResolutionStatus.InvalidCandidate) return new AccModelMatchResult{Matches=true,Reason="CandidateVersionDoesNotExistForItem"};
        if (string.IsNullOrWhiteSpace(issue.VersionUrn)) return new AccModelMatchResult{Matches=true,ImportIdentityConfirmed=model.ResolutionEvidence.IndexOf("UniqueViewableMatch",System.StringComparison.OrdinalIgnoreCase)>=0,Reason=model.ResolutionEvidence.IndexOf("UniqueViewableMatch",System.StringComparison.OrdinalIgnoreCase)>=0?"UniqueViewableMatch":"MissingIssueVersion"};

        if (model.VersionStatus==VersionResolutionStatus.Ambiguous || model.VersionStatus==VersionResolutionStatus.NoAccess) return new AccModelMatchResult{Matches=true,Reason=model.VersionStatus.ToString()};
        var confirmed=model.VersionStatus==VersionResolutionStatus.LatestResolved||model.VersionStatus==VersionResolutionStatus.Resolved||model.VersionStatus==VersionResolutionStatus.Historical;
        return new AccModelMatchResult{Matches=true,ImportIdentityConfirmed=confirmed,Reason=string.IsNullOrWhiteSpace(issue.VersionUrn)?"UniqueViewableMatch":"ConfirmedVersion"};
    }
    private static bool Has(string value) => !string.IsNullOrWhiteSpace(value);
}







