using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace CodexUserData
{
    internal static class PeriodStabilityProbe
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static void Check(bool pass,string text){StabilityProbe.Check(pass,"Period: "+text);}
        private static QuotaSample Q(long at,double value=80){return new QuotaSample{Time=at,Bucket="codex",Minutes=300,Remaining=value,Reset=at+3600};}
        internal static async Task Run(string root)
        {await CheckHistory(Path.Combine(root,"range-cache"));CheckSnapshots();CheckReader(Path.Combine(root,"range-reader"));await CheckCharts();CheckPublishedRanges(root);}
        private static async Task CheckHistory(string root)
        {
            var store=new QuotaHistoryStore(root);string scope=QuotaHistoryStore.Scope(Path.Combine(root,"synthetic"));string folder=Path.Combine(root,scope);Directory.CreateDirectory(folder);
            long now=LocalCodexUsage.Unix(DateTime.UtcNow),start=now-179*86400L;
            for(int day=0;day<180;day++)
            {
                var sample=Q(start+day*86400L);string file=Path.Combine(folder,DateTimeOffset.FromUnixTimeSeconds(sample.Time).UtcDateTime.ToString("yyyy-MM-dd")+".jsonl");
                File.WriteAllText(file,Json.Serialize(sample)+"\n",new UTF8Encoding(false));
            }
            var full=await store.ReadAsync(scope,start,now);Check(full.Length==180,"180 synthetic dates load completely");
            await store.ReadAsync(scope,now-86400,now);var warm=await store.ReadAsync(scope,start,now);
            Check(store.LastBytesRead==0&&warm.Length==full.Length,"180d -> 1d -> 180d reuses all unchanged day files");
            string today=Path.Combine(folder,DateTime.UtcNow.ToString("yyyy-MM-dd")+".jsonl"),append=Json.Serialize(Q(now+60,70))+"\n";File.AppendAllText(today,append);
            var updated=await store.ReadAsync(scope,start,now+60);
            Check(updated.Length==181&&store.LastBytesRead==Encoding.UTF8.GetByteCount(append),"appending one quota sample reads only appended bytes");
            File.AppendAllText(today,"{\"Time\":");await store.ReadAsync(scope,start,now+120);File.AppendAllText(today,"broken\n"+Json.Serialize(Q(now+120,60))+"\n");
            Check((await store.ReadAsync(scope,start,now+120)).Length==182,"an incomplete tail cannot hide the next valid sample");
            File.WriteAllText(today,"\uFEFF"+Json.Serialize(Q(now,25))+"\n");updated=await store.ReadAsync(scope,start,now+120);
            Check(updated.Length==180&&updated.Last().Remaining==25,"a replaced UTF-8 day removes its old cached contribution");
            File.AppendAllText(today,Json.Serialize(Q(start,12))+"\n");updated=await store.ReadAsync(scope,start,now);
            Check(updated.Length==180&&updated.First().Time==start&&updated.First().Remaining==12,"misplaced imported dates retain global deduplication and order");
            using(var cancel=new CancellationTokenSource())
            {cancel.Cancel();bool stopped=false;try{await store.ReadAsync(scope,start,now,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"superseded reads honor cancellation");}
            typeof(QuotaHistoryStore).GetField("sampleLimit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(store,20);
            await store.ReadAsync(scope,start,now);Check(store.CachedSamples<=20,"LRU eviction bounds retained samples");
            string canonical=Json.Serialize(Q(now,12.5));var parsed=QuotaSample.Parse(canonical,Json);
            var imported=QuotaSample.Parse("{ \"Remaining\":12.5, \"Minutes\":300, \"Bucket\":\"codex\", \"Reset\":"+(now+3600)+", \"Time\":"+now+" }",Json);
            Check(parsed.Remaining==12.5&&imported.Time==parsed.Time,"canonical fast parsing and general JSON fallback agree");
            Check(QuotaSample.Parse(canonical+"garbage",Json)==null,"fast parsing rejects trailing non-JSON data");
        }
        private static void Same(UsageSnapshot a,UsageSnapshot b,string range)
        {
            Check(a.TotalTokens==b.TotalTokens&&a.InputTokens==b.InputTokens&&a.CacheReadTokens==b.CacheReadTokens&&a.CacheCreationTokens==b.CacheCreationTokens&&a.OutputTokens==b.OutputTokens
                &&a.ReasoningTokens==b.ReasoningTokens&&a.Requests==b.Requests&&a.Sessions==b.Sessions&&a.InferredRecords==b.InferredRecords&&a.EquivalentUsd==b.EquivalentUsd&&a.Warning==b.Warning&&a.LatestRecord==b.LatestRecord,
                range+" projection agrees with full event aggregation");
        }
        private static void CheckSnapshots()
        {
            var records=new Dictionary<string,LogCursor>();var now=DateTime.Now;long at=LocalCodexUsage.Unix(now);
            for(int session=0;session<12;session++)
            {
                var c=new LogCursor{Id="sample-"+session,Meta=true,Started=at-240*86400};records[c.Id]=c;
                for(int d=0;d<240;d++)c.Events.Add(new LocalUsageEvent{Time=LocalCodexUsage.Unix(now.AddDays(-d)),Signature="sample-"+d,Model="gpt-5.6-sol",Effort="high",Input=10+d,Cached=3,CacheWrite=2,Output=5,Reasoning=2,Inferred=d%3==0});
            }
            var memo=new UsageSnapshotMemo();ApiPrices.Configure(null);
            foreach(string range in new[]{"today","week","month","all"})Same(memo.Get(records,0,range,now),LocalCodexUsage.Aggregate(records,range,now),range);
            var first=memo.Get(records,0,"today",now);var second=memo.Get(records,0,"month",now);
            Check(Object.ReferenceEquals(first.Daily,second.Daily),"periods share the 180-day/hourly summaries");
            first.Warning="caller-only";Check(memo.Get(records,0,"today",now).Warning!="caller-only","view status cannot contaminate cached statistics");
            ApiPrices.Configure(new Dictionary<string,decimal[]>());Check(Object.ReferenceEquals(first.Daily,memo.Get(records,0,"today",now).Daily),"unchanged prices preserve cached statistics");
            ApiPrices.Configure(new Dictionary<string,decimal[]>{{"gpt-5.6-sol",new[]{1m,1m,1m,1m}}});Same(memo.Get(records,0,"month",now),LocalCodexUsage.Aggregate(records,"month",now),"changed prices");ApiPrices.Configure(null);
            Same(memo.Get(records,0,"today",now.ToUniversalTime()),LocalCodexUsage.Aggregate(records,"today",now),"UTC input with local calendar");
            Same(memo.Get(records,0,"today",now.Date.AddDays(1).AddSeconds(1)),LocalCodexUsage.Aggregate(records,"today",now.Date.AddDays(1).AddSeconds(1)),"midnight rollover");
            var c1=records.Values.First();c1.Events.Add(new LocalUsageEvent{Time=at+2,Signature="future",Model="gpt-5.6-sol",Input=55});
            var before=memo.Get(records,1,"all",now);var after=memo.Get(records,1,"all",now.AddSeconds(3));Check(after.TotalTokens==before.TotalTokens+55,"future samples become visible at their actual timestamp");
            Same(memo.Get(records,1,"today",now),LocalCodexUsage.Aggregate(records,"today",now),"clock rollback");
            c1.TaskStarted=at-1;c1.TaskRunning=true;c1.Modified=now.ToUniversalTime().Ticks;
            Check(memo.Get(records,2,"today",now).ActiveTasks==1&&memo.Get(records,2,"today",now.AddMinutes(4)).ActiveTasks==0,"cached legacy activity still expires at its boundary");
            var local=records.Values.ToList();var server=local.Select(LocalCodexUsage.CopyCursor).ToList();var union=new UsageUnionCache();var combined=union.Get(local,server,"combined","today",now,"combined");
            Same(combined,UsageUnion.Snapshot(local.Concat(server),"today",now,"combined"),"cross-source duplicated sessions");
            Check(Object.ReferenceEquals(combined.Daily,union.Get(local,server,"combined","month",now,"combined").Daily),"remote union shares merged summaries across periods");
        }
        private static string Header(string id,DateTime at){return Json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n";}
        private static string Token(DateTime at,long input){return Json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type="token_count",info=new{last_token_usage=new{input_tokens=input,output_tokens=1}}}})+"\n";}
        private static void CheckReader(string root)
        {
            string sessions=Path.Combine(root,"sessions");Directory.CreateDirectory(sessions);string id=Guid.NewGuid().ToString(),file=Path.Combine(sessions,"rollout-"+id+".jsonl");var now=DateTime.Now.AddSeconds(-5);
            File.WriteAllText(file,Header(id,now)+Token(now,10));var reader=new LocalCodexUsage(root,Path.Combine(root,"cache.gz"));
            Check(reader.Read("today",DateTime.Now,null,null).TotalTokens==11,"initial numeric ledger is available");File.AppendAllText(file,Token(now.AddSeconds(1),20));
            Check(reader.Snapshot("month",DateTime.Now).TotalTokens==11,"selecting a period does not launch filesystem reads");reader.Update(null,null);
            Check(reader.Snapshot("today",DateTime.Now).TotalTokens==32,"the independent update publishes appended usage");File.WriteAllText(file,Header(id,now)+Token(now,4));reader.Update(null,null);
            Check(reader.Snapshot("month",DateTime.Now).TotalTokens==5,"log replacement invalidates period totals");
        }
        private static void CheckPublishedRanges(string root)
        {
            var prefs=new Preferences{Source="local",CodexHome=Path.Combine(root,"empty-codex"),LiveQuota=false,BallMode=false,OrbAnimation="off"};
            var widget=new WidgetWindow(prefs,true);
            try
            {
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                typeof(WidgetWindow).GetField("readyRanges",flags).SetValue(widget,new Dictionary<string,UsageSnapshot>{{"week",new UsageSnapshot{TotalTokens=777}}});
                typeof(WidgetWindow).GetField("rangesObserved",flags).SetValue(widget,DateTime.Now);typeof(WidgetWindow).GetField("busy",flags).SetValue(widget,true);prefs.Range="week";
                Check((bool)StabilityProbe.Call(widget,"ApplyCachedRange")&&StabilityProbe.Field<UsageSnapshot>(widget,"snapshot").TotalTokens==777,"published periods remain immediately usable while the scanner is busy");
                typeof(WidgetWindow).GetField("rangesObserved",flags).SetValue(widget,DateTime.Now.AddDays(-1));
                Check(!(bool)StabilityProbe.Call(widget,"ApplyCachedRange"),"the UI does not reuse yesterday's range snapshot after midnight");
                StabilityProbe.Call(widget,"SelectionChanged");Check(StabilityProbe.Field<Dictionary<string,UsageSnapshot>>(widget,"readyRanges")==null,"changing data source clears UI-owned period snapshots");
            }
            finally{widget.Close();}
        }
        private static async Task CheckCharts()
        {
            long end=LocalCodexUsage.Unix(DateTime.UtcNow);var samples=Enumerable.Range(0,259200).Select(i=>Q(end-259200*60L+i*60L,100-i%100)).ToArray();
            var chart=new QuotaHistoryChart();var host=new Window{Content=chart,Width=800,Height=430,ShowActivated=false,ShowInTaskbar=false};int pulses=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};timer.Tick+=delegate{pulses++;};
            try
            {
                host.Show();timer.Start();var watch=Stopwatch.StartNew();await chart.SetSeriesAsync(QuotaHistorySeries.Prepare(samples),end-180*86400L,end);watch.Stop();
                Check(chart.GeometryBuilds>0&&pulses>0,"large curve preparation leaves the dispatcher responsive");Console.WriteLine("PERIOD ASYNC GEOMETRY: "+watch.ElapsedMilliseconds+" ms; UI pulses="+pulses);
                var first=chart.SetSeriesAsync(QuotaHistorySeries.Prepare(samples),end-180*86400L,end);var last=chart.SetSeriesAsync(QuotaHistorySeries.Prepare(samples.Skip(samples.Length-60).ToArray()),end-3600,end);
                await Task.WhenAll(first,last);Check(StabilityProbe.Field<long>(chart,"to")-StabilityProbe.Field<long>(chart,"from")==3600,"old geometry cannot overwrite the latest period");
                var data=DailyUsage.Empty(DateTime.Today,180);foreach(var d in data)d.Models.Add(new ModelUsage{Model="fixture",Tokens=10});var usage=new UsageChart(false);usage.SetData(data,7);
                var series=StabilityProbe.Field<Dictionary<string,double[]>>(usage,"series")["fixture"];usage.SetData(data,30);
                Check(Object.ReferenceEquals(series,StabilityProbe.Field<Dictionary<string,double[]>>(usage,"series")["fixture"]),"period changes retain the built model sequence");
            }
            finally{timer.Stop();host.Close();}
        }
    }
}
