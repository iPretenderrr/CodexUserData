using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal static class MilestoneDataStabilityProbe
    {
        private static void Check(bool pass,string name){StabilityProbe.Check(pass,"Milestone data: "+name);}
        private static long Sum(MilestoneInput input){return input.Events.Sum(e=>e.Tokens);}
        private static LocalUsageEvent E(long at,long tokens,string signature,bool inferred=false){return new LocalUsageEvent{Time=at,Input=tokens,Signature=signature,Inferred=inferred,Model="test",Effort="unknown"};}
        private static LogCursor C(string id,long at,params LocalUsageEvent[] events){return new LogCursor{Id=id,Started=at,Meta=true,Events=events.ToList()};}
        internal static void Run(string root)
        {
            DateTime now=new DateTime(2026,9,22,12,0,0,DateTimeKind.Local);long at=LocalCodexUsage.Unix(now);
            var parent=C("parent",at-100,E(at-90,3000000000L,"a"));
            var child=C("child",at-50,E(at-50,3000000000L,"a"),E(at-40,11,"b",true));child.Parent="parent";
            var future=C("future",at-100,E(at+60,17,"future"));var missing=C("missing",at,E(at-10,99,"missing"));missing.Meta=false;
            var orphan=C("orphan",at,E(at-10,200,"orphan"));orphan.Parent="absent";
            var records=new[]{parent,child,future,missing,orphan}.ToDictionary(c=>c.Id);
            var input=LocalCodexUsage.BuildMilestones(records,now,0,CancellationToken.None);
            Check(Sum(input)==3000000011L&&Sum(input)==LocalCodexUsage.Aggregate(records,"all",now).TotalTokens,"effective parent/meta filtering and Int64 match Aggregate");
            Check(input.Events.Count==2&&input.Events.Count(e=>e.Precision==1)==1&&input.NextChangeAt==at+60,"inferred time is unknown and future usage is deferred");
            var memo=new MilestoneLedgerMemo();var first=memo.Get(records,now,0,CancellationToken.None);
            Check(Object.ReferenceEquals(first,memo.Get(records,now.AddSeconds(1),0,CancellationToken.None)),"unchanged numeric ledger reuses full event timeline");
            Check(Sum(memo.Get(records,now.AddSeconds(60),0,CancellationToken.None))==3000000028L,"future timestamp invalidates warm timeline");
            Check(Sum(memo.Get(records,now.AddSeconds(-1),0,CancellationToken.None))==3000000011L,"clock rollback removes future usage again");
            var union=new UsageUnionCache();var local=new List<LogCursor>{parent};var remote=new List<LogCursor>{LocalCodexUsage.CopyCursor(parent),child};
            var combined=union.GetMilestones(local,remote,"all",now,CancellationToken.None);
            Check(Sum(combined)==UsageUnion.Snapshot(local.Concat(remote),"all",now,"test").TotalTokens,"union deduplication matches headline totals");
            var copied=local.Select(LocalCodexUsage.CopyCursor).ToList();copied[0].TaskRunning=true;
            Check(Object.ReferenceEquals(combined,union.GetMilestones(copied,remote,"all",now,CancellationToken.None)),"activity publication copies do not rebuild union timeline");
            var replaced=C("parent",at-100,E(at-90,41,"different"));
            Check(Sum(union.GetMilestones(new List<LogCursor>{replaced},new List<LogCursor>(),"local",now,CancellationToken.None))==41,"replacement cursor identity invalidates equal-count/version source stamps");
            var conflict=C("parent",at-99,E(at-90,55,"conflict"));var conflicted=union.GetMilestones(local,new List<LogCursor>{conflict},"all",now,CancellationToken.None);
            Check(conflicted.Warning.Length>0&&Object.ReferenceEquals(conflicted,union.GetMilestones(local,new List<LogCursor>{LocalCodexUsage.CopyCursor(conflict)},"all",now.AddSeconds(1),CancellationToken.None)),"unchanged conflict warning preserves input identity and engine cache");
            TestLocal(root,now);TestDatabase(root,now);
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();bool stopped=false;try{LocalCodexUsage.BuildMilestones(records,now,0,cancel.Token);}catch(OperationCanceledException){stopped=true;}
                Check(stopped,"cancelled adapter returns without building timeline");
            }
        }
        private static void TestLocal(string root,DateTime now)
        {
            string folder=Path.Combine(root,"milestone-local");Directory.CreateDirectory(Path.Combine(folder,"sessions"));
            string id=Guid.NewGuid().ToString(),file=Path.Combine(folder,"sessions","rollout-"+id+".jsonl");var json=new JavaScriptSerializer();DateTime at=now.AddMinutes(-1);
            string prefix=json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n"+
                json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type="token_count",info=new{last_token_usage=new{input_tokens=100,output_tokens=20}}}})+"\n";
            File.WriteAllText(file,prefix,new UTF8Encoding(false));var reader=new LocalCodexUsage(folder,Path.Combine(folder,"cache.gz"));reader.Update(null,null);
            var first=reader.GetMilestones(now,CancellationToken.None);File.AppendAllText(file,json.Serialize(new{type="event_msg",timestamp=now.ToString("o"),payload=new{type="task_started",turn_id="fixture"}})+"\n"+"{\"type\":\"response_item\",\"payload\":\"ignored\"}\n");reader.Update(null,null);
            Check(Sum(first)==120&&Object.ReferenceEquals(first,reader.GetMilestones(now,CancellationToken.None)),"activity and irrelevant payload appends retain local timeline cache");
            File.WriteAllText(file,prefix.Replace("100","200"),new UTF8Encoding(false));reader.Update(null,null);
            Check(Sum(reader.GetMilestones(now,CancellationToken.None))==220,"same-count log rewrite invalidates numeric timeline");
        }
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2(byte[] name,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr data,out IntPtr error);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void sqlite3_free(IntPtr pointer);
        private static void Write(string path,string sql)
        {
            IntPtr db,error;if(sqlite3_open_v2(Encoding.UTF8.GetBytes(path+"\0"),out db,6,IntPtr.Zero)!=0)throw new IOException("fixture open failed");
            try{int result=sqlite3_exec(db,Encoding.UTF8.GetBytes(sql+"\0"),IntPtr.Zero,IntPtr.Zero,out error);if(error!=IntPtr.Zero)sqlite3_free(error);if(result!=0)throw new IOException("fixture SQL failed: "+result);}finally{sqlite3_close(db);}
        }
        private static void Schema(string path)
        {
            Write(path,"CREATE TABLE proxy_request_logs(id INTEGER PRIMARY KEY,created_at INTEGER,app_type TEXT DEFAULT 'codex',model TEXT DEFAULT 'test',input_tokens INTEGER DEFAULT 0,output_tokens INTEGER DEFAULT 0,cache_read_tokens INTEGER DEFAULT 0,cache_creation_tokens INTEGER DEFAULT 0,input_token_semantics INTEGER DEFAULT 2,status_code INTEGER DEFAULT 200,data_source TEXT DEFAULT 'proxy',total_cost_usd TEXT DEFAULT '0');"+
                "CREATE TABLE usage_daily_rollups(date TEXT,app_type TEXT DEFAULT 'codex',model TEXT DEFAULT 'test',input_tokens INTEGER DEFAULT 0,output_tokens INTEGER DEFAULT 0,cache_read_tokens INTEGER DEFAULT 0,cache_creation_tokens INTEGER DEFAULT 0,input_token_semantics INTEGER DEFAULT 2,request_count INTEGER DEFAULT 1,success_count INTEGER DEFAULT 1,total_cost_usd TEXT DEFAULT '0');");
        }
        private static void TestDatabase(string root,DateTime now)
        {
            string path=Path.Combine(root,"milestone.db"),replacement=Path.Combine(root,"milestone-replacement.db");Schema(path);long at=LocalCodexUsage.Unix(now),yesterday=LocalCodexUsage.Unix(now.Date.AddDays(-1).AddHours(12));
            Write(path,"PRAGMA journal_mode=WAL;INSERT INTO proxy_request_logs(created_at,input_tokens,cache_read_tokens,cache_creation_tokens,input_token_semantics) VALUES("+(at-20)+",100,20,10,1);"+
                "INSERT INTO proxy_request_logs(created_at,input_tokens,data_source,cache_read_tokens,cache_creation_tokens,input_token_semantics) VALUES("+(at-20)+",100,'codex_session',20,10,1);"+
                "INSERT INTO proxy_request_logs(created_at,input_tokens) VALUES("+yesterday+",7),("+(at+60)+",13);"+
                "INSERT INTO proxy_request_logs(created_at,input_tokens,app_type) VALUES("+(at-10)+",19,'claude-desktop');"+
                "INSERT INTO usage_daily_rollups(date,input_tokens) VALUES('2026-09-21',3000000000),('2026-09-22',23);");
            using(var reader=new UsageDatabase.MilestoneReader(path,"codex"))
            {
                var first=reader.Read(now,CancellationToken.None);
                Check(Sum(first)==3000000107L&&Sum(first)==UsageDatabase.Read(path,"all","codex",now).TotalTokens,"database fresh-input semantics, effective filter and app match all-time total");
                Check(first.Events.Any(e=>e.Precision==2&&e.Tokens==3000000007L)&&first.Events.Count==2,"mixed detail and rollup day stays a single honest date interval");
                Check(Object.ReferenceEquals(first,reader.Read(now.AddSeconds(1),CancellationToken.None)),"unchanged persistent data_version reuses database timeline");
                Write(path,"UPDATE proxy_request_logs SET input_tokens=101 WHERE id=1;");
                var edited=reader.Read(now,CancellationToken.None);
                Check(!Object.ReferenceEquals(first,edited)&&Sum(edited)==UsageDatabase.Read(path,"all","codex",now).TotalTokens,"WAL update with unchanged row count invalidates data_version");
                Write(path,"BEGIN;DELETE FROM proxy_request_logs WHERE created_at="+yesterday+";UPDATE usage_daily_rollups SET input_tokens=input_tokens+7 WHERE date='2026-09-21';COMMIT;");
                Check(Sum(reader.Read(now,CancellationToken.None))==Sum(edited),"atomic archival keeps detail/rollup total consistent");
                Write(path,"DELETE FROM proxy_request_logs WHERE id=1;");
                Check(Sum(reader.Read(now,CancellationToken.None))==UsageDatabase.Read(path,"all","codex",now).TotalTokens,"deletion invalidates historical timeline");
                Check(Sum(reader.Read(now.AddSeconds(60),CancellationToken.None))==UsageDatabase.Read(path,"all","codex",now.AddSeconds(60)).TotalTokens,"future detail becomes eligible without database write");
                DateTime end=now.Date.AddHours(23).AddMinutes(59);
                Check(Sum(reader.Read(end,CancellationToken.None))==UsageDatabase.Read(path,"all","codex",end).TotalTokens,"23:59 rollup activation matches existing source boundary");
                Check(Sum(reader.Read(now,CancellationToken.None))==UsageDatabase.Read(path,"all","codex",now).TotalTokens,"database cache invalidates when clock moves backward");
            }
            using(var reader=new UsageDatabase.MilestoneReader(path,"claude"))Check(Sum(reader.Read(now,CancellationToken.None))==19,"claude-desktop alias matches app filtering");
            // A rollback-journal fixture permits external in-place replacement while a read-only
            // connection is idle; its file ID and SQLite change counter can both remain unchanged.
            string replaceTarget=Path.Combine(root,"milestone-replace-target.db");Schema(replaceTarget);Schema(replacement);
            Write(replaceTarget,"INSERT INTO proxy_request_logs(created_at,input_tokens) VALUES("+(at-1)+",31);");Write(replacement,"INSERT INTO proxy_request_logs(created_at,input_tokens) VALUES("+(at-1)+",47);");
            using(var reader=new UsageDatabase.MilestoneReader(replaceTarget,"codex"))
            {
                Check(Sum(reader.Read(now,CancellationToken.None))==31,"replacement fixture baseline");
                File.Copy(replacement,replaceTarget,true);File.SetLastWriteTimeUtc(replaceTarget,DateTime.UtcNow.AddSeconds(2));
                Check(Sum(reader.Read(now,CancellationToken.None))==47,"same-path replacement refreshes a persistent connection");
            }
        }
    }
}
