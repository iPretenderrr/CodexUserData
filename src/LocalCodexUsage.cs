using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Only numeric usage, timestamps and identifiers enter this cache. Never store messages/tools/auth.
    internal sealed class TokenCounters
    {
        public long Input, Cached, CacheWrite, Output, Reasoning, Total;
        public string Signature;
    }
    internal sealed class LocalUsageEvent
    {
        public long Time;
        public string Signature, Model, Effort;
        public long Input, Cached, CacheWrite, Output, Reasoning;
        public bool Inferred;
    }
    internal sealed class LogCursor
    {
        internal object MemberCopy(){return MemberwiseClone();}
        public string Id, Parent, Path, Model = "unknown", Effort="unknown", Previous, Head, Tail;
        public List<QuotaBucket> Quotas=new List<QuotaBucket>();
        public long Started, Offset, Length, Modified, Created;
        public long TaskStarted, TaskEnded, TaskOrder;
        public string TaskTurn, TaskEndedTurn;
        public bool TaskRunning;
        public int Invalid;
        public bool Meta;
        public TokenCounters High;
        public Dictionary<string, string> Lanes = new Dictionary<string, string>();
        public List<LocalUsageEvent> Events = new List<LocalUsageEvent>();
        // An irrelevant or oversized partial line has already been classified. Retain only
        // its byte checkpoint/hash in memory; cache restarts still resume at the last newline.
        internal readonly object MilestoneIdentity=new object();
        internal long MilestoneVersion;
        internal long SkippedOffset;
        internal string SkippedTail;
    }
    internal sealed class LogCache
    {
        public int Version = 4;
        public string Root = "";
        public List<LogCursor> Files = new List<LogCursor>();
    }
    // Range changes reuse the numeric ledger. Recompute only after a ledger/price
    // change, a local hour boundary, or a timestamp that was not yet observable.
    internal sealed class UsageSnapshotMemo
    {
        private readonly Dictionary<string,UsageSnapshot> ranges=new Dictionary<string,UsageSnapshot>();
        private object prices;
        private string hour;
        private int version=-1,failures=-1;
        private long readAt,nextChange=Int64.MaxValue;
        internal UsageSnapshot Get(Dictionary<string,LogCursor> records,int dataVersion,string range,DateTime now,int failed=0)
        {
            if(now.Kind==DateTimeKind.Utc)now=now.ToLocalTime();
            PriceState priceState=ApiPrices.Snapshot();long at=LocalCodexUsage.Unix(now);string clock=now.ToString("yyyyMMddHH",CultureInfo.InvariantCulture)+"/"+TimeZoneInfo.Local.GetUtcOffset(now).Ticks;
            if(version!=dataVersion||failures!=failed||prices!=priceState||hour!=clock||at<readAt||at>=nextChange)
            {ranges.Clear();version=dataVersion;failures=failed;prices=priceState;hour=clock;nextChange=Int64.MaxValue;}
            readAt=at;UsageSnapshot value;
            if(!ranges.TryGetValue(range,out value))
            {
                UsageRangeSpec custom;
                if(UsageRangeSpec.TryParse(range,out custom))
                {
                    // Custom intervals need event timestamps, so aggregate directly from
                    // the already-read ledger instead of rounding to whole days.
                    value=LocalCodexUsage.Aggregate(records,range,now,failed,priceState);ranges[range]=value;
                }
                else
                {
                    UsageSnapshot all;
                    if(!ranges.TryGetValue("all",out all)){all=LocalCodexUsage.Aggregate(records,"all",now,failed,priceState);ranges["all"]=all;nextChange=all.NextChangeAt;}
                    value=Select(all,range,now,priceState);ranges[range]=value;
                }
            }
            return value.Copy();
        }
        private static UsageSnapshot Select(UsageSnapshot all,string range,DateTime now,PriceState prices)
        {
            if(range=="all")return all;
            int count=range=="week"?7:range=="month"?30:1,index=count==1?0:count==7?1:2;
            var result=all.Copy();var days=all.Daily.Skip(Math.Max(0,all.Daily.Length-count)).ToArray();
            result.TotalTokens=days.Sum(d=>d.Tokens);result.InputTokens=days.Sum(d=>d.Input);result.OutputTokens=days.Sum(d=>d.Output);
            result.CacheReadTokens=days.Sum(d=>d.CacheRead);result.CacheCreationTokens=days.Sum(d=>d.CacheWrite);result.ReasoningTokens=days.Sum(d=>d.Reasoning);result.Requests=days.Sum(d=>d.Requests);
            result.Models=new List<ModelUsage>();foreach(var model in days.SelectMany(d=>d.Models))ModelUsage.Accumulate(result.Models,model.Model,model.Effort,model.Input,model.Output,model.CacheRead,model.CacheWrite,model.Requests,prices);
            result.EquivalentUsd=result.Models.Sum(m=>m.EquivalentUsd);result.UnpricedTokens=result.Models.Sum(m=>m.UnpricedTokens);
            long input=result.InputTokens+result.CacheReadTokens+result.CacheCreationTokens;result.CacheHitRate=input>0?result.CacheReadTokens*100.0/input:0;
            result.Sessions=all.PeriodSessions[index];result.InferredRecords=all.PeriodInferred[index];
            result.LatestRecord=all.LatestUsageUnix>=LocalCodexUsage.Unix(now.Date.AddDays(1-count))?all.LatestRecord:"暂无记录";
            result.Warning=all.CommonWarning;if(result.InferredRecords>0)result.Warning+=(result.Warning.Length>0?"\n":"")+result.InferredRecords+" 条旧记录由累计计数差值还原。";
            return result;
        }
    }
    internal sealed class MilestoneLedgerMemo
    {
        private sealed class Stamp
        {
            internal object Identity;internal string Id,Parent;internal long Version,Started;internal bool Meta;internal int Count,Invalid;
            internal Stamp(LogCursor c){Identity=c.MilestoneIdentity;Id=c.Id;Parent=c.Parent;Version=c.MilestoneVersion;Started=c.Started;Meta=c.Meta;Count=c.Events.Count;Invalid=c.Invalid;}
            internal bool Same(LogCursor c){return Identity==c.MilestoneIdentity&&Id==c.Id&&Parent==c.Parent&&Version==c.MilestoneVersion&&Started==c.Started&&Meta==c.Meta&&Count==c.Events.Count&&Invalid==c.Invalid;}
        }
        private Dictionary<string,Stamp> stamps=new Dictionary<string,Stamp>();
        private MilestoneInput value;private long readAt;private int failures;
        internal MilestoneInput Get(Dictionary<string,LogCursor> records,DateTime now,int failed,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();long at=LocalCodexUsage.Unix(now);
            bool same=value!=null&&failed==failures&&at>=readAt&&at<value.NextChangeAt&&stamps.Count==records.Count;
            if(same)foreach(var pair in records){cancel.ThrowIfCancellationRequested();Stamp stamp;if(!stamps.TryGetValue(pair.Key,out stamp)||!stamp.Same(pair.Value)){same=false;break;}}
            if(!same)
            {
                var next=LocalCodexUsage.BuildMilestones(records,now,failed,cancel);
                var nextStamps=new Dictionary<string,Stamp>();foreach(var pair in records)nextStamps[pair.Key]=new Stamp(pair.Value);
                value=next;stamps=nextStamps;failures=failed;
            }
            readAt=at;return value;
        }
    }
    internal sealed class LocalCodexUsage
    {
        private readonly string root, cachePath;
        private readonly Dictionary<string, LogCursor> cursors;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 100 };
        private DateTime saved;
        private bool dirty;
        private readonly bool readOnlyCache;
        private readonly bool remoteCache;
        internal long LastBytesRead { get; private set; }
        internal int DataVersion {get;private set;}
        private readonly UsageSnapshotMemo snapshots=new UsageSnapshotMemo();
        private readonly MilestoneLedgerMemo milestones=new MilestoneLedgerMemo();
        private int lastFailures;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal LocalCodexUsage(string directory,string cacheFile,bool readOnly=false):this(directory,cacheFile,readOnly,false){}
        internal LocalCodexUsage(string directory, string cacheFile,bool readOnly,bool remote)
        {
            root = System.IO.Path.GetFullPath(directory); cachePath = cacheFile;readOnlyCache=readOnly;remoteCache=remote;
            cursors=new Dictionary<string,LogCursor>(remote?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase);
            LoadCache();
        }
        internal static long Unix(DateTime date) { return (long)(date.ToUniversalTime() - Epoch).TotalSeconds; }
        internal static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        private static object Get(Dictionary<string, object> obj, string key) { object value; return obj != null && obj.TryGetValue(key, out value) ? value : null; }
        private static string Str(Dictionary<string, object> obj, string key) { return Get(obj, key) as string; }
        private static long Num(Dictionary<string, object> obj, string key)
        {
            object value = Get(obj, key); long number;
            return value != null && Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? Math.Max(0, number) : 0;
        }
        private static long Timestamp(object value)
        {
            DateTimeOffset date;
            return value is string && DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) ? date.ToUnixTimeSeconds() : 0;
        }
        private static TokenCounters Counters(object value)
        {
            var obj = Obj(value); if (obj == null || Get(obj, "input_tokens") == null || Get(obj, "output_tokens") == null) return null;
            TokenCounters c = new TokenCounters { Input = Num(obj, "input_tokens"), Cached = Get(obj, "cached_input_tokens") != null ? Num(obj, "cached_input_tokens") : Num(obj, "cache_read_input_tokens"), CacheWrite = Num(obj,"cache_write_input_tokens"), Output = Num(obj, "output_tokens"), Reasoning = Num(obj, "reasoning_output_tokens"), Total = Num(obj, "total_tokens") };
            // Include missing-vs-zero distinctions when identifying quota refresh replays.
            c.Signature = String.Join(",", new[] { "input_tokens", "cached_input_tokens", "cache_read_input_tokens", "cache_write_input_tokens", "output_tokens", "reasoning_output_tokens", "total_tokens" }.Select(k => Get(obj,k) == null ? "?" : Convert.ToString(Get(obj,k), CultureInfo.InvariantCulture)));
            return c;
        }
        private void ParseLine(LogCursor cursor, string text)
        {
            if (text.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 && text.IndexOf("\"session_meta\"", StringComparison.Ordinal) < 0 && text.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0 && !ActivityLine(text)) return;
            Dictionary<string, object> entry;
            try { entry = Obj(json.DeserializeObject(text)); }
            catch (ArgumentException) { cursor.Invalid++; return; }
            if (entry == null) return;
            string type = Str(entry, "type"); var payload = Obj(Get(entry, "payload"));
            if(type=="event_msg")
            {
                string activity=Str(payload,"type");long at=Timestamp(Get(entry,"timestamp"));
                if(activity=="task_started"||activity=="turn_started"||activity=="task_complete"||activity=="turn_completed"||activity=="turn_aborted"||activity=="task_failed")
                {
                    // Statistics and the live tail share turn identity/order rules. Older caches
                    // omit the new fields safely; a stale completion cannot stop a newer turn.
                    var task=new ActivityTurn{Session=cursor.Id,Created=cursor.Started,Started=cursor.TaskStarted,Ended=cursor.TaskEnded,
                        Signal=Math.Max(cursor.TaskStarted,cursor.TaskEnded),Order=cursor.TaskOrder,Turn=cursor.TaskTurn,EndedTurn=cursor.TaskEndedTurn,Running=cursor.TaskRunning,Explicit=true};
                    DateTimeOffset precise;long order=DateTimeOffset.TryParse(Str(entry,"timestamp"),out precise)?precise.ToUnixTimeMilliseconds():0;
                    task.Accept(type,payload,at,order);cursor.TaskStarted=task.Started;cursor.TaskEnded=task.Ended;cursor.TaskOrder=task.Order;
                    cursor.TaskTurn=task.Turn;cursor.TaskEndedTurn=task.EndedTurn;cursor.TaskRunning=task.Running;return;
                }
            }
            if (type == "session_meta" && !cursor.Meta)
            {
                cursor.Meta = true;
                string id = Str(payload, "id") ?? Str(payload, "session_id");
                if (!String.IsNullOrEmpty(id) && !String.Equals(id, cursor.Id, StringComparison.OrdinalIgnoreCase) && !String.Equals(id,SegmentSessionId(cursor.Path,cursor.Id),StringComparison.OrdinalIgnoreCase)) { cursor.Invalid++; cursor.Meta = false; return; }
                cursor.Started = Timestamp(Get(payload, "timestamp")); if (cursor.Started == 0) cursor.Started = Timestamp(Get(entry, "timestamp"));
                var source = Obj(Get(payload, "source")); var sub = Obj(Get(source, "subagent")); var spawn = Obj(Get(sub, "thread_spawn"));
                cursor.Parent = Str(payload, "forked_from_id") ?? Str(payload, "parent_thread_id") ?? Str(spawn, "parent_thread_id");
                return;
            }
            if (type == "turn_context") { cursor.Model = Str(payload, "model") ?? cursor.Model;cursor.Effort=Str(payload,"effort")??Str(payload,"reasoning_effort")??"unknown"; return; }
            if (type != "event_msg" || Str(payload, "type") != "token_count") return;
            var quota=QuotaReader.Parse(Get(payload,"rate_limits"),Timestamp(Get(entry,"timestamp")),"日志快照");
            if(quota!=null){var old=cursor.Quotas.FirstOrDefault(q=>q.Id==quota.Id);if(old==null||old.ObservedAt<=quota.ObservedAt){if(old!=null)cursor.Quotas.Remove(old);cursor.Quotas.Add(quota);}}
            var info = Obj(Get(payload, "info")); if (info == null) return;
            var total = Counters(Get(info, "total_token_usage")); var last = Counters(Get(info, "last_token_usage"));
            if (total == null && last == null) return;
            string signature = (total == null ? "-" : total.Signature) + "/" + (last == null ? "-" : last.Signature);
            string lane = Str(Obj(Get(payload, "rate_limits")), "limit_id") ?? "";
            string prior; bool duplicate = total != null && ((cursor.Lanes.TryGetValue(lane, out prior) && prior == signature) || cursor.Previous == signature);
            if (total != null) cursor.Lanes[lane] = signature;
            cursor.Previous = signature;
            TokenCounters delta = new TokenCounters();
            if (!duplicate)
            {
                if (last != null) delta = last;
                else
                {
                    TokenCounters high = cursor.High ?? new TokenCounters();
                    delta.Input = Math.Max(0, total.Input-high.Input); delta.Cached = Math.Max(0, total.Cached-high.Cached);
                    delta.CacheWrite = Math.Max(0,total.CacheWrite-high.CacheWrite);
                    delta.Output = Math.Max(0, total.Output-high.Output); delta.Reasoning = Math.Max(0, total.Reasoning-high.Reasoning);
                }
            }
            if (total != null)
            {
                if (cursor.High == null) cursor.High = new TokenCounters();
                cursor.High.Input = Math.Max(cursor.High.Input, total.Input); cursor.High.Cached = Math.Max(cursor.High.Cached, total.Cached);
                cursor.High.CacheWrite = Math.Max(cursor.High.CacheWrite,total.CacheWrite);
                cursor.High.Output = Math.Max(cursor.High.Output, total.Output); cursor.High.Reasoning = Math.Max(cursor.High.Reasoning, total.Reasoning);
            }
            long time = Timestamp(Get(entry, "timestamp"));
            if (time == 0) { cursor.Invalid++; return; }
            string eventModel=Str(info,"model")??Str(info,"model_name")??Str(payload,"model");
            if(eventModel!=null&&eventModel!=cursor.Model){cursor.Model=eventModel;cursor.Effort="unknown";}
            cursor.Events.Add(new LocalUsageEvent { Time = time, Signature = signature, Model = cursor.Model, Effort=cursor.Effort, Input = delta.Input, Cached = Math.Min(delta.Input, delta.Cached), CacheWrite = Math.Min(Math.Max(0,delta.Input-delta.Cached),delta.CacheWrite), Output = delta.Output, Reasoning = Math.Min(delta.Output, delta.Reasoning), Inferred = !duplicate && last == null });
        }

        internal static string Fingerprint(Stream stream, long offset, int count)
        {
            stream.Position = Math.Max(0, offset); byte[] data = new byte[count]; int used=0,read;
            while(used<count&&(read=stream.Read(data,used,count-used))>0)used+=read;
            using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(data,0,used));
        }
        private bool ReadFile(LogCursor cursor, FileInfo file, Func<bool> cancel)
        {
            using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            {
                ReadStream(cursor, new LogFile {Path=file.FullName,Length=stream.Length,Modified=file.LastWriteTimeUtc.Ticks,Created=file.CreationTimeUtc.Ticks},stream,cancel,Int64.MaxValue/2);
            }
            return true;
        }
        internal void ReadStream(LogCursor cursor, LogFile file, Stream stream, Func<bool> cancel, long budget)
        {
                int oldCount=cursor.Events.Count,oldInvalid=cursor.Invalid;bool oldMeta=cursor.Meta;string oldParent=cursor.Parent;long oldStarted=cursor.Started;
                long length = file.Length; long limit = Math.Min(length, Math.Max(cursor.Offset,cursor.SkippedOffset) + budget);
                string head = Fingerprint(stream, 0, (int)Math.Min(512, length));
                bool reset = cursor.Offset > length || (cursor.Head != null && cursor.Head != head && cursor.Length >= 512) || (cursor.Path == file.Path && cursor.Created != file.Created) || (cursor.Length == length && cursor.Modified != file.Modified && cursor.Offset == length);
                if (!reset && cursor.Offset > 0 && cursor.Tail != null) reset = cursor.Tail != Fingerprint(stream, Math.Max(0,cursor.Offset-64), (int)Math.Min(64,cursor.Offset));
                if(!reset&&cursor.SkippedOffset>cursor.Offset)reset=cursor.SkippedOffset>length||cursor.SkippedTail!=Fingerprint(stream,Math.Max(0,cursor.SkippedOffset-64),(int)Math.Min(64,cursor.SkippedOffset));
                if (reset)
                {
                    cursor.Events.Clear(); cursor.Lanes.Clear();cursor.Quotas.Clear();cursor.Effort="unknown"; cursor.Offset=0; cursor.High=null; cursor.Previous=null; cursor.Meta=false; cursor.Parent=null; cursor.Invalid=0; cursor.Model="unknown";cursor.TaskStarted=0;cursor.TaskEnded=0;cursor.TaskOrder=0;cursor.TaskTurn=cursor.TaskEndedTurn=null;cursor.TaskRunning=false;
                    cursor.SkippedOffset=0;cursor.SkippedTail=null;
                }
                if(reset)limit=Math.Min(length,budget);
                cursor.Path=file.Path; cursor.Created=file.Created; cursor.Head=head;
                bool skip=cursor.SkippedOffset>cursor.Offset;long baseOffset=skip?cursor.SkippedOffset:cursor.Offset;
                stream.Position=baseOffset;
                byte[] buffer=new byte[65536]; var line=new MemoryStream();int bytes;
                try
                {
                    while (stream.Position < limit && (bytes=stream.Read(buffer,0,(int)Math.Min(buffer.Length,limit-stream.Position)))>0)
                    {
                        LastBytesRead+=bytes;
                        if (cancel != null && cancel()) throw new OperationCanceledException();
                        int start=0;
                        while(start<bytes)
                        {
                            int newline=Array.IndexOf(buffer,(byte)10,start,bytes-start); int end=newline<0 ? bytes : newline;
                            if(!skip)
                            {
                                int take=Math.Min(end-start, 2*1024*1024-(int)line.Length);
                                if(take>0) line.Write(buffer,start,take);
                                if(line.Length>=2048)
                                {
                                    string prefix=Encoding.UTF8.GetString(line.GetBuffer(),0,(int)Math.Min(line.Length,2048));
                                    bool candidate=prefix.Contains("\"token_count\"") || prefix.Contains("\"session_meta\"") || prefix.Contains("\"turn_context\"") || ActivityLine(prefix);
                                    if(!candidate) skip=true;
                                    else if(line.Length>=2*1024*1024) { cursor.Invalid++; skip=true; }
                                }
                            }
                            if(newline<0) break;
                            if(!skip) ParseLine(cursor, Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length));
                            cursor.Offset=baseOffset+newline+1; line.SetLength(0); skip=false; start=newline+1;
                        }
                        baseOffset+=bytes;
                    }
                }
                finally
                {
                    cursor.SkippedOffset=skip?baseOffset:0;
                    cursor.SkippedTail=skip?Fingerprint(stream,Math.Max(0,baseOffset-64),(int)Math.Min(64,baseOffset)):null;
                    line.Dispose(); cursor.Tail=Fingerprint(stream,Math.Max(0,cursor.Offset-64),(int)Math.Min(64,cursor.Offset));
                    cursor.Length=length; cursor.Modified=file.Modified;dirty=true;DataVersion++;
                    if(reset||oldCount!=cursor.Events.Count||oldInvalid!=cursor.Invalid||oldMeta!=cursor.Meta||oldParent!=cursor.Parent||oldStarted!=cursor.Started)cursor.MilestoneVersion++;
                }
            dirty=true;
        }

        private static void Enumerate(string path, List<FileInfo> files, ref int failures, int depth)
        {
            if(depth>8){failures++;return;}
            try
            {
                foreach(string file in Directory.GetFiles(path,"rollout-*.jsonl")) files.Add(new FileInfo(file));
                foreach(string child in Directory.GetDirectories(path)) if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0) Enumerate(child,files,ref failures,depth+1);
            }
            // Missing optional roots are normal. Exists() also returns false for inaccessible
            // directories, so let enumeration distinguish absence from a partial/failed scan.
            catch(DirectoryNotFoundException){} catch(IOException){failures++;} catch(UnauthorizedAccessException){failures++;}
        }
        private static string IdFor(FileInfo file)
        {
            string stem=System.IO.Path.GetFileNameWithoutExtension(file.Name); Guid id;
            string candidate=stem.Length>=36?stem.Substring(stem.Length-36):"";
            return Guid.TryParse(candidate,out id) ? id.ToString() : file.FullName;
        }
        private static string SegmentSessionId(string path,string ledgerId)
        {
            // New rollouts may end in <session UUID>_<segment UUID>. Keep the segment ledger
            // distinct, but validate its metadata against the session UUID rather than the suffix.
            if(String.IsNullOrEmpty(path)||String.IsNullOrEmpty(ledgerId))return null;
            string stem=System.IO.Path.GetFileNameWithoutExtension(path),suffix="_"+ledgerId;Guid id;
            if(!stem.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)||stem.Length<suffix.Length+37)return null;
            int start=stem.Length-suffix.Length-36;
            return stem[start-1]=='-'&&Guid.TryParse(stem.Substring(start,36),out id)?id.ToString():null;
        }

        private static bool ActivityLine(string text){return text.Contains("\"task_started\"")||text.Contains("\"turn_started\"")||text.Contains("\"task_complete\"")||text.Contains("\"turn_completed\"")||text.Contains("\"turn_aborted\"")||text.Contains("\"task_failed\"");}
        internal UsageSnapshot Read(string range, DateTime now, Action<string> progress, Func<bool> cancel)
        {Update(progress,cancel);return Snapshot(range,now);}
        internal void Update(Action<string> progress,Func<bool> cancel)
        {
            if(!Directory.Exists(System.IO.Path.Combine(root,"sessions")) && !Directory.Exists(System.IO.Path.Combine(root,"archived_sessions"))) throw new DirectoryNotFoundException("找不到 Codex sessions 日志目录，可在设置中选择 Codex 数据目录。");
            int failed=0; List<FileInfo> found=new List<FileInfo>();
            Enumerate(System.IO.Path.Combine(root,"sessions"),found,ref failed,0); Enumerate(System.IO.Path.Combine(root,"archived_sessions"),found,ref failed,0);
            UpdateEnumerated(found,failed,progress,cancel);
        }
        // Keep enumeration completeness explicit through reconciliation and aggregation. Tests
        // can exercise partial listings without altering directory ACLs or reading user logs.
        internal UsageSnapshot ReadEnumerated(string range,DateTime now,List<FileInfo> found,int failed,Action<string> progress,Func<bool> cancel)
        {UpdateEnumerated(found,failed,progress,cancel);return Snapshot(range,now);}
        private void UpdateEnumerated(List<FileInfo> found,int failed,Action<string> progress,Func<bool> cancel)
        {
            LastBytesRead=0;
            // One ledger per thread: moving/duplicating a rollout in archived_sessions must not add its usage twice.
            var files=found.GroupBy(IdFor,StringComparer.OrdinalIgnoreCase).Select(g=>g.OrderByDescending(f=>f.Length).ThenByDescending(f=>f.LastWriteTimeUtc).First()).OrderByDescending(f=>f.LastWriteTimeUtc).ToList();
            var active=new HashSet<string>(files.Select(IdFor),StringComparer.OrdinalIgnoreCase);
            // Not seeing a file during a partial traversal is not evidence of deletion. Retain
            // its last numeric ledger until a complete traversal can authoritatively remove it.
            if(failed==0)foreach(string deleted in cursors.Keys.Where(k=>!active.Contains(k)).ToArray()) {cursors.Remove(deleted);dirty=true;DataVersion++;}
            int completed=0; DateTime reported=DateTime.MinValue;
            foreach(FileInfo file in files)
            {
                if(cancel!=null && cancel()) { SaveCache(true); throw new OperationCanceledException(); }
                string id=IdFor(file); LogCursor cursor;
                if(!cursors.TryGetValue(id,out cursor)) { cursor=new LogCursor {Id=id};cursors[id]=cursor;dirty=true;DataVersion++; }
                if(cursor.Path!=file.FullName || cursor.Created!=file.CreationTimeUtc.Ticks || cursor.Modified!=file.LastWriteTimeUtc.Ticks || cursor.Length!=file.Length || Math.Max(cursor.Offset,cursor.SkippedOffset)<file.Length)
                {
                    try { ReadFile(cursor,file,cancel);dirty=true; }
                    catch(IOException){failed++;} catch(UnauthorizedAccessException){failed++;}
                }
                completed++;
                if(progress!=null && (DateTime.UtcNow-reported).TotalMilliseconds>200) {progress("读取本地日志 "+completed+" / "+files.Count);reported=DateTime.UtcNow;}
            }
            lastFailures=failed;SaveCache(false);
        }
        internal UsageSnapshot Snapshot(string range,DateTime now){return snapshots.Get(cursors,DataVersion,range,now,lastFailures);}
        internal MilestoneInput GetMilestones(DateTime now,CancellationToken cancel)
        {return milestones.Get(cursors,now,lastFailures,cancel);}
        // Only a complete remote traversal may prune the numeric cache; never touch logs.
        internal bool RemoteReadComplete(string path,long length)
        {LogCursor cursor;return cursors.TryGetValue(path,out cursor)&&Math.Max(cursor.Offset,cursor.SkippedOffset)>=length;}
        internal bool ReconcileRemote(HashSet<string> paths)
        {
            bool removed=false;
            foreach(string key in cursors.Keys.Where(k=>!paths.Contains(k)).ToArray()){cursors.Remove(key);removed=true;}
            if(removed){dirty=true;DataVersion++;}return removed;
        }
        internal List<LogCursor> Export(){return cursors.Values.Select(CopyCursor).ToList();}
        internal static LogCursor CopyCursor(LogCursor value)
        {
            var copy=(LogCursor)value.MemberCopy();copy.Events=new List<LocalUsageEvent>(value.Events);copy.Quotas=new List<QuotaBucket>(value.Quotas);return copy;
        }
        internal LogCursor RemoteCursor(string path)
        {
            LogCursor value;if(!cursors.TryGetValue(path,out value)){value=new LogCursor{Id=LogFile.SessionId(path),Path=path};cursors[path]=value;dirty=true;}return value;
        }
        internal static UsageSnapshot Aggregate(Dictionary<string,LogCursor> cursors,string range,DateTime now,int failed=0)
        {return Aggregate(cursors,range,now,failed,ApiPrices.Snapshot());}
        internal static UsageSnapshot Aggregate(Dictionary<string,LogCursor> cursors,string range,DateTime now,int failed,PriceState prices)
        {
            if(now.Kind==DateTimeKind.Utc)now=now.ToLocalTime();
            UsageRangeSpec custom;
            bool customRange=UsageRangeSpec.TryParse(range,out custom);
            UsageSnapshot result=new UsageSnapshot {SourceName="本地 Codex",CostAvailable=false,CountLabel="用量记录",LatestRecord="暂无记录",CoverageFiles=cursors.Count,PeriodSessions=new long[3],PeriodInferred=new long[3]};
            result.KnownModels=cursors.Values.SelectMany(c=>c.Events).Select(e=>e.Model).Where(m=>!String.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Quotas=cursors.Values.SelectMany(c=>c.Quotas).GroupBy(q=>q.Id).Select(g=>g.OrderByDescending(q=>q.ObservedAt).First()).ToList();
            DateTime historyStart=customRange?custom.From.Date:now.Date.AddDays(-179);
            int historyDays=customRange?checked((custom.To.Date-historyStart).Days+1):180;
            result.Daily=DailyUsage.Empty(customRange?custom.To:now,historyDays);long historyFrom=Unix(historyStart);
            result.Hourly=DailyUsage.Hours(customRange?custom.To:now);result.HourlyThrough=customRange?24:now.Hour+1;long todayFrom=Unix(now.Date);
            long[] periodStarts={todayFrom,Unix(now.Date.AddDays(-6)),Unix(now.Date.AddDays(-29))};
            long from=customRange?Unix(custom.From):range=="all"?Int64.MinValue:Unix(now.Date.AddDays(range=="week"?-6:range=="month"?-29:0));
            long until=customRange?Unix(custom.To):Unix(now);
            long timelineStep=0;
            if(customRange)
            {
                timelineStep=UsageTimeline.StepSeconds(custom.From,custom.To);result.TimelineStepSeconds=timelineStep;
                result.Timeline=timelineStep>=86400?result.Daily:UsageTimeline.Empty(from,until,timelineStep);
            }
            int deferred=0,invalid=0,missingMeta=0,missingParent=0,unverifiedParent=0;long latest=0;
            foreach(LogCursor cursor in cursors.Values)
            {
                invalid+=cursor.Invalid;
                if(!cursor.Meta) {deferred++;missingMeta++;continue;}
                // Unfinished tasks expire after three minutes without file activity. An interrupted
                // client or old cached start event must not leave an endlessly animated orb.
                long touched=cursor.Modified>0?Unix(new DateTime(cursor.Modified,DateTimeKind.Utc)):0;
                if(cursor.TaskRunning&&cursor.TaskStarted>0&&cursor.TaskStarted<=until&&touched<=until&&touched+180>until)
                {result.ActiveTasks++;result.ActivityUntil=Math.Max(result.ActivityUntil,touched+180);result.NextChangeAt=Math.Min(result.NextChangeAt,touched+180);}
                if(cursor.TaskRunning){if(cursor.TaskStarted>until)result.NextChangeAt=Math.Min(result.NextChangeAt,cursor.TaskStarted);if(touched>until)result.NextChangeAt=Math.Min(result.NextChangeAt,touched);}
                int inherited;int exclusion=InheritedPrefix(cursors,cursor,out inherited,CancellationToken.None);
                if(exclusion!=0){deferred++;if(exclusion==2)missingParent++;else unverifiedParent++;continue;}
                bool used=false;long cursorLatest=0;
                foreach(LocalUsageEvent item in cursor.Events.Skip(inherited))
                {
                    if(item.Time>until){result.NextChangeAt=Math.Min(result.NextChangeAt,item.Time);continue;}
                    if(item.Input+item.Output==0)continue;
                    cursorLatest=Math.Max(cursorLatest,item.Time);
                    if(item.Inferred)for(int p=0;p<3;p++)if(item.Time>=periodStarts[p])result.PeriodInferred[p]++;
                    result.LatestUsageUnix=Math.Max(result.LatestUsageUnix,item.Time);
                    if(item.Time>=todayFrom)
                    {
                        var hour=result.Hourly[Epoch.AddSeconds(item.Time).ToLocalTime().Hour];
                        hour.Add(item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,item.Reasoning,0);
                        ModelUsage.Accumulate(hour.Models,item.Model,item.Effort,item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,prices);
                    }
                    // Reuse the already-deduplicated events; daily charts never reread the rollout files.
                    bool inHistory=item.Time>=historyFrom&&(!customRange||item.Time>=from)&&(!customRange||item.Time<=until);
                    if(inHistory)
                    {
                        int index=(Epoch.AddSeconds(item.Time).ToLocalTime().Date-historyStart).Days;
                        if(index>=0&&index<result.Daily.Length)result.Daily[index].Add(item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,item.Reasoning,0);
                        if(index>=0&&index<result.Daily.Length)ModelUsage.Accumulate(result.Daily[index].Models,item.Model,item.Effort,item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,prices);
                    }
                    if(item.Time<from)continue;
                    if(customRange&&timelineStep<86400)
                    {
                        int timelineIndex=(int)((item.Time-from)/timelineStep);
                        if(timelineIndex>=0&&timelineIndex<result.Timeline.Length)
                        {
                            var bucket=result.Timeline[timelineIndex];
                            bucket.Add(item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,item.Reasoning,0);
                            ModelUsage.Accumulate(bucket.Models,item.Model,item.Effort,item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,prices);
                        }
                    }
                    checked {result.TotalTokens+=item.Input+item.Output;result.InputTokens+=item.Input-item.Cached-item.CacheWrite;result.CacheReadTokens+=item.Cached;result.CacheCreationTokens+=item.CacheWrite;result.OutputTokens+=item.Output;result.ReasoningTokens+=item.Reasoning;result.Requests++;}
                    ModelUsage.Accumulate(result.Models,item.Model,item.Effort,item.Input-item.Cached-item.CacheWrite,item.Output,item.Cached,item.CacheWrite,1,prices);
                    if(item.Inferred)result.InferredRecords++;
                    latest=Math.Max(latest,item.Time);used=true;
                }
                if(used)result.Sessions++;
                if(cursorLatest>0)for(int p=0;p<3;p++)if(cursorLatest>=periodStarts[p])result.PeriodSessions[p]++;
            }
            long allInput=result.InputTokens+result.CacheReadTokens+result.CacheCreationTokens;
            result.EquivalentUsd=result.Models.Sum(m=>m.EquivalentUsd);result.UnpricedTokens=result.Models.Sum(m=>m.UnpricedTokens);
            result.CacheHitRate=allInput>0?100.0*result.CacheReadTokens/allInput:0;
            result.LatestRecord=latest>0?Epoch.AddSeconds(latest).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"暂无记录";
            result.CoverageWarnings=failed+deferred+invalid;
            var notices=new List<string>();
            if(failed>0)notices.Add(failed+" 个日志文件或目录读取失败（可能被占用或无权限），暂时保留上次可读数据。");
            if(missingParent>0)notices.Add(missingParent+" 个会话日志缺少原会话记录，无法排除复制的历史用量，暂未计入。");
            if(unverifiedParent>0)notices.Add(unverifiedParent+" 个会话日志的关联关系或开始时间不完整，暂未计入。");
            if(missingMeta>0)notices.Add(missingMeta+" 个会话日志缺少可核验的会话标识，暂未计入。");
            if(invalid>0)notices.Add(invalid+" 条记录无法解析或校验（格式、时间、会话标识或长度不符合要求）；其余可核验记录仍参与统计。");
            result.Warning=String.Join("\n",notices);
            result.CommonWarning=result.Warning;
            if(result.InferredRecords>0)result.Warning+=(result.Warning.Length>0?"\n":"")+result.InferredRecords+" 条旧记录由累计计数差值还原。";
            return result;
        }
        // Shared with Aggregate so forks have exactly the same effective event denominator.
        private static int InheritedPrefix(Dictionary<string,LogCursor> records,LogCursor cursor,out int inherited,CancellationToken cancel)
        {
            inherited=0;if(!cursor.Meta)return 1;if(String.IsNullOrEmpty(cursor.Parent))return 0;
            LogCursor parent;if(!records.TryGetValue(cursor.Parent,out parent))return 2;
            if(!parent.Meta||cursor.Parent==cursor.Id||cursor.Started==0)return 3;
            var signatures=new List<string>();foreach(var e in parent.Events){cancel.ThrowIfCancellationRequested();if(e.Time<=cursor.Started)signatures.Add(e.Signature);}
            int at=0;foreach(var item in cursor.Events)
            {cancel.ThrowIfCancellationRequested();int match=signatures.IndexOf(item.Signature,at);if(match<0)break;at=match+1;inherited++;}
            return 0;
        }
        internal static MilestoneInput BuildMilestones(Dictionary<string,LogCursor> records,DateTime now,int failed,CancellationToken cancel)
        {
            var result=new MilestoneInput();long until=Unix(now);int excluded=0,invalid=0;
            foreach(var cursor in records.Values)
            {
                cancel.ThrowIfCancellationRequested();invalid+=cursor.Invalid;int inherited;
                if(InheritedPrefix(records,cursor,out inherited,cancel)!=0){excluded++;continue;}
                for(int i=inherited;i<cursor.Events.Count;i++)
                {
                    cancel.ThrowIfCancellationRequested();var item=cursor.Events[i];
                    if(item.Time>until){result.NextChangeAt=Math.Min(result.NextChangeAt,item.Time);continue;}
                    long tokens=checked(item.Input+item.Output);if(tokens==0)continue;
                    result.Events.Add(new MilestoneEvent{From=item.Time,To=item.Time,Tokens=tokens,Precision=item.Inferred?1:0});
                }
            }
            var warnings=new List<string>();if(failed>0)warnings.Add(failed+" 个日志文件或目录读取失败，保留上次可读数据。");
            if(excluded>0)warnings.Add(excluded+" 个会话缺少可核验标识或父会话关系，暂未计入。");
            if(invalid>0)warnings.Add(invalid+" 条记录无法解析或校验，其余可核验记录仍参与统计。");
            result.Warning=String.Join("\n",warnings);return result;
        }
        private void LoadCache()
        {
            try
            {
                if(!File.Exists(cachePath))return;
                using(var input=File.OpenRead(cachePath)) using(var zip=new GZipStream(input,CompressionMode.Decompress)) using(var reader=new StreamReader(zip,Encoding.UTF8))
                {
                    var cache=json.Deserialize<LogCache>(reader.ReadToEnd());
                    if(cache==null || cache.Version!=4 || !String.Equals(cache.Root,root,StringComparison.OrdinalIgnoreCase))return;
                    foreach(var file in cache.Files) if(file.Id!=null)
                    {
                        // Repair only previously rejected segment files; keep the multi-GB warm cache intact.
                        if(!file.Meta&&SegmentSessionId(file.Path,file.Id)!=null){file.Offset=Int64.MaxValue;file.Length=-1;}
                        cursors[remoteCache?file.Path:file.Id]=file;
                    }
                }
            }
            catch(Exception){cursors.Clear();}
        }
        internal void SaveCache(bool force)
        {
            if(readOnlyCache)return;
            if(!dirty || (!force && (DateTime.UtcNow-saved).TotalSeconds<30))return;
            string temp=cachePath+".tmp";
            try
            {
                using(var output=File.Create(temp)) using(var zip=new GZipStream(output,CompressionLevel.Fastest)) using(var writer=new StreamWriter(zip,new UTF8Encoding(false)))
                {
                    writer.Write("{\"Version\":4,\"Root\":"+json.Serialize(root)+",\"Files\":[");bool first=true;
                    foreach(LogCursor cursor in cursors.Values){if(!first)writer.Write(',');writer.Write(json.Serialize(cursor));first=false;}
                    writer.Write("]}");
                }
                if(File.Exists(cachePath))File.Replace(temp,cachePath,null);else File.Move(temp,cachePath);
                dirty=false;saved=DateTime.UtcNow;
            }
            catch(IOException){} catch(UnauthorizedAccessException){}
        }
    }
}
