using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace BuildAI.AccIssueReturn.Core;

public static class AccIssueReturnLog
{
    private static readonly object Gate=new();
    public static string LogDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"BuildAI Logs");
    public static void Event(string eventName,object? data=null)
    {
        try
        {
            var entry=new JObject{{"timestampUtc",DateTime.UtcNow.ToString("O")},{"event",eventName??""}};
            if(data!=null)entry["data"]=JToken.FromObject(data);
            var line=entry.ToString(Formatting.None)+Environment.NewLine;lock(Gate){Directory.CreateDirectory(LogDirectory);File.AppendAllText(Path.Combine(LogDirectory,"AccIssueReturn-"+DateTime.UtcNow.ToString("yyyyMMdd")+".jsonl"),line);}
        }
        catch{}
    }
}
