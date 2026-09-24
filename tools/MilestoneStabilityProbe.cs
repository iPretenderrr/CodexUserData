using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class MilestoneStabilityProbe
    {
        private const long M=100000000L;
        private static void Check(bool value,string message){StabilityProbe.Check(value,"Milestone: "+message);}
        private static MilestoneEvent E(long at,long tokens,int precision=0,long until=0)
        {return new MilestoneEvent{From=at,To=until==0?at:until,Tokens=tokens,Precision=precision};}
        private static MilestoneInput Input(params MilestoneEvent[] events){var value=new MilestoneInput();value.Events.AddRange(events);return value;}
        internal static void Run(string root)
        {
            string folder=Path.Combine(root,"milestone-core");var engine=new MilestoneEngine(folder);long now=1000000;
            var empty=engine.Get(Input(),"empty","fixture",now,CancellationToken.None);
            Check(empty.TotalTokens==0&&empty.Completed(M)==0&&empty.Stage(M,1,now).Start==null,"empty history has no invented starting date");
            var input=Input(E(100,40*M/100),E(3700,90*M/100),E(90000,190*M/100));
            var value=engine.Get(input,"source-a","fixture",now,CancellationToken.None);
            Check(value.TotalTokens==320*M/100&&value.Completed(M)==3&&value.Stage(M,4,now).Tokens==20*M/100,"overshoot carries into the next stage");
            var first=value.Stage(M,1,now);var second=value.Stage(M,2,now);var third=value.Stage(M,3,now);
            Check(value.Curve.Times.SequenceEqual(new long[]{100,3700,90000})&&value.Curve.Totals.SequenceEqual(new long[]{40*M/100,130*M/100,320*M/100}),"cumulative index uses the same all-history records as milestone boundaries");
            Check(first.DurationKnown&&first.DurationMin==3600&&first.DurationMax==3600,"first stage starts at the first positive report");
            Check(second.Start.From==3700&&second.DurationMin==86300,"later stages start at the previous threshold and include idle time");
            Check(third.SameBatch&&!third.DurationKnown,"multiple thresholds in one report do not claim zero elapsed time");
            Check(value.Stage(M,4,now).DurationMin==now-90000,"unfinished stage runs from the last crossing");
            int builds=engine.Builds;var warm=engine.Get(input,"source-a","fixture",now+60,CancellationToken.None);
            Check(engine.Builds==builds&&Object.ReferenceEquals(value.Marks,warm.Marks)&&warm.ObservedAt==now+60,"minute refresh reuses crossings and advances only observation time");
            Check(Object.ReferenceEquals(value.Curve,warm.Curve),"unchanged refresh shares the immutable cumulative index");
            var append=Input(input.Events.Concat(new[]{E(100001,80*M/100)}).ToArray());value=engine.Get(append,"source-a","fixture",now,CancellationToken.None);
            Check(engine.UsedIncremental&&value.TotalTokens==4*M&&value.Stage(M,5,now).Tokens==0,"verified sequence append extends the cached result");
            var replacement=Input(E(100,20*M/100),E(3700,90*M/100),E(90000,190*M/100),E(100001,80*M/100));value=engine.Get(replacement,"source-a","fixture",now,CancellationToken.None);
            Check(!engine.UsedIncremental&&value.TotalTokens==380*M/100,"same-length historical edits rebuild rather than append");
            value=engine.Get(Input(E(100,20*M/100),E(90000,190*M/100)),"source-a","fixture",now,CancellationToken.None);
            Check(!engine.UsedIncremental&&value.TotalTokens==210*M/100,"deletion removes old contributions");
            value=engine.Get(Input(E(50,2*M),E(100,20*M/100),E(90000,190*M/100)),"source-a","fixture",now,CancellationToken.None);
            Check(!engine.UsedIncremental&&value.Origin.From==50,"older backfill rebuilds the origin and thresholds");
            value=engine.Get(Input(E(100,100*M+17)),"huge","fixture",now,CancellationToken.None);
            Check(value.Marks.Count==1&&MilestoneEngine.Steps.All(step=>value.Completed(step)==100*M/step&&value.Stage(step,1,now).SameBatch),"five steps share compressed crossings even for a huge first report");
            value=engine.Get(Input(E(100,40*M/100,1),E(3700,70*M/100),E(5000,M)),"inferred","fixture",now,CancellationToken.None);
            Check(!value.Stage(M,1,now).DurationKnown&&value.Stage(M,2,now).DurationKnown,"an inferred endpoint is unknown until both later endpoints are reported");
            value=engine.Get(Input(E(100,40*M/100,2,86499),E(86500,70*M/100,2,172899)),"daily","fixture",now,CancellationToken.None);
            var daily=value.Stage(M,1,now);
            Check(daily.DurationKnown&&daily.DurationMin==1&&daily.DurationMax==172799&&daily.Start.Precision==2,"day-only reports retain endpoint intervals and duration bounds");
            var cacheInput=Input(E(100,M/2),E(200,M));var cached=engine.Get(cacheInput,"persisted","fixture",now,CancellationToken.None);
            var restarted=new MilestoneEngine(folder);var loaded=restarted.Get(cacheInput,"persisted","fixture",now,CancellationToken.None);
            Check(restarted.Builds==0&&loaded.TotalTokens==cached.TotalTokens&&loaded.Stage(M,1,now).End.From==200,"validated numeric cache survives restart");
            Check(loaded.Curve.Totals.Last()==loaded.TotalTokens&&!new JavaScriptSerializer().Serialize(loaded).Contains("Curve"),"disk cache restores the numeric curve in memory without persisting another event ledger");
            string file=Path.Combine(folder,RemoteOptions.Hash("persisted")+".json");var json=new JavaScriptSerializer();var damaged=json.Deserialize<MilestoneSnapshot>(File.ReadAllText(file));damaged.Marks[0].From=damaged.Marks[0].To=999;File.WriteAllText(file,json.Serialize(damaged));
            restarted=new MilestoneEngine(folder);loaded=restarted.Get(cacheInput,"persisted","fixture",now,CancellationToken.None);
            Check(restarted.Builds==1&&loaded.Marks[0].From==200,"a corrupted derived timestamp is rebuilt from the verified sequence");
            loaded=restarted.Get(Input(E(100,M/2),E(200,2*M)),"persisted","fixture",now,CancellationToken.None);
            Check(loaded.TotalTokens==250*M/100&&!restarted.UsedIncremental,"disk cache never overrides changed effective history");
            loaded=restarted.Get(cacheInput,"another-source","fixture",now,CancellationToken.None);
            Check(!restarted.UsedIncremental&&loaded.Scope=="another-source","different source identities cannot share incremental state");
            string blocked=Path.Combine(root,"milestone-cache-file");File.WriteAllText(blocked,"fixture");loaded=new MilestoneEngine(blocked).Get(cacheInput,"a","fixture",now,CancellationToken.None);
            Check(loaded.TotalTokens==150*M/100&&!String.IsNullOrEmpty(loaded.Warning),"cache write failure keeps usable statistics with a notice");
            using(var cts=new CancellationTokenSource())
            {cts.Cancel();bool canceled=false;try{engine.Get(input,"cancel","fixture",now,cts.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled,"canceled requests cannot publish a new result");}
            value=engine.Get(Input(E(100,Int64.MaxValue)),"large-total","fixture",now,CancellationToken.None);
            Check(value.Marks.Count==1&&value.Stage(M,value.Completed(M)+1,now).ToTokens==Int64.MaxValue,"extreme valid totals have bounded memory and no target overflow");
            var prefs=new Preferences{MilestoneStep=123};prefs.Validate();Check(prefs.MilestoneStep==M,"invalid saved step recovers to one hundred million");
            var multiple=Input(E(100,M/2),E(300,M),E(200,M/2),E(400,M));
            var sortedEngine=new MilestoneEngine(Path.Combine(root,"milestone-order"));sortedEngine.Get(multiple,"mixed","fixture",now,CancellationToken.None);int sorts=sortedEngine.Sorts;
            var additions=Input(E(100,M/2),E(300,M),E(500,M),E(200,M/2),E(400,M),E(600,M));
            value=sortedEngine.Get(additions,"mixed","fixture",now,CancellationToken.None);
            Check(sortedEngine.Sorts==sorts&&sortedEngine.UsedIncremental&&value.TotalTokens==5*M,"multiple session appends merge linearly without sorting old history again");
            var backfill=Input(E(100,M/2),E(300,M),E(500,M),E(200,M/2),E(400,M),E(600,M),E(50,M));value=sortedEngine.Get(backfill,"mixed","fixture",now,CancellationToken.None);
            Check(value.Origin.From==50&&!sortedEngine.UsedIncremental&&value.TotalTokens==6*M,"merge optimization still rebuilds thresholds after old backfill");
            value=engine.Get(Input(E(100,0),E(200,M/2),E(200,M),E(300,0),E(400,M/2)),"same-second","fixture",now,CancellationToken.None);
            Check(value.Curve.Times.SequenceEqual(new long[]{200,400})&&value.Curve.Totals.SequenceEqual(new long[]{150*M/100,2*M}),"same-second reports share one cumulative point and zero reports do not invent a start");
        }
        private static Button Tab(DependencyObject parent,string name)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {var child=VisualTreeHelper.GetChild(parent,i);var button=child as Button;if(button!=null&&Object.Equals(button.Content,name))return button;var nested=Tab(child,name);if(nested!=null)return nested;}
            return null;
        }
        internal static async Task Integration(string root)
        {
            string home=Path.Combine(root,"milestone-widget"),sessions=Path.Combine(home,"sessions"),id=Guid.NewGuid().ToString();Directory.CreateDirectory(sessions);DateTime now=DateTime.UtcNow.AddMinutes(-10);var json=new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(sessions,"rollout-"+id+".jsonl"),json.Serialize(new{type="session_meta",timestamp=now.ToString("o"),payload=new{id=id,timestamp=now.ToString("o")}})+"\n"+
                json.Serialize(new{type="event_msg",timestamp=now.AddMinutes(1).ToString("o"),payload=new{type="token_count",info=new{last_token_usage=new{input_tokens=150000000,output_tokens=0}}}})+"\n");
            var prefs=new Preferences{CodexHome=home,Source="local",Database=Path.Combine(root,"unused.db"),QuotaCli="",LiveQuota=false,OrbAnimation="off",MilestoneStep=5*M};
            var widget=new WidgetWindow(prefs,true);
            try
            {
                widget.Show();StabilityProbe.Call(widget,"OpenHistory");var window=StabilityProbe.Field<Window>(widget,"historyWindow");window.UpdateLayout();
                var panel=StabilityProbe.Field<MilestonePanel>(widget,"milestonePanel");
                var curve=StabilityProbe.Field<MilestoneCurvePanel>(widget,"milestoneCurvePanel");
                Check(Tab(window,"用量")!=null&&Tab(window,"额度")!=null&&Tab(window,"里程碑")!=null&&Tab(window,"累计里程碑")!=null&&!panel.IsVisible&&!curve.IsVisible,"history exposes a separate cumulative milestone tab and leaves its work hidden initially");
                Check(typeof(HistoryPanel).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).All(f=>f.FieldType!=typeof(MilestoneCurvePanel)),"home and usage charts contain no embedded milestone curve");
                Tab(window,"里程碑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StabilityProbe.Until(()=>StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")!=null,"widget milestone load failed",5000);
                var snapshot=StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot");
                Check(snapshot.TotalTokens==150000000&&StabilityProbe.Field<long>(panel,"step")==5*M,"integrated page reads all-history local usage and remembers its saved step");
                Tab(window,"累计里程碑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var usage=StabilityProbe.Field<HistoryPanel>(widget,"largeHistory");
                await StabilityProbe.Until(()=>StabilityProbe.Field<MilestoneSnapshot>(curve,"snapshot")!=null,"integrated cumulative curve did not load",5000);
                var curveSnapshot=StabilityProbe.Field<MilestoneSnapshot>(curve,"snapshot");
                Check(curve.IsVisible&&!usage.IsVisible&&!panel.IsVisible&&curveSnapshot.TotalTokens==snapshot.TotalTokens&&curveSnapshot.Curve.Totals.Last()==snapshot.TotalTokens,"independent cumulative page shares the historical total without showing other pages");
                StabilityProbe.Call(widget,"SetMilestoneStep",M);
                Check(prefs.MilestoneStep==M&&StabilityProbe.Field<long>(curve,"step")==M&&StabilityProbe.Field<long>(panel,"step")==M,"milestone interval stays synchronized between the two dedicated pages");
                int oldDays=StabilityProbe.Field<int>(usage,"days");StabilityProbe.Call(curve,"SelectRange",7);
                Check(StabilityProbe.Field<int>(curve,"rangeDays")==7&&StabilityProbe.Field<int>(usage,"days")==oldDays&&StabilityProbe.Field<MilestoneSnapshot>(curve,"snapshot").TotalTokens==snapshot.TotalTokens,"cumulative date controls do not change usage range or cumulative total");
                StabilityProbe.Call(curve,"SelectRange",0);Check(StabilityProbe.Field<long>(curve,"from")==curveSnapshot.Origin.From,"all-history curve starts at the first effective record instead of the Unix epoch");
                foreach(int width in new[]{880,380})
                {
                    window.Width=width;window.UpdateLayout();await Task.Delay(30);var content=(FrameworkElement)window.Content;
                    var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"cumulative-tab-"+width+".png")))png.Save(file);
                    Check(curve.ActualWidth<=window.ActualWidth&&Tab(window,"累计里程碑").IsVisible,"dedicated cumulative tab and its range controls fit the history window at "+width+"px");
                }
                window.Width=880;window.UpdateLayout();
                Tab(window,"用量").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));usage.SetAggregation("cumulative");
                Check(!curve.IsVisible&&!StabilityProbe.Field<System.Windows.Threading.DispatcherTimer>(curve,"timer").IsEnabled&&StabilityProbe.Field<UsageChart>(usage,"trend").IsVisible,"ordinary cumulative usage remains independent and hides the milestone timer");
                Tab(window,"里程碑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                string identity=(string)StabilityProbe.Call(widget,"MilestoneScope");prefs.Range="week";
                Check(identity==(string)StabilityProbe.Call(widget,"MilestoneScope"),"home period selection does not change milestone history identity");
                prefs.CodexHome=Path.Combine(root,"milestone-other-home");Directory.CreateDirectory(Path.Combine(prefs.CodexHome,"sessions"));StabilityProbe.Call(widget,"SelectionChanged");
                await StabilityProbe.Until(()=>StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")!=null&&StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").Scope!=identity,"new home did not load",5000);
                Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").TotalTokens==0,"same source label with a different log directory cannot reuse old milestones");
                Check(StabilityProbe.Field<MilestoneSnapshot>(curve,"snapshot")==null,"source change clears the hidden independent cumulative page");
                prefs.CodexHome=Path.Combine(root,"milestone-missing-home");int failures=0;
                for(int i=0;i<2;i++)try{await (Task<MilestoneSnapshot>)StabilityProbe.Call(widget,"LoadMilestones",CancellationToken.None);}catch(IOException){failures++;}
                Check(failures==2,"repeated failed initial reads cannot become a fabricated zero-usage result");
                window.Close();StabilityProbe.Call(widget,"OpenHistory");var reopened=StabilityProbe.Field<MilestonePanel>(widget,"milestonePanel");
                Check(!Object.ReferenceEquals(panel,reopened)&&StabilityProbe.Field<bool>(panel,"disposed"),"closing and reopening charts disposes the old milestone lifecycle");
                Check(StabilityProbe.Field<bool>(curve,"disposed"),"closing charts disposes their cumulative milestone loader");
            }
            finally{widget.Close();}
        }
    }
}
