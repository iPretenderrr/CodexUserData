using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    }

    // Daily append-only files bound range reads and retention work. They contain only
    // numeric quota observations, never CLI responses, credentials or user paths.
    internal sealed class QuotaHistoryStore
    {
        private readonly string root;
        private readonly object gate=new object();
        private readonly Dictionary<string,long> lastMinute=new Dictionary<string,long>();
        private sealed class CachedDay {internal long Length,Stamp;internal QuotaSample[] Rows;}
        private readonly Dictionary<string,CachedDay> cache=new Dictionary<string,CachedDay>();
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
        {
            return Task.Run(()=>
            {
                var rows=new Dictionary<string,QuotaSample>();var json=new JavaScriptSerializer{MaxJsonLength=4096};var visited=new HashSet<string>();
                lock(gate)
                {
                    string directory=Folder(scope);
                    for(DateTime day=DateTimeOffset.FromUnixTimeSeconds(from).UtcDateTime.Date;day<=DateTimeOffset.FromUnixTimeSeconds(to).UtcDateTime.Date;day=day.AddDays(1))
                    {
                        string file=Path.Combine(directory,day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+".jsonl");if(!File.Exists(file))continue;
                        visited.Add(file);var info=new FileInfo(file);CachedDay cached;
                        if(!cache.TryGetValue(file,out cached)||cached.Length!=info.Length||cached.Stamp!=info.LastWriteTimeUtc.Ticks)
                        {
                            var loaded=new List<QuotaSample>();
                            foreach(string line in File.ReadLines(file))
                            {
                                if(String.IsNullOrWhiteSpace(line)||line.Length>4096)continue;
                                QuotaSample sample;try{sample=json.Deserialize<QuotaSample>(line);}catch(ArgumentException){continue;}catch(InvalidOperationException){continue;}
                                if(sample!=null&&sample.Valid&&sample.Supported)loaded.Add(sample);
                            }
                            cached=new CachedDay{Length=info.Length,Stamp=info.LastWriteTimeUtc.Ticks,Rows=loaded.ToArray()};cache[file]=cached;
                        }
                        foreach(var sample in cached.Rows)if(sample.Time>=from&&sample.Time<=to)rows[sample.Key+"/"+sample.Time/60]=sample;
                    }
                    // Cache only the requested period; old months are never retained in RAM.
                    foreach(string file in cache.Keys.Where(k=>!visited.Contains(k)).ToArray())cache.Remove(file);
                }
                return rows.Values.OrderBy(s=>s.Time).ThenBy(s=>s.Key).ToArray();
            });
        }
    }
}
