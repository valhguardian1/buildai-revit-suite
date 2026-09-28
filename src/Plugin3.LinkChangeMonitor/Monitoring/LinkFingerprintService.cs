using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    public sealed class LinkFingerprintService
    {
        private string _last;
        public bool HasChanged(Document doc)
        {
            var parts=new List<string>();
            foreach(var t in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().OrderBy(x=>x.UniqueId))
            {
                string path=""; try{var r=t.GetExternalFileReference(); if(r!=null)path=ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath());}catch{}
                long len=0,ticks=0; try{if(File.Exists(path)){var f=new FileInfo(path);len=f.Length;ticks=f.LastWriteTimeUtc.Ticks;}}catch{}
                parts.Add(t.UniqueId+"|"+path+"|"+len+"|"+ticks+"|"+RevitLinkType.IsLoaded(doc,t.Id));
            }
            var current=string.Join("\n",parts); var changed=!string.Equals(current,_last,StringComparison.Ordinal); _last=current; return changed;
        }
    }
}
