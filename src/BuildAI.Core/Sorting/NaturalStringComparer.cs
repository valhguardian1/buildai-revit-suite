using System;
using System.Collections.Generic;
using System.Globalization;

namespace BuildAI.Core.Sorting
{
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer OrdinalIgnoreCase = new NaturalStringComparer();
        public int Compare(string left, string right)
        {
            left = left ?? ""; right = right ?? "";
            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    int i0=i, j0=j;
                    while (i<left.Length && char.IsDigit(left[i])) i++;
                    while (j<right.Length && char.IsDigit(right[j])) j++;
                    var a=left.Substring(i0,i-i0).TrimStart('0');
                    var b=right.Substring(j0,j-j0).TrimStart('0');
                    if (a.Length==0) a="0"; if (b.Length==0) b="0";
                    if (a.Length!=b.Length) return a.Length.CompareTo(b.Length);
                    var n=string.CompareOrdinal(a,b); if(n!=0)return n;
                    var raw=(i-i0).CompareTo(j-j0); if(raw!=0)return raw;
                    continue;
                }
                var ca=char.ToUpperInvariant(left[i]); var cb=char.ToUpperInvariant(right[j]);
                if(ca!=cb)return ca.CompareTo(cb); i++;j++;
            }
            return (left.Length-i).CompareTo(right.Length-j);
        }
    }
}
