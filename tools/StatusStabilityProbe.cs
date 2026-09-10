using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUserData
{
    internal static class StatusStabilityProbe
    {
        private static void Check(bool pass,string name){StabilityProbe.Check(pass,"Status: "+name);}
        private static QuotaBucket Bucket(long now,string source,double used=27)
        {return new QuotaBucket{Id="codex",Name="Codex",Origin=source,ObservedAt=now,Secondary=new QuotaWindow{UsedPercent=used,Minutes=10080,ResetsAt=now+600}};}
        private static TextBlock FindText(DependencyObject node,string id)
        {
            var text=node as TextBlock;if(text!=null&&AutomationProperties.GetAutomationId(text)==id)return text;
            foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()){var found=FindText(child,id);if(found!=null)return found;}return null;
        }
        internal static void Run(string root)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);var online=Bucket(now,"在线查询");var log=Bucket(now,"日志快照",35);
            Check(!QuotaBucket.Prefer(online,log)&&QuotaBucket.Prefer(log,online),"same-second snapshots prefer an online account response");
            var future=Bucket(Int64.MaxValue,"日志快照");Check(!QuotaBucket.Prefer(online,future)&&QuotaBucket.Prefer(future,online),"future-dated snapshots are rejected and cannot poison later online updates");
            log.ObservedAt=now+1;Check(QuotaBucket.Prefer(online,log),"a newer historical snapshot still replaces an older response");log.ObservedAt=now;
            var preferences=new Preferences{CodexHome=Path.Combine(root,"codex"),Database=Path.Combine(root,"missing.db"),QuotaCli="",LiveQuota=false};
            using(var status=new QuotaStatus(()=>preferences,delegate{},delegate{},true))
            {
                status.Accept(new[]{online});status.Accept(new[]{log});Check(Object.ReferenceEquals(status.MainBucket,online),"actual quota display retains online provenance at a timestamp tie");
                Check(status.StatusText.Contains("在线额度")&&status.StatusText.Contains("更新于"),"main quota text identifies online source and update time");
                log.ObservedAt=now+1;status.Accept(new[]{log});Check(status.StatusText.Contains("历史快照")&&status.StatusText.Contains("记录于"),"main quota text identifies a historical snapshot");
            }
            var value=Bucket(now,"在线查询");Check(value.Secondary.RemainingPercent(value,now)==73,"fresh quota retains its precise remaining percentage");
            Check(value.Secondary.RemainingPercent(value,now+300)==73&&!value.Secondary.RemainingPercent(value,now+301).HasValue,"freshness boundary clears percentages after five minutes");
            value.Secondary.ResetsAt=now;Check(!value.Secondary.RemainingPercent(value,now).HasValue,"reset boundary requires a new sample rather than implying full quota");value.Secondary.ResetsAt=0;
            foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-1d,101d}){value.Secondary.UsedPercent=invalid;Check(!value.Secondary.RemainingPercent(value,now).HasValue,"invalid quota percentage is unknown: "+invalid);}
            value.Secondary.UsedPercent=27;value.ObservedAt=now+6;Check(!value.Secondary.RemainingPercent(value,now).HasValue,"future-dated account sample is unknown");
            value.ObservedAt=now;value.Secondary.ResetsAt=Int64.MaxValue;Check(!value.Secondary.RemainingPercent(value,now).HasValue&&QuotaBucket.Timestamp(Int64.MaxValue)=="时间未知","out-of-range reset timestamps neither display percentages nor throw");
            value.ObservedAt=0;Check(!value.IsFresh(now)&&value.DescribeSource(now).Contains("时间异常"),"missing observation timestamps have an explicit unknown state");
            value=Bucket(now-301,"日志快照");Check(value.DescribeSource(now).Contains("已过期"),"expired historical snapshots are labelled explicitly");
            Check(QuotaStatus.ActivityLabel(null,false,now)=="任务状态未知"&&QuotaStatus.ActivityLabel(new ActivityReport(),false,now)=="任务状态未知","missing activity reports never claim idle");
            var report=new ActivityReport{ObservedAt=now,ActiveTasks=2,Until=now+4};Check(QuotaStatus.ActivityLabel(report,true,now).StartsWith("正在运行"),"fresh active tasks take precedence over completion text");
            report.ActiveTasks=0;report.UncertainTasks=1;Check(QuotaStatus.ActivityLabel(report,false,now)=="任务状态待确认","uncertain tasks are distinguished from idle");
            report.UncertainTasks=0;Check(QuotaStatus.ActivityLabel(report,false,now)=="当前空闲","fresh explicit idle report is displayed as idle");
            Check(QuotaStatus.ActivityLabel(report,false,now+6)=="任务状态未知"&&QuotaStatus.ActivityLabel(null,true,now).Contains("已完成"),"stale telemetry becomes unknown while an unacknowledged completion remains visible");
            Check(QuotaStatus.ShowCompletionMark(true,0,false)&&QuotaStatus.ShowCompletionMark(true,1,false),"disabled or unavailable motion retains the completion mark on every frame");
            Check(QuotaStatus.ShowCompletionMark(true,0,true)&&!QuotaStatus.ShowCompletionMark(true,1,true)&&!QuotaStatus.ShowCompletionMark(false,0,false),"allowed completion animation alternates and acknowledgement removes the mark");
            using(var orb=new QuotaOrb())
            {
                orb.Apply(preferences,Bucket(now,"日志快照"),null,"fixture");Check(orb.VisibleRingCount==1&&orb.ToolTip==null,"historical single-window ring remains compact without hover text");
                orb.Apply(preferences,Bucket(now-301,"日志快照"),null,"fixture");Check(orb.VisibleRingCount==0&&!AutomationProperties.GetName(orb).Contains("%"),"expired orb clears numeric quotas into a neutral waiting state");
                orb.ApplyActivity(null);Check(AutomationProperties.GetItemStatus(orb)=="任务状态未知","orb accessibility status distinguishes unavailable activity");
            }
            var popup=new TrayFlyout(delegate{},delegate{},delegate{});
            try
            {
                var expiring=Bucket(LocalCodexUsage.Unix(DateTime.UtcNow),"在线查询");expiring.Secondary.ResetsAt=LocalCodexUsage.Unix(DateTime.UtcNow)+1;
                popup.Apply(new[]{expiring},null,"fixture","fixture source");Check(FindText(popup,"TrayQuotaRemaining").Text=="73%","tray initially displays a valid quota");
                Thread.Sleep(1100);popup.Apply(new[]{expiring},null,"fixture","fixture source");
                Check(FindText(popup,"TrayQuotaRemaining").Text=="—","unchanged snapshot clears tray numbers after reset without requiring new data");
            }
            finally{popup.Close();}
            TestUnavailableMonitoring(Path.Combine(root,"monitoring"));
            TestBacklogMonitoring(Path.Combine(root,"backlog"));
            TestUncertainCompletion(Path.Combine(root,"uncertain"));
            TestCompletionMotion();
        }
        private static void TestUnavailableMonitoring(string root)
        {
            DateTime instant=DateTime.UtcNow;long now=LocalCodexUsage.Unix(instant);
            using(var missing=new CodexActivity(Path.Combine(root,"absent"),false))
            {
                var report=missing.Scan(instant);Check(report.MonitoringUnavailable&&QuotaStatus.ActivityLabel(report,false,now)=="任务状态未知","missing sessions directory is unavailable rather than idle");
                string createdLogs=Path.Combine(root,"absent","sessions"),createdFile=Path.Combine(createdLogs,"rollout-created.jsonl");
                Directory.CreateDirectory(createdLogs);File.WriteAllText(createdFile,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"created\"}}\n{\"type\":\"event_msg\",\"timestamp\":\""+instant.AddSeconds(1).ToString("o")+"\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"new\"}}\n");
                report=missing.Scan(instant.AddSeconds(1));Check(!report.MonitoringUnavailable&&report.ActiveTasks==1,"new sessions directory is discovered on the next poll");
                File.Delete(createdFile);Directory.Delete(createdLogs);report=missing.Scan(instant.AddSeconds(2));Check(report.MonitoringUnavailable,"deleted sessions directory invalidates monitoring");
                Directory.CreateDirectory(createdLogs);File.WriteAllText(createdFile,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"recreated\"}}\n{\"type\":\"event_msg\",\"timestamp\":\""+instant.AddSeconds(3).ToString("o")+"\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"replacement\"}}\n");
                report=missing.Scan(instant.AddSeconds(3));Check(!report.MonitoringUnavailable&&report.ActiveTasks==1,"deleted and recreated sessions directory gets a fresh watcher immediately");
            }
            string home=Path.Combine(root,"locked"),logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string file=Path.Combine(logs,"rollout-locked.jsonl");
            Func<DateTime,string,string> line=(at,type)=>"{\"type\":\"event_msg\",\"timestamp\":\""+at.ToString("o")+"\",\"payload\":{\"type\":\""+type+"\",\"turn_id\":\"locked-turn\"}}\n";
            File.WriteAllText(file,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"locked-session\"}}\n"+line(instant,"task_started"),new UTF8Encoding(false));
            var queue=typeof(CodexActivity).GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var monitor=new CodexActivity(home,false))
            {
                monitor.Scan(instant);File.AppendAllText(file,line(instant.AddSeconds(1),"task_complete"));
                using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                {
                    queue.Invoke(monitor,new object[]{file});var report=monitor.Scan(instant.AddSeconds(1));
                    Check(report.MonitoringUnavailable&&report.ActiveTasks==1&&report.CompletedTasks==0,"read failure preserves confirmed running evidence without inventing completion");
                }
                queue.Invoke(monitor,new object[]{file});var recovered=monitor.Scan(instant.AddSeconds(2));
                Check(!recovered.MonitoringUnavailable&&recovered.CompletedTasks==1&&recovered.ActiveTasks==0,"released log lock recovers monitoring and delivers the actual completion");
            }
            var preferences=new Preferences{LiveQuota=false,CodexHome=home,QuotaCli=""};
            using(var status=new QuotaStatus(()=>preferences,delegate{},delegate{},true))
            {
                status.ApplyActivity(new ActivityReport{ObservedAt=now,CompletedTasks=1,MonitoringUnavailable=true});Check(!status.CompletionPending,"unavailable monitoring cannot create a completion notification");
                status.ApplyActivity(new ActivityReport{ObservedAt=now,CompletedTasks=1});status.ApplyActivity(new ActivityReport{ObservedAt=now,MonitoringUnavailable=true});
                Check(status.CompletionPending,"unavailable monitoring preserves an already pending acknowledgement");
            }
        }
        private static void TestBacklogMonitoring(string root)
        {
            Check(CodexActivity.IsMissingLog(new FileNotFoundException())&&CodexActivity.IsMissingLog(new DirectoryNotFoundException()),"only definite missing-path errors discard a log tail");
            Check(!CodexActivity.IsMissingLog(new UnauthorizedAccessException())&&!CodexActivity.IsMissingLog(new IOException("fixture read failure")),"access and IO failures retain unknown monitoring rather than imply deletion");
            TestQueuedBatch(Path.Combine(root,"active"),true,false);
            TestQueuedBatch(Path.Combine(root,"completed"),false,false);
            TestQueuedBatch(Path.Combine(root,"historical"),false,true);
        }
        private static void TestQueuedBatch(string root,bool activeQueued,bool historical)
        {
            string logs=Path.Combine(root,"sessions");Directory.CreateDirectory(logs);DateTime instant=DateTime.UtcNow;
            var queue=typeof(CodexActivity).GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var monitor=new CodexActivity(root,false))
            {
                // Queue fixtures explicitly, without OS notification timing changing which 256
                // files form the first batch. No permissions or real user logs are modified.
                if(!historical)
                {
                    monitor.Scan(instant);var watcher=(FileSystemWatcher)typeof(CodexActivity).GetField("watcher",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor);watcher.EnableRaisingEvents=false;
                }
                for(int i=0;i<300;i++)
                {
                    string path=Path.Combine(logs,"rollout-queue-"+i+".jsonl");
                    File.WriteAllText(path,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"queue-"+i+"\"}}\n",new UTF8Encoding(false));queue.Invoke(monitor,new object[]{path});
                }
                var pending=(System.Collections.Concurrent.ConcurrentDictionary<string,byte>)typeof(CodexActivity).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor);
                string[] ordered=pending.Keys.ToArray();
                Func<string,int,string,string> line=(kind,second,turn)=>"{\"type\":\"event_msg\",\"timestamp\":\""+instant.AddSeconds(second).ToString("o")+"\",\"payload\":{\"type\":\""+kind+"\",\"turn_id\":\""+turn+"\"}}\n";
                // Independent tasks have different turn IDs; copied logs deliberately share an
                // ID in production and must continue to be deduplicated across session files.
                File.AppendAllText(ordered[0],line("task_started",historical?-2:1,"completed-turn")+line("task_complete",historical?-1:2,"completed-turn"));
                if(activeQueued)File.AppendAllText(ordered[299],line("task_started",2,"running-turn"));
                var report=monitor.Scan(instant.AddSeconds(historical?0:2));
                Check(report.MonitoringUnavailable&&report.ActiveTasks==0&&report.UncertainTasks==0&&report.CompletedTasks==0,"an unread task beyond the 256-file budget prevents idle and premature completion without fabricated task counts");
                Check(QuotaStatus.ActivityLabel(report,false,LocalCodexUsage.Unix(instant.AddSeconds(historical?0:2)))=="任务状态未知","a bounded scan backlog is shown as unknown until caught up");
                report=monitor.Scan(instant.AddSeconds(historical?1:3));
                Check(!report.MonitoringUnavailable&&report.ActiveTasks==(activeQueued?1:0)&&report.CompletedTasks==(historical||activeQueued?0:1),historical?"bootstrap completions cannot flash after a historical backlog is consumed":activeQueued?"queued active work continues without announcing another task's completion":"the next bounded scan delivers the deferred genuine completion once all work is confirmed idle");
                report=monitor.Scan(instant.AddSeconds(historical?2:4));Check(report.CompletedTasks==0,"a consumed backlog never repeats its completion notification");
            }
        }
        private static void TestUncertainCompletion(string root)
        {
            DateTime instant=DateTime.UtcNow;string logs=Path.Combine(root,"sessions");Directory.CreateDirectory(logs);
            Func<string,string,int,string> line=(id,kind,second)=>"{\"type\":\"event_msg\",\"timestamp\":\""+instant.AddSeconds(second).ToString("o")+"\",\"payload\":{\"type\":\""+kind+"\",\"turn_id\":\""+id+"\"}}\n";
            string first=Path.Combine(logs,"rollout-silent.jsonl"),secondFile=Path.Combine(logs,"rollout-completed.jsonl");
            File.WriteAllText(first,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"silent-session\"}}\n"+line("silent-turn","task_started",0));
            File.WriteAllText(secondFile,"{\"type\":\"session_meta\",\"timestamp\":\""+instant.ToString("o")+"\",\"payload\":{\"id\":\"completed-session\"}}\n"+line("completed-turn","task_started",0));
            var queue=typeof(CodexActivity).GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var monitor=new CodexActivity(root,false))
            {
                var report=monitor.Scan(instant);Check(report.ActiveTasks==2,"two independent tasks are initially confirmed running");
                ((FileSystemWatcher)typeof(CodexActivity).GetField("watcher",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor)).EnableRaisingEvents=false;
                File.AppendAllText(secondFile,line("completed-turn","task_complete",901));queue.Invoke(monitor,new object[]{secondFile});report=monitor.Scan(instant.AddSeconds(901));
                Check(!report.MonitoringUnavailable&&report.UncertainTasks==1&&report.ActiveTasks==0&&report.CompletedTasks==0,"an uncertain concurrent task postpones completion without marking it delivered");
                File.AppendAllText(first,line("silent-turn","turn_aborted",902));queue.Invoke(monitor,new object[]{first});report=monitor.Scan(instant.AddSeconds(902));
                Check(!report.MonitoringUnavailable&&report.UncertainTasks==0&&report.ActiveTasks==0&&report.CompletedTasks==1,"resolving uncertain work delivers the previously suppressed genuine completion");
                report=monitor.Scan(instant.AddSeconds(903));Check(report.CompletedTasks==0,"resolved uncertainty does not repeatedly announce completion");
            }
        }
        private static void Pump(int milliseconds)
        {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};
            timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        private static void TestCompletionMotion()
        {
            // Only the fixture's motion preference changes; OS accessibility settings and real
            // windows remain untouched. Restore the prior shared preference after the check.
            var mode=typeof(Theme).GetField("motionMode",BindingFlags.Static|BindingFlags.NonPublic);string previous=(string)mode.GetValue(null);
            var skin=new Border{Background=Brushes.Transparent};var outer=new Border{Child=skin};
            var window=new Window{Width=80,Height=60,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Opacity=0,Content=outer};
            Func<UIElement,bool> animated=element=>DependencyPropertyHelper.GetValueSource(element,UIElement.OpacityProperty).IsAnimated;
            try
            {
                mode.SetValue(null,"smooth");window.Show();CompletionFeedback.Set(window,true);bool allowed=Theme.MotionAllowed;Pump(30);
                Console.WriteLine("COMPLETION FIXTURE: policy="+allowed+", animated="+animated(skin)+", visible="+window.IsVisible);
                Check(animated(skin)==allowed,"completion feedback obeys the actual WPF motion policy");
                if(allowed)
                {
                    Pump(350);double opacity=skin.Opacity;CompletionFeedback.Set(window,true);Pump(40);
                    Check(opacity<.98&&skin.Opacity<=opacity+.03,"repeating a pending status preserves the existing completion clock phase");
                }
                outer.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(1,.8,TimeSpan.FromSeconds(3)));
                mode.SetValue(null,"off");CompletionFeedback.Set(window,true);Pump(20);
                Check(!animated(skin)&&animated(outer),"switching to off with unchanged pending clears only the completion clock");
                mode.SetValue(null,"smooth");CompletionFeedback.Set(window,true);Pump(20);Check(animated(skin)==Theme.MotionAllowed,"restoring full motion resumes the still-pending completion");
                window.Hide();Check(!animated(skin),"hiding the fixture clears completion animation clocks");
                window.Show();Pump(20);Check(animated(skin)==Theme.MotionAllowed,"showing the fixture resumes its pending completion according to policy");
                window.Close();Check(!animated(skin),"closing the fixture clears completion animation clocks");
            }
            finally{window.Close();mode.SetValue(null,previous);}
        }
    }
}
