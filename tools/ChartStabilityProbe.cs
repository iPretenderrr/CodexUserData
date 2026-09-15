using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class ChartStabilityProbe
    {
        private static void Add(DailyUsage day,string model,long tokens,decimal cost,long cached=0)
        {
            long fresh=Math.Max(0,tokens-cached);day.Add(fresh,0,cached,0,1,0,999);day.Models.Add(new ModelUsage{Model=model,Effort="high",Tokens=tokens,Input=fresh,CacheRead=cached,Requests=1,EquivalentUsd=cost});
        }
        private static UsageSnapshot Fixture()
        {
            var days=DailyUsage.Empty(DateTime.Today,180);
            for(int i=0;i<days.Length;i++)
            {
                int ago=days.Length-1-i;
                long alpha=ago==0?900000:ago<=7?2000:1000,beta=ago==0?300000:10000;
                Add(days[i],"fixture-alpha",alpha,ago==0?90:ago<=7?2:1,alpha*(2+ago%5)/8);
                Add(days[i],"fixture-beta",beta,ago==0?30:10,beta*(4+ago%3)/8);
            }
            var hours=DailyUsage.Hours(DateTime.Today);Add(hours[0],"fixture-alpha",250,.0000001m,100);Add(hours[1],"fixture-beta",500,.0000002m,350);
            return new UsageSnapshot{Daily=days,Hourly=hours,HourlyThrough=2,SourceName="Generated chart fixture"};
        }
        internal static async Task Run(string root)
        {
            var data=Fixture();var today=DateTime.Today;
            var comparison=ChartComparison.Calculate(data.Daily,7,today,false,false);
            StabilityProbe.Check(comparison.Available&&comparison.Current==84000&&comparison.Previous==77000,"complete-period comparison excludes today's large partial sample and compares equal seven-day windows");
            comparison=ChartComparison.Calculate(data.Daily,7,today,true,false);
            StabilityProbe.Check(comparison.Available&&comparison.Current==84&&comparison.Previous==77&&comparison.Message.Contains("USD"),"cost comparison uses model API estimates rather than source billed amounts");
            var missing=data.Daily.Where(d=>d.Date!=today.AddDays(-8).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)).ToArray();
            StabilityProbe.Check(!ChartComparison.Calculate(missing,7,today,false,false).Available,"a missing comparison date is not silently filled with zero");
            StabilityProbe.Check(!ChartComparison.Calculate(data.Daily,90,today,false,false).Available&&!ChartComparison.Calculate(data.Daily,180,today,false,false).Available,"the 180-day source does not claim enough complete history for 90-day or 180-day comparisons");
            StabilityProbe.Check(!ChartComparison.Calculate(data.Daily,1,today,false,false).Available&&!ChartComparison.Calculate(data.Daily,7,today,false,true).Available,"hour-only today data and incomplete log coverage do not produce misleading changes");
            var zero=DailyUsage.Empty(today.AddDays(-1),14);Add(zero.Last(),"fixture-alpha",5,.0000001m);
            comparison=ChartComparison.Calculate(zero,7,today,true,false);
            StabilityProbe.Check(comparison.Available&&comparison.Previous==0&&comparison.Current==.0000001m&&!comparison.ChangePercent.HasValue&&comparison.Message.Contains("不计算百分比"),"positive usage over a zero baseline is reported without an infinite or fabricated percentage");
            comparison=ChartComparison.Calculate(DailyUsage.Empty(today.AddDays(-1),14),7,today,false,false);
            StabilityProbe.Check(comparison.Available&&!comparison.ChangePercent.HasValue&&comparison.Message.Contains("均为 0"),"two observed zero periods remain zero rather than an undefined percentage");
            StabilityProbe.Check(ChartValue.Money(.0000001m)!="$0.00"&&ChartValue.Axis(.0000001,true)!=ChartValue.Axis(.0000002,true)&&ChartValue.Axis(2000,false)==TokenText.Axis(2000),"very small positive estimates remain readable and token axes keep token units");
            var cachePoint=new DailyUsage();cachePoint.Add(20,500,60,20,1,0,0);StabilityProbe.Check(Math.Abs(ChartValue.CacheRate(cachePoint)-60)<.001,"cache hit rate divides cache reads by fresh, cached and cache-write input while excluding output");
            var modeSource=data.Daily.Skip(data.Daily.Length-14).ToArray();var weekly=UsageTrendSeries.Build(modeSource,"weekly");var cumulative=UsageTrendSeries.Build(modeSource,"cumulative");
            StabilityProbe.Check(weekly.Length>=2&&weekly.All(d=>DateTime.ParseExact(d.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture).DayOfWeek==DayOfWeek.Sunday)&&weekly.Sum(d=>d.Tokens)==modeSource.Sum(d=>d.Tokens),"weekly trend buckets start on Sunday and preserve the selected range total");
            StabilityProbe.Check(cumulative.Length==modeSource.Length&&cumulative.Last().Tokens==modeSource.Sum(d=>d.Tokens)&&cumulative.Zip(cumulative.Skip(1),(a,b)=>b.Tokens>=a.Tokens).All(v=>v),"cumulative trend keeps each date and grows to the selected range total");
            var weeklyHeat=UsageHeatmapSeries.Build(modeSource,"weekly");var cumulativeHeat=UsageHeatmapSeries.Build(modeSource,"cumulative");
            StabilityProbe.Check(weeklyHeat.Length>=2&&weeklyHeat.All(d=>DateTime.ParseExact(d.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture).DayOfWeek==DayOfWeek.Sunday)&&weeklyHeat.Sum(d=>d.Tokens)==modeSource.Sum(d=>d.Tokens)&&weeklyHeat.Last().DisplayLabel.EndsWith(modeSource.Last().Date),"weekly heatmap uses one Sunday-based column, preserves totals and labels the current partial week accurately");
            StabilityProbe.Check(cumulativeHeat.Length==weeklyHeat.Length&&cumulativeHeat.Last().Tokens==modeSource.Sum(d=>d.Tokens)&&cumulativeHeat.Zip(cumulativeHeat.Skip(1),(a,b)=>b.Tokens>=a.Tokens).All(v=>v)&&cumulativeHeat.Last().DisplayLabel.StartsWith("累计 "),"cumulative heatmap builds a monotonic weekly staircase ending at the visible total");
            StabilityProbe.Check(UsageChart.HeatRows(1,100)==1&&UsageChart.HeatRows(100,100)==7&&UsageChart.HeatRows(0,100)==0,"weekly block heights keep positive activity visible and remain within seven rows");
            var modePreferences=new Preferences{TrendAggregation="weekly",HeatmapAggregation="cumulative"};modePreferences.Validate();var clonedModes=modePreferences.Clone();StabilityProbe.Check(clonedModes.TrendAggregation=="weekly"&&clonedModes.HeatmapAggregation=="cumulative","independent heatmap and trend aggregation choices survive settings persistence");

            var picker=new DateTimeRangePicker(DateTime.Now.AddDays(-2).Date.AddHours(9).AddMinutes(15).AddSeconds(20),DateTime.Now.AddMinutes(-1));
            var pickerWindow=new StyledWindow{Width=760,Height=430,ShowActivated=false,ShowInTaskbar=false};pickerWindow.SetBody(picker,"选择日期和时间","CUSTOM RANGE",false);
            try
            {
                pickerWindow.Show();await Task.Delay(35);pickerWindow.UpdateLayout();DateTime selectedFrom,selectedTo;
                var calendar=StabilityProbe.Field<RangeMonthCalendar>(picker,"calendar");var cells=StabilityProbe.Field<System.Collections.Generic.List<Button>>(calendar,"dayButtons");
                StabilityProbe.Check(cells.Count==42&&calendar.ActualWidth>300&&picker.ActualWidth>700,"custom date-time picker keeps a two-column month layout with reusable day cells");
                StabilityProbe.Check(picker.TryRead(out selectedFrom,out selectedTo)&&selectedFrom.Second==20&&selectedTo<=DateTime.Now,"custom date-time picker preserves second precision and validates its initial range");
                var startInput=StabilityProbe.Field<TextBox>(picker,"startTime");startInput.Text="08:04:03";StabilityProbe.Call(picker,"Activate",false);
                StabilityProbe.Check(startInput.Text=="08:04:03","switching date cards does not discard a typed time");
                var follow=StabilityProbe.Field<CheckBox>(picker,"followNow");follow.IsChecked=true;await Task.Delay(20);
                StabilityProbe.Check(picker.TryRead(out selectedFrom,out selectedTo)&&selectedFrom.Hour==8&&selectedFrom.Second==3&&Math.Abs((DateTime.Now-selectedTo).TotalSeconds)<3,"custom date-time picker can keep its end bound at the current time without changing the start time");
                var rendered=(FrameworkElement)pickerWindow.Content;var image=new RenderTargetBitmap((int)Math.Ceiling(rendered.ActualWidth),(int)Math.Ceiling(rendered.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(rendered);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"date-time-picker.png")))png.Save(file);
            }
            finally{pickerWindow.Close();}

            var panel=new HistoryPanel{Margin=new Thickness(12)};panel.Configure(true,true,7);panel.Apply(data,"Generated chart fixture");
            var window=new Window{Width=880,Height=780,Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await Task.Delay(60);window.UpdateLayout();
                var trend=StabilityProbe.Field<UsageChart>(panel,"trend");var heat=StabilityProbe.Field<UsageChart>(panel,"heat");var cacheTrend=StabilityProbe.Field<UsageChart>(panel,"cacheTrend");var detail=StabilityProbe.Field<UsageDetails>(panel,"trendDetail");
                StabilityProbe.Check(cacheTrend.IsRate&&!cacheTrend.IsHourly&&cacheTrend.WindowDays==7&&cacheTrend.Days.Length==data.Daily.Length,"cache hit chart follows the selected token date range without its own aggregation controls");
                var chartImage=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);chartImage.Render(window);var chartPng=new PngBitmapEncoder();chartPng.Frames.Add(BitmapFrame.Create(chartImage));using(var file=File.Create(Path.Combine(root,"chart-mode-controls.png")))chartPng.Save(file);
                panel.SetHeatAggregation("weekly");window.UpdateLayout();var weeklyImage=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);weeklyImage.Render(window);var weeklyPng=new PngBitmapEncoder();weeklyPng.Frames.Add(BitmapFrame.Create(weeklyImage));using(var file=File.Create(Path.Combine(root,"heatmap-weekly.png")))weeklyPng.Save(file);
                panel.SetHeatAggregation("cumulative");window.UpdateLayout();var cumulativeImage=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);cumulativeImage.Render(window);var cumulativePng=new PngBitmapEncoder();cumulativePng.Frames.Add(BitmapFrame.Create(cumulativeImage));using(var file=File.Create(Path.Combine(root,"heatmap-cumulative.png")))cumulativePng.Save(file);panel.SetHeatAggregation("daily");window.UpdateLayout();
                var chartScroller=(ScrollViewer)window.Content;chartScroller.ScrollToVerticalOffset(500);window.UpdateLayout();var trendImage=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);trendImage.Render(window);var trendPng=new PngBitmapEncoder();trendPng.Frames.Add(BitmapFrame.Create(trendImage));using(var file=File.Create(Path.Combine(root,"trend-mode-controls.png")))trendPng.Save(file);chartScroller.ScrollToEnd();window.UpdateLayout();var cacheImage=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);cacheImage.Render(window);var cachePng=new PngBitmapEncoder();cachePng.Frames.Add(BitmapFrame.Create(cacheImage));using(var file=File.Create(Path.Combine(root,"cache-hit-rate.png")))cachePng.Save(file);chartScroller.ScrollToTop();window.Width=340;await Task.Delay(25);window.UpdateLayout();
                var heatModes=StabilityProbe.Field<System.Collections.Generic.Dictionary<string,Button>>(panel,"heatAggregations");var trendModes=StabilityProbe.Field<System.Collections.Generic.Dictionary<string,Button>>(panel,"trendAggregations");var heatDetail=StabilityProbe.Field<UsageDetails>(panel,"heatDetail");
                panel.SetHeatAggregation("weekly");heat.Choose(heat.Days.Length-1,true);StabilityProbe.Check(heat.Days.Length>=25&&heat.Days.Length<=27&&heat.HeatMode=="weekly"&&heat.Days.Last().DisplayLabel.StartsWith("周 ")&&heatDetail.Heading.StartsWith("周 ")&&AutomationProperties.GetItemStatus(heatModes["weekly"])=="已选中"&&AutomationProperties.GetItemStatus(trendModes["daily"])=="已选中","heatmap weekly mode uses one column per Sunday-based week without changing the trend mode");
                long fullHeatTotal=data.Daily.Sum(d=>d.Tokens);int heatWeeks=heat.Days.Length;panel.SetHeatAggregation("cumulative");StabilityProbe.Check(heat.Days.Length==heatWeeks&&heat.Days.Last().Tokens==fullHeatTotal&&heat.HeatMode=="cumulative"&&AutomationProperties.GetItemStatus(heatModes["cumulative"])=="已选中"&&AutomationProperties.GetItemStatus(trendModes["daily"])=="已选中","heatmap cumulative mode is an independent weekly staircase ending at the 180-day total");panel.SetHeatAggregation("daily");
                DateTime customFrom=today.AddHours(8),customTo=today.AddHours(9);long customStart=LocalCodexUsage.Unix(customFrom);var customDays=DailyUsage.Empty(customTo,1);var customTimeline=UsageTimeline.Empty(customStart,LocalCodexUsage.Unix(customTo),300);
                Add(customDays[0],"fixture-alpha",1000,1,600);Add(customTimeline[0],"fixture-alpha",300,.3m,210);Add(customTimeline.Last(),"fixture-alpha",700,.7m,350);
                var customSnapshot=new UsageSnapshot{Daily=customDays,Hourly=DailyUsage.Hours(customTo),Timeline=customTimeline,TimelineStepSeconds=300,HourlyThrough=24,SourceName="Generated custom fixture"};
                panel.BeginCustomRange(customFrom,customTo,"Generated chart fixture");var rangeButtons=StabilityProbe.Field<System.Collections.Generic.Dictionary<int,Button>>(panel,"ranges");var customButton=StabilityProbe.Field<Button>(panel,"customRangeButton");
                StabilityProbe.Check(rangeButtons.Values.All(b=>AutomationProperties.GetItemStatus(b)=="未选中")&&AutomationProperties.GetItemStatus(customButton)=="已选中"&&heat.Days.Length==180,"selecting a custom curve clears preset highlights without replacing the 180-day heatmap");
                panel.ApplyCustomRange(customSnapshot,"Generated chart fixture",customFrom,customTo);await Task.Delay(35);window.UpdateLayout();
                StabilityProbe.Check(trend.Days.Length==customTimeline.Length&&trend.Days[0].Date.Length==19&&trend.Days.Sum(d=>d.Tokens)==1000,"custom trend renders the selected timestamp buckets instead of reusing daily aggregates");
                StabilityProbe.Check(cacheTrend.IsRate&&cacheTrend.Days.Length==customTimeline.Length&&cacheTrend.WindowDays==customTimeline.Length,"cache hit chart follows the exact custom timestamp buckets");
                StabilityProbe.Check(heat.Days.Length==180&&heat.Days[0].Date==data.Daily[0].Date&&AutomationProperties.GetItemStatus(customButton)=="已选中","the completed custom curve leaves heatmap dates and custom selection state unchanged");
                panel.SetAggregation("weekly");StabilityProbe.Check(trend.Days.Length==1&&DateTime.ParseExact(trend.Days[0].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture).DayOfWeek==DayOfWeek.Sunday&&trend.Days[0].Tokens==1000&&cacheTrend.Days.Length==customTimeline.Length,"weekly token mode does not aggregate the cache hit curve");
                panel.SetAggregation("cumulative");StabilityProbe.Check(trend.Days.Length==customTimeline.Length&&trend.Days.Last().Tokens==1000&&cacheTrend.Days.Length==customTimeline.Length,"cumulative token mode does not alter the cache hit curve");panel.SetAggregation("daily");
                panel.SetRange(7);await Task.Delay(25);window.UpdateLayout();
                var costButton=(Button)GuideStabilityProbe.Find(panel,"ChartMetricCost");costButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(35);
                StabilityProbe.Check(trend.IsCost&&heat.IsCost&&cacheTrend.IsRate&&!cacheTrend.IsCost&&trend.Selected==-1&&detail.Heading.Contains("未选择"),"clicking the cost metric leaves the independent cache hit rate curve unchanged");
                panel.SetModel("fixture-alpha");await Task.Delay(25);
                var filtered=StabilityProbe.Field<UsageSnapshot>(panel,"snapshot");comparison=ChartComparison.Calculate(filtered.Daily,7,today,true,false);
                StabilityProbe.Check(comparison.Current==14&&comparison.Previous==7&&Math.Abs(comparison.ChangePercent.Value-100)<.001&&filtered.Daily.Last().Tokens==900000,"model filtering applies to API estimates, token totals and complete-period changes together");
                var total=(TextBlock)GuideStabilityProbe.Find(panel,"TrendMetricTotal");var title=(TextBlock)GuideStabilityProbe.Find(panel,"TrendMetricTitle");var compare=(TextBlock)GuideStabilityProbe.Find(panel,"TrendComparison");
                StabilityProbe.Check(total.Text=="$102.00"&&title.Text.Contains("USD")&&compare.Text.Contains("不含今日")&&compare.Text.Contains("按当前已读记录"),"visible current range and comparison clearly identify different time windows and USD estimates");
                int selected=data.Daily.Length-2;trend.Choose(selected,false);
                StabilityProbe.Check(trend.Selected==-1&&detail.Heading.Contains("未选择"),"moving over a trend point does not rebuild or select the detail panel");
                trend.Choose(selected,true);string heading=detail.Heading;heat.Choose(data.Daily.Length-3,true);panel.SetMetric(false);
                StabilityProbe.Check(trend.Selected==selected&&detail.Heading==heading&&heat.Selected==data.Daily.Length-3&&!trend.IsCost,"manual trend and heatmap selections stay independent when the metric changes");
                panel.SetRange(1);panel.SetMetric(true);await Task.Delay(35);window.UpdateLayout();
                StabilityProbe.Check(trend.IsHourly&&trend.Selected==-1&&trend.Days.Length==24&&detail.Heading.Contains("未选择"),"switching from daily to hourly data clears the old selection without choosing a replacement");
                StabilityProbe.Check(cacheTrend.IsRate&&cacheTrend.IsHourly&&cacheTrend.Days.Length==24&&cacheTrend.WindowDays==1,"the cache hit chart follows the token chart into today's hourly range");
                double max=StabilityProbe.Field<double>(trend,"max");
                StabilityProbe.Check(max>0&&max<.00001&&trend.PointFor(0).Y<StabilityProbe.Field<Rect>(trend,"plot").Bottom,"a tiny model-filtered cost produces a visible nonzero curve instead of a token-sized axis");
                int builds=trend.GeometryBuilds;trend.Highlight("fixture-alpha");trend.Choose(0,false);await Task.Delay(30);panel.Apply(data,"Generated chart fixture");await Task.Delay(30);
                StabilityProbe.Check(trend.GeometryBuilds==builds,"hover highlighting and an unchanged snapshot reuse the chart geometry cache");
                StabilityProbe.Check(StabilityProbe.Field<UniformGrid>(panel,"summary").Columns==2&&costButton.ActualWidth<=panel.ActualWidth,"narrow chart layouts reflow summary cards and keep metric controls within the page");
                data.CoverageWarnings=1;panel.Apply(data,"Generated chart fixture");panel.SetRange(7);
                StabilityProbe.Check(compare.Text.Contains("未计入")&&!compare.Text.Contains("增加"),"a newly incomplete snapshot replaces a previously available comparison with an explanation");
            }
            finally{window.Close();}
        }
    }
}
