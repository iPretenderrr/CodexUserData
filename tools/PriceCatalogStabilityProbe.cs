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
            string first=ModelColors.ColorHex("gpt-future-alpha",false),same=ModelColors.ColorHex("GPT-FUTURE-ALPHA",false),second=ModelColors.ColorHex("gpt-future-beta",false);
            StabilityProbe.Check(first==same&&first!=second&&ModelColors.ColorHex("gpt-future-alpha",true)!=first,"future model colors are stable, case-insensitive, distinct and theme-aware");

            Reject(()=>PriceCatalog.Parse(Json(2026092400).Replace("standard-short","unknown-tier")),"unsupported pricing basis is rejected");
            Reject(()=>PriceCatalog.Parse(Json(2026092400).Replace("developers.openai.com/api/docs/pricing","example.com/pricing")),"untrusted catalog source metadata is rejected");
            Reject(()=>PriceCatalog.Parse(Json(2026092400,"bad/model")),"invalid model IDs are rejected");
            using(var oversized=new MemoryStream(new byte[PriceCatalog.MaxResponseBytes+1]))Reject(()=>PriceCatalog.ReadResponseAsync(oversized,CancellationToken.None).GetAwaiter().GetResult(),"catalog response size is bounded");
        }
    }
}
