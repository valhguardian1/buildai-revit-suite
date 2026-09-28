using System;
using System.Collections.Generic;

namespace BuildAI.AccIssueReturn.Core;

public static class ApsRegionResolver
{
    private static readonly HashSet<string> Supported=new(StringComparer.OrdinalIgnoreCase){"US","EMEA","AUS","GBR","DEU","JPN","CAN","IND"};
    public static string Normalize(string? value)
    {
        var region=(value??"").Trim().ToUpperInvariant();
        if(region=="EU")region="EMEA";else if(region=="APAC")region="AUS";
        return region.Length==0?"US":region;
    }
    public static bool IsSupported(string? value)=>Supported.Contains(Normalize(value));
    public static string BuildModelDerivativeUrl(string baseUrl,string region,string derivativeUrn,string relativePath)
    {
        var root=(baseUrl??"").TrimEnd('/');var normalized=Normalize(region);if(!IsSupported(normalized))normalized="US";var path=(relativePath??"").TrimStart('/');
        var prefix=normalized=="US"||normalized=="AUTO"?root:root+"/modelderivative/v2/regions/"+Uri.EscapeDataString(normalized.ToLowerInvariant())+"/designdata/"+Uri.EscapeDataString(derivativeUrn??"");
        if(normalized=="US"||normalized=="AUTO")prefix=root+"/modelderivative/v2/designdata/"+Uri.EscapeDataString(derivativeUrn??"");
        return prefix+"/"+path;
    }
}
