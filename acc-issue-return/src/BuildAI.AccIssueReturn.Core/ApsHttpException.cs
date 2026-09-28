using System;
using System.Net;

namespace BuildAI.AccIssueReturn.Core;

public sealed class ApsHttpException : InvalidOperationException
{
    public string Endpoint { get; }
    public HttpStatusCode StatusCode { get; }
    public string SafeResponseExcerpt { get; }
    public string CorrelationId { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }
    public ApsHttpException(string endpoint,HttpStatusCode status,string excerpt,string correlationId,bool retryable,TimeSpan? retryAfter=null,Exception? inner=null):base("APS request failed with HTTP "+(int)status+" at "+endpoint+" ["+correlationId+"].",inner){Endpoint=endpoint;StatusCode=status;SafeResponseExcerpt=excerpt;CorrelationId=correlationId;Retryable=retryable;RetryAfter=retryAfter;}
}
