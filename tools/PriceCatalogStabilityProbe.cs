using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexUserData
{
    internal static class PriceCatalogStabilityProbe
    {
        private static string Json(long revision,string model="gpt-future-price",decimal input=3m)
        {
            return Program.Json.Serialize(new{schema=1,revision=revision,checkedOn="2026-09-23",basis="standard-short",source="https://developers.openai.com/api/docs/pricing",models=new Dictionary<string,decimal[]>{{model,new[]{input,.3m,.5m,7m}}}});
        }
        private static void Reject(Action action,string message)
        {
            bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}StabilityProbe.Check(rejected,message);
        }
        internal static void Run(string root)
        {
            string cache=Path.Combine(Program.DataFolder,"api-prices-cache-v1.json");Directory.CreateDirectory(Program.DataFolder);
            File.WriteAllText(cache,Json(2026092398),new UTF8Encoding(false));PriceCatalog.LoadCached();
            StabilityProbe.Check(ApiPrices.Default("gpt-future-price")[0]==3m&&ApiPrices.Basis.Contains("2026-09-23"),"last valid cached price catalog loads before network refresh");

            var parsed=PriceCatalog.Parse(Json(2026092399));object before=ApiPrices.Version;
            StabilityProbe.Check(ApiPrices.ConfigureCatalog(parsed.models,parsed.revision,parsed.checkedOn)&&!Object.ReferenceEquals(before,ApiPrices.Version),"new catalog revision atomically replaces the effective price state");
            object stable=ApiPrices.Version;
            StabilityProbe.Check(!ApiPrices.ConfigureCatalog(parsed.models,parsed.revision,parsed.checkedOn)&&Object.ReferenceEquals(stable,ApiPrices.Version),"unchanged catalog does not invalidate cached statistics");
            Reject(()=>ApiPrices.ConfigureCatalog(PriceCatalog.Parse(Json(2026092399,"gpt-future-price",4m)).models,2026092399,"2026-09-23"),"same catalog revision cannot silently change prices");
            Reject(()=>ApiPrices.ConfigureCatalog(PriceCatalog.Parse(Json(2026092397)).models,2026092397,"2026-09-23"),"older catalog cannot replace or overwrite the last valid revision");

            long unknown;PriceState captured=ApiPrices.Snapshot();decimal catalog=ApiPrices.Estimate("gpt-future-price",1000000,0,0,0,out unknown);
            ApiPrices.Configure(new Dictionary<string,decimal[]>{{"gpt-future-price",new[]{9m,0m,0m,0m}}});
            decimal custom=ApiPrices.Estimate("gpt-future-price",1000000,0,0,0,out unknown),capturedValue=ApiPrices.Estimate(captured,"gpt-future-price",1000000,0,0,0,out unknown);ApiPrices.Configure(null);
            StabilityProbe.Check(catalog==3m&&custom==9m&&capturedValue==3m,"user prices override the online catalog while an in-flight aggregation keeps one price snapshot");

            long now=LocalCodexUsage.Unix(DateTime.Now);var cursor=new LogCursor{Meta=true,Id="price-memo",Events=new List<LocalUsageEvent>{new LocalUsageEvent{Time=now-1,Model="gpt-memo-model",Effort="high",Input=1000000}}};
            var records=new Dictionary<string,LogCursor>{{"price-memo",cursor}};var memo=new UsageSnapshotMemo();decimal missing=memo.Get(records,1,"today",DateTime.Now).EquivalentUsd;
            ApiPrices.Configure(new Dictionary<string,decimal[]>{{"gpt-memo-model",new[]{2m,0m,0m,0m}}});decimal refreshed=memo.Get(records,1,"today",DateTime.Now).EquivalentUsd;ApiPrices.Configure(null);
            StabilityProbe.Check(missing==0&&refreshed==2m,"price version change invalidates numeric snapshots without rereading logs");

            var models=new List<ModelUsage>();ModelUsage.Accumulate(models,"gpt-future-price","high",10,2,3,1,1);
            StabilityProbe.Check(models.Count==1&&models[0].Model=="gpt-future-price","an unseen model ID is accepted without a software update");
            string colorPath=Path.Combine(Program.DataFolder,"model-colors.json"),colorBackup=colorPath+".bak";
            File.WriteAllText(colorPath,"{damaged",new UTF8Encoding(false));File.WriteAllText(colorBackup,"{\"schema\":1,\"assignments\":{\"gpt-recovered\":7}}",new UTF8Encoding(false));
            ModelColorRegistry.Initialize(true);
            StabilityProbe.Check(ModelColors.ColorHex("gpt-recovered",false)==ModelColors.ColorHex("GPT-RECOVERED",false)&&ModelColorRegistry.Parse(File.ReadAllText(colorPath,Encoding.UTF8)).assignments.ContainsKey("gpt-recovered")&&Directory.GetFiles(Program.DataFolder,"model-colors.json.corrupt-*").Length==1,"a damaged color registry is repaired from backup while preserving the original");
            string[] generated=Enumerable.Range(0,20).Select(i=>"gpt-future-"+i).ToArray();ModelColors.EnsureModels(generated.Concat(new[]{"gpt-future-alpha","gpt-future-beta"}));
            string first=ModelColors.ColorHex("gpt-future-alpha",false),same=ModelColors.ColorHex("GPT-FUTURE-ALPHA",false),second=ModelColors.ColorHex("gpt-future-beta",false);
            StabilityProbe.Check(first==same&&first!=second&&ModelColors.ColorHex("gpt-future-alpha",true)!=first,"future model colors are stable, case-insensitive, distinct and theme-aware");
            var colorDocument=ModelColorRegistry.Parse(File.ReadAllText(colorPath,Encoding.UTF8));
            StabilityProbe.Check(colorDocument.assignments.ContainsKey("gpt-future-alpha")&&generated.Select(model=>ModelColors.ColorHex(model,false)).Distinct(StringComparer.OrdinalIgnoreCase).Count()==generated.Length,"new model colors are persisted and remain distinct across the active model set");
            ModelColors.EnsureModels(new[]{"gpt-future-later-a"});ModelColors.EnsureModels(new[]{"gpt-future-later-b"});colorDocument=ModelColorRegistry.Parse(File.ReadAllText(colorPath,Encoding.UTF8));
            StabilityProbe.Check(colorDocument.assignments.ContainsKey("gpt-future-later-b")&&File.Exists(colorPath+".bak"),"repeated color updates atomically retain the latest registry and a backup");
            string[] current={"gpt-5.6-sol","gpt-5.6-luna","gpt-6-sol","gpt-6-luna"};
            StabilityProbe.Check(current.Select(model=>ModelColors.ColorHex(model,false)).Distinct(StringComparer.OrdinalIgnoreCase).Count()==current.Length&&current.Select(model=>ModelColors.ColorHex(model,true)).Distinct(StringComparer.OrdinalIgnoreCase).Count()==current.Length,"GPT 5.6 and GPT 6 Sol/Luna colors remain visually distinct in both themes");
            var lightHover=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E7EFF7");var darkHover=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B3846");
            StabilityProbe.Check(Enumerable.Range(0,ModelColorRegistry.CandidateCount).All(slot=>ModelColors.Contrast((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ModelColors.SlotHex(slot,true)),lightHover)>=4.5&&ModelColors.Contrast((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ModelColors.SlotHex(slot,false)),darkHover)>=4.5),"generated model colors retain readable text contrast on normal and hover surfaces");
            ModelColors.EnsureModels(new[]{new string('x',161),"bad\u0001model","gpt-valid-after-invalid"});
            colorDocument=ModelColorRegistry.Parse(File.ReadAllText(colorPath,Encoding.UTF8));
            StabilityProbe.Check(colorDocument.assignments.ContainsKey("gpt-valid-after-invalid")&&!colorDocument.assignments.Keys.Any(model=>model.Length>160||model.Any(Char.IsControl)),"invalid model names cannot poison the persisted color registry");

            Reject(()=>PriceCatalog.Parse(Json(2026092400).Replace("standard-short","unknown-tier")),"unsupported pricing basis is rejected");
            Reject(()=>PriceCatalog.Parse(Json(2026092400).Replace("developers.openai.com/api/docs/pricing","example.com/pricing")),"untrusted catalog source metadata is rejected");
            Reject(()=>PriceCatalog.Parse(Json(2026092400,"bad/model")),"invalid model IDs are rejected");
            Reject(()=>ModelColorRegistry.Parse("{\"schema\":1,\"assignments\":{\"bad\":999}}"),"invalid persisted color slots are rejected");
            using(var oversized=new MemoryStream(new byte[PriceCatalog.MaxResponseBytes+1]))Reject(()=>PriceCatalog.ReadResponseAsync(oversized,CancellationToken.None).GetAwaiter().GetResult(),"catalog response size is bounded");
        }
    }
}
