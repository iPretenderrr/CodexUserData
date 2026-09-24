using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Precision: 0 = reported instant, 1 = inferred usage with unknown timing,
    // 2 = a date interval. These are numeric records, never conversation content.
    internal sealed class MilestoneEvent
    {
        public long From,To,Tokens;
        public int Precision;
    }
    internal sealed class MilestoneInput
    {
        internal List<MilestoneEvent> Events=new List<MilestoneEvent>();
        internal long NextChangeAt=Int64.MaxValue;
        internal string Warning="";
    }
    internal class MilestoneBoundary
    {
        public long From {get;set;}
        public long To {get;set;}
        public long Batch {get;set;}
        public int Precision {get;set;}
    }
    internal sealed class MilestoneMark : MilestoneBoundary
    {
        // One large report may cross many thresholds. Keep a range instead of
        // allocating one object for every hundred million tokens in that report.
        public long First {get;set;}
        public long Last {get;set;}
    }
    internal sealed class MilestoneStage
    {
        internal long Number,FromTokens,ToTokens,Tokens;
        internal bool Complete;
        internal MilestoneBoundary Start,End;
        internal bool SameBatch {get{return Complete&&Start!=null&&End!=null&&Start.Batch>0&&Start.Batch==End.Batch;}}
        internal bool DurationKnown {get{return Start!=null&&End!=null&&!SameBatch&&Start.Precision!=1&&End.Precision!=1&&End.To>=Start.From;}}
        internal long DurationMin {get{return DurationKnown?Math.Max(0,End.From-Start.To):0;}}
        internal long DurationMax {get{return DurationKnown?Math.Max(0,End.To-Start.From):0;}}
    }
    internal sealed class MilestoneSnapshot
    {
        // In-memory numeric index only; the compact persisted milestone cache does
        // not duplicate the event ledger. Every viewport keeps the same baseline.
        internal MilestoneCurveSeries Curve;
        public int Version {get;set;}
        public string Scope {get;set;}
        public string Source {get;set;}
        public string Signature {get;set;}
        public string Integrity {get;set;}
        public string Warning {get;set;}
        public long TotalTokens {get;set;}
        public long ObservedAt {get;set;}
        public long NextChangeAt {get;set;}
        public MilestoneBoundary Origin {get;set;}
        public List<MilestoneMark> Marks {get;set;}
        internal long Completed(long step){MilestoneEngine.CheckStep(step);return TotalTokens/step;}
        internal MilestoneStage Stage(long step,long number,long now)
        {
            long completed=Completed(step);if(number<1||number>completed+1)throw new ArgumentOutOfRangeException("number");
            long from=checked((number-1)*step);bool complete=number<=completed;
            return new MilestoneStage{Number=number,FromTokens=from,ToTokens=from>Int64.MaxValue-step?Int64.MaxValue:from+step,
                Tokens=complete?step:TotalTokens-from,Complete=complete,Start=Boundary((number-1)*(step/MilestoneEngine.Base)),
                End=complete?Boundary(number*(step/MilestoneEngine.Base)):new MilestoneBoundary{From=now,To=now}};
        }
        private MilestoneBoundary Boundary(long index)
        {
            if(index==0)return Origin;int low=0,high=Marks.Count-1;
            while(low<=high){int at=low+(high-low)/2;var mark=Marks[at];if(index<mark.First)high=at-1;else if(index>mark.Last)low=at+1;else return mark;}
            throw new InvalidDataException("里程碑记录不连续。");
        }
        internal MilestoneSnapshot WithStatus(string source,string warning,long at,long next)
        {return new MilestoneSnapshot{Version=Version,Scope=Scope,Source=source,Signature=Signature,Integrity=Integrity,Warning=warning,TotalTokens=TotalTokens,ObservedAt=at,NextChangeAt=next,Origin=Origin,Marks=Marks,Curve=Curve};}
    }
    internal sealed class MilestoneCurveSeries
    {
        internal readonly long[] Times,Totals;
        internal MilestoneCurveSeries(long[] times,long[] totals){Times=times;Totals=totals;}
        internal static MilestoneCurveSeries Build(MilestoneEvent[] events,CancellationToken cancel)
        {
            int count=0;long last=Int64.MinValue;
            for(int i=0;i<events.Length;i++){if((i&4095)==0)cancel.ThrowIfCancellationRequested();var e=events[i];if(e.Tokens>0&&(count==0||e.From!=last)){count++;last=e.From;}}
            var times=new long[count];var totals=new long[count];int at=-1;long total=0;
            for(int i=0;i<events.Length;i++)
            {
                if((i&4095)==0)cancel.ThrowIfCancellationRequested();var e=events[i];if(e.Tokens==0)continue;
                total=checked(total+e.Tokens);if(at<0||times[at]!=e.From)times[++at]=e.From;totals[at]=total;
            }
            return new MilestoneCurveSeries(times,totals);
        }
    }
    internal sealed class MilestoneEngine
    {
        internal const long Base=100000000L;
        internal static readonly long[] Steps={Base,5*Base,10*Base,50*Base,100*Base};
        private const int CacheLimit=16*1024*1024;
        private readonly string folder;
        private MilestoneInput previousInput;
        private MilestoneEvent[] previousEvents;
        private MilestoneSnapshot previous;
        private string previousScope,cacheNotice="";
        internal bool UsedIncremental {get;private set;}
        internal int Builds {get;private set;}
        internal int Sorts {get;private set;}
        internal MilestoneEngine(string directory){folder=directory;}
        internal static void CheckStep(long step){if(!Steps.Contains(step))throw new ArgumentOutOfRangeException("step");}
        internal MilestoneSnapshot Get(MilestoneInput input,string scope,string source,long now,CancellationToken cancel)
        {
            if(input==null)throw new ArgumentNullException("input");cancel.ThrowIfCancellationRequested();UsedIncremental=false;
            if(scope==previousScope&&Object.ReferenceEquals(input,previousInput)&&previous!=null)return Status(previous,input,source,now);
            if(scope!=previousScope)cacheNotice="";
            var events=Order(input,scope,cancel);
            string signature=Fingerprint(events,cancel);MilestoneSnapshot result=null;
            if(scope==previousScope&&previous!=null&&previous.Signature==signature)result=previous;
            else result=Load(scope,signature);
            if(result==null)
            {
                bool append=scope==previousScope&&previous!=null&&previousEvents!=null&&events.Length>=previousEvents.Length;
                if(append)for(int i=0;i<previousEvents.Length;i++){if((i&4095)==0)cancel.ThrowIfCancellationRequested();if(!Equal(events[i],previousEvents[i])){append=false;break;}}
                result=Build(events,scope,signature,append?previous:null,append?previousEvents.Length:0,cancel);UsedIncremental=append;Builds++;
                cancel.ThrowIfCancellationRequested();Save(result);
            }
            if(result.Curve==null)result.Curve=MilestoneCurveSeries.Build(events,cancel);
            cancel.ThrowIfCancellationRequested();previousInput=input;previousEvents=events;previous=result;previousScope=scope;
            return Status(result,input,source,now);
        }
        private MilestoneSnapshot Status(MilestoneSnapshot value,MilestoneInput input,string source,long now)
        {return value.WithStatus(source,String.Join("\n",new[]{input.Warning,cacheNotice}.Where(s=>!String.IsNullOrWhiteSpace(s))),now,input.NextChangeAt);}
        private static bool Equal(MilestoneEvent a,MilestoneEvent b){return a.From==b.From&&a.To==b.To&&a.Tokens==b.Tokens&&a.Precision==b.Precision;}
        private static int Compare(MilestoneEvent a,MilestoneEvent b)
        {int n=a.From.CompareTo(b.From);if(n==0)n=a.To.CompareTo(b.To);if(n==0)n=a.Precision.CompareTo(b.Precision);return n==0?a.Tokens.CompareTo(b.Tokens):n;}
        private void Sort(MilestoneEvent[] events,CancellationToken cancel)
        {
            bool ordered=true;for(int i=1;i<events.Length;i++){if((i&4095)==0)cancel.ThrowIfCancellationRequested();if(Compare(events[i-1],events[i])>0){ordered=false;break;}}
            if(ordered)return;int comparisons=0;Sorts++;
            try{Array.Sort(events,delegate(MilestoneEvent a,MilestoneEvent b){if((++comparisons&4095)==0)cancel.ThrowIfCancellationRequested();return Compare(a,b);});}
            catch(InvalidOperationException){cancel.ThrowIfCancellationRequested();throw;}
        }
        private MilestoneEvent[] Order(MilestoneInput input,string scope,CancellationToken cancel)
        {
            var events=input.Events.ToArray();
            // Cursor iteration order is stable while multiple sessions append. Prove the
            // previous raw sequence still exists unchanged, sort only the new events, then
            // merge with the previous ordering in linear time. Rewrites/deletions fall back.
            if(scope==previousScope&&previousInput!=null&&previousEvents!=null&&events.Length>=previousInput.Events.Count)
            {
                int extra=events.Length-previousInput.Events.Count,old=0;var added=new List<MilestoneEvent>(extra);
                foreach(var e in events)
                {
                    if((old+added.Count&4095)==0)cancel.ThrowIfCancellationRequested();
                    if(old<previousInput.Events.Count&&Equal(e,previousInput.Events[old]))old++;else added.Add(e);
                    if(added.Count>extra)break;
                }
                if(old==previousInput.Events.Count&&added.Count==extra)
                {
                    var tail=added.ToArray();Sort(tail,cancel);int a=0,b=0;
                    for(int i=0;i<events.Length;i++){if((i&4095)==0)cancel.ThrowIfCancellationRequested();events[i]=b>=tail.Length||a<previousEvents.Length&&Compare(previousEvents[a],tail[b])<=0?previousEvents[a++]:tail[b++];}
                    return events;
                }
            }
            Sort(events,cancel);return events;
        }
        private static MilestoneSnapshot Build(MilestoneEvent[] events,string scope,string signature,MilestoneSnapshot prior,int offset,CancellationToken cancel)
        {
            var result=new MilestoneSnapshot{Version=1,Scope=scope,Signature=signature,Origin=prior==null?null:prior.Origin,TotalTokens=prior==null?0:prior.TotalTokens,Marks=prior==null?new List<MilestoneMark>():new List<MilestoneMark>(prior.Marks)};
            for(int i=offset;i<events.Length;i++)
            {
                if((i&2047)==0)cancel.ThrowIfCancellationRequested();var e=events[i];if(e.Tokens==0)continue;
                if(result.Origin==null)result.Origin=new MilestoneBoundary{From=e.From,To=e.To,Precision=e.Precision,Batch=i+1L};
                long before=result.TotalTokens/Base;result.TotalTokens=checked(result.TotalTokens+e.Tokens);long after=result.TotalTokens/Base;
                if(after>before)result.Marks.Add(new MilestoneMark{First=before+1,Last=after,From=e.From,To=e.To,Precision=e.Precision,Batch=i+1L});
            }
            return result;
        }
        private static string Fingerprint(MilestoneEvent[] events,CancellationToken cancel)
        {
            // Hash fixed-size numeric records with a reusable buffer. No per-event strings
            // or duplicate persisted event ledger, even for a large usage history.
            using(var hash=SHA256.Create())
            {
                byte[] buffer=new byte[32768];int at=0;
                foreach(var e in events)
                {
                    if(at+32>buffer.Length){hash.TransformBlock(buffer,0,at,buffer,0);at=0;cancel.ThrowIfCancellationRequested();}
                    if(e==null||e.Tokens<0||e.To<e.From||e.Precision<0||e.Precision>2)throw new InvalidDataException("用量记录无效。");
                    Put(buffer,ref at,e.From);Put(buffer,ref at,e.To);Put(buffer,ref at,e.Tokens);Put(buffer,ref at,e.Precision);
                }
                hash.TransformFinalBlock(buffer,0,at);return BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();
            }
        }
        private static void Put(byte[] bytes,ref int at,long value){for(int i=0;i<8;i++){bytes[at++]=(byte)value;value>>=8;}}
        private string CachePath(string scope){return Path.Combine(folder,RemoteOptions.Hash(scope)+".json");}
        private static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=CacheLimit,RecursionLimit=12};}
        private MilestoneSnapshot Load(string scope,string signature)
        {
            try
            {
                string path=CachePath(scope);if(!File.Exists(path)||new FileInfo(path).Length>CacheLimit)return null;
                var value=Json().Deserialize<MilestoneSnapshot>(File.ReadAllText(path,Encoding.UTF8));
                if(value==null||value.Version!=1||value.Scope!=scope||value.Signature!=signature||value.TotalTokens<0||value.Marks==null||(value.TotalTokens>0&&!Valid(value.Origin))||value.Integrity!=Integrity(value))return null;
                long last=0;foreach(var mark in value.Marks){if(!Valid(mark)||mark.First!=last+1||mark.Last<mark.First)return null;last=mark.Last;}
                return last==value.TotalTokens/Base?value:null;
            }
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
        }
        private static bool Valid(MilestoneBoundary value){return value!=null&&value.Batch>0&&value.From<=value.To&&value.Precision>=0&&value.Precision<=2&&(value.Precision!=0||value.From==value.To);}
        // Verify both the source sequence and the derived payload; a damaged cache
        // must never silently change a threshold date while retaining its source hash.
        private static string Integrity(MilestoneSnapshot value)
        {return RemoteOptions.Hash(Json().Serialize(new{value.Version,value.Scope,value.Signature,value.TotalTokens,value.Origin,value.Marks}));}
        private void Save(MilestoneSnapshot value)
        {
            string temporary=null;
            try
            {
                cacheNotice="";value.Integrity=Integrity(value);string json=Json().Serialize(value);if(Encoding.UTF8.GetByteCount(json)>CacheLimit){cacheNotice="里程碑缓存较大，本次使用内存结果。";return;}
                Directory.CreateDirectory(folder);string path=CachePath(value.Scope);temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                File.WriteAllText(temporary,json,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            }
            catch(IOException){cacheNotice="里程碑缓存暂时无法保存，本次统计仍可查看。";}catch(UnauthorizedAccessException){cacheNotice="里程碑缓存目录无法写入，本次统计仍可查看。";}catch(InvalidOperationException){cacheNotice="里程碑缓存较大，本次使用内存结果。";}
            finally{try{if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}
