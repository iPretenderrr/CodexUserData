using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUserData
{
    // Synthetic numeric fixtures only: these checks never open a usage database
    // or issue a remote range request. Each probe exercises the changed charts.
    internal static class UsageViewsStabilityProbe
    {
        private static void Check(bool value,string message){StabilityProbe.Check(value,"Usage views: "+message);}
        private static void Add(DailyUsage day,string model,long tokens,decimal amount,string effort="high")
        {
            long cached=tokens/3,input=tokens-cached;day.Add(input,0,cached,0,1,0,999);
            day.Models.Add(new ModelUsage{Model=model,Effort=effort,Tokens=tokens,Input=input,CacheRead=cached,Requests=1,EquivalentUsd=amount,UnpricedTokens=amount==0?tokens:0});
        }
        private static DailyUsage Day(string date,long alpha,long beta,decimal alphaCost=0,decimal betaCost=0)
        {
            var day=new DailyUsage{Date=date};if(alpha>0)Add(day,"fixture-alpha",alpha,alphaCost);if(beta>0)Add(day,"fixture-beta",beta,betaCost);return day;
        }
        private static void Draw(ModelShareChart chart,double width)
        {
            chart.Height=350;chart.Measure(new Size(width,350));chart.Arrange(new Rect(0,0,width,350));
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())StabilityProbe.Call(chart,"OnRender",dc);
        }
        private static double StackHeight(ModelShareChart.ShareCell[] cells,string date)
        {return cells.Where(c=>c.Bucket!=null&&c.Bucket.Date==date).Sum(c=>c.Bounds.Height);}
        private static decimal Amount(DailyUsage[] points){return points.Sum(d=>d.Models.Sum(m=>m.EquivalentUsd));}
        private static async Task Layout(Window window){window.UpdateLayout();await Task.Delay(40);window.UpdateLayout();}
        private static bool Inside(FrameworkElement child,FrameworkElement parent)
        {
            Point point=child.TranslatePoint(new Point(0,0),parent);
            return point.X>=-.1&&point.X+child.ActualWidth<=parent.ActualWidth+.5&&child.ActualWidth>0;
        }
        private static UsageSnapshot Fixture()
        {
            string[] models={"gpt-6-astra","gpt-6-sol","gpt-6.1-sol","gpt-6-luna","gpt-5.6-sol","gpt-5.3-codex"};
            decimal[] rates={18m,7m,5m,1m,4m,3m};var days=DailyUsage.Empty(DateTime.Today,180);
            for(int d=0;d<days.Length;d++)for(int m=0;m<models.Length;m++)
            {
                long tokens=(long)((7-m)*(180000+80000*Math.Sin(d*.21+m)));Add(days[d],models[m],tokens,tokens*rates[m]/1000000m);
            }
            var hours=DailyUsage.Hours(DateTime.Today);Add(hours[0],models[0],600,.0000001m);Add(hours[1],models[1],300,.0000002m);
            return new UsageSnapshot{Daily=days,Hourly=hours,HourlyThrough=2,Timeline=new DailyUsage[0],SourceName="Synthetic usage views",CountLabel="模拟请求数"};
        }

        private static void RendererChecks()
        {
            var chart=new ModelShareChart();var raw=new[]{Day("2026-09-01",900,100),Day("2026-09-02",0,100),Day("2026-09-03",0,0)};
            chart.SetUsageData(raw,raw,"bars",false,false,false);Draw(chart,800);var cells=chart.LayoutCells;
            Check(cells.Length==3&&cells.All(c=>c.Bounds.Width>0&&c.Bounds.Height>0),"empty token buckets remain empty and all positive segments have finite geometry");
            double firstHeight=StackHeight(cells,raw[0].Date),secondHeight=StackHeight(cells,raw[1].Date);
            Check(Math.Abs(firstHeight/secondHeight-10)<.001,"token bars share an absolute scale: 1,000 Tokens is ten times the height of 100 Tokens");
            Check(cells.First(c=>c.Model=="fixture-alpha").Value==900&&cells.First().TotalValue==1000,"amount cells retain exact token values and the bucket denominator");
            var target=cells[0];Check(chart.Hit(new Point(target.Bounds.Left+target.Bounds.Width/2,target.Bounds.Top+target.Bounds.Height/2))>=0,"amount bar segments are reachable by hit testing");
            int builds=chart.GeometryBuilds;chart.Highlight("fixture-alpha");Draw(chart,800);chart.SetUsageData(raw,raw,"bars",false,false,false);Draw(chart,800);
            Check(chart.GeometryBuilds==builds,"hover highlighting and identical amount data reuse cached geometry");
            chart.Choose(0,true);var changed=new[]{Day(raw[0].Date,0,100),raw[1],raw[2]};chart.SetUsageData(changed,changed,"bars",false,false,false);Draw(chart,800);
            Check(chart.SelectedBucket!=null&&chart.SelectedBucket.Date==raw[0].Date&&StabilityProbe.Field<int>(chart,"selected")>=0,"a disappearing model segment retains its still-valid selected interval and whole-column outline");

            var tiny=new DailyUsage{Date="2026-09-01"};Add(tiny,"fixture-alpha",300,.0000001m,"low");Add(tiny,"fixture-alpha",400,.0000002m,"high");Add(tiny,"fixture-beta",600,0);tiny.Tokens+=50;
            var priced=new[]{tiny,Day("2026-09-02",0,400,0,.0000001m)};
            chart.SetUsageData(priced,priced,"bars",false,false,true);Draw(chart,800);cells=chart.LayoutCells;
            Check(cells.Length==2&&cells[0].Value==.0000003m&&cells[0].TotalValue==.0000003m&&cells[1].Value==.0000001m,"USD combines model efforts without rounding away sub-micro estimates or adding billed CostUsd");
            Check(cells[0].Tokens==700&&cells[0].Total==1350&&cells.All(c=>c.Value>0),"USD cells retain token counts while zero-price and unclassified costs stay absent");
            Check(Math.Abs(StackHeight(cells,priced[0].Date)/StackHeight(cells,priced[1].Date)-3)<.001,"tiny USD bars use a nonzero absolute USD scale");
            builds=chart.GeometryBuilds;double oldHeight=StackHeight(cells,priced[1].Date);
            var repriced=new[]{tiny,Day("2026-09-02",0,400,0,.0000002m)};chart.SetUsageData(repriced,repriced,"bars",false,false,true);Draw(chart,800);
            Check(chart.GeometryBuilds>builds&&StackHeight(chart.LayoutCells,repriced[1].Date)>oldHeight*1.9,"price-only refresh invalidates amount geometry even when all token totals are unchanged");

            // Closed-form cumulative points avoid the production price catalogue:
            // these endpoint expectations preserve the manually supplied USD amounts.
            DateTime start=new DateTime(2026,9,1);var dense=Enumerable.Range(0,2000).Select(i=>Day(start.AddMinutes(i).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture),i%2==0?900:0,100,i%2==0?.0000003m:0,.0000001m)).ToArray();
            var running=Enumerable.Range(0,dense.Length).Select(i=>Day(dense[i].Date,900L*(i/2+1),100L*(i+1),.0000003m*(i/2+1),.0000001m*(i+1))).ToArray();
            chart.SetUsageData(dense,dense,"bars",false,false,false);Draw(chart,300);
            var buckets=chart.LayoutCells.Select(c=>c.Bucket).Distinct().ToArray();
            Check(chart.ColumnCount<=Math.Max(1,(int)(chart.PlotBounds.Width/5))&&buckets.Sum(d=>d.Tokens)==dense.Sum(d=>d.Tokens),"dense token series stays bounded by viewport width and conserves every token");
            chart.SetUsageData(dense,dense,"bars",false,false,true);Draw(chart,300);
            Check(chart.LayoutCells.Sum(c=>c.Value)==Amount(dense),"dense USD compression sums exact decimal estimates rather than averaging segment shares");
            chart.SetUsageData(running,dense,"bars",true,false,true);Draw(chart,300);
            buckets=chart.LayoutCells.Select(c=>c.Bucket).Distinct().ToArray();var last=buckets.Last();
            Check(last.Tokens==dense.Sum(d=>d.Tokens)&&last.Models.Sum(m=>m.EquivalentUsd)==Amount(dense)&&buckets.All(d=>d.Tokens<=last.Tokens),"cumulative compression takes running endpoints and never multiplies token or USD totals");
            chart.SetUsageData(running,dense,"treemap",true,false,true);Draw(chart,800);cells=chart.LayoutCells;
            Check(cells.Sum(c=>c.Value)==Amount(dense)&&cells.All(c=>c.TotalValue==Amount(dense)),"USD treemap uses the raw selected interval even when incoming chart points are cumulative");
            double area=cells.Sum(c=>c.Bounds.Width*c.Bounds.Height);var alpha=cells.Single(c=>c.Model=="fixture-alpha");
            Check(Math.Abs(alpha.Bounds.Width*alpha.Bounds.Height/area-(double)(alpha.Value/alpha.TotalValue))<.025,"USD treemap area follows model cost within separator tolerance");
            chart.SetUsageData(priced,priced,"treemap",false,false,false);Draw(chart,800);
            Check(chart.LayoutCells.Sum(c=>c.Tokens)==1750&&chart.LayoutCells.Any(c=>c.Model=="unknown"&&c.Value==50),"token treemap preserves usage without a model and does not omit zero-price token models");
            chart.SetUsageData(priced,priced,"treemap",false,false,true);Draw(chart,800);
            Check(chart.LayoutCells.Length==2&&chart.LayoutCells.Sum(c=>c.Value)==.0000004m,"cost treemap omits unpriced amounts and retains exact raw USD total");
            chart.ClearPointer();chart.SetUsageData(new DailyUsage[0],new DailyUsage[0],"treemap",false,false,true);Draw(chart,300);
            Check(chart.LayoutCells.Length==0&&chart.SelectedBucket==null,"empty amount range removes previous geometry and selection");
        }

        private static async Task PanelChecks()
        {
            var snapshot=Fixture().Reprice(ApiPrices.Snapshot());var panel=new HistoryPanel();panel.Configure(false,true,30);panel.Apply(snapshot,"模拟数据 · 仅用于图表验证");
            int reads=0;panel.RangeChanged+=delegate{reads++;};panel.CacheRangeChanged+=delegate{reads++;};panel.CustomRangeRequested+=delegate{reads++;};panel.CacheCustomRangeRequested+=delegate{reads++;};
            var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=panel};
            var window=new Window{Width=1060,Height=900,ShowActivated=false,ShowInTaskbar=false,Background=Theme.Background,Content=new Border{Background=Theme.Background,Padding=new Thickness(18),Child=scroll}};
            try
            {
                window.Show();await Layout(window);panel.SetTrendView("bars");await Layout(window);
                var alternative=StabilityProbe.Field<ModelShareChart>(panel,"trendAlternative");var curve=StabilityProbe.Field<UsageChart>(panel,"trend");var detail=StabilityProbe.Field<UsageDetails>(panel,"trendDetail");
                var views=StabilityProbe.Field<Dictionary<string,Button>>(panel,"trendViewChoices");var aggregations=StabilityProbe.Field<WrapPanel>(panel,"trendAggregationChoices");
                Check(alternative.IsVisible&&!curve.IsVisible&&views.Count==3&&AutomationProperties.GetItemStatus(views["bars"])=="已选中"&&reads==0,"three trend view choices select bars without requesting a data range");
                var raw=StabilityProbe.Field<DailyUsage[]>(panel,"trendRaw");Check(raw.Length==30&&raw.Sum(d=>d.Tokens)==snapshot.Daily.Skip(150).Sum(d=>d.Tokens),"amount views use precisely the same selected thirty-day interval");
                alternative.Choose(0,false);Check(detail.ModelRows==0&&detail.Heading.Contains("未选择"),"hover paints the amount chart without opening lower model details");
                alternative.Choose(0,true);Check(alternative.SelectedBucket!=null&&detail.ModelRows>0&&detail.Heading.Contains("已选中"),"click pins an amount bar and displays its existing lower detail component");
                panel.SetAggregation("weekly");await Layout(window);Check(alternative.SelectedBucket==null&&detail.ModelRows==0&&StabilityProbe.Field<DailyUsage[]>(panel,"trendBuckets").Length<=6&&reads==0,"weekly regrouping reuses in-memory data and clears the old daily selection");
                panel.SetAggregation("cumulative");views["treemap"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Layout(window);
                Check(alternative.LayoutCells.Sum(c=>c.Tokens)==raw.Sum(d=>d.Tokens)&&aggregations.Visibility==Visibility.Collapsed&&reads==0,"treemap consumes raw range totals and hides temporal aggregation controls");
                alternative.Choose(0,false);Check(detail.ModelRows==0,"treemap hover also keeps lower details unselected");alternative.Choose(0,true);
                Check(detail.ModelRows==1&&detail.Heading.Contains("区间合计"),"treemap click selects one model's range total in the existing detail component");
                string selectedTree=detail.Heading;snapshot.Daily.Last().Models[0].EquivalentUsd+=.01m;panel.Apply(snapshot,"模拟数据 · 仅用于图表验证");await Layout(window);
                Check(StabilityProbe.Field<int>(alternative,"selected")>=0&&detail.Heading==selectedTree,"refresh retains the selected model tile outline together with its updated interval detail");
                panel.SetMetric(true);await Layout(window);Check(detail.ModelRows==0&&StabilityProbe.Field<int>(alternative,"selected")==-1,"changing the treemap metric clears both the tile selection and its previous detail");panel.SetMetric(false);
                panel.SetTrendView("bars");await Layout(window);Check(aggregations.Visibility==Visibility.Visible&&StabilityProbe.Field<string>(panel,"trendAggregation")=="cumulative","leaving treemap restores the preceding temporal aggregation");
                panel.SetAggregation("daily");panel.SetRange(7);panel.SetModel("gpt-6-sol");panel.SetMetric(true);await Layout(window);
                raw=StabilityProbe.Field<DailyUsage[]>(panel,"trendRaw");decimal expected=snapshot.Daily.Skip(173).SelectMany(d=>d.Models).Where(m=>m.Model=="gpt-6-sol").Sum(m=>m.EquivalentUsd);
                Check(raw.Length==7&&raw.All(d=>d.Models.All(m=>m.Model=="gpt-6-sol"))&&alternative.LayoutCells.Sum(c=>c.Value)==expected&&reads==0,"preset, model and USD changes reuse the selected numeric data without range callbacks");
                Check(StabilityProbe.Field<TextBlock>(panel,"sum").Text==ChartValue.Money(expected)&&StabilityProbe.Field<TextBlock>(panel,"trendTitle").Text.Contains("USD"),"USD summary and absolute bars use the same equivalent estimate");
                panel.SetTrendView("treemap");await Layout(window);Check(alternative.LayoutCells.Length==1&&alternative.LayoutCells[0].Value==expected,"model-filtered cost treemap preserves the selected range estimate");
                panel.SetModel("");panel.SetRange(1);panel.SetTrendView("bars");await Layout(window);
                Check(StabilityProbe.Field<DailyUsage[]>(panel,"trendRaw").Length==2&&alternative.LayoutCells.Sum(c=>c.Value)==Amount(snapshot.Hourly.Take(2).ToArray()),"today bars exclude future hours and preserve current hourly USD estimates");
                panel.SetMetric(false);panel.SetRange(180);await Layout(window);alternative.Choose(0,true);
                Check(alternative.SelectedBucket!=null&&detail.ModelRows>0,"wide long-range amount bars support an individual selected day");
                window.Width=370;await Layout(window);var ignored=alternative.LayoutCells;
                Check(alternative.SelectedBucket==null&&detail.ModelRows==0&&detail.Heading.Contains("未选择"),"viewport compression removes a merged selected bar and clears its old lower details");
                var rangeHeader=StabilityProbe.Field<Grid>(panel,"trendRangeHeader");var viewActions=StabilityProbe.Field<WrapPanel>(panel,"trendViewActions");
                var rangeActions=(WrapPanel)rangeHeader.Children[0];var trendActions=StabilityProbe.Field<WrapPanel>(panel,"trendActions");
                Check(rangeActions.Children.OfType<Button>().All(b=>Inside(b,panel))&&views.Values.All(b=>Inside(b,panel))&&Inside(viewActions,panel)&&Inside(trendActions,panel)&&alternative.ActualWidth<=panel.ActualWidth+.5,"narrow period, view and aggregation controls remain inside the page width");
                panel.SetTrendView("curve");Check(curve.IsVisible&&!alternative.IsVisible&&reads==0,"returning to curve restores the original view without data requests");
                panel.SetTrendView("treemap");panel.Apply(null,"模拟新来源");await Layout(window);
                Check(alternative.LayoutCells.Length==0&&detail.ModelRows==0&&StabilityProbe.Field<DailyUsage[]>(panel,"trendRaw").Length==0,"source replacement clears old amount cells, interval cache and lower details immediately");
            }
            finally{window.Close();}
        }

        private static string MetricText(DependencyObject root,string id)
        {
            var pending=new Queue<DependencyObject>();pending.Enqueue(root);
            while(pending.Count>0)
            {
                var node=pending.Dequeue();var text=node as TextBlock;
                if(text!=null&&AutomationProperties.GetAutomationId(text)==id)return text.Text;
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)pending.Enqueue(VisualTreeHelper.GetChild(node,i));
            }
            return null;
        }
        private static async Task RegressionChecks()
        {
            string today=DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var hours=DailyUsage.Hours(DateTime.Today);Add(hours[0],"fixture-alpha",60,0);Add(hours[1],"fixture-alpha",40,0);
            var snapshot=new UsageSnapshot{Daily=new[]{Day(today,1000,0)},Hourly=hours,HourlyThrough=2};
            var panel=new HistoryPanel();panel.Configure(false,true,1);panel.Apply(snapshot,"chart-fixes-fixture");
            var window=new Window{Width=960,Height=850,ShowActivated=false,ShowInTaskbar=false,Content=new ScrollViewer{Content=panel}};
            try
            {
                window.Show();await Layout(window);var timing=StabilityProbe.Field<TextBlock>(panel,"timing");
                foreach(string view in new[]{"curve","bars","treemap","bars","curve"})
                {
                    panel.SetTrendView(view);await Layout(window);
                    Check(timing.Text.Contains("900 Tokens 只有日汇总"),"hourly coverage remains visible in "+view+" view");
                }
                panel.SetRange(7);Check(!timing.Text.Contains("未分摊到小时"),"daily ranges clear the obsolete hourly coverage message");
                panel.SetRange(1);panel.SetAggregation("cumulative");panel.SetTrendView("bars");await Layout(window);
                var chart=StabilityProbe.Field<ModelShareChart>(panel,"trendAlternative");
                string first=(string)StabilityProbe.Call(chart,"AxisLabel",hours[0].Date),second=(string)StabilityProbe.Call(chart,"AxisLabel",hours[1].Date);
                Check(first!=second&&first.Contains("00:00")&&second.Contains("01:00"),"today cumulative bars retain distinct hourly axis labels");
                var custom=new UsageSnapshot{Daily=snapshot.Daily,Timeline=new[]{hours[0],hours[1]},TimelineStepSeconds=3600};
                panel.BeginCustomRange(DateTime.Today,DateTime.Today.AddHours(2),"chart-fixes-fixture");panel.ApplyCustomRange(custom,"chart-fixes-fixture",DateTime.Today,DateTime.Today.AddHours(2));await Layout(window);
                Check(((string)StabilityProbe.Call(chart,"AxisLabel",hours[1].Date)).Contains("01:00")&&!timing.Text.Contains("未分摊到小时"),"custom subday cumulative bars preserve hours and discard the previous preset warning");

                var unknown=new DailyUsage{Date=today};Add(unknown,"UNKNOWN",100,0);unknown.Tokens+=50;
                panel.SetRange(7);panel.Apply(new UsageSnapshot{Daily=new[]{unknown}},"unknown-fixture");panel.SetTrendView("treemap");await Layout(window);
                chart.Choose(0,true);await Layout(window);var detail=StabilityProbe.Field<UsageDetails>(panel,"trendDetail");
                Check(chart.LayoutCells.Length==1&&chart.LayoutCells[0].Tokens==150&&MetricText(detail,"TrendSelectedDayTokens")=="150","uppercase UNKNOWN tile detail includes all 150 tokens including 50 unclassified tokens");
                panel.Apply(new UsageSnapshot{Daily=new[]{new DailyUsage{Date=today,Tokens=50}}},"residual-fixture");await Layout(window);chart.Choose(0,true);await Layout(window);
                var body=StabilityProbe.Field<StackPanel>(detail,"body");
                Check(MetricText(detail,"TrendSelectedDayTokens")=="50"&&body.Children.OfType<TextBlock>().Any(t=>t.Text=="已记录用量，但日志未提供型号信息"),"positive unclassified detail reports missing model metadata instead of no usage");
                detail.Apply(new DailyUsage{Date=today},true,"用量记录");
                Check(body.Children.OfType<TextBlock>().Any(t=>t.Text=="该时段暂无已记录用量"),"truly empty details still report no recorded usage");
            }
            finally{window.Close();}
        }

        private static async Task Captures(string root)
        {
            foreach(string mode in new[]{"light","dark"})foreach(bool cost in new[]{false,true})
            {
                Theme.Apply(new Preferences{ThemeMode=mode,OrbAnimation="off"});var panel=new HistoryPanel();panel.Configure(false,true,30);
                // Only the capture fixture hides the independent cache card so the
                // image concentrates on the actual history metric and view controls.
                StabilityProbe.Field<Border>(panel,"cacheCard").Visibility=Visibility.Collapsed;
                panel.Apply(Fixture(),"模拟数据 · Token / API 估算视图");panel.SetMetric(cost);panel.SetTrendView(cost?"treemap":"bars");
                var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=panel};
                var window=new Window{Width=1060,Height=880,ShowActivated=false,ShowInTaskbar=false,Background=Theme.Background,Content=new Border{Background=Theme.Background,Padding=new Thickness(18),Child=scroll}};
                try
                {
                    window.Show();await Layout(window);scroll.ScrollToTop();await Layout(window);
                    ModelShareStabilityProbe.Capture(window,Path.Combine(root,"usage-"+(cost?"cost-treemap":"token-bars")+"-"+mode+".png"));
                }
                finally{window.Close();}
            }
        }

        internal static async Task Run(string root)
        {
            Theme.Apply(new Preferences{ThemeMode="dark",OrbAnimation="off"});RendererChecks();await PanelChecks();await RegressionChecks();await Captures(root);
        }
    }
}
