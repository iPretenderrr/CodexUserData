using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Controls;

namespace CodexUserData
{
    internal static class OfficialPriceStabilityProbe
    {
        private static void Check(bool ok,string message){StabilityProbe.Check(ok,"Official prices: "+message);}
        private static void Reject(Action action,string message){bool rejected=false;try{action();}catch(Exception){rejected=true;}Check(rejected,message);}
        private static object Wire(object value,int kind=0){return new object[]{kind,value};}
        internal static string Page(decimal input=2m)
        {
            object[] rows={Wire(new[]{Wire("gpt-6.1-sol"),Wire(input),Wire(.1m),Wire(2.5m),Wire(10m)},1),Wire(new[]{Wire("gpt-fixture (<272K context length)"),Wire(3m),Wire(null),Wire(12m)},1)};
            string props=WebUtility.HtmlEncode(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"tier",Wire("standard")},{"rows",Wire(rows,1)}}));
            string[] headers={"","Short context","Long context","Model","Input","Cached input","Cache writes","Output","Input","Cached input","Cache writes","Output"};
            return "<link rel=\"canonical\" href=\""+OfficialPricePage.Endpoint+"\"><h2 id=\"text-tokens\">Text tokens</h2><small>Prices per 1M tokens.</small><astro-island component-export=\"TextTokenPricingTables\" props=\""+props+"\"><table><thead><tr>"+String.Join("",headers.Select(h=>"<th>"+h+"</th>"))+"</tr></thead><tbody><tr><td>gpt-6.1-sol</td><td>$"+input.ToString(CultureInfo.InvariantCulture)+"</td><td>$0.10</td><td>$2.50</td><td>$10.00</td><td>$4</td><td>$0.20</td><td>$5</td><td>$15</td></tr></tbody></table></astro-island><h2>Other prices</h2>";
        }
        internal static async Task Run(string root)
        {
            string page=Page();var parsed=OfficialPricePage.Parse(page);
            Check(parsed["gpt-6.1-sol"].SequenceEqual(new[]{2m,.1m,2.5m,10m}),"only Standard short-context columns are applied");
            Check(parsed["gpt-fixture"].SequenceEqual(new[]{3m,-1m,-1m,12m}),"four-column legacy rows keep output and preserve missing cache fields");
            Reject(()=>OfficialPricePage.Parse(page.Replace("&quot;standard&quot;","&quot;fast&quot;")),"Fast cannot substitute for Standard");
            Reject(()=>OfficialPricePage.Parse(page.Replace("Short context","Unspecified context")),"changed context schema is rejected");
            Reject(()=>OfficialPricePage.Parse(page.Replace("Prices per 1M tokens.","Prices per 1K tokens.")),"unit changes are rejected");
            Reject(()=>OfficialPricePage.Parse(page.Replace("<td>$0.10</td>","<td>$9</td>")),"SSR and structured amount disagreement is rejected");
            Reject(()=>OfficialPricePage.Parse(page.Replace("\\u003c272K","\\u003e272K").Replace("&lt;272K","&gt;272K")),"long-context suffix is never stripped into a base price");
            Reject(()=>OfficialPricePage.Parse(page.Replace("&quot;gpt-6.1-sol&quot;","&quot;private/gpt-6.1-sol&quot;")),"private aliases cannot silently become official model IDs");
            Reject(()=>OfficialPricePage.Parse(new string('x',OfficialPricePage.MaxBytes+1)),"oversized HTML is bounded before parsing");
            string real=Environment.GetEnvironmentVariable("CODEXUSERDATA_PRICE_FIXTURE");
            if(!String.IsNullOrEmpty(real))
            {
                var live=OfficialPricePage.Parse(File.ReadAllText(real));
                Check(live["gpt-6.1-sol"].SequenceEqual(new[]{2m,.1m,2.5m,10m})&&live["gpt-6-sol"][1]==.2m&&live.Count>3,"downloaded official HTML includes hydrated hidden rows and distinguishes Sol versions");
            }
            await Sync(root,page);await MetadataAndCapacity(root,page);await Editor(root,page);Charts();ApiPrices.Configure(null);ApiPrices.ConfigureOfficial(null);
        }
        private static async Task Sync(string root,string page)
        {
            DateTime now=DateTime.UtcNow;int calls=0,changes=0;bool fail=false;var pending=new TaskCompletionSource<string>();string file=Path.Combine(root,"official-cache.json");
            Func<CancellationToken,Task<string>> fetch=token=>{calls++;if(fail)throw new WebException("fixture offline");return calls==1?pending.Task:Task.FromResult(page);};
            using(var sync=new OfficialPriceSync(file,fetch,null,()=>now,false))
            {
                sync.Changed+=()=>changes++;
                var first=sync.RefreshAsync(new[]{"gpt-6.1-sol","private/model"},false);
                await StabilityProbe.Until(()=>calls==1,"price worker did not start",3000);
                var duplicate=sync.RefreshAsync(new[]{"gpt-6.1-sol"},false);Check(Object.ReferenceEquals(first,duplicate),"concurrent automatic refreshes share one worker");
                pending.SetResult(page);await first;
                Check(calls==1&&changes==1&&sync.Status("gpt-6.1-sol").Contains("已核对")&&sync.Status("private/model").Contains("暂未列出"),"one download updates official and unresolved states separately");
                var saved=OfficialPriceSync.ParseCache(File.ReadAllText(file));Check(saved.models["gpt-6.1-sol"][1]==.1m&&saved.checks["private/model"].state=="missing","successful prices and per-model retry metadata persist");
                object version=ApiPrices.Version;await sync.RefreshAsync(null,true);Check(calls==2&&changes==1&&Object.ReferenceEquals(version,ApiPrices.Version),"manual unchanged prices do not invalidate numeric caches");
                now=now.AddMinutes(2);await sync.RefreshAsync(null,false);Check(calls==2,"confirmed and unresolved models respect separate cooldowns");
                await sync.RefreshAsync(new[]{"gpt-new-public-version"},false);Check(calls==3,"new model bypasses another model's 24-hour freshness");
                ApiPrices.Configure(new Dictionary<string,decimal[]>{{"gpt-6.1-sol",new[]{9m,0m,0m,0m}}});long missing;
                Check(ApiPrices.Estimate("gpt-6.1-sol",1000000,0,0,0,out missing)==9m,"custom rates remain higher priority than official values");ApiPrices.Configure(null);
                fail=true;await sync.RefreshAsync(null,true);version=ApiPrices.Version;
                Check(sync.Status("gpt-6.1-sol").Contains("更新失败")&&ApiPrices.Default("gpt-6.1-sol")[1]==.1m,"network failure preserves a valid official price");
                int count=calls;now=now.AddMinutes(4);await sync.RefreshAsync(null,false);Check(calls==count,"failure backoff suppresses repeated discovery requests");
                now=now.AddMinutes(2);await sync.RefreshAsync(null,false);Check(calls==count+1&&Object.ReferenceEquals(version,ApiPrices.Version),"failure retry wakes before the daily refresh without changing prices");
            }
            int restartedCalls=0;
            using(var sync=new OfficialPriceSync(file,t=>{restartedCalls++;return Task.FromResult(page);},null,()=>now,false))
            {await sync.RefreshAsync(new[]{"gpt-6.1-sol"},false);Check(restartedCalls==0&&ApiPrices.DefaultSource("gpt-6.1-sol")=="官方价格","restart loads prior prices and honors persisted backoff");}
            int published=0;
            using(var sync=new OfficialPriceSync(Path.Combine(root,"no-cache.json"),t=>Task.FromResult(page),json=>{throw new IOException("fixture disk full");},()=>now,false))
            {
                sync.Changed+=()=>published++;ApiPrices.ConfigureOfficial(new Dictionary<string,decimal[]>{{"gpt-6.1-sol",new[]{8m,.8m,-1m,20m}}});
                var version=ApiPrices.Version;string status=await sync.RefreshAsync(new[]{"gpt-6.1-sol"},true);
                Check(status.Contains("保存失败")&&published==0&&Object.ReferenceEquals(version,ApiPrices.Version),"disk failure cannot publish an unpersisted price");
            }
            var blocked=new TaskCompletionSource<string>();bool entered=false;var stopped=new OfficialPriceSync(Path.Combine(root,"closed-cache.json"),token=>{entered=true;token.Register(()=>blocked.TrySetCanceled());return blocked.Task;},null,()=>now,false);
            stopped.Changed+=()=>published++;var task=stopped.RefreshAsync(new[]{"gpt-6.1-sol"},true);await StabilityProbe.Until(()=>entered,"pending price download did not start",3000);stopped.Dispose();await task;
            Check(published==0&&!File.Exists(Path.Combine(root,"closed-cache.json")),"closing cancels pending work without publication");
            int queuedCalls=0;Task<string> queued=null;
            using(var sync=new OfficialPriceSync(Path.Combine(root,"queued-cache.json"),t=>{queuedCalls++;return Task.FromResult(page);},null,()=>now,false))
            {
                sync.Checked+=()=>{if(queuedCalls==1)queued=sync.RefreshAsync(new[]{"gpt-6.1-sol"},true);};
                await sync.RefreshAsync(new[]{"gpt-6.1-sol"},true);if(queued!=null)await queued;
                Check(queuedCalls==2,"manual request at worker completion is drained before its shared task completes");
                for(int i=0;i<4;i++)await sync.RefreshAsync(new[]{"gpt-6.1-sol"},true);
                Check(queuedCalls==6,"successive forced refreshes never reuse a finished worker");
            }
        }
        private static async Task Editor(string root,string page)
        {
            using(var sync=new OfficialPriceSync(Path.Combine(root,"editor-cache.json"),t=>Task.FromResult(page),null,()=>DateTime.UtcNow,false))
            {
                var editor=new PriceEditor(new Preferences{KnownModels=new[]{"gpt-6.1-sol","gpt-fixture"}},sync);
                try
                {
                    var rows=StabilityProbe.Field<IDictionary>(editor,"rows");object custom=rows["gpt-6.1-sol"],standard=rows["gpt-fixture"];
                    var inputs=StabilityProbe.Field<TextBox[]>(custom,"Inputs");inputs[0].Text="123";
                    await sync.RefreshAsync(new[]{"gpt-6.1-sol","gpt-fixture"},true);StabilityProbe.Call(editor,"RefreshDefaults");
                    Check(inputs[0].Text=="123"&&StabilityProbe.Field<bool>(custom,"Custom"),"background and manual updates retain unsaved user edits");
                    Check(StabilityProbe.Field<TextBox[]>(standard,"Inputs")[0].Text=="3"&&!StabilityProbe.Field<bool>(standard,"Custom"),"default refresh does not create accidental custom overrides");
                    Check(PriceEditor.DescribeRate("gpt-fixture",false,new[]{0m,0m,0m,0m})=="官方价格","a confirmed zero price stays distinct from a missing price");
                }
                finally{editor.Close();}
            }
        }
        private static async Task MetadataAndCapacity(string root,string page)
        {
            DateTime now=DateTime.UtcNow;bool offline=true;string file=Path.Combine(root,"metadata-recheck.json");
            using(var sync=new OfficialPriceSync(file,t=>{if(offline)throw new IOException("fixture offline");return Task.FromResult(Page(7m));},null,()=>now,false))
            {
                await sync.RefreshAsync(new[]{"gpt-6.1-sol"},true);offline=false;now=now.AddMinutes(2);
                await sync.RefreshAsync(new[]{"gpt-fixture"},false);var saved=OfficialPriceSync.ParseCache(File.ReadAllText(file));var check=saved.checks["gpt-6.1-sol"];
                Check(ApiPrices.Default("gpt-6.1-sol")[0]==7m&&sync.Status("gpt-6.1-sol").Contains("已核对")&&check.failures==0&&Math.Abs((check.checkedUtc-now).TotalMilliseconds)<1,"successful full table clears a non-due model's prior failure with its new price");
                Check(Math.Abs((check.nextUtc-now.AddHours(24)).TotalMilliseconds)<1,"rechecked models receive a fresh daily cooldown");
            }
            now=DateTime.UtcNow;file=Path.Combine(root,"capacity-recheck.json");var initial=OfficialPriceCache.Empty();initial.fetchedUtc=now;
            for(int i=0;i<OfficialPriceSync.MaxModels-1;i++)initial.models["old-price-"+i]=new[]{3m,0m,-1m,8m};
            initial.models["gpt-archived-fixture"]=new[]{7m,0m,-1m,9m};
            for(int i=0;i<OfficialPriceSync.MaxChecks-1;i++)initial.checks["old-check-"+i]=new OfficialPriceCheck{checkedUtc=now.AddHours(-1),nextUtc=now.AddHours(23),failures=0,state="missing"};
            initial.checks["gpt-archived-fixture"]=new OfficialPriceCheck{checkedUtc=now.AddHours(-1),nextUtc=now.AddHours(23),failures=0,state="official"};
            var serializer=new JavaScriptSerializer{MaxJsonLength=OfficialPricePage.MaxBytes};File.WriteAllText(file,serializer.Serialize(initial));
            using(var sync=new OfficialPriceSync(file,t=>Task.FromResult(page),null,()=>now,false))
            {
                await sync.RefreshAsync(new[]{"gpt-6.1-sol","gpt-archived-fixture"},true);var saved=OfficialPriceSync.ParseCache(File.ReadAllText(file));
                Check(saved.models.Count==OfficialPriceSync.MaxModels&&saved.checks.Count==OfficialPriceSync.MaxChecks&&saved.models.ContainsKey("gpt-6.1-sol")&&saved.checks.ContainsKey("gpt-6.1-sol"),"full caches keep new active models within both read/write limits");
                Check(saved.models.ContainsKey("gpt-archived-fixture")&&saved.models["gpt-archived-fixture"][0]==7m,"capacity retention preserves an active archived price absent from the current table");
                using(var restarted=new OfficialPriceSync(file,t=>Task.FromResult(page),null,()=>now,false))
                    Check(ApiPrices.Default("gpt-archived-fixture")[0]==7m&&ApiPrices.DefaultSource("gpt-6.1-sol")=="官方价格","capacity-limited prices survive a fresh service load");
                saved.models["gpt-overflow-fixture"]=new[]{6m,0m,-1m,12m};saved.checks["gpt-overflow-fixture"]=new OfficialPriceCheck{checkedUtc=now.AddMinutes(1),nextUtc=now.AddHours(24),failures=0,state="official"};File.WriteAllText(file,serializer.Serialize(saved));
            }
            using(var recovered=new OfficialPriceSync(file,t=>Task.FromResult(page),null,()=>now,false))
            {
                var repaired=OfficialPriceSync.ParseCache(File.ReadAllText(file));
                Check(repaired.models.Count==OfficialPriceSync.MaxModels&&repaired.checks.Count==OfficialPriceSync.MaxChecks&&ApiPrices.Default("gpt-overflow-fixture")[0]==6m&&ApiPrices.Default("gpt-archived-fixture")[0]==7m,"legacy oversized caches recover recent valid prices instead of losing the entire cache");
            }
        }
        private static void Charts()
        {
            ApiPrices.ConfigureOfficial(new Dictionary<string,decimal[]>{{"gpt-fixture",new[]{2m,0m,0m,0m}}});
            var models=new List<ModelUsage>();ModelUsage.Accumulate(models,"gpt-fixture","high",1000000,0,0,0,1);
            var day=new DailyUsage{Date=DateTime.Today.ToString("yyyy-MM-dd"),Tokens=1000000,Input=1000000,Reasoning=27,CostUsd=17m,Models=models};
            var data=new UsageSnapshot{Models=models,Daily=new[]{day},Hourly=new[]{day},Timeline=new[]{day},EquivalentUsd=2m,TotalTokens=1000000,ReasoningTokens=27,CostUsd=17m};
            var history=new HistoryPanel();DateTime from=DateTime.Today,to=DateTime.Now;
            history.Apply(data,"fixture");history.BeginCustomRange(from,to,"fixture");history.ApplyCustomRange(data,"fixture",from,to);
            PriceState captured=ApiPrices.Snapshot();ApiPrices.ConfigureOfficial(new Dictionary<string,decimal[]>{{"gpt-fixture",new[]{5m,0m,0m,0m}}});
            Check(UsageTrendSeries.Build(new[]{day},"weekly",captured)[0].Models.Sum(m=>m.EquivalentUsd)==2m,"grouped series honor a supplied price snapshot despite a concurrent price update");
            history.SetMetric(true);
            Check(StabilityProbe.Field<UsageChart>(history,"heat").Days.Last().Models.Sum(m=>m.EquivalentUsd)==5m,"switching to cost while refresh is pending updates both daily heatmap and trend prices");
            foreach(string mode in new[]{"weekly","cumulative","daily"})
            {
                history.SetHeatAggregation(mode);var heat=StabilityProbe.Field<UsageChart>(history,"heat");
                Check(heat.Days.Last().Models.Sum(m=>m.EquivalentUsd)==5m,"pending refresh "+mode+" heatmap uses the updated price");
            }
            foreach(string mode in new[]{"weekly","cumulative","daily"})
            {
                history.SetAggregation(mode);var buckets=StabilityProbe.Field<DailyUsage[]>(history,"trendBuckets");
                Check(buckets.Last().Models.Sum(m=>m.EquivalentUsd)==5m&&StabilityProbe.Field<TextBlock>(history,"sum").Text==ChartValue.Money(5m),"pending refresh "+mode+" curve and summary use the same updated price");
            }
            history.Apply(data,"fixture");
            var custom=StabilityProbe.Field<UsageSnapshot>(history,"customSnapshot");
            Check(custom.EquivalentUsd==5m&&custom.Daily[0].Models[0].EquivalentUsd==5m&&custom.Hourly[0].Models[0].EquivalentUsd==5m&&custom.Timeline[0].Models[0].EquivalentUsd==5m,"custom range summaries and all numeric bucket types reprice together");
            Check(data.EquivalentUsd==2m&&day.Models[0].EquivalentUsd==2m&&custom.CostUsd==17m&&custom.ReasoningTokens==27&&custom.Daily[0].Reasoning==27,"repricing leaves shared input caches, actual charges and token metadata intact");
            history.ApplyCustomRange(data,"fixture",from,to);Check(StabilityProbe.Field<UsageSnapshot>(history,"customSnapshot").EquivalentUsd==5m,"late old-price custom queries cannot overwrite current estimates");
            history.SetMetric(true);foreach(string mode in new[]{"daily","weekly","cumulative"}){history.SetAggregation(mode);var buckets=StabilityProbe.Field<DailyUsage[]>(history,"trendBuckets");Check(buckets.Last().Models.Sum(m=>m.EquivalentUsd)==5m,"custom "+mode+" curve uses current prices");}
            ApiPrices.ConfigureOfficial(new Dictionary<string,decimal[]>{{"gpt-fixture",new[]{2m,0m,0m,0m}}});var preset=new HistoryPanel();preset.Apply(data,"preset");preset.SetMetric(true);
            ApiPrices.ConfigureOfficial(new Dictionary<string,decimal[]>{{"gpt-fixture",new[]{5m,0m,0m,0m}}});preset.Apply(data,"preset");
            Check(StabilityProbe.Field<TextBlock>(preset,"sum").Text==ChartValue.Money(5m)&&StabilityProbe.Field<UsageChart>(preset,"heat").Days.Last().Models[0].EquivalentUsd==5m,"unchanged preset data cannot bypass repricing after its price version changes");
        }
    }
}
