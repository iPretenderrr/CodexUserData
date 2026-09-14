using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal sealed class QuotaSample
    {
        public long Time {get;set;}
        public string Bucket {get;set;}
        public long Minutes {get;set;}
        public double Remaining {get;set;}
        public long Reset {get;set;}
        internal string Key {get{return Bucket+":"+Minutes;}}
        internal string Label {get{return new QuotaWindow{Minutes=Minutes}.Label;}}
        internal bool Supported {get{return Bucket=="codex"&&(Minutes==300||Minutes==10080);}}
        internal bool Valid {get{return Time>0&&Time<=253402300799&&Minutes>0&&Minutes<=525600&&Bucket!=null&&Bucket.Length<=120&&!Double.IsNaN(Remaining)&&!Double.IsInfinity(Remaining)&&Remaining>=0&&Remaining<=100&&Reset>=0&&Reset<=253402300799;}}
        // Fast path for the numeric-only format written by RecordAsync. Imported/older
        // JSON still uses the regular parser; accepting a prefix alone is never enough.
        internal static QuotaSample Parse(string line,JavaScriptSerializer json)
        {
            const string bucket=",\"Bucket\":\"codex\",\"Minutes\":";
            if(line.StartsWith("{\"Time\":",StringComparison.Ordinal)&&line.EndsWith("}",StringComparison.Ordinal))
            {
                int b=line.IndexOf(bucket,8,StringComparison.Ordinal),r=b<0?-1:line.IndexOf(",\"Remaining\":",b+bucket.Length,StringComparison.Ordinal);
                int z=r<0?-1:line.IndexOf(",\"Reset\":",r+13,StringComparison.Ordinal);
                long time,minutes,reset;double remaining;
                if(b>8&&r>b&&z>r&&Int64.TryParse(line.Substring(8,b-8),NumberStyles.None,CultureInfo.InvariantCulture,out time)
                    &&Int64.TryParse(line.Substring(b+bucket.Length,r-b-bucket.Length),NumberStyles.None,CultureInfo.InvariantCulture,out minutes)
                    &&Double.TryParse(line.Substring(r+13,z-r-13),NumberStyles.Float,CultureInfo.InvariantCulture,out remaining)
                    &&Int64.TryParse(line.Substring(z+9,line.Length-z-10),NumberStyles.None,CultureInfo.InvariantCulture,out reset))
                    return new QuotaSample{Time=time,Bucket="codex",Minutes=minutes,Remaining=remaining,Reset=reset};
            }
            try{return json.Deserialize<QuotaSample>(line);}catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
        }
    }

    // Daily append-only files bound range reads and retention work. They contain only
    // numeric quota observations, never CLI responses, credentials or user paths.
    internal sealed class QuotaHistoryStore
    {
        private readonly string root;
        private readonly object gate=new object();
        private readonly Dictionary<string,long> lastMinute=new Dictionary<string,long>();
        private sealed class CachedDay {internal long Length,Stamp,Created,Offset,Used;internal string Head,Tail;internal QuotaSample[] Rows;}
        private readonly Dictionary<string,CachedDay> cache=new Dictionary<string,CachedDay>();
        private long useClock;
        private int sampleLimit=600000;
        internal long LastBytesRead {get;private set;}
        internal int CachedSamples {get{lock(gate)return cache.Values.Sum(d=>d.Rows.Length);}}
        internal void SetEco(bool eco){lock(gate){sampleLimit=eco?160000:600000;TrimCache();}}
        private DateTime cleaned;
        internal QuotaHistoryStore(string directory){root=directory;}
        internal static string Scope(string home)
        {
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(home).TrimEnd('\\','/').ToUpperInvariant()))).Replace("-","");
        }
        private string Folder(string scope)
        {
            if(scope==null||scope.Length!=64||scope.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("Invalid history scope");
            return Path.Combine(root,scope);
        }
        internal Task RecordAsync(string scope,IEnumerable<QuotaBucket> buckets)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);
            var samples=(buckets??new QuotaBucket[0]).Where(b=>b!=null&&b.Id=="codex"&&b.IsOnline).SelectMany(b=>new[]{b.Primary,b.Secondary}.Where(w=>w!=null&&w.RemainingPercent(b,now).HasValue).Select(w=>new QuotaSample{Time=b.ObservedAt,Bucket=b.Id,Minutes=w.Minutes,Remaining=100-w.UsedPercent,Reset=w.ResetsAt})).Where(s=>s.Valid&&s.Supported).ToArray();
            return Task.Run(()=>
            {
                lock(gate)
                {
                    string directory=Folder(scope);Directory.CreateDirectory(directory);
                    var json=new JavaScriptSerializer();
                    foreach(var sample in samples)
                    {
                        string key=scope+"/"+sample.Key;long previous;
                        // Manual refreshes share the minute sample; no duplicate chart points.
                        if(lastMinute.TryGetValue(key,out previous)&&sample.Time/60<=previous)continue;
                        string file=Path.Combine(directory,DateTimeOffset.FromUnixTimeSeconds(sample.Time).UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+".jsonl");
                        // A leading newline isolates an incomplete final record after a crash.
                        File.AppendAllText(file,"\n"+json.Serialize(sample)+"\n",new UTF8Encoding(false));lastMinute[key]=sample.Time/60;
                    }
                    if(cleaned!=DateTime.UtcNow.Date)
                    {
                        DateTime cutoff=DateTime.UtcNow.Date.AddDays(-180);
                        foreach(string file in Directory.EnumerateFiles(directory,"*.jsonl"))
                        {
                            DateTime date;if(DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)&&date<cutoff)File.Delete(file);
                        }
                        cleaned=DateTime.UtcNow.Date;
                    }
                }
            });
        }
        internal Task<QuotaSample[]> ReadAsync(string scope,long from,long to)
        {return ReadAsync(scope,from,to,CancellationToken.None);}
        internal Task<QuotaSample[]> ReadAsync(string scope,long from,long to,CancellationToken cancel)
        {
            return Task.Run(()=>
            {
                var rows=new List<QuotaSample>();var json=new JavaScriptSerializer{MaxJsonLength=4096};bool misplaced=false;
                string directory=Folder(scope);LastBytesRead=0;
                for(DateTime day=DateTimeOffset.FromUnixTimeSeconds(from).UtcDateTime.Date;day<=DateTimeOffset.FromUnixTimeSeconds(to).UtcDateTime.Date;day=day.AddDays(1))
                {
                    cancel.ThrowIfCancellationRequested();
                    string file=Path.Combine(directory,day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+".jsonl");
                    // Release the gate between days: a long history read must not delay
                    // the minute writer or a newer selection for the whole 180-day scan.
                    lock(gate)
                    {
                        cancel.ThrowIfCancellationRequested();var info=new FileInfo(file);
                        if(!info.Exists){cache.Remove(file);continue;}
                        CachedDay cached;cache.TryGetValue(file,out cached);
                        if(cached==null||cached.Length!=info.Length||cached.Stamp!=info.LastWriteTimeUtc.Ticks||cached.Created!=info.CreationTimeUtc.Ticks)
                        {
                            cached=ReadDay(info,cached,json,cancel);cache[file]=cached;
                        }
                        cached.Used=++useClock;
                        long dayStart=LocalCodexUsage.Unix(day);
                        foreach(var sample in cached.Rows)if(sample.Time>=from&&sample.Time<=to){rows.Add(sample);misplaced|=sample.Time<dayStart||sample.Time>=dayStart+86400;}
                        TrimCache();
                    }
                }
                cancel.ThrowIfCancellationRequested();
                // Normally each file is already sorted/deduplicated for its UTC day.
                // Preserve the old behavior for manually moved or imported records too.
                if(misplaced){var unique=new Dictionary<long,QuotaSample>();foreach(var sample in rows)unique[sample.Time/60*2+(sample.Minutes==300?0:1)]=sample;return unique.Values.OrderBy(s=>s.Time).ThenBy(s=>s.Minutes).ToArray();}
                return rows.ToArray();
            },cancel);
        }
        private void TrimCache()
        {
            int count=cache.Values.Sum(d=>d.Rows.Length);
            if(count<=sampleLimit&&cache.Count<=182)return;
            foreach(var pair in cache.OrderBy(p=>p.Value.Used).ToArray())
            {if(count<=sampleLimit&&cache.Count<=182)break;count-=pair.Value.Rows.Length;cache.Remove(pair.Key);}
        }
        private CachedDay ReadDay(FileInfo info,CachedDay previous,JavaScriptSerializer json,CancellationToken cancel)
        {
            using(var input=new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan))
            {
                long length=input.Length;int headCount=(int)Math.Min(128,length);
                string head=LocalCodexUsage.Fingerprint(input,0,headCount);
                bool append=previous!=null&&previous.Created==info.CreationTimeUtc.Ticks&&length>previous.Length
                    &&(previous.Length<128||head==previous.Head)
                    &&LocalCodexUsage.Fingerprint(input,Math.Max(0,previous.Offset-64),(int)Math.Min(64,previous.Offset))==previous.Tail;
                var unique=new Dictionary<long,QuotaSample>();
                if(append)foreach(var sample in previous.Rows)unique[sample.Time/60*2+(sample.Minutes==300?0:1)]=sample;
                long offset=append?previous.Offset:0,position=offset;input.Position=offset;
                byte[] buffer=new byte[65536],line=new byte[4096];int used=0;bool oversized=false;int read;
                while(position<length&&(read=input.Read(buffer,0,(int)Math.Min(buffer.Length,length-position)))>0)
                {
                    LastBytesRead+=read;cancel.ThrowIfCancellationRequested();
                    int at=0;while(at<read)
                    {
                        int newline=Array.IndexOf(buffer,(byte)10,at,read-at),end=newline<0?read:newline;
                        int take=Math.Min(line.Length-used,end-at);if(take>0){Buffer.BlockCopy(buffer,at,line,used,take);used+=take;}oversized|=take<end-at;
                        if(newline>=0)
                        {
                            if(!oversized&&used>0)
                            {
                                var sample=QuotaSample.Parse(Encoding.UTF8.GetString(line,0,used).TrimStart('\uFEFF').TrimEnd('\r'),json);
                                if(sample!=null&&sample.Valid&&sample.Supported){sample.Bucket="codex";unique[sample.Time/60*2+(sample.Minutes==300?0:1)]=sample;}
                            }
                            used=0;oversized=false;offset=position+newline+1;
                        }
                        at=newline<0?read:newline+1;
                    }
                    position+=read;
                }
                cancel.ThrowIfCancellationRequested();
                // Commit only complete lines, so an interrupted append is retried next time.
                return new CachedDay{Length=length,Stamp=info.LastWriteTimeUtc.Ticks,Created=info.CreationTimeUtc.Ticks,Offset=offset,Head=head,
                    Tail=LocalCodexUsage.Fingerprint(input,Math.Max(0,offset-64),(int)Math.Min(64,offset)),Rows=unique.Values.OrderBy(s=>s.Time).ThenBy(s=>s.Minutes).ToArray()};
            }
        }
    }
}
