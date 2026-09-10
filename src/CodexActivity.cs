using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // A separate, read-only event tail: usage aggregation and its user-selected interval never
    // delay the orb's activity state. Keep identifiers/times only; never cache messages or tools.
    internal sealed class ActivityReport
    {
        internal int ActiveTasks,UncertainTasks,CompletedTasks;
        internal long Until,ObservedAt,CompletionSerial;
        internal bool MonitoringUnavailable;
    }
    internal sealed class ActivityTurn
    {
        internal string Session,Turn,EndedTurn;
        internal long Created,Started,Signal,Ended,Order;
        internal bool Running,Explicit,Child,Completed;
        private static string S(Dictionary<string,object> p,string key){object v;return p!=null&&p.TryGetValue(key,out v)?v as string:null;}
        internal static long Time(object value){DateTimeOffset t;return value is string&&DateTimeOffset.TryParse((string)value,out t)?t.ToUnixTimeSeconds():0;}
        internal void Accept(string type,Dictionary<string,object> p,long at,long order=0)
        {
            if(p==null||at<=0)return;
            if(type=="session_meta")
            {
                Session=S(p,"id")??S(p,"session_id");Child=!String.IsNullOrEmpty(S(p,"parent_thread_id"))||S(p,"thread_source")=="guardian_review";object raw;p.TryGetValue("timestamp",out raw);Created=Time(raw);return;
            }
            // Ignore inherited history that predates this session. Never use the file mtime as
            // evidence of inference: copying a log or refreshing quota is not a running task.
            order=order>0?order:at*1000;
            if(at<Created||at<Signal||order<Order)return;
            string kind=S(p,"type"),id=S(p,"turn_id");
            if(type=="event_msg"&&(kind=="task_started"||kind=="turn_started"))
            {
                if(id!=null&&id==EndedTurn)return;
                Turn=id;Started=Signal=at;Order=order;Completed=false;Running=Explicit=true;return;
            }
            if(type=="event_msg"&&(kind=="task_complete"||kind=="turn_completed"||kind=="turn_aborted"||kind=="task_failed"))
            {
                // A late completion for an older turn must not stop a newer one.
                if(id!=null&&Turn!=null&&id!=Turn)return;
                Running=false;Ended=Signal=at;Order=order;Completed=kind=="task_complete"||kind=="turn_completed";Turn=Turn??id;EndedTurn=id??Turn;return;
            }
            // Final answers are a fallback when no explicit lifecycle start was observed.
            bool final=(type=="event_msg"&&kind=="agent_message"||type=="response_item"&&kind=="message"&&S(p,"role")=="assistant")&&S(p,"phase")=="final_answer";
            // When an explicit turn is open, an assistant final message can precede tool cleanup
            // or further work. Only its lifecycle end closes that turn. Older logs keep a fallback.
            if(final&&!Explicit){Running=false;Completed=false;Ended=Signal=at;Order=order;EndedTurn=Turn;return;}
            bool work=type=="event_msg"&&(kind=="agent_reasoning"||kind=="agent_message"||kind=="item_started"||kind=="item_completed"||kind=="exec_command_begin"||kind=="exec_command_end"||kind=="patch_apply_begin"||kind=="patch_apply_end")
                ||type=="response_item"&&(kind=="function_call"||kind=="custom_tool_call"||kind=="function_call_output"||kind=="custom_tool_call_output"||kind=="reasoning");
            if(!work||id!=null&&Turn!=null&&id!=Turn)return;
            if(!Running)
            {
                // Tool/history replays after a terminal event do not reopen that turn. A fresh
                // task_started is required; truncated startup tails can still use a brief fallback.
                if(Ended>0)return;
                Running=true;Explicit=false;Started=at;Turn=id;
            }
            Signal=at;Order=order;
        }
        internal bool Active(long now){return Running&&Signal<=now+5&&now-Signal<=(Explicit?900:45);}
        internal string Key {get{return !String.IsNullOrEmpty(Turn)?"turn:"+Turn:"session:"+Session;}}
    }
    internal sealed class CodexActivity : IDisposable
    {
        private sealed class Tail
        {
            internal string Path;
            internal long Offset,Length,Modified,Created;
            internal byte[] Head,Anchor;
            internal ActivityTurn Turn=new ActivityTurn();
            internal readonly ActivityJsonLine Line=new ActivityJsonLine();
            internal bool HeaderDone,Bootstrap;internal long Resume;
            internal bool Skip;
            internal bool AnchorChecked;
        }
        private readonly string directory;
        private readonly ConcurrentDictionary<string,byte> pending=new ConcurrentDictionary<string,byte>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Tail> tails=new Dictionary<string,Tail>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> readFailures=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024};
        private readonly Timer timer;
        private FileSystemWatcher watcher;
        private int working,disposed,resetWatcher;
        private long discoverAt,checkAt;
        private bool baselined;
        private bool discoveryUnavailable;
        private bool directoryPresent;
        private long directoryCreated;
        private readonly Dictionary<string,long> initialOffsets=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> bootstrapFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long baselineAt;
        private long completionSerial;
        private readonly Dictionary<string,long> notified=new Dictionary<string,long>();
        internal event Action<ActivityReport> Changed;
        internal long LastBytesRead {get;private set;}
        internal CodexActivity(string home,bool start=true)
        {
            directory=System.IO.Path.GetFullPath(System.IO.Path.Combine(home,"sessions"));
            timer=new Timer(delegate{Poll();},null,Timeout.Infinite,Timeout.Infinite);
            if(start)timer.Change(0,1000);
        }
        private bool Stopped {get{return Volatile.Read(ref disposed)!=0;}}
        internal void Start(){if(!Stopped)timer.Change(0,1000);}
        private void Queue(string path){if(!Stopped)pending[path]=0;}
        private void Watch()
        {
            if(watcher!=null||!Directory.Exists(directory))return;
            try
            {
                watcher=new FileSystemWatcher(directory,"rollout-*.jsonl"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName};
                watcher.Changed+=delegate(object s,FileSystemEventArgs e){Queue(e.FullPath);};watcher.Created+=delegate(object s,FileSystemEventArgs e){Queue(e.FullPath);};watcher.Deleted+=delegate(object s,FileSystemEventArgs e){Queue(e.FullPath);};
                watcher.Renamed+=delegate(object s,RenamedEventArgs e){Queue(e.OldFullPath);Queue(e.FullPath);};watcher.Error+=delegate{Interlocked.Exchange(ref resetWatcher,1);Interlocked.Exchange(ref discoverAt,0);};watcher.EnableRaisingEvents=true;
                if(Stopped)watcher.Dispose();
            }
            catch(IOException){if(watcher!=null)watcher.Dispose();watcher=null;}catch(UnauthorizedAccessException){if(watcher!=null)watcher.Dispose();watcher=null;}
        }
        private void Find(string path,List<FileInfo> result,int depth)
        {
            if(Stopped||depth>8||!Directory.Exists(path))return;
            try
            {
                foreach(string file in Directory.GetFiles(path,"rollout-*.jsonl"))result.Add(new FileInfo(file));
                foreach(string child in Directory.GetDirectories(path))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)Find(child,result,depth+1);
            }
            catch(IOException){discoveryUnavailable=true;}catch(UnauthorizedAccessException){discoveryUnavailable=true;}
        }
        private void Discover(long now)
        {
            discoveryUnavailable=false;
            var files=new List<FileInfo>();Find(directory,files,0);
            // Only the recent tail of current sessions is relevant. Archive/history never wakes an orb.
            if(!baselined)foreach(var file in files){try{initialOffsets[file.FullName]=file.Length;if(LocalCodexUsage.Unix(file.LastWriteTimeUtc)>now-86400)bootstrapFiles.Add(file.FullName);}catch(IOException){}}
            foreach(var file in files.Where(f=>LocalCodexUsage.Unix(f.LastWriteTimeUtc)>now-86400).OrderByDescending(f=>f.LastWriteTimeUtc))Queue(file.FullName);
        }
        private void Decode(ActivityTurn turn,byte[] buffer,int count)
        {
            try
            {
                var e=json.DeserializeObject(Encoding.UTF8.GetString(buffer,0,count)) as Dictionary<string,object>;if(e==null)return;
                object type,payload,time;e.TryGetValue("type",out type);e.TryGetValue("payload",out payload);e.TryGetValue("timestamp",out time);
                DateTimeOffset precise;long order=DateTimeOffset.TryParse(time as string,out precise)?precise.ToUnixTimeMilliseconds():0;
                turn.Accept(type as string,payload as Dictionary<string,object>,ActivityTurn.Time(time),order);
            }
            catch(ArgumentException){}catch(InvalidOperationException){} // Bad input is not evidence of activity.
        }
        private void Read(Tail tail,FileInfo file)
        {
            using(var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,16384,FileOptions.SequentialScan))
            {
                long length=stream.Length;
                // A log may be replaced by a larger file at the same path. Size checks alone
                // would retain the previous turn and skip the replacement's start/end markers.
                bool replaced=tail.Offset>length||tail.Resume>length||tail.Created!=0&&tail.Created!=file.CreationTimeUtc.Ticks
                    ||tail.Length==length&&tail.Modified!=file.LastWriteTimeUtc.Ticks
                    ||tail.Head!=null&&!tail.Head.SequenceEqual(Sample(stream,0,tail.Head.Length))
                    ||tail.Anchor!=null&&!tail.Anchor.SequenceEqual(Sample(stream,Math.Max(0,tail.Offset-tail.Anchor.Length),tail.Anchor.Length));
                if(replaced){tail.Offset=0;tail.Line.Reset();tail.Skip=false;tail.HeaderDone=false;tail.Resume=0;tail.Bootstrap=false;tail.AnchorChecked=false;tail.Turn=new ActivityTurn();tail.Head=null;}
                tail.Created=file.CreationTimeUtc.Ticks;
                if(tail.Head==null||tail.Head.Length<64)tail.Head=Sample(stream,0,(int)Math.Min(length,64));
                stream.Position=tail.Offset;byte[] data=new byte[16384];int read;long budget=4*1024*1024;
                while(!Stopped&&budget>0&&(read=stream.Read(data,0,(int)Math.Min(data.Length,Math.Min(budget,length-stream.Position))))>0)
                {
                    LastBytesRead+=read;budget-=read;
                    for(int i=0;i<read;i++)
                    {
                        if(data[i]!=10){if(!tail.Skip)tail.Line.Feed(data[i]);continue;}
                        if(!tail.Skip)tail.Line.Finish(tail.Turn);else tail.Line.Reset();tail.Skip=false;
                        if(!tail.HeaderDone)
                        {
                            tail.HeaderDone=true;long after=stream.Position-read+i+1;
                            long jump=tail.Bootstrap?Math.Max(after,length-512*1024):Math.Max(after,Math.Min(length,tail.Resume));
                            if(jump>after)
                            {
                                // Existing idle logs resume at the byte position captured when monitoring
                                // began, so a new turn appended to a multi-GB old session is not skipped.
                                stream.Position=jump-1;tail.Skip=stream.ReadByte()!=10;stream.Position=jump;break;
                            }
                        }
                    }
                }
                tail.Offset=stream.Position;tail.Length=length;tail.Modified=file.LastWriteTimeUtc.Ticks;
                tail.Anchor=Sample(stream,Math.Max(0,tail.Offset-64),(int)Math.Min(64,tail.Offset));
                if(tail.Offset<length)Queue(tail.Path);
                if(!tail.AnchorChecked&&tail.HeaderDone&&tail.Offset>=length)
                {
                    tail.AnchorChecked=true;
                    if(tail.Bootstrap&&tail.Turn.Running&&!tail.Turn.Explicit&&length>512*1024)FindStart(stream,tail,length);
                }
            }
        }
        private static byte[] Sample(FileStream stream,long offset,int count)
        {
            stream.Position=offset;byte[] result=new byte[count];int used=0,read;
            while(used<count&&(read=stream.Read(result,used,count-used))>0)used+=read;
            if(used<count)Array.Resize(ref result,used);return result;
        }
        private void FindStart(FileStream stream,Tail tail,long length)
        {
            // A long reasoning/tool turn may start before the normal tail window. Look back
            // once for its small start marker; do not repeatedly parse multi-megabyte history.
            long from=Math.Max(0,length-4*1024*1024);stream.Position=from;int size=(int)Math.Min(length-from,4*1024*1024);byte[] bytes=new byte[size];int used=0,n;
            while(used<size&&(n=stream.Read(bytes,used,size-used))>0)used+=n;LastBytesRead+=used;
            string text=Encoding.UTF8.GetString(bytes,0,used);int at=Math.Max(text.LastIndexOf("\"task_started\"",StringComparison.Ordinal),text.LastIndexOf("\"turn_started\"",StringComparison.Ordinal));
            if(at<0)return;int first=text.LastIndexOf('\n',at),last=text.IndexOf('\n',at);if(first<0&&from>0||last<0||last-first>8192)return;
            var anchor=new ActivityTurn{Session=tail.Turn.Session,Created=tail.Turn.Created};byte[] line=Encoding.UTF8.GetBytes(text.Substring(first+1,last-first-1));Decode(anchor,line,line.Length);
            if(anchor.Explicit&&anchor.Running&&anchor.Started<=tail.Turn.Signal&&(tail.Turn.Turn==null||tail.Turn.Turn==anchor.Turn)){tail.Turn.Explicit=true;tail.Turn.Started=anchor.Started;tail.Turn.Turn=anchor.Turn;}
        }
        internal ActivityReport Scan(DateTime instant)
        {
            long now=LocalCodexUsage.Unix(instant);LastBytesRead=0;
            bool exists=Directory.Exists(directory);long created=0;
            if(exists)try{created=Directory.GetCreationTimeUtc(directory).Ticks;}catch(IOException){exists=false;}catch(UnauthorizedAccessException){exists=false;}
            bool changed=exists!=directoryPresent||exists&&created!=directoryCreated,watcherLost=Interlocked.Exchange(ref resetWatcher,0)!=0;
            if(changed||watcherLost)
            {
                // FileSystemWatcher remains tied to a deleted directory's old handle. Rebind
                // after recreation and discover immediately rather than waiting 15 seconds.
                if(watcher!=null){watcher.Dispose();watcher=null;}Interlocked.Exchange(ref discoverAt,0);
                if(changed){foreach(var tail in tails.Values)tail.Line.Reset();tails.Clear();readFailures.Clear();initialOffsets.Clear();bootstrapFiles.Clear();}
                directoryPresent=exists;directoryCreated=created;
            }
            Watch();
            if(now>=Interlocked.Read(ref discoverAt)){Discover(now);Interlocked.Exchange(ref discoverAt,now+15);}
            if(now>=checkAt){foreach(string path in tails.Keys.Concat(readFailures).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())Queue(path);checkAt=now+5;}
            foreach(string path in pending.Keys.Take(256).ToArray())
            {
                if(Stopped||LastBytesRead>=16*1024*1024)break;byte ignored;if(!pending.TryRemove(path,out ignored))continue;
                if(!path.StartsWith(directory+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
                try
                {
                    // Exists returns false for access-denied as well as missing files. Inspect
                    // attributes instead so only a definite missing-path exception forgets state.
                    FileAttributes attributes=File.GetAttributes(path);var file=new FileInfo(path);Tail tail;
                    if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0){readFailures.Remove(path);continue;}
                    if(!tails.TryGetValue(path,out tail)){long resume;initialOffsets.TryGetValue(path,out resume);tail=new Tail{Path=path,Bootstrap=bootstrapFiles.Remove(path),Resume=resume};tails[path]=tail;}
                    if(readFailures.Contains(path)||tail.Created!=file.CreationTimeUtc.Ticks||tail.Modified!=file.LastWriteTimeUtc.Ticks||tail.Length!=file.Length||tail.Offset<file.Length)Read(tail,file);
                    readFailures.Remove(path);
                }
                catch(IOException error)
                {
                    if(IsMissingLog(error))
                    {
                        Tail removed;if(tails.TryGetValue(path,out removed))removed.Line.Reset();tails.Remove(path);readFailures.Remove(path);
                        // The path may later belong to a new session. Its first start event must
                        // not be skipped using the deleted file's startup resume checkpoint.
                        initialOffsets.Remove(path);bootstrapFiles.Remove(path);
                    }
                    else readFailures.Add(path);
                }
                catch(UnauthorizedAccessException){readFailures.Add(path);}
            }
            // A successful poll clock is not proof that all logs were readable or consumed.
            // A bounded scan's remaining queue may contain a new task with no Tail yet: keep
            // confirmed running evidence, but never turn that backlog into invented idle/counts.
            var report=new ActivityReport{ObservedAt=now,MonitoringUnavailable=!exists||!Directory.Exists(directory)||discoveryUnavailable||readFailures.Count>0||!pending.IsEmpty};
            // A header/event still being consumed is unresolved, not proof that the user is idle.
            report.UncertainTasks=tails.Values.Count(t=>(!t.HeaderDone||t.Offset<t.Length)&&!t.Turn.Running&&!t.Turn.Child);
            // Segmented/copied logs for the same turn count once; the latest terminal event wins.
            var retainedCompletions=new HashSet<string>();
            var states=tails.Values.Select(t=>t.Turn).Where(t=>!String.IsNullOrEmpty(t.Session)&&!t.Child)
                .GroupBy(t=>t.Session).Select(g=>g.OrderByDescending(t=>t.Order).ThenBy(t=>t.Running).First())
                .GroupBy(t=>t.Key).Select(g=>g.OrderByDescending(t=>t.Order).ThenBy(t=>t.Running).First()).ToArray();
            foreach(var state in states)
            {
                if(state.Active(now)){report.ActiveTasks++;report.Until=now+4;}
                else if(state.Running)report.UncertainTasks++;
            }
            // Match the UI's completed-state gate only after every concurrent task is known.
            // Otherwise a completion encountered before an uncertain task is marked delivered
            // even though the UI correctly suppresses it, and can never be shown after recovery.
            bool canComplete=!report.MonitoringUnavailable&&report.ActiveTasks==0&&report.UncertainTasks==0;
            foreach(var state in states)
            {
                if(state.Completed)
                {
                    string key=state.Session+"/"+state.Key;
                    retainedCompletions.Add(key);
                    if(!notified.ContainsKey(key)&&canComplete)
                    {
                        // Bootstrap, copied historical logs and timeouts never emit a completion.
                        // Delay acknowledgement until the queue/read failures recover so a real
                        // completion suppressed during incomplete monitoring is not lost forever.
                        if(baselined&&state.Order>baselineAt&&state.Ended<=now)report.CompletedTasks++;
                        notified[key]=state.Ended;
                    }
                }
            }
            if(!baselined){baselined=true;baselineAt=new DateTimeOffset(instant.ToUniversalTime()).ToUnixTimeMilliseconds();}
            if(report.CompletedTasks>0)completionSerial++;report.CompletionSerial=completionSerial;
            // A finished turn can remain the latest turn while quota refreshes keep its log
            // current. Do not expire its deduplication marker and announce it again each day.
            foreach(string key in notified.Where(p=>p.Value<now-86400&&!retainedCompletions.Contains(p.Key)).Select(p=>p.Key).ToArray())notified.Remove(key);
            foreach(string path in tails.Where(p=>p.Value.Modified>0&&LocalCodexUsage.Unix(new DateTime(p.Value.Modified,DateTimeKind.Utc))<now-86400).Select(p=>p.Key).ToArray()){initialOffsets[path]=tails[path].Offset;tails[path].Line.Reset();tails.Remove(path);}
            return report;
        }
        internal static bool IsMissingLog(Exception error){return error is FileNotFoundException||error is DirectoryNotFoundException;}
        private void Poll()
        {
            if(Stopped||Interlocked.Exchange(ref working,1)!=0)return;
            try{var report=Scan(DateTime.UtcNow);var handler=Changed;if(!Stopped&&handler!=null)handler(report);}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ObjectDisposedException){}
            finally{Interlocked.Exchange(ref working,0);if(Stopped)TryClean();}
        }
        private void Clean(){foreach(var tail in tails.Values)tail.Line.Reset();tails.Clear();readFailures.Clear();}
        private void TryClean(){if(Interlocked.CompareExchange(ref working,1,0)!=0)return;try{Clean();}finally{Interlocked.Exchange(ref working,0);}}
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;timer.Dispose();if(watcher!=null)watcher.Dispose();TryClean();}
    }
}

namespace CodexUserData
{
    internal static class CompletionFeedback
    {
        private sealed class State
        {
            internal bool Pending,Animating;
            internal System.Windows.UIElement Target;
        }
        private static readonly System.Windows.DependencyProperty StateProperty=System.Windows.DependencyProperty.RegisterAttached("State",typeof(State),typeof(CompletionFeedback),new System.Windows.PropertyMetadata(null));
        internal static void Set(System.Windows.Window window,bool pending)
        {
            var state=window.GetValue(StateProperty) as State;
            if(state==null)
            {
                state=new State();window.SetValue(StateProperty,state);
                window.IsVisibleChanged+=delegate{Render(window,state);};window.StateChanged+=delegate{Render(window,state);};
                window.Closed+=delegate{state.Pending=false;Render(window,state);};
            }
            state.Pending=pending;Render(window,state);
        }
        private static void Render(System.Windows.Window window,State state)
        {
            // App windows retain a separate outer motion host across every form. Clearing a
            // completion clock must never clear the host's entrance/exit opacity animation.
            var host=window.Content as System.Windows.Controls.Decorator;
            var content=host==null?null:host.Child;
            if(!Object.ReferenceEquals(content,state.Target))
            {
                if(state.Target!=null&&state.Animating)state.Target.BeginAnimation(System.Windows.UIElement.OpacityProperty,null);
                state.Target=content;state.Animating=false;
            }
            bool animate=content!=null&&state.Pending&&window.IsVisible&&window.WindowState!=System.Windows.WindowState.Minimized&&Theme.MotionAllowed;
            // Polls frequently repeat the same pending value. Reevaluate motion policy, while
            // retaining the existing clock whenever nothing changed instead of restarting it.
            if(animate==state.Animating)return;state.Animating=animate;
            if(!animate){content.BeginAnimation(System.Windows.UIElement.OpacityProperty,null);return;}
            // A slow breath persists until acknowledged. Hidden/minimized windows consume no frames.
            var animation=new System.Windows.Media.Animation.DoubleAnimation(1,.5,TimeSpan.FromMilliseconds(1100)){
                AutoReverse=true,RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever,FillBehavior=System.Windows.Media.Animation.FillBehavior.Stop};
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animation,30);
            content.BeginAnimation(System.Windows.UIElement.OpacityProperty,animation);
        }
    }
}
