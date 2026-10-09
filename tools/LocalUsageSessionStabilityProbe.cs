using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Generated numeric fixtures exercise ownership, retry and cancellation;
    // no normal user settings, logs, watchers or remote services are opened.
    internal static class LocalUsageSessionStabilityProbe
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static void Check(bool pass,string message)
        {if(!pass)throw new InvalidOperationException("Local usage session: "+message);Console.WriteLine("PASS Local usage session: "+message);}
        private static string Record(DateTime at,int tokens)
        {return Json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=tokens,output_tokens=0,total_tokens=tokens}}}})+"\n";}
        private static string Fixture(string home,DateTime at,int tokens)
        {
            string sessions=Path.Combine(home,"sessions"),id=Guid.NewGuid().ToString();Directory.CreateDirectory(sessions);
            string path=Path.Combine(sessions,"rollout-"+id+".jsonl");
            File.WriteAllText(path,Json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n"+Record(at,tokens));
            return path;
        }
        internal static async Task Run(string root)
        {
            string fixture=Path.Combine(root,"local-session-"+Guid.NewGuid().ToString("N")),home=Path.Combine(fixture,"first"),other=Path.Combine(fixture,"other"),missing=Path.Combine(fixture,"missing");
            DateTime at=DateTime.Now.AddMinutes(-5);string log=Fixture(home,at,100);Fixture(other,at,700);
            var usage=new LocalUsageSession(Path.Combine(fixture,"ledger.gz"),null);
            Func<LocalCodexUsage,long> tokens=reader=>reader.Snapshot("all",DateTime.Now).TotalTokens;
            bool failed=false,queried=false;
            try{usage.Refresh(missing,true,null,null,false,(reader,failure)=>{queried=true;return tokens(reader);});}catch(IOException){failed=true;}
            bool chartFailed=false;
            try{usage.Read(missing,tokens,null);}catch(IOException){chartFailed=true;}
            Check(failed&&chartFailed&&!queried,"a failed first scan remains an error for subsequent chart reads");

            Check(usage.Read(home,tokens,null)==100,"the first successful query initializes its own source");
            var first=usage.Read(home,reader=>usage.Export(reader),null);
            Check(Object.ReferenceEquals(first,usage.Read(home,reader=>usage.Export(reader),null)),"unchanged queries reuse exported records");
            File.AppendAllText(log,Record(at.AddSeconds(1),200));
            Check(usage.Refresh(home,false,null,null,false,(reader,failure)=>tokens(reader))==100,"changing a range reuses the ledger without discovering files");
            Check(usage.Refresh(home,true,null,null,false,(reader,failure)=>tokens(reader))==200,"a requested scan discovers appended usage");
            Check(!Object.ReferenceEquals(first,usage.Read(home,reader=>usage.Export(reader),null)),"changed data replaces the exported view");
            var partial=usage.Read(home,reader=>reader.ReadEnumerated("all",DateTime.Now,new List<FileInfo>(),1,null,null),null);
            Check(partial.TotalTokens==200&&partial.CoverageWarnings>0&&!String.IsNullOrEmpty(partial.Warning),"partial directory discovery preserves prior usage and its coverage warning");
            usage.Refresh(home,true,null,null,false,(reader,failure)=>tokens(reader));

            bool warned=false;
            var unavailable=usage.Refresh(missing,true,null,null,true,(reader,failure)=>{warned=!String.IsNullOrEmpty(failure);return reader;});
            Check(unavailable==null&&warned,"remote fallback never borrows records from a different local source");
            Check(usage.Read(missing,(reader,failure)=>reader==null&&!String.IsNullOrEmpty(failure),null,true),"chart and milestone fallback can retain remote data after a failed local initialization");
            Check(usage.Read(home,tokens,null)==200,"a failed source change retains the last successful reader");
            string sessions=Path.Combine(home,"sessions"),offline=sessions+"-offline";Directory.Move(sessions,offline);
            try
            {
                bool retained=false;
                long previous=usage.Refresh(home,true,null,null,true,(reader,failure)=>{retained=!String.IsNullOrEmpty(failure);return tokens(reader);});
                Check(retained&&previous==200,"a later read failure preserves successful data with a warning");
            }
            finally{Directory.Move(offline,sessions);}

            bool cancelled=false;
            try{usage.Read(other,tokens,()=>true);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&usage.Read(home,tokens,null)==200,"cancellation cannot replace the active source");
            CheckRemoteCharts(fixture,missing,usage.Read(other,reader=>usage.Export(reader),null),at);
            var requests=new List<Task<long>>();
            for(int index=0;index<8;index++)
            {
                string selected=index%2==0?home:other;long expected=index%2==0?200:700;
                requests.Add(Task.Run(()=>{long value=usage.Read(selected,tokens,null);if(value!=expected)throw new InvalidOperationException("Concurrent source query returned another source's usage.");return value;}));
            }
            await Task.WhenAll(requests);
            Check(true,"concurrent queries keep their captured source identities");

            string preloadHome=Path.Combine(fixture,"preload"),preloadLog=Fixture(preloadHome,at,100);
            var preload=new StartupUsagePreload(preloadHome,Path.Combine(fixture,"preload.gz"));await preload.Completion;
            Check(preload.Failure==null,"startup preload reads the fixture");
            var startup=new LocalUsageSession(Path.Combine(fixture,"preload.gz"),preload);
            Check(startup.Read(preloadHome,tokens,null)==100,"a chart can adopt the completed startup reader");
            File.AppendAllText(preloadLog,Record(at.AddSeconds(1),200));
            Check(startup.Refresh(preloadHome,true,null,null,false,(reader,failure)=>tokens(reader))==100,"the first refresh reuses the chart's startup scan");
            Check(startup.Refresh(preloadHome,true,null,null,false,(reader,failure)=>tokens(reader))==200,"the next refresh discovers usage added after startup");
        }

        private static void CheckRemoteCharts(string root,string missing,List<LogCursor> server,DateTime at)
        {
            var preferences=new Preferences{Source="local",CodexHome=missing,UsageView="remote",LiveQuota=false,Remote=new RemoteOptions{Enabled=true}};
            var widget=new WidgetWindow(preferences,true);
            var monitor=new RemoteMonitor(()=>{throw new InvalidOperationException("The fixture must never connect.");},Path.Combine(root,"remote"),true);
            var remoteField=typeof(WidgetWindow).GetField("remote",BindingFlags.Instance|BindingFlags.NonPublic);
            typeof(RemoteMonitor).GetField("view",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(monitor,new RemoteView{Records=server,Connected=true,Status="Fixture"});
            remoteField.SetValue(widget,monitor);
            try
            {
                var method=typeof(WidgetWindow).GetMethod("ReadChartRange",BindingFlags.Instance|BindingFlags.NonPublic);
                Func<UsageSnapshot> read=()=>((Task<UsageSnapshot>)method.Invoke(widget,new object[]{at.AddMinutes(-1),DateTime.Now,CancellationToken.None,null})).GetAwaiter().GetResult();
                var remote=read();
                Check(remote.TotalTokens==700&&!remote.DataUnavailable&&remote.CoverageWarnings>0,"a remote chart stays available when local initialization fails");
                preferences.UsageView="combined";var combined=read();
                Check(combined.TotalTokens==700&&!combined.DataUnavailable&&combined.CoverageWarnings>0,"a combined chart retains server records and discloses local failure");
                preferences.UsageView="local";bool failed=false;
                try{read();}catch(IOException){failed=true;}
                Check(failed,"the local-only chart still reports its failed source");
            }
            finally{widget.Close();}
        }
    }
}
