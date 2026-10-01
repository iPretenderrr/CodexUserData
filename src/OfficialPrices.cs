using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal static class OfficialPricePage
    {
        internal const string Endpoint="https://developers.openai.com/api/docs/pricing";
        internal const int MaxBytes=2*1024*1024;
        private static readonly Regex Id=new Regex(@"\A[a-z0-9][a-z0-9._-]{0,79}\z",RegexOptions.CultureInvariant);
        internal static bool ValidId(string model){return model!=null&&Id.IsMatch(model);}
        private static MatchCollection Matches(string value,string pattern)
        {return Regex.Matches(value,pattern,RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));}
        private static string Plain(string html)
        {return WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]*>","",RegexOptions.Singleline,TimeSpan.FromSeconds(1))).Trim();}
        // Decode only the two Astro wire types required by this table. Never execute scripts.
        private static object Unwrap(object value,int kind)
        {
            var pair=value as object[];
            if(pair==null||pair.Length!=2||!(pair[0] is int)||(int)pair[0]!=kind)throw new InvalidDataException("官方价格表结构已变化。");
            return pair[1];
        }
        private static decimal Amount(object value)
        {
            if(value==null||Object.Equals(value,"-"))return -1;
            if(!(value is int)&&!(value is long)&&!(value is decimal)&&!(value is double))throw new InvalidDataException("官方金额格式无效。");
            decimal amount=Convert.ToDecimal(value,CultureInfo.InvariantCulture);
            if(amount<0||amount>1000000)throw new InvalidDataException("官方金额超出范围。");return amount;
        }
        internal static Dictionary<string,decimal[]> Parse(string html)
        {
            if(String.IsNullOrEmpty(html)||Encoding.UTF8.GetByteCount(html)>MaxBytes)throw new InvalidDataException("官方价格页面大小无效。");
            if(!Matches(html,@"<link\b[^>]*rel=""canonical""[^>]*href=""https://developers\.openai\.com/api/docs/pricing/?""").Cast<Match>().Any())throw new InvalidDataException("不是官方价格页面。");
            int start=html.IndexOf("id=\"text-tokens\"",StringComparison.Ordinal);
            if(start<0)throw new InvalidDataException("未找到文本价格部分。");
            int end=html.IndexOf("<h2",start,StringComparison.Ordinal);string section=html.Substring(start,(end<0?html.Length:end)-start);
            if(!section.Contains("Prices per 1M tokens."))throw new InvalidDataException("价格单位不匹配。");
            Dictionary<string,decimal[]> result=null;
            foreach(Match island in Matches(section,@"<astro-island\b(?<attrs>[^>]*)>(?<body>.*?)</astro-island>"))
            {
                string attrs=island.Groups["attrs"].Value;
                if(!attrs.Contains("component-export=\"TextTokenPricingTables\""))continue;
                var propsMatch=Matches(attrs,@"\bprops=""([^""]*)""").Cast<Match>().SingleOrDefault();if(propsMatch==null)throw new InvalidDataException("缺少价格数据。");
                var serializer=new JavaScriptSerializer{MaxJsonLength=MaxBytes,RecursionLimit=16};
                var props=serializer.DeserializeObject(WebUtility.HtmlDecode(propsMatch.Groups[1].Value)) as Dictionary<string,object>;
                if(props==null||!props.ContainsKey("tier")||!props.ContainsKey("rows"))throw new InvalidDataException("缺少价格档位。");
                if(!Object.Equals(Unwrap(props["tier"],0),"standard"))continue;
                if(result!=null)throw new InvalidDataException("标准价格表重复。");
                string body=island.Groups["body"].Value;
                var header=Matches(body,@"<thead\b[^>]*>(.*?)</thead>").Cast<Match>().SingleOrDefault();
                if(header==null)throw new InvalidDataException("缺少上下文表头。");
                var cells=Matches(header.Groups[1].Value,@"<th\b[^>]*>(.*?)</th>").Cast<Match>().Select(m=>Plain(m.Groups[1].Value)).ToArray();
                string[] expected={"","Short context","Long context","Model","Input","Cached input","Cache writes","Output","Input","Cached input","Cache writes","Output"};
                if(!cells.SequenceEqual(expected))throw new InvalidDataException("标准短上下文列已变化。");
                var rows=Unwrap(props["rows"],1) as object[];
                if(rows==null||rows.Length==0||rows.Length>512)throw new InvalidDataException("价格行数无效。");
                result=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase);
                foreach(object row in rows)
                {
                    var wrapped=Unwrap(row,1) as object[];
                    if(wrapped==null||(wrapped.Length!=4&&wrapped.Length!=5))throw new InvalidDataException("价格列数无效。");
                    var values=wrapped.Select(v=>Unwrap(v,0)).ToArray();string model=values[0] as string;
                    // Only this explicit short-context suffix is allowed; no fuzzy model aliases.
                    const string suffix=" (<272K context length)";
                    if(model!=null&&model.EndsWith(suffix,StringComparison.Ordinal))model=model.Substring(0,model.Length-suffix.Length);
                    if(model==null||!Id.IsMatch(model)||result.ContainsKey(model))throw new InvalidDataException("官方模型 ID 无效或重复。");
                    var rate=new[]{Amount(values[1]),Amount(values[2]),values.Length==5?Amount(values[3]):-1m,Amount(values[values.Length-1])};
                    if(rate[0]<0||rate[3]<0)continue;
                    result.Add(model,rate);
                }
                // Cross-check visible short-context dollar cells against the hydrated data.
                // This prevents a future props column reorder from silently swapping prices.
                int verified=0;
                foreach(Match row in Matches(body,@"<tbody\b[^>]*>(.*?)</tbody>").Cast<Match>().SelectMany(m=>Matches(m.Groups[1].Value,@"<tr\b[^>]*>(.*?)</tr>").Cast<Match>()))
                {
                    var values=Matches(row.Groups[1].Value,@"<td\b[^>]*>(.*?)</td>").Cast<Match>().Select(m=>Plain(m.Groups[1].Value)).ToArray();decimal[] rates;
                    if(values.Length!=9||!result.TryGetValue(values[0],out rates))continue;
                    for(int i=0;i<4;i++)
                    {
                        decimal number;string text=values[i+1];
                        if(text=="-"&&rates[i]==-1)continue;
                        if(!text.StartsWith("$",StringComparison.Ordinal)||!Decimal.TryParse(text.Substring(1),NumberStyles.AllowDecimalPoint|NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out number)||number!=rates[i])throw new InvalidDataException("官方价格列交叉核对失败。");
                    }
                    verified++;
                }
                if(verified==0||result.Count==0)throw new InvalidDataException("未核实标准短上下文价格。");
            }
            if(result==null)throw new InvalidDataException("未找到 Standard 价格表。");return result;
        }
        internal static async Task<string> Download(CancellationToken cancellation)
        {
            // The classic compiler does not emit a target-framework attribute. Select
            // TLS 1.2 explicitly; legacy framework defaults cannot reach this HTTPS host.
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(Endpoint);request.AllowAutoRedirect=false;request.Accept="text/html";
            request.UserAgent="CodexUserData/"+typeof(OfficialPricePage).Assembly.GetName().Version.ToString(3);
            request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;request.Timeout=10000;request.ReadWriteTimeout=10000;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(10000);using(deadline.Token.Register(request.Abort))
                using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>MaxBytes||!response.ContentType.StartsWith("text/html",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("官方价格响应无效。");
                    using(var stream=response.GetResponseStream())using(var bytes=new MemoryStream())
                    {
                        var buffer=new byte[8192];int count;
                        while((count=await stream.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0)
                        {if(bytes.Length+count>MaxBytes)throw new InvalidDataException("官方价格响应过大。");bytes.Write(buffer,0,count);}
                        deadline.Token.ThrowIfCancellationRequested();return new UTF8Encoding(false,true).GetString(bytes.ToArray());
                    }
                }
            }
        }
    }

    internal sealed class OfficialPriceCheck
    {
        public DateTime checkedUtc {get;set;}
        public DateTime nextUtc {get;set;}
        public int failures {get;set;}
        public string state {get;set;}
    }
    internal sealed class OfficialPriceCache
    {
        public int schema {get;set;}
        public string source {get;set;}
        public string basis {get;set;}
        public DateTime fetchedUtc {get;set;}
        public Dictionary<string,decimal[]> models {get;set;}
        public Dictionary<string,OfficialPriceCheck> checks {get;set;}
        internal static OfficialPriceCache Empty()
        {return new OfficialPriceCache{schema=1,source=OfficialPricePage.Endpoint,basis="standard-short",models=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase),checks=new Dictionary<string,OfficialPriceCheck>(StringComparer.OrdinalIgnoreCase)};}
    }
    // One application-owned worker batches discoveries. The timer checks only small metadata;
    // no log scans, per-model HTTP requests, UI-thread parsing or model names sent to a server.
    internal sealed class OfficialPriceSync : IDisposable
    {
        internal const int MaxModels=512,MaxChecks=2048;
        private readonly object gate=new object();
        private readonly string path;
        private readonly Func<CancellationToken,Task<string>> download;
        private readonly Action<string> save;
        private readonly Func<DateTime> clock;
        private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
        private readonly Timer timer;
        private readonly bool useFallback;
        private readonly HashSet<string> observed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private OfficialPriceCache cache;
        private Task<string> running;
        private bool force,disposed;
        private DateTime nextFallback,nextFetch;
        internal event Action Changed;
        internal event Action Checked;
        internal OfficialPriceSync():this(Path.Combine(Program.DataFolder,"official-prices-v1.json"),OfficialPricePage.Download,null,()=>DateTime.UtcNow,true){}
        internal OfficialPriceSync(string file,Func<CancellationToken,Task<string>> fetch,Action<string> persist,Func<DateTime> now,bool startTimer)
        {
            path=file;download=fetch;save=persist??Save;clock=now;cache=Load(file);useFallback=startTimer;
            ApiPrices.ConfigureOfficial(cache.models);
            if(startTimer)timer=new Timer(_=>RefreshAsync(null,false),null,TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(1));
        }
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=OfficialPricePage.MaxBytes,RecursionLimit=16};}
        internal static OfficialPriceCache ParseCache(string json)
        {
            if(String.IsNullOrEmpty(json)||Encoding.UTF8.GetByteCount(json)>OfficialPricePage.MaxBytes)throw new InvalidDataException("官方价格缓存过大。");
            var data=Serializer().Deserialize<OfficialPriceCache>(json);
            if(data==null||data.schema!=1||data.source!=OfficialPricePage.Endpoint||data.basis!="standard-short"||data.models==null||data.checks==null)throw new InvalidDataException("官方价格缓存无效。");
            var clean=ApiPrices.Clean(data.models);if(clean.Count!=data.models.Count||clean.Any(p=>!OfficialPricePage.ValidId(p.Key)||p.Value[0]<0||p.Value[3]<0))throw new InvalidDataException("官方价格缓存金额无效。");
            if(data.checks.Any(p=>p.Value==null||p.Value.failures<0||p.Value.failures>10||p.Key.Length>160||p.Value.nextUtc> DateTime.UtcNow.AddDays(2)||!(p.Value.state=="official"||p.Value.state=="missing"||p.Value.state=="failed")))throw new InvalidDataException("官方价格缓存状态无效。");
            data.models=clean;data.checks=new Dictionary<string,OfficialPriceCheck>(data.checks,StringComparer.OrdinalIgnoreCase);
            // Recover valid oversized caches written by 1.9.13 instead of discarding
            // every official price. JSON bytes remain bounded before deserialization.
            LimitCache(data,new HashSet<string>(StringComparer.OrdinalIgnoreCase));return data;
        }
        private static void LimitCache(OfficialPriceCache data,HashSet<string> active)
        {
            // Only sort at capacity. Keep active models first, then the most recently
            // checked entries; deterministic ties make save/restart retention stable.
            if(data.models.Count>MaxModels)
                data.models=data.models.OrderByDescending(p=>active.Contains(p.Key)).ThenByDescending(p=>data.checks.ContainsKey(p.Key)?data.checks[p.Key].checkedUtc:DateTime.MinValue).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Take(MaxModels).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            if(data.checks.Count>MaxChecks)
                data.checks=data.checks.OrderByDescending(p=>active.Contains(p.Key)).ThenByDescending(p=>p.Value.checkedUtc).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Take(MaxChecks).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        }
        private static OfficialPriceCache Load(string file)
        {
            try{if(File.Exists(file)&&new FileInfo(file).Length<=OfficialPricePage.MaxBytes)return ParseCache(File.ReadAllText(file,Encoding.UTF8));}catch(Exception){}return OfficialPriceCache.Empty();
        }
        internal static void LoadCached(){ApiPrices.ConfigureOfficial(Load(Path.Combine(Program.DataFolder,"official-prices-v1.json")).models);}
        private void Save(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,json,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}}
        }
        internal void ObserveModels(IEnumerable<string> models){RefreshAsync(models,false);}
        internal Task<string> RefreshAsync(IEnumerable<string> models,bool manual)
        {
            lock(gate)
            {
                if(disposed)return Task.FromResult("已停止价格更新");
                if(models!=null)foreach(string model in models)if(!String.IsNullOrWhiteSpace(model)&&model.Length<=160&&observed.Count<MaxChecks)observed.Add(model.Trim());
                force|=manual;
                if(running!=null)return running;
                DateTime now=clock();if(!force&&(now<nextFetch||!observed.Any(m=>Due(m,now))))return Task.FromResult("价格已是最新检查结果");
                // Complete and release the shared task under the same gate. A manual
                // request arriving during worker teardown must start/join the next check.
                var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                running=completion.Task;Task.Run(()=>Run(completion));return running;
            }
        }
        private bool Due(string model,DateTime now){OfficialPriceCheck check;return !cache.checks.TryGetValue(model,out check)||check.nextUtc<=now;}
        internal string Status(string model)
        {
            lock(gate)
            {
                OfficialPriceCheck check;if(!cache.checks.TryGetValue(model,out check))return "等待核对官方价格";
                string state=check.state=="official"?"官方价格已核对":check.state=="missing"?"官方表暂未列出，保留已有价格":"更新失败，保留已有价格";
                return state+" · "+check.checkedUtc.ToLocalTime().ToString("MM-dd HH:mm",CultureInfo.InvariantCulture);
            }
        }
        private void Complete(TaskCompletionSource<string> completion,string message)
        {
            if(Object.ReferenceEquals(running,completion.Task))running=null;
            completion.TrySetResult(message);
        }
        private async Task Run(TaskCompletionSource<string> completion)
        {
            string message="价格检查完成";
            try
            {
                while(true)
                {
                    string[] due;OfficialPriceCache candidate;DateTime now=clock();
                    lock(gate)
                    {
                        due=observed.Where(m=>force||Due(m,now)).ToArray();force=false;
                        if(disposed||due.Length==0){Complete(completion,message);return;}
                        candidate=Serializer().Deserialize<OfficialPriceCache>(Serializer().Serialize(cache));
                        candidate.models=new Dictionary<string,decimal[]>(candidate.models,StringComparer.OrdinalIgnoreCase);
                        candidate.checks=new Dictionary<string,OfficialPriceCheck>(candidate.checks,StringComparer.OrdinalIgnoreCase);
                    }
                    // This fallback has its own revision namespace and never overwrites official values.
                    bool changed=false;
                    if(useFallback&&now>=nextFallback){nextFallback=now.AddHours(24);changed=await PriceCatalog.RefreshAsync(cancellation.Token).ConfigureAwait(false);}
                    Dictionary<string,decimal[]> table=null;bool failed=false;
                    try{table=OfficialPricePage.Parse(await download(cancellation.Token).ConfigureAwait(false));}
                    catch(Exception){cancellation.Token.ThrowIfCancellationRequested();failed=true;}
                    now=clock();
                    if(!failed){foreach(var pair in table)candidate.models[pair.Key]=pair.Value;candidate.fetchedUtc=now;}
                    var checkedModels=new HashSet<string>(due,StringComparer.OrdinalIgnoreCase);HashSet<string> active;
                    lock(gate)active=new HashSet<string>(observed,StringComparer.OrdinalIgnoreCase);
                    // The complete table also rechecks observed models whose cooldown
                    // has not expired. Update their status/date together with the price.
                    if(!failed)checkedModels.UnionWith(active.Where(table.ContainsKey));
                    foreach(string model in checkedModels)
                    {
                        OfficialPriceCheck old;candidate.checks.TryGetValue(model,out old);
                        int failures=failed?Math.Min(10,(old==null?0:old.failures)+1):0;
                        bool found=!failed&&table.ContainsKey(model);
                        double minutes=failed?(failures==1?5:failures==2?30:failures==3?120:360):found?1440:360;
                        candidate.checks[model]=new OfficialPriceCheck{checkedUtc=now,nextUtc=now.AddMinutes(minutes),failures=failures,state=failed?"failed":found?"official":"missing"};
                    }
                    LimitCache(candidate,active);
                    bool persisted=true;
                    try{save(Serializer().Serialize(candidate));}
                    catch(Exception){persisted=false;}
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock(gate)
                    {
                        if(disposed){Complete(completion,"已停止价格更新");return;}
                        if(persisted){cache=candidate;changed|=ApiPrices.ConfigureOfficial(candidate.models);}
                        // Disk failure must not publish a price that disappears on restart.
                        nextFetch=now.AddMinutes(persisted?1:5);
                    }
                    message=!persisted?"价格缓存保存失败，保留原价格":failed?"官方价格更新失败，保留已有价格":checkedModels.Count(m=>table.ContainsKey(m))+" 个模型已核对官方价格；未列出型号保留原价格";
                    if(changed){var handler=Changed;if(handler!=null)try{handler();}catch(Exception){}}
                    var checkedHandler=Checked;if(checkedHandler!=null)try{checkedHandler();}catch(Exception){}
                    lock(gate)
                    {
                        if(!force){Complete(completion,message);return;}
                    }
                }
            }
            catch(OperationCanceledException){lock(gate)Complete(completion,"已停止价格更新");}
            catch(Exception){lock(gate)Complete(completion,"价格更新失败，保留已有价格");}
        }
        public void Dispose()
        {lock(gate){if(disposed)return;disposed=true;Changed=null;Checked=null;}if(timer!=null)timer.Dispose();cancellation.Cancel();}
    }
}
