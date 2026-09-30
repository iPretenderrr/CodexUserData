using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUserData
{
    internal static class ModelShareViewsStabilityProbe
    {
        private static void Check(bool value,string text){StabilityProbe.Check(value,"Share views: "+text);}
        private static DailyUsage Day(string date,long a,long b)
        {return new DailyUsage{Date=date,Tokens=a+b,Models=new List<ModelUsage>{new ModelUsage{Model="gpt-6-sol",Tokens=a},new ModelUsage{Model="gpt-6.1-sol",Tokens=b}}};}
        private static Dictionary<string,long> Totals(DailyUsage[] days)
        {
            var totals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            foreach(var day in days)foreach(var part in ModelShareValues.Tokens(day)){long prior;totals.TryGetValue(part.Key,out prior);totals[part.Key]=prior+part.Value;}return totals;
        }
        private static void Draw(ModelShareChart chart,double width)
        {
            chart.Measure(new Size(width,350));chart.Arrange(new Rect(0,0,width,350));
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())StabilityProbe.Call(chart,"OnRender",dc);
        }
        private static UsageSnapshot Demo()
        {
            string[] names={"gpt-6-astra","gpt-6-sol","gpt-6.1-sol","gpt-5.6-terra","gpt-6-luna","gpt-5.6-luna","gpt-5.3-codex","gpt-5.3-codex-spark","gpt-5.4","codex-auto-review"};
            var days=DailyUsage.Empty(new DateTime(2026,9,30),180);
            for(int d=0;d<days.Length;d++)for(int m=0;m<names.Length;m++){long n=(long)((11-m)*(1000000+350000*Math.Sin(d*.17+m)));days[d].Models.Add(new ModelUsage{Model=names[m],Tokens=n});days[d].Tokens+=n;}
            return new UsageSnapshot{Daily=days,Hourly=new DailyUsage[0],Timeline=new DailyUsage[0]};
        }
        internal static async Task Run(string root)
        {
            Theme.Apply(new Preferences{ThemeMode="dark",OrbAnimation="off"});
            var raw=new[]{Day("2026-09-01",900,100),Day("2026-09-02",0,100),Day("2026-09-03",0,0)};var totals=Totals(raw);
            var chart=new ModelShareChart();chart.SetData(raw,totals,"bars",false,false);Draw(chart,800);var cells=chart.LayoutCells;
            Check(cells.Length==3&&cells.All(c=>c.Bounds.Width>0&&c.Bounds.Height>0),"zero buckets stay empty and positive portions produce finite rectangles");
            var bucket=cells.First(c=>c.Bucket.Date=="2026-09-01");Check(bucket.Total==1000&&Math.Abs(100.0*bucket.Tokens/bucket.Total-90)<.001,"stacked bar uses each bucket's token denominator");
            Check(chart.Hit(new Point(bucket.Bounds.Left+bucket.Bounds.Width/2,bucket.Bounds.Top+bucket.Bounds.Height/2))>=0,"bar hit testing reaches the painted segment");
            int picks=0;chart.Pick+=delegate{picks++;};chart.Choose(0,false);Check(picks==0&&chart.SelectedBucket==null,"hover never pins a bucket or emits a detail event");chart.Choose(0,true);Check(picks==1&&chart.SelectedBucket!=null,"click pins the displayed bucket");
            int builds=chart.GeometryBuilds;chart.Highlight("gpt-6-sol");Draw(chart,800);chart.SetData(raw,totals,"bars",false,false);Draw(chart,800);Check(chart.GeometryBuilds==builds,"highlight and identical snapshot reuse layout");
            var refreshed=new[]{Day("2026-09-01",1200,300),raw[1],raw[2]};chart.SetData(refreshed,Totals(refreshed),"bars",false,false);Draw(chart,800);Check(chart.SelectedBucket!=null&&chart.SelectedBucket.Tokens==1500,"refresh updates pinned bucket values without keeping a stale object");

            var dense=Enumerable.Range(0,2000).Select(i=>Day(new DateTime(2026,9,1).AddMinutes(i).ToString("yyyy-MM-dd HH:mm"),i%2==0?900:0,100)).ToArray();
            chart.SetData(dense,Totals(dense),"bars",false,false);Draw(chart,300);
            var buckets=chart.LayoutCells.Select(c=>c.Bucket).Distinct().ToArray();Check(buckets.Length<150&&buckets.Sum(b=>b.Tokens)==dense.Sum(b=>b.Tokens),"dense timeline is bounded by viewport and compression conserves all tokens");
            Check(buckets.All(b=>b.Models.Sum(m=>m.Tokens)==b.Tokens)&&buckets.Any(b=>b.Tokens>1000),"compressed daily bar aggregates raw counts rather than averaging percentages");
            var cumulative=ModelShareValues.Aggregate(dense,"cumulative");chart.SetData(cumulative,Totals(dense),"bars",true,false);Draw(chart,300);buckets=chart.LayoutCells.Select(c=>c.Bucket).Distinct().ToArray();Check(buckets.Last().Tokens==dense.Sum(b=>b.Tokens)&&buckets.All(b=>b.Tokens<=dense.Sum(d=>d.Tokens)),"cumulative compression takes endpoints instead of summing running totals");

            chart.SetData(cumulative,totals,"treemap",true,false);Draw(chart,800);cells=chart.LayoutCells;
            Check(cells.Length==2&&cells.Sum(c=>c.Tokens)==1100&&cells.All(c=>c.Total==1100),"treemap consumes raw interval totals even when timeline is cumulative");
            double area=cells.Sum(c=>c.Bounds.Width*c.Bounds.Height);var first=cells.Single(c=>c.Model=="gpt-6-sol");Check(Math.Abs(first.Bounds.Width*first.Bounds.Height/area-900.0/1100)<.025,"treemap area represents token share within separator tolerance");
            Check(!cells[0].Bounds.IntersectsWith(cells[1].Bounds)||Rect.Intersect(cells[0].Bounds,cells[1].Bounds).Width*Rect.Intersect(cells[0].Bounds,cells[1].Bounds).Height<.01,"treemap model rectangles do not overlap");
            var many=Enumerable.Range(0,120).ToDictionary(i=>"fixture-model-"+i,i=>(long)(i+1));chart.SetData(new DailyUsage[0],many,"treemap",false,false);Draw(chart,300);Check(chart.LayoutCells.Length==120&&chart.LayoutCells.Sum(c=>c.Tokens)==many.Values.Sum(),"small models retain their own area instead of being silently removed");
            Check(chart.LayoutCells.All(c=>c.Bounds.Left>=0&&c.Bounds.Top>=0&&c.Bounds.Right<=300.01&&c.Bounds.Bottom<=350.01),"narrow tree geometry stays inside the viewport");
            chart.ClearPointer();chart.SetData(new DailyUsage[0],new Dictionary<string,long>(),"treemap",false,false);Draw(chart,300);Check(chart.LayoutCells.Length==0&&chart.SelectedBucket==null,"empty range clears geometry and stale selection");

            int calls=0;var snapshot=Demo();var panel=new ModelSharePanel((a,b,c)=>{calls++;return Task.FromResult(snapshot);},()=>"views-fixture");panel.Apply(snapshot,"views-fixture","模拟数据");
            var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=panel};
            var window=new Window{Width=1060,Height=900,ShowActivated=false,ShowInTaskbar=false,Content=new Border{Background=Theme.Background,Padding=new Thickness(18),Child=scroll}};
            try
            {
                window.Show();window.UpdateLayout();await Task.Delay(40);panel.SelectRange(30);panel.SetView("bars");window.UpdateLayout();await Task.Delay(40);
                var alternative=StabilityProbe.Field<ModelShareChart>(panel,"alternative");var curve=StabilityProbe.Field<UsageChart>(panel,"chart");var detail=StabilityProbe.Field<Border>(panel,"detail");
                Check(alternative.IsVisible&&!curve.IsVisible&&calls==0,"view switch displays stacked bars without a data read");
                var views=StabilityProbe.Field<Dictionary<string,Button>>(panel,"views");Check(views.Count==3&&views["bars"].Background==Theme.Hover,"three view buttons expose the current selection");
                long rangeTotal=snapshot.Daily.Skip(150).Sum(d=>d.Tokens);Check(StabilityProbe.Field<Dictionary<string,long>>(panel,"viewTotals").Values.Sum()==rangeTotal,"all views use exactly the selected interval");
                alternative.Choose(0,false);Check(detail.Visibility==Visibility.Collapsed,"panel hover keeps lower detail hidden");alternative.Choose(0,true);Check(detail.Visibility==Visibility.Visible,"panel bar click opens lower detail");
                panel.Visibility=Visibility.Collapsed;panel.Visibility=Visibility.Visible;window.UpdateLayout();await Task.Delay(30);Check(alternative.SelectedBucket!=null&&detail.Visibility==Visibility.Visible&&calls==0,"hiding and restoring the page retains a pinned bar detail without reading again");panel.ClearSelection();
                panel.SetAggregation("weekly");window.UpdateLayout();await Task.Delay(30);Check(StabilityProbe.Field<DailyUsage[]>(panel,"viewBuckets").Length<=6&&calls==0,"weekly bars reuse correctly aggregated in-memory data");
                panel.SetAggregation("cumulative");panel.SetView("treemap");window.UpdateLayout();await Task.Delay(30);Check(alternative.LayoutCells.Sum(c=>c.Tokens)==rangeTotal&&StabilityProbe.Field<WrapPanel>(panel,"aggregationChoices").Visibility==Visibility.Collapsed,"tree does not double-count cumulative values and hides irrelevant aggregation buttons");
                alternative.Choose(0,true);string tileModel=alternative.LayoutCells[0].Model;
                var treeRanking=StabilityProbe.Field<WrapPanel>(panel,"ranking");var other=treeRanking.Children.OfType<Button>().First(b=>(string)b.Tag!=tileModel);string rankedModel=(string)other.Tag;
                int treeBuilds=alternative.GeometryBuilds;other.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(StabilityProbe.Field<string>(alternative,"selectedModel")==rankedModel&&alternative.LayoutCells[StabilityProbe.Field<int>(alternative,"selected")].Model==rankedModel&&StabilityProbe.Field<StackPanel>(panel,"detailRows").Children.OfType<TextBlock>().First().Text.StartsWith(rankedModel+" · "),"ranking selection moves the selected tile outline and detail to the same model");
                window.Width=900;window.UpdateLayout();await Task.Delay(30);var selectedCells=alternative.LayoutCells;
                Check(selectedCells[StabilityProbe.Field<int>(alternative,"selected")].Model==rankedModel,"ranking-selected model stays selected when the treemap resizes");
                treeBuilds=alternative.GeometryBuilds;other.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(StabilityProbe.Field<int>(alternative,"selected")==-1&&detail.Visibility==Visibility.Collapsed&&alternative.HighlightedModel==null&&alternative.GeometryBuilds==treeBuilds,"clicking the selected ranking clears tile, highlight and details without rebuilding geometry");window.Width=1060;
                panel.SetView("bars");Check(StabilityProbe.Field<string>(panel,"aggregation")=="cumulative"&&StabilityProbe.Field<WrapPanel>(panel,"aggregationChoices").Visibility==Visibility.Visible,"leaving tree restores the prior temporal aggregation");
                panel.SetAggregation("daily");
                panel.SelectRange(180);window.UpdateLayout();await Task.Delay(30);alternative.Choose(0,true);Check(alternative.SelectedBucket!=null&&detail.Visibility==Visibility.Visible,"wide 180-day chart can pin an individual day");window.Width=370;window.UpdateLayout();await Task.Delay(30);Check(alternative.SelectedBucket==null&&detail.Visibility==Visibility.Collapsed,"resize that merges the selected day also clears its stale lower detail");window.Width=1060;panel.SelectRange(30);window.UpdateLayout();
                foreach(string theme in new[]{"light","dark"})foreach(string view in new[]{"bars","treemap"})
                {Theme.Apply(new Preferences{ThemeMode=theme,OrbAnimation="off"});panel.SetView(view);window.UpdateLayout();await Task.Delay(40);ModelShareStabilityProbe.Capture(window,Path.Combine(root,"model-share-"+view+"-"+theme+".png"));}
                window.Width=370;window.UpdateLayout();await Task.Delay(40);ModelShareStabilityProbe.Capture(window,Path.Combine(root,"model-share-views-narrow.png"));
                var ranking=StabilityProbe.Field<WrapPanel>(panel,"ranking");Check(ranking.Children.OfType<Button>().All(b=>b.ActualWidth<=panel.ActualWidth+1)&&alternative.ActualWidth<=panel.ActualWidth+1,"narrow view keeps chart and model ranking within available width");
                panel.SetView("curve");Check(curve.IsVisible&&!alternative.IsVisible&&StabilityProbe.Field<WrapPanel>(panel,"legend").Visibility==Visibility.Visible&&calls==0,"return to curve restores original pills without any data reads");
                panel.SetView("treemap");panel.Apply(null,"new-source","新来源");window.UpdateLayout();Check(alternative.LayoutCells.Length==0&&ranking.Children.OfType<Button>().Count()==0,"source change immediately removes all old-source tiles and ranks");
            }
            finally{panel.Dispose();window.Close();}
        }
    }
}
