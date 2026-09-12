using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class QuotaHistoryStabilityProbe
    {
        private static QuotaBucket Bucket(long time,double used,string origin="在线查询")
        {return new QuotaBucket{Id="codex",Origin=origin,ObservedAt=time,Primary=new QuotaWindow{Minutes=300,UsedPercent=used,ResetsAt=time+3600},Secondary=new QuotaWindow{Minutes=10080,UsedPercent=35,ResetsAt=time+86400}};}
        internal static async Task Run(string root)
        {
            var curve=QuotaHistoryChart.SmoothCurve(new[]{new Point(0,0),new Point(20,30),new Point(100,35),new Point(110,100)},new bool[4],new bool[4]);
            var path=PathGeometry.CreateFromGeometry(curve);
            StabilityProbe.Check(path.Figures[0].Segments.Any(s=>s is BezierSegment||s is PolyBezierSegment)&&curve.Bounds.Top>=0&&curve.Bounds.Bottom<=100,"quota observations use smooth cubics without overshooting the observed percentage range");
            var broken=QuotaHistoryChart.SmoothCurve(new[]{new Point(0,50),new Point(10,70),new Point(20,10),new Point(30,20)},new[]{false,false,true,false},new bool[4]);
            StabilityProbe.Check(PathGeometry.CreateFromGeometry(broken).Figures.Count==2,"smooth curves do not bridge missing observations");
            var reset=QuotaHistoryChart.SmoothCurve(new[]{new Point(0,90),new Point(20,0)},new bool[2],new[]{false,true});
            StabilityProbe.Check(PathGeometry.CreateFromGeometry(reset).Figures[0].Segments.All(s=>!(s is BezierSegment)&&!(s is PolyBezierSegment)),"quota resets remain explicit jumps instead of smoothed fabricated recovery");
            StabilityProbe.Check(((SolidColorBrush)QuotaHistoryChart.Color(300)).Color!=((SolidColorBrush)QuotaHistoryChart.Color(10080)).Color,"5-hour and 7-day curves have distinct stable colors");
            var mixed=new[]{new QuotaSample{Bucket="codex",Minutes=10080},new QuotaSample{Bucket="codex_bengalfox",Minutes=300},new QuotaSample{Bucket="codex_bengalfox",Minutes=10080},new QuotaSample{Bucket="codex",Minutes=300}};
            StabilityProbe.Check(QuotaHistorySeries.Prepare(mixed).Length==2&&QuotaHistorySeries.Prepare(mixed.Take(3).ToArray()).Single().Points[0].Label=="7天","only general 5-hour and 7-day windows appear; other buckets cannot substitute a missing 5-hour curve");
            StabilityProbe.Check(QuotaHistoryPanel.PeriodHours.SequenceEqual(new[]{1,3,6,12,24,168,336,720,1440,2160,4320})&&QuotaHistoryPanel.PeriodHours.All(h=>123456789-QuotaHistoryPanel.RangeStart(123456789,h)==h*3600L),"all eleven ranges use exact rolling hours through 180 days");
            string directory=Path.Combine(root,"quota-history"),scope=QuotaHistoryStore.Scope(Path.Combine(root,"codex"));long now=LocalCodexUsage.Unix(DateTime.UtcNow);
            var store=new QuotaHistoryStore(directory);
            await store.RecordAsync(scope,new[]{Bucket(now-240,10),Bucket(now-180,12),Bucket(now-120,0)});
            var extra=Bucket(now-60,15);extra.Id="codex_bengalfox";
            await store.RecordAsync(scope,new[]{Bucket(now-120,0),Bucket(now-60,15,"日志快照"),Bucket(now-1000,99),extra});
            var loaded=await new QuotaHistoryStore(directory).ReadAsync(scope,now-600,now);
            StabilityProbe.Check(loaded.Length==6&&loaded.Any(s=>s.Remaining==100),"history persists both windows and quota resets across restart, ignoring stale/log samples and duplicate minute refreshes");
            string file=Directory.GetFiles(Path.Combine(directory,scope),"*.jsonl").Last();File.AppendAllText(file,"{broken tail");
            await store.RecordAsync(scope,new[]{Bucket(now,20)});
            loaded=await new QuotaHistoryStore(directory).ReadAsync(scope,now-600,now);
            StabilityProbe.Check(loaded.Length==8&&loaded.Last().Time==now,"a truncated history tail does not discard earlier or subsequently appended observations");
            string other=QuotaHistoryStore.Scope(Path.Combine(root,"other"));
            StabilityProbe.Check((await store.ReadAsync(other,now-600,now)).Length==0,"separate Codex data directories never merge quota histories");
            StabilityProbe.Check(!Directory.GetFiles(Path.Combine(directory,scope)).Any(p=>File.ReadAllText(p).Contains(root)),"history persists no local paths or raw account responses");
            string expired=Path.Combine(directory,scope,DateTime.UtcNow.AddDays(-181).ToString("yyyy-MM-dd")+".jsonl");File.WriteAllText(expired,"fixture");
            string keep=Path.Combine(directory,scope,"keep.txt");File.WriteAllText(keep,"fixture");
            await new QuotaHistoryStore(directory).RecordAsync(scope,new[]{Bucket(now,20)});
            StabilityProbe.Check(!File.Exists(expired)&&File.Exists(keep),"retention removes only expired daily history files and preserves unrelated files");

            var samples=new List<QuotaSample>();long start=now-180*86400L;
            for(int i=0;i<180*1440;i++)
            {
                if(i%1440>=600&&i%1440<610)continue;
                samples.Add(new QuotaSample{Bucket="codex",Minutes=300,Time=start+i*60,Remaining=100-(i%300)/3.0,Reset=start+(i/300+1)*18000});
                samples.Add(new QuotaSample{Bucket="codex",Minutes=10080,Time=start+i*60,Remaining=100-(i%10080)/100.8,Reset=start+(i/10080+1)*604800});
            }
            var all=samples.OrderBy(s=>s.Time).ToArray();var points=all.Where(s=>s.Minutes==300).ToArray();var watch=Stopwatch.StartNew();
            var reduced=QuotaHistoryChart.Reduce(points,start,now,800);watch.Stop();
            StabilityProbe.Check(reduced.Length<8000&&reduced.Min(s=>s.Remaining)==points.Min(s=>s.Remaining)&&reduced.Max(s=>s.Remaining)==100,"180-day minute data is reduced to pixel extrema while preserving reset boundaries");
            bool boundaries=true;var kept=new HashSet<long>(reduced.Select(s=>s.Time));for(int i=1;i<points.Length;i++)if(points[i].Time-points[i-1].Time>90)boundaries&=kept.Contains(points[i-1].Time)&&kept.Contains(points[i].Time);
            StabilityProbe.Check(boundaries,"decimation retains both sides of every missing-query interval");
            Console.WriteLine("QUOTA REDUCE: "+points.Length+" -> "+reduced.Length+" points, "+watch.ElapsedMilliseconds+" ms");
            var chart=new QuotaHistoryChart();var panel=new StackPanel{Margin=new Thickness(20)};panel.Children.Add(Theme.Text("额度历史 · 模拟数据",16,Theme.Ink));panel.Children.Add(chart);
            var window=new Window{Width=880,Height=470,Content=panel,Background=Theme.Background,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                var prepared=await Task.Run(()=>QuotaHistorySeries.Prepare(all));
                watch.Restart();chart.SetSeries(prepared,start,now);window.Show();await Task.Delay(100);window.UpdateLayout();watch.Stop();
                StabilityProbe.Check(chart.GeometryBuilds>0,"large history renders on one cached drawing surface");Console.WriteLine("QUOTA FIRST RENDER (180 days): "+watch.ElapsedMilliseconds+" ms");
                int builds=chart.GeometryBuilds;for(int i=0;i<500;i++){StabilityProbe.Call(chart,"At",now-i*60L);chart.InvalidateVisual();}await Task.Delay(40);
                StabilityProbe.Check(chart.GeometryBuilds==builds,"500 pointer lookups and overlay invalidations do not rebuild quota geometry");
                StabilityProbe.Check(StabilityProbe.Field<long>(chart,"selected")==-1,"quota chart initially has no selected time");
                QuotaSample[] picked=null;chart.Pick+=p=>picked=p;StabilityProbe.Call(chart,"Select",points.Last().Time);
                StabilityProbe.Check(picked!=null&&picked.Length==2,"click selection supplies both observed quota windows");chart.Clear();
                StabilityProbe.Check(picked.Length==0,"clearing selection removes detail without reserving empty height");
                chart.Toggle("codex:300");StabilityProbe.Check(chart.IsHidden("codex:300"),"legend toggles the selected quota curve independently");chart.Toggle("codex:300");
                long gap=points.First().Time+605*60;StabilityProbe.Check(((QuotaSample[])StabilityProbe.Call(chart,"At",gap)).Length==0,"missing observations are not represented as zero or an interpolated quota");
                window.Width=340;await Task.Delay(50);window.UpdateLayout();StabilityProbe.Check(chart.ActualWidth>100&&chart.ActualWidth<340,"quota axes render inside a narrow window");window.Width=880;
                foreach(string mode in new[]{"dark","light"})
                {
                    Theme.Apply(new Preferences{ThemeMode=mode,LiveQuota=false,OrbAnimation="off"});chart.SetData(all.Where(s=>s.Time>=now-86400).ToArray(),now-86400,now);await Task.Delay(50);window.UpdateLayout();
                    panel.Background=Theme.Background;var visual=new DrawingVisual();using(var context=visual.RenderOpen())context.DrawRectangle(new VisualBrush(panel),null,new Rect(0,0,panel.ActualWidth,panel.ActualHeight));
                    var image=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
                    using(var output=File.Create(Path.Combine(Path.GetDirectoryName(root),"quota-history-"+mode+".png")))png.Save(output);
                }
            }
            finally{window.Close();}

            var history=new QuotaHistoryPanel(store,()=>scope);var host=new Window{Width=420,Height=500,Content=history,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                host.Show();await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(history,"loading"),"quota panel loading timed out",5000);
                StabilityProbe.Check(StabilityProbe.Field<TextBlock>(history,"detail").Visibility==Visibility.Collapsed,"history detail starts collapsed without blank space");
                history.Visibility=Visibility.Collapsed;history.Refresh();StabilityProbe.Check(!StabilityProbe.Field<bool>(history,"loading"),"hidden history does not read or rebuild data");
                history.Visibility=Visibility.Visible;history.Refresh();history.Visibility=Visibility.Collapsed;await Task.Delay(80);history.Visibility=Visibility.Visible;
                await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(history,"loading"),"reopened history did not settle",5000);
                StabilityProbe.Check(!StabilityProbe.Field<bool>(history,"pending"),"hide/reopen and coalesced refreshes settle without a stale request");
                var rangeButtons=StabilityProbe.Field<Dictionary<int,Button>>(history,"ranges");
                rangeButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));rangeButtons[4320].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));rangeButtons[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(history,"loading"),"range switches did not settle",5000);
                var rangeChart=StabilityProbe.Field<QuotaHistoryChart>(history,"chart");
                StabilityProbe.Check(StabilityProbe.Field<long>(rangeChart,"to")-StabilityProbe.Field<long>(rangeChart,"from")==10800,"rapid range changes show only the last requested 3-hour interval");
                host.Width=340;await Task.Delay(50);host.UpdateLayout();
                var clear=(Button)GuideStabilityProbe.Find(history,"QuotaClearSelection");
                Point clearAt=clear.TranslatePoint(new Point(0,0),history);
                StabilityProbe.Check(Math.Abs(clearAt.X+clear.ActualWidth-history.ActualWidth)<2&&rangeButtons.Values.All(b=>b.TranslatePoint(new Point(0,0),history).X+b.ActualWidth<=clearAt.X),"clear selection stays at the right edge while eleven range buttons wrap inside a narrow window");

            }
            finally{host.Close();}
            string home=Path.Combine(root,"quota-integration");Directory.CreateDirectory(home);File.WriteAllText(Path.Combine(home,"fake-quota.fixture"),"");File.WriteAllText(Path.Combine(home,"fake-quota.reply"),"");
            var prefs=new Preferences{CodexHome=home,Database=Path.Combine(home,"missing.db"),Source="local",QuotaCli=Path.Combine(Path.GetDirectoryName(root),"FakeQuotaHelper.exe"),LiveQuota=true,BallMode=false,OrbAnimation="off"};
            var widget=new WidgetWindow(prefs,false){ShowActivated=false};
            try
            {
                widget.Show();var quota=StabilityProbe.Field<QuotaStatus>(widget,"quota");bool recorded=false;quota.HistoryChanged+=delegate{recorded=true;};
                await StabilityProbe.Until(()=>recorded,"successful quota response was not recorded",5000);
                var rows=await quota.HistoryStore.ReadAsync(QuotaHistoryStore.Scope(home),now-600,LocalCodexUsage.Unix(DateTime.Now));
                StabilityProbe.Check(rows.Length==1&&rows[0].Remaining==76,"the actual online refresh path writes the observed quota without a second query");
                StabilityProbe.Call(widget,"OpenHistory");await Task.Delay(60);var dialog=StabilityProbe.Field<Window>(widget,"historyWindow");
                var tab=FindButton(dialog,"额度");StabilityProbe.Check(tab!=null,"the shared chart window exposes the quota tab");tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var historyPanel=StabilityProbe.Field<QuotaHistoryPanel>(widget,"quotaHistoryPanel");await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(historyPanel,"loading"),"quota tab not loaded",5000);
                StabilityProbe.Check(historyPanel.IsVisible&&!StabilityProbe.Field<HistoryPanel>(widget,"largeHistory").IsVisible,"quota and usage tabs display exactly one page");
                await Task.Delay(50);dialog.UpdateLayout();var liveChart=StabilityProbe.Field<QuotaHistoryChart>(historyPanel,"chart");
                StabilityProbe.Check(liveChart.ActualHeight>=380&&liveChart.ActualHeight<dialog.ActualHeight,"the quota curve fills the expanded chart viewport without an oversized detail placeholder");
                var content=(FrameworkElement)dialog.Content;var visual=new DrawingVisual();using(var c=visual.RenderOpen())c.DrawRectangle(new VisualBrush(content),null,new Rect(0,0,content.ActualWidth,content.ActualHeight));
                var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(Path.GetDirectoryName(root),"quota-history-window.png")))encoder.Save(output);
                FindButton(dialog,"用量").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));dialog.Close();
                StabilityProbe.Call(widget,"OpenHistory");await Task.Delay(50);StabilityProbe.Check(StabilityProbe.Field<Window>(widget,"historyWindow").IsVisible,"closing and reopening the shared charts creates a working window");
            }
            finally{widget.Close();}
        }
        private static Button FindButton(DependencyObject root,string label)
        {
            var b=root as Button;if(b!=null&&Convert.ToString(b.Content)==label)return b;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=FindButton(VisualTreeHelper.GetChild(root,i),label);if(found!=null)return found;}return null;
        }
    }
}
