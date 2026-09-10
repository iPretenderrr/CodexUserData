using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Run through tools/verify-source.ps1. The host sets TestDataFolder to a unique fixture
    // directory before touching Program; all logs/settings below are generated numeric fixtures.
    internal static class DataStabilityProbe
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
        private static void Check(bool pass,string name){StabilityProbe.Check(pass,name);}
        private static string Event(DateTime at,string type,string turn){return Json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type=type,turn_id=turn}})+"\n";}
        private static string Header(DateTime at,string id){return Json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n";}
        private static ActivityReport Read(CodexActivity activity,string path,DateTime at)
        {
            typeof(CodexActivity).GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(activity,new object[]{path});
            return activity.Scan(at);
        }
        internal static void Run(string fixtureRoot)
        {
            Check(String.Equals(Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar),Program.DataFolder.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase),"fixture data folder is isolated");
            string home=Path.Combine(fixtureRoot,"activity"),logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);
            DateTime start=DateTime.UtcNow.AddSeconds(-30);string file=Path.Combine(logs,"rollout-stability.jsonl");
            File.WriteAllText(file,Header(start,"stability")+Event(start,"task_started","a"),new UTF8Encoding(false));
            using(var activity=new CodexActivity(home,false))
            {
                Check(Read(activity,file,start).ActiveTasks==1,"explicit lifecycle starts activity");
                File.AppendAllText(file,Event(start.AddSeconds(1),"task_started","b")+Event(start.AddSeconds(2),"task_complete","a"));
                var state=Read(activity,file,start.AddSeconds(2));Check(state.ActiveTasks==1&&state.CompletedTasks==0,"late old-turn completion does not stop current turn");
                File.AppendAllText(file,Json.Serialize(new{type="event_msg",timestamp=start.AddSeconds(3).ToString("o"),payload=new{type="agent_message",phase="final_answer",turn_id="b"}})+"\n");
                Check(Read(activity,file,start.AddSeconds(3)).ActiveTasks==1,"assistant final answer does not end an explicit turn");
                state=Read(activity,file,start.AddSeconds(1000));Check(state.ActiveTasks==0&&state.UncertainTasks==1&&state.CompletedTasks==0,"missing signals become unknown without a completion");
            }
            File.WriteAllText(file,Header(start,"stability")+Event(start,"task_started","large")+new string(' ',3*1024*1024)+"\n");
            using(var activity=new CodexActivity(home,false))
            {
                Read(activity,file,start);
                // A rewrite with a longer length used to leave the old turn/header in memory.
                File.WriteAllText(file,Header(start,"replacement")+Event(start.AddSeconds(4),"turn_started","replacement")+new string(' ',3*1024*1024+1024)+"\n");
                var state=Read(activity,file,start.AddSeconds(4));Check(state.ActiveTasks==1,"larger replacement log starts its new turn");
                File.AppendAllText(file,Event(start.AddSeconds(5),"turn_completed","replacement"));
                state=Read(activity,file,start.AddSeconds(5));Check(state.ActiveTasks==0&&state.CompletedTasks==1,"replacement identity accepts its own completion");
                state=Read(activity,file,start.AddSeconds(6));Check(state.CompletedTasks==0&&activity.LastBytesRead==0,"warm polling neither rereads payload nor repeats completion");
                File.AppendAllText(file,Event(start.AddSeconds(7),"task_started","huge-final"));Read(activity,file,start.AddSeconds(7));
                string end=Json.Serialize(new{type="event_msg",timestamp=start.AddSeconds(8).ToString("o"),payload=new{last_agent_message=new string('x',5*1024*1024),type="task_complete",turn_id="huge-final"}})+"\n";
                File.AppendAllText(file,end);state=Read(activity,file,start.AddSeconds(8));Check(activity.LastBytesRead<=4*1024*1024&&state.CompletedTasks==0,"large event respects the per-file read budget");
                state=Read(activity,file,start.AddSeconds(9));Check(state.CompletedTasks==1&&state.ActiveTasks==0,"split large event retains streaming lifecycle metadata");
            }
            TestRotationAndRetention(Path.Combine(fixtureRoot,"rotation"),start);
            TestDeletedCheckpoint(Path.Combine(fixtureRoot,"deleted-checkpoint"),start);
            TestUsage(Path.Combine(fixtureRoot,"usage"),start);
            TestPartialEnumeration(Path.Combine(fixtureRoot,"partial-enumeration"),start);
            TestIncrementalPerformance(Path.Combine(fixtureRoot,"performance"),start);
            TestSettings(fixtureRoot);
            var preferences=new Preferences{CodexHome=home,QuotaCli="",LiveQuota=false};
            using(var quota=new QuotaStatus(()=>preferences,delegate{},delegate{},true))
            {
                quota.ApplyActivity(new ActivityReport{CompletedTasks=1,UncertainTasks=1});Check(!quota.CompletionPending,"unresolved concurrent work does not announce all tasks complete");
                quota.ApplyActivity(new ActivityReport{CompletedTasks=1});Check(quota.CompletionPending,"confirmed completion remains pending");
                quota.ApplyActivity(new ActivityReport());Check(quota.CompletionPending,"completion survives subsequent idle polls");
                quota.ApplyActivity(new ActivityReport{ActiveTasks=1});Check(!quota.CompletionPending,"new task clears pending completion");
                quota.ApplyActivity(new ActivityReport{CompletedTasks=1});quota.AcknowledgeCompletion();Check(!quota.CompletionPending,"hover acknowledgement clears completion");
            }
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();bool canceled=false;
                try{QuotaReader.Query(Path.Combine(fixtureRoot,"absent-cli.exe"),home,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}
                Check(canceled,"canceled quota request never starts a helper");
            }
            TestQuotaCancellation(Path.Combine(fixtureRoot,"quota-cancel"));
        }
        private static void TestIncrementalPerformance(string home,DateTime start)
        {
            string logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string id=Guid.NewGuid().ToString(),file=Path.Combine(logs,"rollout-"+id+".jsonl");
            File.WriteAllText(file,Header(start,id)+Event(start,"task_started","incremental"));
            using(var activity=new CodexActivity(home,false))
            {
                Read(activity,file,start);long expected=0,actual=0;bool stayedActive=true;
                for(int i=1;i<=20;i++)
                {
                    string line=Event(start.AddMilliseconds(i),"agent_reasoning","incremental");expected+=Encoding.UTF8.GetByteCount(line);File.AppendAllText(file,line);
                    stayedActive&=Read(activity,file,start.AddSeconds(1)).ActiveTasks==1;actual+=activity.LastBytesRead;
                }
                Check(stayedActive,"twenty incremental activity updates remain running");
                Check(actual==expected,"activity consumes only appended payload bytes");
                long idle=0;for(int i=0;i<20;i++){Read(activity,file,start.AddSeconds(2));idle+=activity.LastBytesRead;}
                Check(idle==0,"twenty unchanged activity polls read zero payload bytes");
                Console.WriteLine("MEASURE activity appended="+expected+" consumed="+actual+" idle20="+idle+" payload bytes (fixed identity samples excluded)");
            }
            string prefix=Header(start,id)+"{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\"";
            File.WriteAllText(file,prefix+new string('x',3*1024*1024));
            string cache=Path.Combine(home,"fixture-cache.gz");var reader=new LocalCodexUsage(home,cache);
            reader.Read("today",DateTime.UtcNow,null,null);long cold=reader.LastBytesRead,idleUsage=0;
            for(int i=0;i<10;i++){reader.Read("today",DateTime.UtcNow,null,null);idleUsage+=reader.LastBytesRead;}
            Check(idleUsage==0,"unfinished irrelevant multi-megabyte lines are not rescanned on idle polls");
            File.AppendAllText(file,new string('x',4096));reader.Read("today",DateTime.UtcNow,null,null);long appended=reader.LastBytesRead;
            Check(appended==4096,"unfinished irrelevant line resumes at its byte checkpoint");
            string usage=Json.Serialize(new{type="event_msg",timestamp=start.ToString("o"),payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=100,output_tokens=3,total_tokens=103}}}})+"\n";
            File.AppendAllText(file,"\"}}\n"+usage);
            Check(reader.Read("today",DateTime.UtcNow,null,null).TotalTokens==103,"skipping a partial line preserves the next complete usage event");
            reader.SaveCache(true);var warmed=new LocalCodexUsage(home,cache);
            Check(warmed.Read("today",DateTime.UtcNow,null,null).TotalTokens==103&&warmed.LastBytesRead==0,"persisted numeric cache resumes without rereading complete payloads");
            Console.WriteLine("MEASURE usage cold="+cold+" idle10="+idleUsage+" append4096="+appended+" cached="+warmed.LastBytesRead+" payload bytes");
        }
        private static void TestQuotaCancellation(string home)
        {
            Directory.CreateDirectory(home);File.WriteAllText(Path.Combine(home,"fake-quota.fixture"),"synthetic fixture");
            string helper=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FakeQuotaHelper.exe");Check(File.Exists(helper),"synthetic quota helper is compiled");
            using(var cancel=new CancellationTokenSource())
            {
                var request=Task.Run(()=>QuotaReader.Query(helper,home,cancel.Token));Process child=null;
                try
                {
                    var startup=Stopwatch.StartNew();while(!File.Exists(Path.Combine(home,"helper.ready"))&&startup.ElapsedMilliseconds<5000)Thread.Sleep(10);
                    Check(File.Exists(Path.Combine(home,"helper.ready")),"synthetic helper reached an in-flight quota read");
                    child=Process.GetProcessById(Int32.Parse(File.ReadAllText(Path.Combine(home,"helper.pid"))));
                    var elapsed=Stopwatch.StartNew();cancel.Cancel();
                    Check(Task.WhenAny(request,Task.Delay(5000)).GetAwaiter().GetResult()==request,"running quota cancellation completes within its bound");
                    bool canceled=false;try{request.GetAwaiter().GetResult();}catch(OperationCanceledException){canceled=true;}
                    Check(canceled,"in-flight quota request observes cancellation");
                    Check(child.WaitForExit(1000),"canceled request terminates only its synthetic helper");
                    Console.WriteLine("MEASURE quota cancellation="+elapsed.ElapsedMilliseconds+" ms");
                }
                finally
                {
                    cancel.Cancel();
                    if(child!=null){try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException){}finally{child.Dispose();}}
                }
            }
        }
        private static void TestRotationAndRetention(string home,DateTime start)
        {
            string logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string file=Path.Combine(logs,"rollout-rotation.jsonl");
            // Equal-length lifecycle names make the new task observable without appending bytes.
            File.WriteAllText(file,Header(start,"session-a")+Event(start,"turn_aborted","turn-a"));
            using(var activity=new CodexActivity(home,false))
            {
                Read(activity,file,start);DateTime modified=File.GetLastWriteTimeUtc(file),created=File.GetCreationTimeUtc(file);long length=new FileInfo(file).Length;
                File.WriteAllText(file,Header(start,"session-b")+Event(start,"task_started","turn-b"));
                File.SetLastWriteTimeUtc(file,modified);File.SetCreationTimeUtc(file,created.AddSeconds(2));
                Check(new FileInfo(file).Length==length,"same-size rotation fixture preserves modification time");
                Check(Read(activity,file,start.AddSeconds(1)).ActiveTasks==1,"replacement is detected before another write changes its length");
                File.AppendAllText(file,Event(start.AddSeconds(2),"task_complete","turn-b"));
                var state=Read(activity,file,start.AddSeconds(2));Check(state.CompletedTasks==1&&state.ActiveTasks==0,"creation identity detects same-size timestamp-preserving replacement");
                DateTime later=start.AddDays(2);
                File.AppendAllText(file,"{}\n");File.SetLastWriteTimeUtc(file,later);
                state=Read(activity,file,later);Check(state.CompletedTasks==0,"old completion retained in an updated log is not replayed after a day");
                state=Read(activity,file,later.AddSeconds(1));Check(state.CompletedTasks==0,"completion deduplication survives repeated late polls");
            }
        }
        private static void TestUsage(string home,DateTime start)
        {
            string logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string id=Guid.NewGuid().ToString(),file=Path.Combine(logs,"rollout-"+id+".jsonl");
            File.WriteAllText(file,Header(start,id)+Event(start,"task_started","one")+Event(start.AddSeconds(1),"turn_started","two")+Event(start.AddSeconds(2),"task_complete","one"));
            var reader=new LocalCodexUsage(home,Path.Combine(home,"fixture-cache.gz"));
            Check(reader.Read("today",DateTime.UtcNow,null,null).ActiveTasks==1,"statistics lifecycle uses the same old-turn filtering");
            File.AppendAllText(file,Event(start.AddSeconds(3),"turn_completed","two"));
            Check(reader.Read("today",DateTime.UtcNow,null,null).ActiveTasks==0,"statistics accepts alternate lifecycle end names");
            reader.Read("today",DateTime.UtcNow,null,null);Check(reader.LastBytesRead==0,"unchanged usage logs use their cached events");
        }
        private static void TestDeletedCheckpoint(string home,DateTime start)
        {
            string logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string file=Path.Combine(logs,"rollout-reused-path.jsonl");
            File.WriteAllText(file,Header(start,"original")+new string(' ',4096)+"\n");
            using(var monitor=new CodexActivity(home,false))
            {
                Read(monitor,file,start);File.Delete(file);Read(monitor,file,start.AddSeconds(1));
                // A longer replacement isolates the deletion cleanup from the separate
                // Resume > length truncation guard exercised by the next fixture.
                File.WriteAllText(file,Header(start,"replacement")+Event(start.AddSeconds(2),"task_started","replacement-turn")+new string(' ',8192)+"\n");
                Check(Read(monitor,file,start.AddSeconds(2)).ActiveTasks==1,"a deleted and recreated log cannot inherit the old path's resume checkpoint");
            }
            File.WriteAllText(file,Header(start.AddDays(-2),"old-unopened")+new string(' ',4096)+"\n");File.SetLastWriteTimeUtc(file,start.AddDays(-2));
            using(var monitor=new CodexActivity(home,false))
            {
                monitor.Scan(start);Check(monitor.LastBytesRead==0,"old unmodified fixture records only a startup checkpoint without reading its history");
                File.WriteAllText(file,Header(start,"short-replacement")+Event(start.AddSeconds(2),"task_started","short-turn"));
                Check(Read(monitor,file,start.AddSeconds(2)).ActiveTasks==1,"a shorter replacement invalidates an unopened historical resume checkpoint");
            }
        }
        private static void TestPartialEnumeration(string home,DateTime start)
        {
            string firstDirectory=Path.Combine(home,"sessions","first"),secondDirectory=Path.Combine(home,"sessions","second");Directory.CreateDirectory(firstDirectory);Directory.CreateDirectory(secondDirectory);
            string firstId=Guid.NewGuid().ToString(),secondId=Guid.NewGuid().ToString(),first=Path.Combine(firstDirectory,"rollout-"+firstId+".jsonl"),second=Path.Combine(secondDirectory,"rollout-"+secondId+".jsonl");
            Func<int,int,string> usage=(tokens,secondOffset)=>Json.Serialize(new{type="event_msg",timestamp=start.AddSeconds(secondOffset).ToString("o"),payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=tokens,output_tokens=0,total_tokens=tokens}}}})+"\n";
            File.WriteAllText(first,Header(start,firstId)+usage(100,1));File.WriteAllText(second,Header(start,secondId)+usage(200,1));
            string cache=Path.Combine(home,"fixture-cache.gz");var reader=new LocalCodexUsage(home,cache);var snapshot=reader.Read("all",DateTime.UtcNow,null,null);
            Check(snapshot.TotalTokens==300&&snapshot.CoverageWarnings==0,"complete initial enumeration aggregates both synthetic directories");
            // Reuse the production post-enumeration path with one explicit enumeration failure.
            // This models an unreadable first directory while the second continues receiving data.
            File.AppendAllText(second,usage(300,2));snapshot=reader.ReadEnumerated("all",DateTime.UtcNow,new List<FileInfo>{new FileInfo(second)},1,null,null);
            Check(snapshot.TotalTokens==400&&snapshot.CoverageWarnings==1&&snapshot.Warning.Contains("保留上次可读数据"),"partial enumeration retains missing cached usage while updating readable files and warning about coverage");
            reader.SaveCache(true);var warmed=new LocalCodexUsage(home,cache);snapshot=warmed.ReadEnumerated("all",DateTime.UtcNow,new List<FileInfo>{new FileInfo(second)},1,null,null);
            Check(snapshot.TotalTokens==400&&warmed.LastBytesRead==0,"retained ledgers survive a cache save and reload during partial enumeration");
            File.Delete(first);snapshot=warmed.Read("all",DateTime.UtcNow,null,null);
            Check(snapshot.TotalTokens==300&&snapshot.CoverageWarnings==0,"a later complete real traversal removes genuinely deleted logs and clears coverage failure");
        }
        private static void TestSettings(string root)
        {
            var preferences=new Preferences{CodexHome=Path.Combine(root,"activity"),Database=Path.Combine(root,"fixture.db"),QuotaCli="",LiveQuota=false,Width=430};
            Program.Save(preferences);preferences.Width=470;Program.Save(preferences);
            Check(Program.ReadPreferences().Width==470&&File.Exists(Program.SettingsPath+".bak"),"atomic settings save keeps previous valid settings");
            File.WriteAllText(Program.SettingsPath,"{damaged fixture");
            var recovered=Program.ReadPreferences();Check(recovered.Width==430,"damaged settings recover from valid backup");
            recovered.Width=490;Program.Save(recovered);
            Check(Program.ReadPreferences().Width==490&&File.ReadAllText(Program.SettingsPath+".corrupt")=="{damaged fixture","recovery save preserves damaged original separately");
            Check(Json.Deserialize<Preferences>(File.ReadAllText(Program.SettingsPath+".bak")).Width==430,"recovery save preserves the known-good backup");
            File.WriteAllText(Program.SettingsPath,"{");File.WriteAllText(Program.SettingsPath+".bak","{");bool refused=false;
            try{Program.ReadPreferences();}catch(IOException){refused=true;}
            Check(refused,"two invalid settings files are retained rather than silently reset");
            // Restore valid synthetic settings for other probes sharing the isolated host.
            File.WriteAllText(Program.SettingsPath,Json.Serialize(preferences));File.WriteAllText(Program.SettingsPath+".bak",Json.Serialize(preferences));
            Check(Directory.GetFiles(root,"widget-settings.*.tmp").Length==0,"settings writes leave no temporary files");
        }
    }
}
