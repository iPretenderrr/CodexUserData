using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet.Common;

namespace CodexUserData
{
    internal sealed class RemoteView
    {
        internal List<LogCursor> Records=new List<LogCursor>();
        internal ActivityReport Activity=new ActivityReport{MonitoringUnavailable=true};
        internal string Status="远程尚未同步";
        internal long SyncedAt;
        internal bool Connected,Discovering;
    }
    // One owner for the SFTP connection. Directory enumeration and historical reads yield
    // between batches; UI refreshes only access published numeric snapshots.
    internal sealed class RemoteMonitor : IDisposable
    {
        private static readonly ConcurrentDictionary<string,SemaphoreSlim> cacheOwners=new ConcurrentDictionary<string,SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private sealed class Tail
        {
            internal ActivityTurn State=new ActivityTurn();internal ActivityJsonLine Line=new ActivityJsonLine();
            internal long Offset,Length,Modified,Observed;internal bool Skip,Baseline=true,FreshEvidence;internal string Stamp,Notified;
        }
        private readonly Func<IRemoteFiles> factory;private LocalCodexUsage ledger;private volatile int interval;private readonly string dataFolder;
        private readonly CancellationTokenSource stop=new CancellationTokenSource();
        private readonly Dictionary<string,LogFile> known=new Dictionary<string,LogFile>(StringComparer.Ordinal);
        private readonly Dictionary<string,Tail> tails=new Dictionary<string,Tail>(StringComparer.Ordinal);
        private readonly Queue<string> directories=new Queue<string>();
        private readonly HashSet<string> visited=new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> history=new Queue<string>();private readonly HashSet<string> queued=new HashSet<string>(StringComparer.Ordinal);
        private IRemoteFiles files;private IEnumerator<LogFile> listing;private string listingPath;
        private long nextScan,nextActivity,lastPublish,syncedAt;private int roundRobin;private bool scanned,discoveryFailed,scanning,initialDiscoveryDone,ledgerChanged;
        private readonly Stopwatch clock=Stopwatch.StartNew();private volatile RemoteView view=new RemoteView();
        internal event Action<RemoteView> Changed;
        internal string Configuration {get;private set;}
        internal RemoteView View {get{return view;}}
        internal void SetEco(bool eco){interval=eco?4000:2000;}
        internal RemoteMonitor(RemoteOptions options,string dataFolder,bool eco):this(()=>new SftpFiles(options),dataFolder,eco){Configuration=options.Configuration;}
        internal RemoteMonitor(Func<IRemoteFiles> source,string dataFolder,bool eco)
        {
            factory=source;interval=eco?4000:2000;this.dataFolder=dataFolder;
        }
        internal void Start(){Task.Run((Func<Task>)Run);}
        private void Queue(string path){if(queued.Add(path))history.Enqueue(path);}
        private void BeginScan()
        {
            directories.Clear();visited.Clear();discoveryFailed=false;scanning=true;nextScan=clock.ElapsedMilliseconds+30000;
            directories.Enqueue(files.Root.TrimEnd('/')+"/sessions");directories.Enqueue(files.Root.TrimEnd('/')+"/archived_sessions");
        }
        private void DiscoverBatch()
        {
            if(!scanning)return;
            var slice=Stopwatch.StartNew();int count=0;
            while(count++<128&&slice.ElapsedMilliseconds<100)
            {
                if(listing==null)
                {
                    if(directories.Count==0){scanned=true;initialDiscoveryDone=true;scanning=false;return;}
                    listingPath=directories.Dequeue();if(!visited.Add(listingPath))continue;
                    listing=files.List(listingPath).GetEnumerator();
                }
                bool more;
                try{more=listing.MoveNext();}
                catch(SftpPathNotFoundException){more=false;}
                catch(DirectoryNotFoundException){more=false;}
                if(!more){listing.Dispose();listing=null;continue;}
                var item=listing.Current;
                if(item.Directory){if(item.Path.Count(c=>c=='/')-files.Root.Count(c=>c=='/')<10)directories.Enqueue(item.Path);continue;}
                LogFile old;bool changed=!known.TryGetValue(item.Path,out old)||old.Length!=item.Length||old.Modified!=item.Modified;
                item.Live=old!=null?old.Live:initialDiscoveryDone&&!known.Keys.Any(p=>LogFile.SessionId(p)==LogFile.SessionId(item.Path));
                known[item.Path]=item;if(changed)Queue(item.Path);
            }
        }
        private void ActivityBatch()
        {
            if(!scanning)
            {
                // Discover fresh files without re-enumerating all old dates each activity tick.
                var recent=known.Values.OrderByDescending(f=>f.Modified).Take(3).Select(f=>f.Path.Substring(0,f.Path.LastIndexOf('/')));
                foreach(string directory in recent.Concat(new[]{-1,0,1}.Select(d=>files.Root.TrimEnd('/')+"/sessions/"+DateTime.UtcNow.AddDays(d).ToString("yyyy/MM/dd"))).Distinct(StringComparer.Ordinal))
                {visited.Remove(directory);directories.Enqueue(directory);}scanning=true;
            }
            var all=known.Values.OrderByDescending(f=>f.Modified).ToArray();if(all.Length==0)return;
            // Always revisit open turns; rotate through old files as well as recent ones.
            var selected=new HashSet<string>(all.Take(6).Select(f=>f.Path),StringComparer.Ordinal);
            foreach(var p in tails.Where(p=>p.Value.State.Running).Select(p=>p.Key))selected.Add(p);
            for(int i=0;i<12&&i<all.Length;i++)selected.Add(all[(roundRobin+i)%all.Length].Path);
            roundRobin=(roundRobin+12)%all.Length;
            foreach(string path in selected)
            {
                if(stop.IsCancellationRequested)return;
                LogFile meta;
                try{meta=files.Stat(path);}catch(SftpPathNotFoundException){tails.Remove(path);known.Remove(path);continue;}catch(FileNotFoundException){tails.Remove(path);known.Remove(path);continue;}
                LogFile previous=known[path];meta.Live=previous.Live;known[path]=meta;if(meta.Length!=previous.Length||meta.Modified!=previous.Modified)Queue(path);
                Tail tail;if(!tails.TryGetValue(path,out tail)){tail=new Tail();tail.State.Session=LogFile.SessionId(path);tails[path]=tail;}
                bool first=tail.Stamp==null;long previousOrder=tail.State.Order;
                if(!first&&tail.Length==meta.Length&&tail.Modified==meta.Modified&&tail.Offset>=meta.Length)continue;
                using(var stream=files.Open(path))
                {
                    bool reset=!first&&(tail.Offset>meta.Length||tail.Stamp!=LocalCodexUsage.Fingerprint(stream,Math.Max(0,tail.Offset-64),(int)Math.Min(64,tail.Offset)));
                    if(first||reset){tail.Offset=Math.Max(0,meta.Length-256*1024);tail.Skip=tail.Offset>0;tail.Line.Reset();tail.State=new ActivityTurn{Session=LogFile.SessionId(path)};tail.Baseline=reset||!meta.Live;tail.FreshEvidence=false;}
                    stream.Position=tail.Offset;long until=Math.Min(meta.Length,tail.Offset+512*1024);byte[] buffer=new byte[65536];int n;
                    while(stream.Position<until&&(n=stream.Read(buffer,0,(int)Math.Min(buffer.Length,until-stream.Position)))>0)
                    {
                        for(int i=0;i<n;i++)
                        {
                            byte b=buffer[i];if(b==10){if(!tail.Skip)tail.Line.Finish(tail.State);tail.Skip=false;}else if(!tail.Skip)tail.Line.Feed(b);
                        }
                        tail.Offset+=n;
                    }
                    tail.Stamp=LocalCodexUsage.Fingerprint(stream,Math.Max(0,tail.Offset-64),(int)Math.Min(64,tail.Offset));
                }
                tail.Length=meta.Length;tail.Modified=meta.Modified;
                if(!tail.Baseline&&tail.State.Order>previousOrder){tail.Observed=clock.ElapsedMilliseconds;tail.FreshEvidence=true;}
                // Existing completion at first observation is historical. Future appended events
                // remain eligible across reconnects, even if remote wall time differs.
                if(tail.Baseline&&tail.Offset>=meta.Length){if(tail.State.Completed)tail.Notified=tail.State.Key;tail.Baseline=false;}
            }
        }
        private ActivityReport Report(bool online)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);var report=new ActivityReport{ObservedAt=now,MonitoringUnavailable=!online||!scanned||discoveryFailed||known.Count==0};
            foreach(var tail in tails.Values.GroupBy(t=>t.State.Session).Select(g=>g.OrderByDescending(t=>t.State.Order).ThenBy(t=>t.State.Running).First()))
            {
                if(tail.State.Child)continue;
                if(tail.Offset<tail.Length||tail.Baseline){report.UncertainTasks++;continue;}
                if(tail.State.Running)
                {
                    if(online&&tail.FreshEvidence&&clock.ElapsedMilliseconds-tail.Observed<900000){report.ActiveTasks++;report.ActiveKeys.Add(tail.State.Session+"/"+tail.State.Key);report.Until=now+6;}else report.UncertainTasks++;
                }
                else if(tail.State.Completed&&tail.Notified!=tail.State.Key&&online)
                {report.CompletedTasks++;tail.Notified=tail.State.Key;}
            }
            return report;
        }
        private void Publish(bool connected,string status,bool copy)
        {
            var old=view;var records=copy?ledger.Export():old.Records;
            if(copy)foreach(var c in records)c.Quotas=c.Quotas.Select(q=>new QuotaBucket{Id=q.Id,Name=q.Name,Origin="远程日志",ObservedAt=q.ObservedAt,Primary=q.Primary,Secondary=q.Secondary}).ToList();
            view=new RemoteView{Records=records,Activity=Report(connected),Connected=connected,SyncedAt=syncedAt,Discovering=!scanned||history.Count>0,Status=status};
            var changed=Changed;if(changed!=null)changed(view);
        }
        private async Task Run()
        {
            int retry=2;bool blocked=false,ownsCache=false;var owner=cacheOwners.GetOrAdd(Path.GetFullPath(dataFolder),p=>new SemaphoreSlim(1,1));
            try
            {
                // A canceled connection may still be unwinding its network timeout. Do not let
                // a rapid disable/re-enable race its final cache write.
                await owner.WaitAsync(stop.Token);ownsCache=true;
                Directory.CreateDirectory(dataFolder);
                ledger=new LocalCodexUsage(dataFolder,Path.Combine(dataFolder,"usage.json.gz"),false,true);
                foreach(var c in ledger.Export())if(!String.IsNullOrEmpty(c.Path)){known[c.Path]=new LogFile{Path=c.Path,Length=c.Length,Modified=c.Modified};Queue(c.Path);}
                Publish(false,"正在连接服务器，已保留上次统计",true);
                while(!stop.IsCancellationRequested&&!blocked)
                {
                    int reconnectDelay=0;
                    try
                    {
                        if(files==null){files=factory();files.Connect();BeginScan();scanned=false;nextActivity=0;}
                        if(clock.ElapsedMilliseconds>=nextActivity){ActivityBatch();nextActivity=clock.ElapsedMilliseconds+interval;Publish(true,"远程已连接",false);}
                        if(scanned&&directories.Count==0&&listing==null&&clock.ElapsedMilliseconds>=nextScan)BeginScan();
                        DiscoverBatch();
                        if(history.Count>0)
                        {
                            string path=history.Dequeue();queued.Remove(path);LogFile meta;
                            if(known.TryGetValue(path,out meta))
                            {
                                var cursor=ledger.RemoteCursor(path);long before=Math.Max(cursor.Offset,cursor.SkippedOffset);
                                try{using(var stream=files.Open(path)){meta.Length=stream.Length;ledger.ReadStream(cursor,meta,stream,()=>stop.IsCancellationRequested,4*1024*1024);}}
                                catch(SftpPathNotFoundException){known.Remove(path);tails.Remove(path);continue;}
                                catch(FileNotFoundException){known.Remove(path);tails.Remove(path);continue;}
                                catch{Queue(path);throw;}
                            ledgerChanged=true;long after=Math.Max(cursor.Offset,cursor.SkippedOffset);
                                if(after<meta.Length&&after>before)Queue(path);
                            }
                        }
                        if(clock.ElapsedMilliseconds-lastPublish>=5000||view.Records.Count==0&&scanned)
                        {
                            syncedAt=LocalCodexUsage.Unix(DateTime.UtcNow);ledger.SaveCache(false);lastPublish=clock.ElapsedMilliseconds;
                            string label=known.Count==0?"未找到远程会话日志":history.Count>0?"远程同步中 · "+history.Count+" 个文件待处理":"远程已同步";
                            Publish(true,label,ledgerChanged);ledgerChanged=false;
                        }
                        retry=2;await Task.Delay(history.Count>0||directories.Count>0||listing!=null?30:200,stop.Token);
                    }
                    catch(OperationCanceledException){break;}
                    catch(Exception ex)
                    {
                        if(listing!=null){listing.Dispose();listing=null;}if(files!=null){files.Dispose();files=null;}
                        blocked=ex is HostTrustException||ex is SshAuthenticationException||ex is System.Security.Cryptography.CryptographicException||ex is FormatException||ex is ArgumentException;
                        Publish(false,SftpFiles.Error(ex),true);ledger.SaveCache(true);
                        if(!blocked){reconnectDelay=retry*1000;retry=Math.Min(60,retry*2);}
                    }
                    if(reconnectDelay>0)await Task.Delay(reconnectDelay,stop.Token);
                }
            }
            catch(OperationCanceledException){}
            catch(Exception ex){Publish(false,SftpFiles.Error(ex),false);}
            finally{if(listing!=null)listing.Dispose();if(files!=null)files.Dispose();if(ledger!=null)ledger.SaveCache(true);if(ownsCache)owner.Release();}
        }
        public void Dispose(){stop.Cancel();}
    }
}
