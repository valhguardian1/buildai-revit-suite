using System;
using System.Linq;

namespace BuildAI.Core.Issues
{
    public static class ResultKeyBuilder
    {
        public static string ElementKey(string modelUid, string linkInstanceUid, string elementUniqueId, int elementId)
        {
            var model = Clean(modelUid, "unknown-model");
            var link = Clean(linkInstanceUid, "host");
            var element = Clean(elementUniqueId, elementId.ToString());
            return model + ":" + link + ":" + element;
        }

        public static string Clash(string modelUidA, string linkUidA, string uniqueIdA, int idA,
                                   string modelUidB, string linkUidB, string uniqueIdB, int idB)
        {
            var keys = new[]
            {
                ElementKey(modelUidA, linkUidA, uniqueIdA, idA),
                ElementKey(modelUidB, linkUidB, uniqueIdB, idB)
            }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return "clash:" + keys[0] + ":" + keys[1];
        }

        public static string ArSt(string arModelUid, string arLinkUid, string arUniqueId, int arId,
                                  string stModelUid, string stLinkUid, string stUniqueId, int stId,
                                  string checkType)
        {
            return "ar-st:" + ElementKey(arModelUid, arLinkUid, arUniqueId, arId) + ":" +
                   ElementKey(stModelUid, stLinkUid, stUniqueId, stId) + ":" + Clean(checkType, "comparison").ToLowerInvariant();
        }

        private static string Clean(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace(" ", "_");
    }
}
