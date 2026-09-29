using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class ModelShareStabilityProbe
    {
        private static void Check(bool value,string message){StabilityProbe.Check(value,"Model share: "+message);}
        private static string LegendText(Button button){var text=(TextBlock)button.Content;return new System.Windows.Documents.TextRange(text.ContentStart,text.ContentEnd).Text;}
        private static DailyUsage Point(string date,long first,long second,long unknown=0)
        {
            var day=new DailyUsage{Date=date,Tokens=first+second+unknown};
            day.Models.Add(new ModelUsage{Model="gpt-5",Effort="high",Tokens=first/3});day.Models.Add(new ModelUsage{Model="gpt-5",Effort="low",Tokens=first-first/3});day.Models.Add(new ModelUsage{Model="gpt-4.1",Effort="unknown",Tokens=second});return day;
        }
        private static UsageSnapshot Fixture()
        {
            var days=DailyUsage.Empty(DateTime.Today,180);for(int i=0;i<days.Length;i++){long first=200+(long)(150*Math.Sin(i*.33));days[i]=Point(days[i].Date,first,400-first,i%8==0?40:0);}
            var hours=DailyUsage.Hours(DateTime.Today);hours[0]=Point(hours[0].Date,20,80);hours[1]=Point(hours[1].Date,70,30);
            return new UsageSnapshot{Daily=days,Hourly=hours,HourlyThrough=2,HourlyUnallocatedTokens=40,Timeline=new DailyUsage[0],Warning="演示数据 · 小时覆盖不完整",CoverageWarnings=1};
        }
        private static async Task Settled(ModelSharePanel panel)
        {await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(panel,"loading")&&!StabilityProbe.Field<bool>(panel,"pumpQueued"),"model share panel did not settle",5000);}
        private static async Task Layout(Window window){window.UpdateLayout();await Task.Delay(35);}
        internal static void Capture(Window window,string path)
        {
            var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);
        }
        internal static async Task Run(string root)
        {
            string scope="share-demo";int calls=0;UsageSnapshot value=Fixture();
            var panel=new ModelSharePanel((from,to,cancel)=>{calls++;return Task.FromResult(value);},()=>scope);panel.Apply(value,scope,"演示来源");
            var window=new Window{Width=900,Height=770,Content=new Border{Padding=new Thickness(16),Background=Theme.Background,Child=panel},Background=Theme.Background,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                Check(StabilityProbe.Field<UsageChart>(panel,"chart").Days.Length==0&&calls==0,"unloaded page stores data without building or reading");
                window.Show();await Layout(window);var chart=StabilityProbe.Field<UsageChart>(panel,"chart");var detail=StabilityProbe.Field<Border>(panel,"detail");
                Check(chart.IsShare&&chart.WindowDays==30&&!chart.IsHourly&&chart.GeometryBuilds>0&&detail.Visibility==Visibility.Collapsed,"default is independent 30-day percentage chart without automatic detail");
                panel.SelectRange(7);await Layout(window);Check(chart.WindowDays==7&&calls==0,"preset range reuses the normal snapshot without a read");
                panel.SelectRange(1);await Layout(window);Check(chart.IsHourly&&chart.Days.Length==24&&StabilityProbe.Field<TextBlock>(panel,"warning").Text.Contains("未分摊"),"today uses hourly data and discloses unallocated daily totals");
                chart.Choose(0,false);Check(detail.Visibility==Visibility.Collapsed&&chart.Selected<0,"hover leaves detail collapsed");chart.Choose(1,true);Check(detail.Visibility==Visibility.Visible&&chart.Selected==1,"click pins one timestamp detail");chart.Choose(0,false);Check(chart.Selected==1&&StabilityProbe.Field<string>(panel,"selectedDate")==chart.Days[1].Date,"hover does not replace a pinned timestamp");
                chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(chart),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});Check(detail.Visibility==Visibility.Collapsed&&chart.Selected<0,"Escape clears selection");

                var weighted=new UsageSnapshot{Daily=new[]{Point("2026-01-01",900,100),Point("2026-01-02",0,80,20)},Hourly=new DailyUsage[0],Timeline=new DailyUsage[0]};panel.SelectRange(7);panel.Apply(weighted,scope,"权重演示");await Layout(window);
                var legend=StabilityProbe.Field<WrapPanel>(panel,"legend");var entries=legend.Children.OfType<Button>().ToArray();Check(entries.Length==3&&entries.Any(b=>(string)b.Tag=="gpt-5"&&LegendText(b).Contains("81.8%"))&&entries.Any(b=>(string)b.Tag=="unknown"&&LegendText(b).Contains("1.8%")),"compact legend merges efforts, includes unknown tokens and uses 900/1100 weighted share");
                Check(entries.All(b=>!LegendText(b).Contains("区间占比"))&&StabilityProbe.Field<DockPanel>(panel,"state").Visibility==Visibility.Collapsed&&StabilityProbe.Field<TextBlock>(panel,"source").Visibility==Visibility.Collapsed,"healthy preset view hides repeated explanations and uses compact pill labels");
                int builds=chart.GeometryBuilds;entries[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Layout(window);Check(chart.HighlightedModel!=null&&chart.GeometryBuilds==builds,"legend highlight keeps denominator and cached geometry");
                Check(entries[0].BorderBrush==Theme.Accent,"selected model has a visible selected border");
                panel.SetAggregation("weekly");await Layout(window);var grouped=StabilityProbe.Field<Dictionary<string,double[]>>(chart,"series");Check(chart.Days.Length==1&&chart.Days[0].Tokens==1100&&Math.Abs(grouped["gpt-5"][0]-90000.0/1100)<.0001&&calls==0,"weekly share aggregates raw tokens rather than averaging daily percentages, without another read");
                panel.SetAggregation("cumulative");await Layout(window);grouped=StabilityProbe.Field<Dictionary<string,double[]>>(chart,"series");Check(chart.Days.Length==2&&chart.Days[0].Tokens==1000&&chart.Days[1].Tokens==1100&&Math.Abs(grouped["gpt-5"][1]-90000.0/1100)<.0001,"cumulative share uses running token counts including unclassified usage");
                Check(legend.Children.OfType<Button>().Any(b=>(string)b.Tag=="gpt-5"&&LegendText(b).Contains("81.8%")),"cumulative legend does not double-count running totals");chart.Choose(1,true);Check(StabilityProbe.Field<StackPanel>(panel,"detailRows").Children.OfType<TextBlock>().First().Text.Contains("累计至"),"cumulative details identify the selected cumulative endpoint");
                panel.SetAggregation("daily");await Layout(window);Check(chart.Selected<0&&chart.Days[1].Tokens==100,"switching aggregation clears incompatible selections and restores daily buckets");
                panel.FailRefresh("模拟读取失败",scope);Check(StabilityProbe.Field<TextBlock>(panel,"status").Text.Contains("上次结果")&&chart.Days[0].Tokens==1000,"preset refresh failure keeps the curve and visibly marks stale data");panel.Apply(weighted.Copy(),scope,"缓存重放");Check(StabilityProbe.Field<TextBlock>(panel,"status").Text.Contains("上次结果"),"cached snapshot replay cannot clear a source failure");panel.RefreshSucceeded();Check(StabilityProbe.Field<DockPanel>(panel,"state").Visibility==Visibility.Collapsed,"successful refresh clears stale-data notice");
                var casing=new UsageSnapshot{Daily=new[]{Point("2026-01-01",40,60),Point("2026-01-02",70,30)}};foreach(var model in casing.Daily[1].Models)model.Model=model.Model.ToUpperInvariant();panel.Apply(casing,scope,"大小写演示");await Layout(window);
                var caseSeries=StabilityProbe.Field<Dictionary<string,double[]>>(chart,"series");Check(caseSeries.Count==2&&caseSeries["gpt-5"][0]==40&&caseSeries["gpt-5"][1]==70&&legend.Children.OfType<Button>().Count()==2,"model identity merges casing across days without duplicate lines or legend entries");
                chart.Choose(1,true);var shifted=new UsageSnapshot{Daily=new[]{Point("2026-01-02",10,90),Point("2026-01-03",50,50)},Hourly=new DailyUsage[0],Timeline=new DailyUsage[0]};panel.Apply(shifted,scope,"演示");await Layout(window);Check(chart.Selected==0&&StabilityProbe.Field<string>(panel,"selectedDate")=="2026-01-02","refresh realigns pinned selection by timestamp instead of array position");
                panel.Apply(value,scope,"演示来源");panel.SelectRange(30);panel.SetAggregation("cumulative");await Layout(window);Check(chart.Days.Length==30&&chart.Days.Last().Tokens==value.Daily.Skip(150).Sum(d=>d.Tokens),"cumulative starts at the selected range boundary, not the 180-day source boundary");panel.SetAggregation("daily");await Layout(window);chart.Choose(chart.Days.Length-1,true);
                foreach(string mode in new[]{"light","dark"}){Theme.Apply(new Preferences{ThemeMode=mode,LiveQuota=false,OrbAnimation="off"});await Layout(window);Capture(window,Path.Combine(root,"model-share-"+mode+".png"));}
                window.Width=340;await Layout(window);Capture(window,Path.Combine(root,"model-share-narrow.png"));Check(chart.ActualWidth==panel.ActualWidth&&chart.Height==270,"narrow layout keeps chart inside available width");
                var longName=Fixture();longName.Daily.Last().Models[0].Model="custom-provider/very-long-automatically-discovered-model-name-with-a-long-version-suffix";panel.Apply(longName,scope,"长名称演示");await Layout(window);Check(legend.Children.OfType<Button>().All(b=>b.ActualWidth<=panel.ActualWidth)&&legend.Children.OfType<Button>().Any(b=>b.ToolTip.ToString().Contains("automatically-discovered")),"long legend names fit narrow width and retain full text in tooltip");window.Width=900;await Layout(window);Check(legend.Children.OfType<Button>().All(b=>b.MaxWidth>800),"legend bounds update when window grows");
                panel.Apply(new UsageSnapshot{DataUnavailable=true},scope,"不可用演示");await Layout(window);Check(chart.Days.Length==0&&StabilityProbe.Field<TextBlock>(panel,"status").Text.Contains("不可用"),"unavailable normal snapshot clears old plot and is not presented as zero usage");
                panel.Apply(new UsageSnapshot{Daily=new DailyUsage[0]},scope,"空数据");await Layout(window);Check(legend.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("没有已记录")),"empty success replaces unavailable legend even with identical empty geometry");panel.Apply(new UsageSnapshot{DataUnavailable=true},scope,"不可用");await Layout(window);Check(legend.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("不可用")),"unavailable replaces successful empty legend");panel.Apply(value,scope,"演示");
                panel.SetRange(DateTime.Today.AddHours(1),DateTime.Today.AddHours(2));await Settled(panel);await Layout(window);Check(calls==1&&!chart.IsHourly&&chart.Days.Length==180,"custom range reads independently and falls back to daily when timeline is absent");
                value=new UsageSnapshot{Daily=new DailyUsage[0],Hourly=new DailyUsage[0],Timeline=new[]{Point("2026-01-03 01:00:00",40,60),Point("2026-01-03 01:05:00",0,0),Point("2026-01-03 01:10:00",60,40)},TimelineStepSeconds=300};panel.Refresh();await Settled(panel);await Layout(window);
                var series=StabilityProbe.Field<Dictionary<string,double[]>>(chart,"series");Check(chart.Days.Length==3&&Double.IsNaN(series["gpt-5"][1])&&series["gpt-5"][0]==40&&series["gpt-5"][2]==60,"custom timeline is preferred and empty bucket remains a gap");
                builds=chart.GeometryBuilds;panel.Refresh();await Settled(panel);await Layout(window);Check(chart.GeometryBuilds==builds,"identical custom result reuses chart geometry");
                value=new UsageSnapshot{DataUnavailable=true};panel.Refresh();await Settled(panel);Check(StabilityProbe.Field<Button>(panel,"retry").Visibility==Visibility.Visible&&StabilityProbe.Field<TextBlock>(panel,"status").ToolTip.ToString().Contains("不可用")&&chart.Days.Length==3,"unavailable custom result without warning reports failure and retains prior plot");
            }
            finally{panel.Dispose();window.Close();}

            // Providers deliberately ignore cancellation to exercise publication
            // guards, rather than relying on cooperative cancellation for safety.
            var requests=new List<TaskCompletionSource<UsageSnapshot>>();var tokens=new List<CancellationToken>();var starts=new List<DateTime>();scope="old";value=Fixture();
            panel=new ModelSharePanel((from,to,cancel)=>{var request=new TaskCompletionSource<UsageSnapshot>();requests.Add(request);tokens.Add(cancel);starts.Add(from);return request.Task;},()=>scope);panel.Apply(value,scope,"旧来源");
            window=new Window{Width=720,Height=680,Content=panel,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();panel.SetRange(DateTime.Today.AddDays(-3),DateTime.Today);await StabilityProbe.Until(()=>requests.Count==1,"first custom read missing",5000);
                panel.SetRange(DateTime.Today.AddDays(-2),DateTime.Today);panel.SetRange(DateTime.Today.AddDays(-1),DateTime.Today);Check(tokens[0].IsCancellationRequested,"range change cancels old read");requests[0].SetResult(value);await StabilityProbe.Until(()=>requests.Count==2,"latest custom read missing",5000);
                Check(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot")==null&&starts[1]==DateTime.Today.AddDays(-1),"late old range is rejected and rapid changes coalesce");requests[1].SetResult(value);await Settled(panel);await Layout(window);
                panel.Refresh();await StabilityProbe.Until(()=>requests.Count==3,"refresh read missing",5000);panel.Apply(value.Copy(),scope,"演示");panel.Apply(value.Copy(),scope,"演示");Check(!tokens[2].IsCancellationRequested,"ordinary updates do not starve a long custom read");requests[2].SetResult(value);await StabilityProbe.Until(()=>requests.Count==4,"coalesced refresh missing",5000);Check(Object.ReferenceEquals(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot"),value),"in-flight result publishes before pending refresh");requests[3].SetException(new IOException("演示失败"));await Settled(panel);Check(Object.ReferenceEquals(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot"),value)&&StabilityProbe.Field<Button>(panel,"retry").Visibility==Visibility.Visible,"failed refresh keeps last result and offers retry");
                StabilityProbe.Field<Button>(panel,"retry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await StabilityProbe.Until(()=>requests.Count==5,"retry missing",5000);scope="latest";panel.Apply(null,scope,"新来源");Check(StabilityProbe.Field<UsageChart>(panel,"chart").Days.Length==0,"source identity change clears old data immediately");requests[4].SetResult(value);await StabilityProbe.Until(()=>requests.Count==6,"new source read missing",5000);Check(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot")==null,"old-source completion cannot restore data");requests[5].SetResult(value);await Settled(panel);
                panel.Refresh();await StabilityProbe.Until(()=>requests.Count==7,"hidden read missing",5000);panel.Visibility=Visibility.Collapsed;requests[6].SetResult(value.Copy());await Settled(panel);Check(Object.ReferenceEquals(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot"),value)&&tokens[6].IsCancellationRequested,"hidden page cancels and rejects late result while retaining its snapshot");
                panel.Visibility=Visibility.Visible;await StabilityProbe.Until(()=>requests.Count==8,"visible resume missing",5000);requests[7].SetResult(value);await Settled(panel);await Layout(window);int builds=StabilityProbe.Field<UsageChart>(panel,"chart").GeometryBuilds;
                window.WindowState=WindowState.Minimized;panel.Apply(Fixture(),scope,"演示");panel.Refresh();await Task.Delay(50);Check(requests.Count==8&&StabilityProbe.Field<UsageChart>(panel,"chart").GeometryBuilds==builds,"minimized page performs no reads or drawing work");window.WindowState=WindowState.Normal;await StabilityProbe.Until(()=>requests.Count==9,"minimized resume missing",5000);
                panel.Dispose();requests[8].SetResult(value);await Settled(panel);Check(StabilityProbe.Field<UsageSnapshot>(panel,"customSnapshot")==null&&StabilityProbe.Field<UsageChart>(panel,"chart").Days.Length==0&&StabilityProbe.Field<Window>(panel,"owner")==null,"dispose rejects late result and releases owner, data, and geometry");
            }
            finally{panel.Dispose();window.Close();}
        }
    }
}
