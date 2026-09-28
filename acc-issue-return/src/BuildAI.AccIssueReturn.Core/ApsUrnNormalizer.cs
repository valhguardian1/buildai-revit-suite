using System;
using System.Text;
namespace BuildAI.AccIssueReturn.Core;
public enum ApsUrnRelation { Unresolved, SameItem, SameVersion, DerivativeOfVersion, SameViewable }
public static class ApsUrnNormalizer
{
 public static string Preserve(string? value)=>value?.Trim()??"";
 public static string Normalize(string? value){var raw=Preserve(value);if(raw.StartsWith("b.",StringComparison.OrdinalIgnoreCase))raw=raw.Substring(2);var decoded=TryDecode(raw);return decoded??raw.TrimEnd('=');}
 public static string? TryDecode(string value){var s=value.Trim();if(s.Length<8)return null;try{var b=s.Replace('-','+').Replace('_','/');while(b.Length%4!=0)b+="=";var bytes=Convert.FromBase64String(b);var text=Encoding.UTF8.GetString(bytes);return text.StartsWith("urn:",StringComparison.OrdinalIgnoreCase)||text.Contains(":")?text:null;}catch{return null;}}
 public static bool Same(string? left,string? right)=>!string.IsNullOrWhiteSpace(left)&&!string.IsNullOrWhiteSpace(right)&&StringComparer.OrdinalIgnoreCase.Equals(Normalize(left),Normalize(right));
 public static ApsUrnRelation Compare(string? left,string? right){if(string.IsNullOrWhiteSpace(left)||string.IsNullOrWhiteSpace(right))return ApsUrnRelation.Unresolved;if(Same(left,right))return ApsUrnRelation.SameVersion;var l=Normalize(left);var r=Normalize(right);if(l.EndsWith(r,StringComparison.OrdinalIgnoreCase)||r.EndsWith(l,StringComparison.OrdinalIgnoreCase))return ApsUrnRelation.DerivativeOfVersion;return ApsUrnRelation.Unresolved;}
 public static string Short(string? value){var n=Normalize(value);return n.Length<=12?n:"..."+n.Substring(n.Length-12);}
}