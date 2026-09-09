using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace CodexUserData
{
    internal sealed class ModelUsage
    {
        public string Model {get;set;} public string Effort {get;set;}
        public long Tokens {get;set;} public long Input {get;set;} public long Output {get;set;} public long CacheRead {get;set;} public long CacheWrite {get;set;} public long Requests {get;set;}
        public decimal EquivalentUsd {get;set;} public long UnpricedTokens {get;set;}
        internal void Add(long input,long output,long read,long write,long requests)
        {
            checked{Input+=input;Output+=output;CacheRead+=read;CacheWrite+=write;Tokens+=input+output+read+write;Requests+=requests;}
            long unknown;EquivalentUsd+=ApiPrices.Estimate(Model,input,output,read,write,out unknown);UnpricedTokens+=unknown;
        }
        internal static void Accumulate(List<ModelUsage> list,string model,string effort,long input,long output,long read,long write,long requests)
        {
            model=String.IsNullOrWhiteSpace(model)?"unknown":model;effort=String.IsNullOrWhiteSpace(effort)?"unknown":effort;
            var row=list.FirstOrDefault(m=>m.Model==model&&m.Effort==effort);if(row==null){row=new ModelUsage{Model=model,Effort=effort};list.Add(row);}row.Add(input,output,read,write,requests);
        }
    }
    internal static class ApiPrices
    {
        internal const string CheckedOn="2026-09-08";
        internal const string Basis="模型 API 等效值 · USD / 每百万 Tokens · 内置基准核对 2026-09-08；自定义价格优先";
        // USD / 1M tokens: fresh input, cached input, cache writes, output.
        // One explicit baseline makes historical totals comparable; not a reconstruction of invoices.
        private static readonly Dictionary<string,decimal[]> rates=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase) {
            {"gpt-6-astra",new[]{10m,1m,12.5m,50m}}, {"gpt-5.6-sol",new[]{4m,.4m,5m,20m}},
            {"gpt-5.6",new[]{4m,.4m,5m,20m}}, {"gpt-5.6-terra",new[]{2m,.2m,2.5m,12m}},
            {"gpt-5.6-luna",new[]{.2m,.02m,.25m,1.2m}}, {"gpt-5.5",new[]{5m,.5m,-1m,30m}},
            {"gpt-5.4",new[]{2.5m,.25m,-1m,15m}}, {"gpt-5.3-codex",new[]{1.75m,.175m,-1m,14m}}
        };
        private static volatile Dictionary<string,decimal[]> overrides=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase);
        internal static Dictionary<string,decimal[]> Clean(Dictionary<string,decimal[]> values)
        {
            var result=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase);
            if(values!=null)foreach(var pair in values)if(!String.IsNullOrWhiteSpace(pair.Key)&&pair.Value!=null&&pair.Value.Length==4&&pair.Value.All(v=>(v==-1||v>=0)&&v<=1000000))result[pair.Key.Trim()]=(decimal[])pair.Value.Clone();
            return result;
        }
        // Publish a copied, immutable lookup; one worker applies it before a complete aggregation.
        internal static void Configure(Dictionary<string,decimal[]> values){overrides=Clean(values);}
        internal static IEnumerable<string> DefaultModels {get{return rates.Keys;}}
        internal static decimal[] Default(string model){decimal[] value;return model!=null&&rates.TryGetValue(model,out value)?(decimal[])value.Clone():new[]{-1m,-1m,-1m,-1m};}
        internal static decimal Estimate(string model,long input,long output,long cached,long write,out long unknown)
        {
            decimal[] rate;var current=overrides;unknown=0;
            if(model==null||(!current.TryGetValue(model,out rate)&&!rates.TryGetValue(model,out rate))){return 0;}
            // Missing components contribute zero to this estimate; this does not assert free billing.
            return (input*Math.Max(0,rate[0])+cached*Math.Max(0,rate[1])+write*Math.Max(0,rate[2])+output*Math.Max(0,rate[3]))/1000000m;
        }
    }

    internal sealed class DailyUsage
    {
        public List<ModelUsage> Models {get;set;}
        public DailyUsage(){Models=new List<ModelUsage>();}
        public string Date {get;set;}
        public long Tokens {get;set;} public long Input {get;set;} public long Output {get;set;}
        public long CacheRead {get;set;} public long CacheWrite {get;set;} public long Reasoning {get;set;} public long Requests {get;set;}
        public decimal CostUsd {get;set;}
        internal void Add(long input,long output,long read,long write,long requests,long reasoning,decimal cost)
        {
            checked {Input+=input;Output+=output;CacheRead+=read;CacheWrite+=write;Tokens+=input+output+read+write;Requests+=requests;Reasoning+=reasoning;CostUsd+=cost;}
        }
        internal static DailyUsage[] Empty(DateTime today,int count)
        {
            var days=new DailyUsage[count];for(int i=0;i<count;i++)days[i]=new DailyUsage{Date=today.Date.AddDays(i-count+1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)};return days;
        }
        internal static DailyUsage[] Hours(DateTime today)
        {
            return Enumerable.Range(0,24).Select(h=>new DailyUsage{Date=today.Date.AddHours(h).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)}).ToArray();
        }
    }
    internal sealed class UsageSnapshot
    {
        public string[] KnownModels {get;set;}
        public List<ModelUsage> Models {get;set;}
        public List<QuotaBucket> Quotas {get;set;}
        public decimal EquivalentUsd {get;set;} public long UnpricedTokens {get;set;}
        public DailyUsage[] Daily {get;set;}
        public DailyUsage[] Hourly {get;set;}
        public int HourlyThrough {get;set;}
        public long HourlyUnallocatedTokens {get;set;}
        public string SourceName { get; set; }
        public string CountLabel { get; set; }
        public bool CostAvailable { get; set; }
        public long ReasoningTokens { get; set; }
        public long Sessions { get; set; }
        public long InferredRecords { get; set; }
        public int CoverageFiles { get; set; }
        public int CoverageWarnings { get; set; }
        public string Warning { get; set; }
        public UsageSnapshot() { KnownModels=new string[0]; SourceName="CC Switch"; CountLabel="请求数"; CostAvailable=true; Warning=""; Daily=new DailyUsage[0];Hourly=new DailyUsage[0];Models=new List<ModelUsage>();Quotas=new List<QuotaBucket>(); }
        public long TotalTokens { get; set; }
        public long Requests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        // Percentages are 0..100, not fractions.
        public double CacheHitRate { get; set; }
        public decimal CostUsd { get; set; }
        public string LatestRecord { get; set; }
        // Activity metadata is independent of the selected statistics range and contains no text.
        public long LatestUsageUnix {get;set;} public long ActivityUntil {get;set;} public int ActiveTasks {get;set;}
        public long SuccessCount { get; set; }
        public double SuccessRate { get; set; }
        public long DetailRequests { get; set; }
        public long ArchivedRequests { get; set; }
    }

    internal static class UsageDatabase
    {
        // Matches CC Switch v3.20.1's effective_usage_log_filter. Session/proxy
        // fingerprints deliberately compare the original stored token fields.
        private const string EffectiveLogFilter = @"
NOT (
 COALESCE(l.data_source, 'proxy') IN ('session_log', 'codex_session', 'gemini_session', 'opencode_session')
 AND EXISTS (
  SELECT 1 FROM proxy_request_logs proxy_dedup
  WHERE COALESCE(proxy_dedup.data_source, 'proxy') = 'proxy'
   AND proxy_dedup.app_type IN (l.app_type, CASE WHEN l.app_type = 'claude' THEN 'claude-desktop' ELSE l.app_type END)
   AND proxy_dedup.status_code >= 200 AND proxy_dedup.status_code < 300
   AND proxy_dedup.input_tokens = l.input_tokens
   AND proxy_dedup.output_tokens = l.output_tokens
   AND proxy_dedup.cache_read_tokens = l.cache_read_tokens
   AND (proxy_dedup.cache_creation_tokens = l.cache_creation_tokens
    OR (l.cache_creation_tokens = 0 AND COALESCE(l.data_source, 'proxy') IN ('codex_session', 'gemini_session', 'opencode_session')))
   AND proxy_dedup.created_at BETWEEN l.created_at - 600 AND l.created_at + 600
   AND (LOWER(proxy_dedup.model) = LOWER(l.model)
    OR LOWER(proxy_dedup.model) = 'unknown' OR LOWER(l.model) = 'unknown')
 )
)";

        private static string FreshInput(string alias)
        {
            // Legacy 0 subtracts cache READ only; total 1 subtracts both;
            // fresh 2 (including normalized rollups) is never subtracted again.
            // OpenCode already uses fresh-input semantics. Do not infer from model.
            return String.Format(CultureInfo.InvariantCulture, @"
CASE
 WHEN {0}.input_token_semantics = 2 THEN {0}.input_tokens
 WHEN {0}.app_type IN ('codex', 'gemini', 'grokbuild')
  AND {0}.input_token_semantics = 1
  AND {0}.input_tokens >= ({0}.cache_read_tokens + {0}.cache_creation_tokens)
 THEN {0}.input_tokens - {0}.cache_read_tokens - {0}.cache_creation_tokens
 WHEN {0}.app_type IN ('codex', 'gemini', 'grokbuild')
  AND {0}.input_token_semantics = 0
  AND {0}.input_tokens >= {0}.cache_read_tokens
 THEN {0}.input_tokens - {0}.cache_read_tokens
 ELSE {0}.input_tokens
END", alias);
        }

        private static readonly string DetailSql = @"
SELECT COUNT(*),
 COALESCE(SUM(" + FreshInput("l") + @"), 0),
 COALESCE(SUM(l.output_tokens), 0),
 COALESCE(SUM(l.cache_read_tokens), 0),
 COALESCE(SUM(l.cache_creation_tokens), 0),
 COALESCE(SUM(CAST(l.total_cost_usd AS REAL)), 0),
 COALESCE(SUM(CASE WHEN l.status_code >= 200 AND l.status_code < 300 THEN 1 ELSE 0 END), 0),
 MAX(l.created_at)
FROM proxy_request_logs l
WHERE l.created_at >= ?1 AND l.created_at <= ?2
 AND (?3 = '' OR (CASE WHEN l.app_type = 'claude-desktop' THEN 'claude' ELSE l.app_type END) = ?3)
 AND " + EffectiveLogFilter;

        private static readonly string RollupSql = @"
SELECT COALESCE(SUM(r.request_count), 0),
 COALESCE(SUM(" + FreshInput("r") + @"), 0),
 COALESCE(SUM(r.output_tokens), 0),
 COALESCE(SUM(r.cache_read_tokens), 0),
 COALESCE(SUM(r.cache_creation_tokens), 0),
 COALESCE(SUM(CAST(r.total_cost_usd AS REAL)), 0),
 COALESCE(SUM(r.success_count), 0),
 MAX(r.date)
FROM usage_daily_rollups r
WHERE r.date >= ?1 AND r.date <= ?2
 AND (?3 = '' OR (CASE WHEN r.app_type = 'claude-desktop' THEN 'claude' ELSE r.app_type END) = ?3)";

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static UsageSnapshot Read(string dbPath, string range, string app, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                throw new FileNotFoundException("找不到 CC Switch 数据库，请确认 CC Switch 已初始化或选择正确的数据库。");

            DateTime localNow = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : DateTime.SpecifyKind(now, DateTimeKind.Local);
            DateTime? start;
            switch (range)
            {
                case "today": start = localNow.Date; break;
                case "week": start = localNow.Date.AddDays(-6); break;
                case "month": start = localNow.Date.AddDays(-29); break;
                case "all": start = null; break;
                default: throw new ArgumentException("不支持的统计日期范围。");
            }

            long startSeconds = start.HasValue ? ToUnixSeconds(start.Value) : Int64.MinValue;
            long endSeconds = ToUnixSeconds(localNow);
            string firstRollupDay = start.HasValue ? start.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "0001-01-01";
            // Official end bound includes today only when local hour/minute is
            // 23:59. Earlier current-day totals come exclusively from details.
            DateTime lastRollupDay = (localNow.Hour == 23 && localNow.Minute == 59) ? localNow.Date : localNow.Date.AddDays(-1);
            string lastRollupDate = lastRollupDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            UsageSnapshot result = new UsageSnapshot();
            result.Daily=DailyUsage.Empty(localNow,180);
            result.Hourly=DailyUsage.Hours(localNow);result.HourlyThrough=localNow.Hour+1;
            result.LatestRecord = "暂无记录";
            double detailCost = 0;
            double archivedCost = 0;
            long? latestDetail = null;
            string latestArchive = null;

            using (ReadConnection db = new ReadConnection(dbPath))
            {
                // One read transaction keeps the detail and rollup views aligned
                // while CC Switch atomically archives/deletes old detail rows.
                db.Execute("PRAGMA query_only = ON");
                db.Execute("BEGIN");
                try
                {
                    using (ReadStatement statement = db.Prepare(DetailSql))
                    {
                        statement.Bind(1, startSeconds);
                        statement.Bind(2, endSeconds);
                        statement.Bind(3, app ?? String.Empty);
                        statement.ReadRow();
                        result.DetailRequests = statement.Int64(0);
                        result.Requests = result.DetailRequests;
                        result.InputTokens = statement.Int64(1);
                        result.OutputTokens = statement.Int64(2);
                        result.CacheReadTokens = statement.Int64(3);
                        result.CacheCreationTokens = statement.Int64(4);
                        detailCost = statement.Double(5);
                        result.SuccessCount = statement.Int64(6);
                        if (!statement.IsNull(7)) latestDetail = statement.Int64(7);
                    }
                    if (String.CompareOrdinal(firstRollupDay, lastRollupDate) <= 0)
                    {
                        using (ReadStatement statement = db.Prepare(RollupSql))
                        {
                            statement.Bind(1, firstRollupDay);
                            statement.Bind(2, lastRollupDate);
                            statement.Bind(3, app ?? String.Empty);
                            statement.ReadRow();
                            result.ArchivedRequests = statement.Int64(0);
                            checked
                            {
                                result.Requests += result.ArchivedRequests;
                                result.InputTokens += statement.Int64(1);
                                result.OutputTokens += statement.Int64(2);
                                result.CacheReadTokens += statement.Int64(3);
                                result.CacheCreationTokens += statement.Int64(4);
                                result.SuccessCount += statement.Int64(6);
                            }
                            archivedCost = statement.Double(5);
                            latestArchive = statement.Text(7);
                        }
                    }
                    ReadDaily(db,result.Daily,endSeconds,lastRollupDate,app);
                    ReadHourly(db,result.Hourly,localNow,endSeconds,app);
                    ReadModels(db,result,startSeconds,endSeconds,firstRollupDay,lastRollupDate,app);
                }
                finally
                {
                    // ROLLBACK ends the read snapshot; no database writes occurred.
                    db.Execute("ROLLBACK");
                }
            }

            long cacheableInput;
            checked
            {
                cacheableInput = result.InputTokens + result.CacheReadTokens + result.CacheCreationTokens;
                result.TotalTokens = cacheableInput + result.OutputTokens;
            }
            result.CacheHitRate = cacheableInput > 0 ? (100.0 * result.CacheReadTokens / cacheableInput) : 0.0;
            // A daily rollup has no hour-of-day detail. Never distribute it evenly across hours.
            result.HourlyUnallocatedTokens=Math.Max(0,result.Daily.Last().Tokens-result.Hourly.Sum(h=>h.Tokens));
            result.SuccessRate = result.Requests > 0 ? (100.0 * result.SuccessCount / result.Requests) : 0.0;
            // CC Switch sums stored USD costs as SQLite REAL, displaying 6 places.
            result.CostUsd = Decimal.Parse((detailCost + archivedCost).ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (latestDetail.HasValue)
                result.LatestRecord = Epoch.AddSeconds(latestDetail.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            else if (!String.IsNullOrEmpty(latestArchive))
                result.LatestRecord = latestArchive + "（历史汇总）";
            return result;
        }
        internal static string[] ModelCatalog(string path)
        {
            var models=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using(var db=new ReadConnection(path))
            {
                foreach(string table in new[]{"proxy_request_logs","usage_daily_rollups"})
                using(var rows=db.Prepare("SELECT DISTINCT model FROM "+table))while(rows.Next()){string model=rows.Text(0);if(!String.IsNullOrWhiteSpace(model))models.Add(model);}
            }
            return models.OrderBy(m=>m).ToArray();
        }
        private static void ReadModels(ReadConnection db,UsageSnapshot result,long from,long until,string first,string last,string app)
        {
            string tail=" AND (?3='' OR (CASE WHEN l.app_type='claude-desktop' THEN 'claude' ELSE l.app_type END)=?3)";
            string detail="SELECT l.model,SUM("+FreshInput("l")+"),SUM(l.output_tokens),SUM(l.cache_read_tokens),SUM(l.cache_creation_tokens),COUNT(*) FROM proxy_request_logs l WHERE l.created_at>=?1 AND l.created_at<=?2"+tail+" AND "+EffectiveLogFilter+" GROUP BY l.model";
            string rollup="SELECT l.model,SUM("+FreshInput("l")+"),SUM(l.output_tokens),SUM(l.cache_read_tokens),SUM(l.cache_creation_tokens),SUM(l.request_count) FROM usage_daily_rollups l WHERE l.date>=?1 AND l.date<=?2"+tail+" GROUP BY l.model";
            using(var rows=db.Prepare(detail)){rows.Bind(1,from);rows.Bind(2,until);rows.Bind(3,app??"");AddModels(rows,result.Models);}
            using(var rows=db.Prepare(rollup)){rows.Bind(1,first);rows.Bind(2,last);rows.Bind(3,app??"");AddModels(rows,result.Models);}
            result.EquivalentUsd=result.Models.Sum(m=>m.EquivalentUsd);result.UnpricedTokens=result.Models.Sum(m=>m.UnpricedTokens);
        }
        private static void AddModels(ReadStatement rows,List<ModelUsage> models){while(rows.Next())ModelUsage.Accumulate(models,rows.Text(0),"unknown",rows.Int64(1),rows.Int64(2),rows.Int64(3),rows.Int64(4),rows.Int64(5));}

        private static long ToUnixSeconds(DateTime localTime)
        {
            return (long)Math.Floor((localTime.ToUniversalTime() - Epoch).TotalSeconds);
        }

        private static void ReadDaily(ReadConnection db,DailyUsage[] days,long until,string lastRollup,string app)
        {
            // Two grouped queries share the same read transaction as the headline.
            // Do not query once per square: that would multiply SQLite work by 180.
            var byDate=new Dictionary<string,DailyUsage>();foreach(var day in days)byDate[day.Date]=day;
            // Windows' bundled SQLite can return NULL for the localtime modifier.
            // Generate local-midnight UTC bounds in .NET, also covering 23/25-hour DST days.
            var calendar=new StringBuilder("WITH calendar(day,lo,hi) AS (VALUES ");
            for(int i=0;i<days.Length;i++){DateTime day=DateTime.ParseExact(days[i].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture);if(i>0)calendar.Append(',');calendar.Append("('").Append(days[i].Date).Append("',").Append(ToUnixSeconds(day)).Append(',').Append(ToUnixSeconds(day.AddDays(1))).Append(')');}
            calendar.Append(") ");
            string detail=calendar+@"SELECT calendar.day,COUNT(*),SUM("+FreshInput("l")+@"),SUM(l.output_tokens),SUM(l.cache_read_tokens),SUM(l.cache_creation_tokens),SUM(CAST(l.total_cost_usd AS REAL)),l.model
FROM calendar JOIN proxy_request_logs l ON l.created_at>=calendar.lo AND l.created_at<calendar.hi WHERE l.created_at>=?1 AND l.created_at<=?2
AND (?3='' OR (CASE WHEN l.app_type='claude-desktop' THEN 'claude' ELSE l.app_type END)=?3) AND "+EffectiveLogFilter+" GROUP BY 1,l.model";
            string rollup=@"SELECT r.date,SUM(r.request_count),SUM("+FreshInput("r")+@"),SUM(r.output_tokens),SUM(r.cache_read_tokens),SUM(r.cache_creation_tokens),SUM(CAST(r.total_cost_usd AS REAL)),r.model
FROM usage_daily_rollups r WHERE r.date>=?1 AND r.date<=?2
AND (?3='' OR (CASE WHEN r.app_type='claude-desktop' THEN 'claude' ELSE r.app_type END)=?3) GROUP BY 1,r.model";
            using(var rows=db.Prepare(detail))
            {
                rows.Bind(1,ToUnixSeconds(DateTime.ParseExact(days[0].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture)));rows.Bind(2,until);rows.Bind(3,app??"");AddDailyRows(rows,byDate);
            }
            using(var rows=db.Prepare(rollup))
            {
                rows.Bind(1,days[0].Date);rows.Bind(2,lastRollup);rows.Bind(3,app??"");AddDailyRows(rows,byDate);
            }
        }
        private static void AddDailyRows(ReadStatement rows,Dictionary<string,DailyUsage> days)
        {
            while(rows.Next())
            {
                DailyUsage day;if(!days.TryGetValue(rows.Text(0),out day))continue;
                day.Add(rows.Int64(2),rows.Int64(3),rows.Int64(4),rows.Int64(5),rows.Int64(1),0,(decimal)rows.Double(6));
                ModelUsage.Accumulate(day.Models,rows.Text(7),"unknown",rows.Int64(2),rows.Int64(3),rows.Int64(4),rows.Int64(5),rows.Int64(1));
            }
        }

        private static void ReadHourly(ReadConnection db,DailyUsage[] hours,DateTime now,long until,string app)
        {
            // One grouped read in the same snapshot; no per-hour queries and no extra database scan per graph.
            var calendar=new StringBuilder("WITH calendar(day,lo,hi) AS (VALUES ");
            for(int h=0;h<24;h++){if(h>0)calendar.Append(',');calendar.Append("('").Append(hours[h].Date).Append("',").Append(ToUnixSeconds(now.Date.AddHours(h))).Append(',').Append(ToUnixSeconds(now.Date.AddHours(h+1))).Append(')');}
            calendar.Append(") ");
            string sql=calendar+"SELECT calendar.day,COUNT(*),SUM("+FreshInput("l")+"),SUM(l.output_tokens),SUM(l.cache_read_tokens),SUM(l.cache_creation_tokens),SUM(CAST(l.total_cost_usd AS REAL)),l.model FROM calendar JOIN proxy_request_logs l ON l.created_at>=calendar.lo AND l.created_at<calendar.hi WHERE l.created_at>=?1 AND l.created_at<=?2 AND (?3='' OR (CASE WHEN l.app_type='claude-desktop' THEN 'claude' ELSE l.app_type END)=?3) AND "+EffectiveLogFilter+" GROUP BY 1,l.model";
            using(var rows=db.Prepare(sql)){rows.Bind(1,ToUnixSeconds(now.Date));rows.Bind(2,until);rows.Bind(3,app??"");AddDailyRows(rows,hours.ToDictionary(h=>h.Date));}
        }

        private sealed class ReadConnection : IDisposable
        {
            private IntPtr handle;
            internal ReadConnection(string path)
            {
                // READONLY only: never create an empty DB or change user settings.
                // Do not use immutable=1: a live CC Switch database may have a WAL.
                int code = Native.sqlite3_open_v2(Native.Utf8(path), out handle, 1, IntPtr.Zero);
                if (code != 0)
                {
                    string message = ErrorMessage(code);
                    Dispose();
                    throw new InvalidOperationException(message);
                }
                Check(Native.sqlite3_busy_timeout(handle, 2000));
            }

            internal ReadStatement Prepare(string sql)
            {
                IntPtr statement;
                int code = Native.sqlite3_prepare_v2(handle, Native.Utf8(sql), -1, out statement, IntPtr.Zero);
                if (code != 0)
                {
                    if (statement != IntPtr.Zero) Native.sqlite3_finalize(statement);
                    Check(code);
                }
                return new ReadStatement(this, statement);
            }

            internal void Execute(string sql)
            {
                using (ReadStatement statement = Prepare(sql)) statement.Execute();
            }

            internal void Check(int code)
            {
                if (code != 0) throw new InvalidOperationException(ErrorMessage(code));
            }

            private string ErrorMessage(int code)
            {
                if (code == 5 || code == 6) return "CC Switch 数据库暂时忙碌，将在下次刷新时重试。";
                if (code == 14) return "无法只读打开 CC Switch 数据库，请检查路径和文件权限。";
                if (code == 11 || code == 26) return "无法读取数据库：文件损坏或不是兼容的 SQLite 数据库。";
                // Error text is schema/SQLite diagnostics only; no user row data
                // (keys, prompts, settings, or request errors) is ever queried.
                string detail = handle == IntPtr.Zero ? String.Empty : Native.ReadUtf8(Native.sqlite3_errmsg(handle));
                return "统计读取失败（SQLite " + code.ToString(CultureInfo.InvariantCulture) + "）：" + detail;
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                Native.sqlite3_close(handle);
                handle = IntPtr.Zero;
            }
        }

        private sealed class ReadStatement : IDisposable
        {
            private readonly ReadConnection connection;
            private IntPtr handle;
            internal ReadStatement(ReadConnection connection, IntPtr handle)
            {
                this.connection = connection;
                this.handle = handle;
            }
            internal void Bind(int position, long value) { connection.Check(Native.sqlite3_bind_int64(handle, position, value)); }
            internal void Bind(int position, string value)
            {
                byte[] utf8 = Native.Utf8(value);
                // SQLITE_TRANSIENT copies the bytes before the managed buffer moves.
                connection.Check(Native.sqlite3_bind_text(handle, position, utf8, utf8.Length - 1, new IntPtr(-1)));
            }
            internal void ReadRow()
            {
                int code = Native.sqlite3_step(handle);
                if (code == 101) throw new InvalidOperationException("数据库未返回统计结果。");
                if (code != 100) connection.Check(code);
            }
            internal bool Next(){int code=Native.sqlite3_step(handle);if(code==101)return false;if(code!=100)connection.Check(code);return true;}
            internal void Execute()
            {
                int code = Native.sqlite3_step(handle);
                if (code != 100 && code != 101) connection.Check(code);
            }
            internal long Int64(int column) { return Native.sqlite3_column_int64(handle, column); }
            internal double Double(int column) { return Native.sqlite3_column_double(handle, column); }
            internal bool IsNull(int column) { return Native.sqlite3_column_type(handle, column) == 5; }
            internal string Text(int column) { return IsNull(column) ? null : Native.ReadUtf8(Native.sqlite3_column_text(handle, column)); }
            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                Native.sqlite3_finalize(handle);
                handle = IntPtr.Zero;
            }
        }

        private static class Native
        {
            // Windows 10/11 ships this SQLite library; no Python/service/package
            // needs to remain running, keeping the floating widget lightweight.
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_close(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_step(IntPtr statement);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_finalize(IntPtr statement);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_bind_int64(IntPtr statement, int position, long value);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_bind_text(IntPtr statement, int position, byte[] value, int length, IntPtr destructor);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern long sqlite3_column_int64(IntPtr statement, int column);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern double sqlite3_column_double(IntPtr statement, int column);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_column_type(IntPtr statement, int column);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
            internal static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
            internal static string ReadUtf8(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return String.Empty;
                int length = 0;
                while (Marshal.ReadByte(pointer, length) != 0) length++;
                byte[] buffer = new byte[length];
                Marshal.Copy(pointer, buffer, 0, length);
                return Encoding.UTF8.GetString(buffer);
            }
        }
    }
}
