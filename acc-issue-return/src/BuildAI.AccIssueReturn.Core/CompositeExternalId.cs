using System;

namespace BuildAI.AccIssueReturn.Core;

public enum CompositeExternalIdParseStatus { Missing, Plain, Composite, EmptyHead, EmptyTail }

public sealed class CompositeExternalId
{
    public string Raw { get; }
    public string LinkDocumentUniqueId { get; }
    public string ElementUniqueId { get; }
    public bool IsComposite { get; }
    public CompositeExternalIdParseStatus ParseStatus { get; }
    public bool IsUsable => ParseStatus==CompositeExternalIdParseStatus.Plain||ParseStatus==CompositeExternalIdParseStatus.Composite;
    private CompositeExternalId(string raw,string head,string tail,bool composite,CompositeExternalIdParseStatus status){Raw=raw;LinkDocumentUniqueId=head;ElementUniqueId=tail;IsComposite=composite;ParseStatus=status;}
    public static CompositeExternalId Parse(string? raw)
    {
        raw=raw??"";if(raw.Length==0)return new CompositeExternalId(raw,"","",false,CompositeExternalIdParseStatus.Missing);
        var slash=raw.LastIndexOf('/');
        if(slash<0)return new CompositeExternalId(raw,"",raw,false,CompositeExternalIdParseStatus.Plain);
        var head=raw.Substring(0,slash);var tail=slash+1<raw.Length?raw.Substring(slash+1):"";
        if(head.Length==0)return new CompositeExternalId(raw,head,tail,true,CompositeExternalIdParseStatus.EmptyHead);
        if(tail.Length==0)return new CompositeExternalId(raw,head,tail,true,CompositeExternalIdParseStatus.EmptyTail);
        return new CompositeExternalId(raw,head,tail,true,CompositeExternalIdParseStatus.Composite);
    }
}
