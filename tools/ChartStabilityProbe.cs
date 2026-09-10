using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexUserData
{
    internal static class ChartStabilityProbe
    {
        private static void Add(DailyUsage day,string model,long tokens,decimal cost)
        {day.Add(tokens,0,0,0,1,0,999);day.Models.Add(new ModelUsage{Model=model,Effort="high",Tokens=tokens,Input=tokens,Requests=1,EquivalentUsd=cost});}
        private static UsageSnapshot Fixture()
        {
            var days=DailyUsage.Empty(DateTime.Today,180);
            for(int i=0;i<days.Length;i++)
            {
                int ago=days.Length-1-i;
                Add(days[i],"fixture-alpha",ago==0?900000:ago<=7?2000:1000,ago==0?90:ago<=7?2:1);
                Add(days[i],"fixture-beta",ago==0?300000:10000,ago==0?30:10);
            }
            var hours=DailyUsage.Hours(DateTime.Today);Add(hours[0],"fixture-alpha",250,.0000001m);Add(hours[1],"fixture-beta",500,.0000002m);
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

            var panel=new HistoryPanel{Margin=new Thickness(12)};panel.Configure(false,true,7);panel.Apply(data,"Generated chart fixture");
            var window=new Window{Width=340,Height=780,Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await Task.Delay(60);window.UpdateLayout();
                var trend=StabilityProbe.Field<UsageChart>(panel,"trend");var heat=StabilityProbe.Field<UsageChart>(panel,"heat");var detail=StabilityProbe.Field<UsageDetails>(panel,"trendDetail");
                var costButton=(Button)GuideStabilityProbe.Find(panel,"ChartMetricCost");costButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(35);
                StabilityProbe.Check(trend.IsCost&&heat.IsCost&&trend.Selected==-1&&detail.Heading.Contains("未选择"),"clicking the cost metric switches both charts without automatically selecting a trend period");
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
