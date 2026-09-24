using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal static class RemoteStabilityProbe
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static void Check(bool pass,string name){StabilityProbe.Check(pass,"Remote: "+name);}
        private static string Header(string id,DateTime at){return Json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n";}
        private static string Tokens(DateTime at,long input,long output){return Json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type="token_count",info=new{last_token_usage=new{input_tokens=input,output_tokens=output},total_token_usage=new{input_tokens=input,output_tokens=output}}}})+"\n";}
        private static string TaskEvent(DateTime at,string type,string turn){return Json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type=type,turn_id=turn}})+"\n";}
        private static LocalUsageEvent E(long at,string signature,long input){return new LocalUsageEvent{Time=at,Signature=signature,Input=input,Model="test",Effort="high"};}
        private static LogCursor C(string id,long at,params LocalUsageEvent[] events){return new LogCursor{Id=id,Meta=true,Started=at,Events=events.ToList()};}
        private sealed class Fixture
        {
            internal string Root;internal volatile bool Offline;internal volatile string IncompleteDirectory,BlockedRead;internal long Bytes;internal int Connections,FullScans;
        }
        private sealed class CountStream : Stream
        {
            private readonly Stream stream;private readonly Fixture fixture;
            internal CountStream(Stream stream,Fixture fixture){this.stream=stream;this.fixture=fixture;}
            public override int Read(byte[] b,int o,int n){if(fixture.Offline)throw new IOException();int read=stream.Read(b,o,n);fixture.Bytes+=read;return read;}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return true;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{return stream.Length;}}public override long Position{get{return stream.Position;}set{stream.Position=value;}}
            public override long Seek(long o,SeekOrigin w){return stream.Seek(o,w);}public override void Flush(){}public override void SetLength(long l){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
            protected override void Dispose(bool disposing){if(disposing)stream.Dispose();base.Dispose(disposing);}
        }
        private sealed class Files : IRemoteFiles
        {
            private readonly Fixture fixture;internal Files(Fixture f){fixture=f;}
            public string Root{get{return "/fixture";}}
            private string Local(string p){return Path.Combine(fixture.Root,p.Substring(Root.Length).TrimStart('/').Replace('/',Path.DirectorySeparatorChar));}
            public void Connect(){if(fixture.Offline)throw new IOException();fixture.Connections++;}
            public IEnumerable<LogFile> List(string p)
            {
                if(fixture.Offline)throw new IOException();if(p==Root)System.Threading.Interlocked.Increment(ref fixture.FullScans);if(p==fixture.IncompleteDirectory)throw new DirectoryNotFoundException();string local=Local(p);
                foreach(string path in Directory.GetDirectories(local))yield return new LogFile{Path=p+"/"+Path.GetFileName(path),Directory=true};
                foreach(string path in Directory.GetFiles(local,"rollout-*.jsonl"))yield return Stat(p+"/"+Path.GetFileName(path));
            }
            public LogFile Stat(string p){if(fixture.Offline)throw new IOException();var f=new FileInfo(Local(p));return new LogFile{Path=p,Length=f.Length,Modified=f.LastWriteTimeUtc.Ticks};}
            public Stream Open(string p){if(p==fixture.BlockedRead)throw new UnauthorizedAccessException();return new CountStream(new FileStream(Local(p),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),fixture);}
            public void Dispose(){}
        }
        internal static async Task Run(string root)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);var a=C("a",now-50,E(now-30,"first",100),E(now-20,"second",200));
            var copy=C("a",now-50,E(now-30,"first",100),E(now-20,"second",200),E(now-10,"third",300));
            var b=C("b",now-40,E(now-20,"other",50));
            Check(UsageUnion.Snapshot(new[]{a,b},"today",DateTime.Now,"test").TotalTokens==350,"independent sessions add");
            Check(UsageUnion.Snapshot(new[]{a,copy},"today",DateTime.Now,"test").TotalTokens==600,"copied prefix counts once and remote continuation counts");
            var fork=C("fork",now-15,E(now-15,"first",100),E(now-15,"second",200),E(now-5,"own",70));fork.Parent="a";
            Check(UsageUnion.Snapshot(new[]{a,fork},"today",DateTime.Now,"test").TotalTokens==370,"parent on another source resolves fork inheritance");
            var twice=C("twice",now-50,E(now-20,"same",10),E(now-20,"same",10));
            Check(UsageUnion.Snapshot(new[]{twice,twice},"today",DateTime.Now,"test").TotalTokens==20,"equal event multiplicities are retained across duplicate files");
            var options=new RemoteOptions{Host="fixture",User="demo",Secret=RemoteOptions.Protect("fixture-password")};
            Check(options.Unlock()=="fixture-password"&&!Json.Serialize(options).Contains("fixture-password"),"saved credentials are encrypted");
            Check(new Preferences().Remote!=null&&!new Preferences().Remote.Enabled,"older settings keep remote disabled");
            bool waiting=false;var local=new ActivityReport{ObservedAt=now,ActiveTasks=1,Until=now+4};var remote=new ActivityReport{ObservedAt=now,CompletedTasks=1};
            Check(WidgetWindow.MergeActivity(local,remote,ref waiting).CompletedTasks==0,"other active tasks suppress completion");
            local.ActiveTasks=0;remote.MonitoringUnavailable=true;waiting=false;
            Check(WidgetWindow.MergeActivity(local,remote,ref waiting).CompletedTasks==0&&waiting,"offline completion waits for known state");
            remote.CompletedTasks=0;remote.MonitoringUnavailable=false;
            Check(WidgetWindow.MergeActivity(local,remote,ref waiting).CompletedTasks==1&&!waiting,"recovery releases completion once");
            Check(WidgetWindow.MergeActivity(local,remote,ref waiting).CompletedTasks==0,"completion is not replayed");
            local.ActiveTasks=remote.ActiveTasks=1;local.ActiveKeys.Add("session/turn");remote.ActiveKeys.Add("session/turn");
            Check(WidgetWindow.MergeActivity(local,remote,ref waiting).ActiveTasks==1,"same task copied across endpoints counts once");
            string id=Guid.NewGuid().ToString(),folder=Path.Combine(root,"remote-cache","reader");Directory.CreateDirectory(folder);
            var reader=new LocalCodexUsage(folder,Path.Combine(folder,"usage.json.gz"),false,true);string path="/fixture/sessions/rollout-"+id+".jsonl";
            var cursor=reader.RemoteCursor(path);DateTime at=DateTime.UtcNow.AddSeconds(-40);
            string prefix=Header(id,at)+Tokens(at.AddSeconds(1),100,20);
            byte[] bytes=Encoding.UTF8.GetBytes(prefix);using(var stream=new MemoryStream(bytes))reader.ReadStream(cursor,new LogFile{Path=path,Length=bytes.Length,Modified=1},stream,null,4*1024*1024);
            Check(cursor.Events.Count==1&&cursor.Offset==bytes.Length,"remote stream uses existing numeric parser");
            string next=Tokens(at.AddSeconds(2),200,30);byte[] partial=Encoding.UTF8.GetBytes(prefix+next.TrimEnd('\n'));
            using(var stream=new MemoryStream(partial))reader.ReadStream(cursor,new LogFile{Path=path,Length=partial.Length,Modified=2},stream,null,4*1024*1024);
            Check(cursor.Events.Count==1&&cursor.Offset==bytes.Length,"partial line does not commit usage");
            bytes=Encoding.UTF8.GetBytes(prefix+next);using(var stream=new MemoryStream(bytes))reader.ReadStream(cursor,new LogFile{Path=path,Length=bytes.Length,Modified=3},stream,null,4*1024*1024);
            reader.SaveCache(true);var resumed=new LocalCodexUsage(folder,Path.Combine(folder,"usage.json.gz"),false,true);
            Check(resumed.RemoteCursor(path).Events.Count==2&&resumed.RemoteCursor(path).Offset==bytes.Length,"cache restart preserves numeric records and byte cursor together");
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(prefix)))reader.ReadStream(cursor,new LogFile{Path=path,Length=Encoding.UTF8.GetByteCount(prefix),Modified=4},stream,null,4*1024*1024);
            Check(cursor.Events.Count==1,"truncation rebuilds rather than double counting");
            // A large irrelevant line cannot pin the parser at a batch boundary.
            bytes=Encoding.UTF8.GetBytes(Header(id,at)+"{\"type\":\"response_item\",\"payload\":\""+new string('x',5*1024*1024)+"\"}\n"+Tokens(at.AddSeconds(1),100,20));
            var big=reader.RemoteCursor("/fixture/sessions/rollout-big-"+id+".jsonl");
            for(int i=0;i<3;i++)using(var stream=new MemoryStream(bytes))reader.ReadStream(big,new LogFile{Path=big.Path,Length=bytes.Length,Modified=5},stream,null,4*1024*1024);
            Check(big.Offset==bytes.Length&&big.Events.Count==1,"multi-megabyte irrelevant line is streamed with bounded memory");
            string host=Path.Combine(root,"remote-fixture");Directory.CreateDirectory(Path.Combine(host,"sessions","2020","01"));Directory.CreateDirectory(Path.Combine(host,"archived_sessions"));
            string file=Path.Combine(host,"sessions","2020","01","rollout-"+id+".jsonl");File.WriteAllText(file,prefix+TaskEvent(at.AddSeconds(2),"task_complete","old"),new UTF8Encoding(false));
            var fixture=new Fixture{Root=host};using(var monitor=new RemoteMonitor(()=>new Files(fixture),Path.Combine(root,"remote-cache","monitor"),false))
            {
                int completions=0;var messages=new System.Collections.Concurrent.ConcurrentQueue<RemoteView>();monitor.Changed+=state=>{completions+=state.Activity.CompletedTasks;messages.Enqueue(state);};monitor.Start();
                await StabilityProbe.Until(()=>monitor.View.Records.Any(c=>c.Events.Count==1),"remote history not imported",15000);
                await Task.Delay(2500);Check(completions==0,"initial import never flashes historical completion");
                long read=fixture.Bytes;await Task.Delay(2500);Check(fixture.Bytes-read<8192,"unchanged logs do not redownload history");
                // Use a future server timestamp to prove status freshness uses local observation.
                DateTime future=DateTime.UtcNow.AddHours(2);File.AppendAllText(file,TaskEvent(future,"task_started","live"));
                await StabilityProbe.Until(()=>monitor.View.Activity.ActiveTasks==1,"old session restart not detected",10000);
                Check(monitor.View.Activity.ActiveTasks==1,"old directory session and remote clock offset are supported");
                fixture.Offline=true;await StabilityProbe.Until(()=>!monitor.View.Connected,"disconnect not reported",10000);
                Check(monitor.View.Records.Count>0&&monitor.View.Activity.MonitoringUnavailable,"disconnect retains numeric data and reports unknown");
                File.AppendAllText(file,Tokens(DateTime.UtcNow.AddSeconds(-1),200,30)+TaskEvent(future.AddSeconds(1),"task_complete","live"));fixture.Offline=false;
                await StabilityProbe.Until(()=>completions==1&&monitor.View.Records.Any(c=>c.Events.Count==2),"reconnect did not catch up",20000);
                await Task.Delay(2500);Check(completions==1,"reconnect completion is emitted once");
                Check(fixture.Connections>=2,"transport is recreated after disconnect");
                string fresh=Path.Combine(Path.GetDirectoryName(file),"rollout-"+Guid.NewGuid().ToString()+".jsonl");
                string freshId=LogFile.SessionId(fresh);File.WriteAllText(fresh,Header(freshId,future.AddSeconds(2))+TaskEvent(future.AddSeconds(3),"task_started","fresh"));
                await StabilityProbe.Until(()=>monitor.View.Activity.ActiveTasks==1,"new session not detected",12000);
                File.AppendAllText(fresh,TaskEvent(future.AddSeconds(4),"task_complete","fresh"));
                await StabilityProbe.Until(()=>completions==2,"new session completion missing",12000);
                Check(completions==2,"newly discovered session runs and completes without historical replay");
                Check(messages.Sum(m=>m.Activity.CompletedTasks)==2,"queued publications preserve completion events even after newer snapshots");
                string archived=Path.Combine(host,"archived_sessions",Path.GetFileName(file));
                string archivedPath="/fixture/archived_sessions/"+Path.GetFileName(file);
                fixture.BlockedRead=archivedPath;File.Move(file,archived);monitor.Rescan();
                await StabilityProbe.Until(()=>!monitor.View.Connected,"blocked archive read not reported",15000);
                Check(monitor.View.Records.Any(c=>c.Id==id&&c.Events.Count==2),"moving a log retains its ledger until archive read completes");
                fixture.BlockedRead=null;
                await StabilityProbe.Until(()=>monitor.View.Records.Any(c=>c.Path==archivedPath&&c.Events.Count==2)&&!monitor.View.Records.Any(c=>c.Id==id&&c.Path!=archivedPath),"archive move did not reconcile",20000);
                Check(UsageUnion.Snapshot(monitor.View.Records,"all",DateTime.Now,"test").TotalTokens==350,"archive move retains exactly one copy of usage");
                File.Delete(archived);fixture.IncompleteDirectory="/fixture/sessions/2020/01";int scans=fixture.FullScans;monitor.Rescan();
                await StabilityProbe.Until(()=>fixture.FullScans>scans,"partial scan not started",10000);await Task.Delay(6000);
                Check(monitor.View.Records.Any(c=>c.Id==id&&c.Events.Count==2),"missing recursive directory makes scan incomplete and preserves absent ledger");
                fixture.IncompleteDirectory=null;monitor.Rescan();
                await StabilityProbe.Until(()=>!monitor.View.Records.Any(c=>c.Id==id),"authoritative remote deletion did not remove ledger",15000);
                Check(UsageUnion.Snapshot(monitor.View.Records,"all",DateTime.Now,"test").TotalTokens==0,"complete traversal publishes deleted usage immediately");
                var persisted=new LocalCodexUsage(Path.Combine(root,"remote-cache","monitor"),Path.Combine(root,"remote-cache","monitor","usage.json.gz"),true,true);
                Check(!persisted.Export().Any(c=>c.Id==id),"remote deletion is persisted before restart");
            }
            await Task.Delay(500);
            string error=SftpFiles.Error(new IOException("private-server/private-user/private-prompt"));Check(!error.Contains("private"),"remote errors do not leak raw server or path text");
            var preferences=new Preferences{Source="local",ThemeMode="light",OrbAnimation="off",LiveQuota=false,QuotaCli="",CodexHome=Path.Combine(root,"demo-codex"),Remote=new RemoteOptions{Enabled=true,Host="server.example.com",User="demo",Directory="/home/demo/.codex"}};Theme.Apply(preferences);
            var settings=new SettingsWindow(preferences,v=>{}){ShowActivated=false};settings.Show();
            var panel=StabilityProbe.Field<RemoteSettings>(settings,"remoteSettings");panel.Background=Theme.Background;
            string output=Path.Combine(Path.GetDirectoryName(root),"guide-images");Directory.CreateDirectory(output);
            panel.Measure(new System.Windows.Size(460,Double.PositiveInfinity));panel.Arrange(new System.Windows.Rect(0,0,460,panel.DesiredSize.Height));panel.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(920,(int)Math.Ceiling(panel.ActualHeight*2),192,192,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(panel);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var outputFile=File.Create(Path.Combine(output,"remote-settings.png")))png.Save(outputFile);
            settings.Close();Check(File.Exists(Path.Combine(output,"remote-settings.png")),"manual screenshot uses synthetic server settings only");
        }
    }
}
