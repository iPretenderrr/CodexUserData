using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    internal static class TokenText
    {
        // Truncate at 万. Only the home hero explicitly opts into the exact integer.
        internal static string Compact(long value)
        {
            if(value<10000)return value.ToString(CultureInfo.InvariantCulture);
            long yi=value/100000000,wan=value%100000000/10000;
            if(yi>0)return yi+"亿"+(wan>0?wan+"万":"");
            long units=value/10000;return units>=1000&&units%1000==0?(units/1000)+"千万":units+"万";
        }
        internal static string Exact(long value){return value.ToString(CultureInfo.InvariantCulture);}
        internal static string Full(long value){return Compact(value);}
        internal static string Axis(double value)
        {
            if(value>=1e8)return (value/1e8).ToString("0.#",CultureInfo.InvariantCulture)+"亿";
            if(value>=1e4)return (value/1e4).ToString("0.#",CultureInfo.InvariantCulture)+"万";
            return value.ToString("0.#",CultureInfo.InvariantCulture);
        }
    }

    internal static class ChartValue
    {
        internal static decimal Of(DailyUsage day,bool cost){return cost?day.Models.Sum(m=>m.EquivalentUsd):day.Tokens;}
        internal static string Money(decimal value)
        {return "$"+(value>0&&value<.0001m?value.ToString("0.##E+0",CultureInfo.InvariantCulture):value.ToString(value>0&&value<.01m?"0.######":"N2",CultureInfo.InvariantCulture));}
        internal static string Axis(double value,bool cost)
        {
            if(!cost)return TokenText.Axis(value);
            if(value==0)return "$0";
            if(value>=1000000)return "$"+(value/1000000).ToString("0.#",CultureInfo.InvariantCulture)+"M";
            if(value>=1000)return "$"+(value/1000).ToString("0.#",CultureInfo.InvariantCulture)+"k";
            return "$"+value.ToString(value<.0001?"0.##E+0":value<.01?"0.######":"0.##",CultureInfo.InvariantCulture);
        }
    }
    internal sealed class ChartComparison
    {
        internal bool Available;
        internal decimal Current,Previous;
        internal double? ChangePercent;
        internal string Message;
        internal static ChartComparison Calculate(DailyUsage[] records,int days,DateTime today,bool cost,bool incomplete)
        {
            var result=new ChartComparison();
            if(days<=1){result.Message="仅有今日小时数据，暂不提供跨日对比。";return result;}
            if(days>60){result.Message="当前保留近 180 日图表数据；完整周期对比支持 7 / 14 / 30 / 60 天，90 / 180 天仍可查看趋势。";return result;}
            if(incomplete){result.Message="部分记录未计入，暂不计算周期差值。";return result;}
            var byDate=new Dictionary<DateTime,DailyUsage>();
            foreach(var day in records??new DailyUsage[0]){DateTime date;if(DateTime.TryParseExact(day.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date))byDate[date]=day;}
            DateTime end=today.Date.AddDays(-1),start=end.AddDays(1-days),previousStart=start.AddDays(-days);
            // Require every daily aggregate. Never synthesize zero for an absent date; a
            // present zero aggregate means no usage in the currently read records, not proof
            // that the source contains all historical logs.
            for(DateTime date=previousStart;date<=end;date=date.AddDays(1))
            {
                DailyUsage day;if(!byDate.TryGetValue(date,out day)){result.Message="现有日期记录不足，需要 "+(days*2)+" 个完整日（不含今日）。";return result;}
                if(date<start)result.Previous+=ChartValue.Of(day,cost);else result.Current+=ChartValue.Of(day,cost);
            }
            result.Available=true;
            string current=start.ToString("MM/dd")+"–"+end.ToString("MM/dd"),previous=previousStart.ToString("MM/dd")+"–"+start.AddDays(-1).ToString("MM/dd");
            string unit=cost?" USD":" Tokens";
            Func<decimal,string> amount=v=>cost?ChartValue.Money(v):TokenText.Compact((long)v);
            string change;
            if(result.Previous==0)change=result.Current==0?"两个周期均为 0":"前期为 0，新增 "+amount(result.Current)+unit+"；不计算百分比";
            else{result.ChangePercent=((double)result.Current/(double)result.Previous-1)*100;change=(result.ChangePercent>0?"增加 ":result.ChangePercent<0?"减少 ":"持平 ")+Math.Abs(result.ChangePercent.Value).ToString("0.#",CultureInfo.InvariantCulture)+"%";}
            result.Message=current+"："+amount(result.Current)+unit+"\n"+previous+"："+amount(result.Previous)+unit+"\n"+change;return result;
        }
    }

    // One lightweight drawing surface per graph, with arithmetic hit testing.
    // Hundreds of squares/points do not require hundreds of WPF controls or timers.
    internal sealed class UsageChart : FrameworkElement
    {
        internal bool IsHeatmap;
        internal DailyUsage[] Days=new DailyUsage[0];
        internal int WindowDays=30;
        internal bool IsHourly;
        internal bool IsCost {get;private set;}
        private int through;
        internal string HighlightedModel {get;private set;}
        internal int GeometryBuilds {get;private set;}
        private sealed class Layer {internal string Model;internal StreamGeometry Area;internal double[] Upper;}
        private readonly List<Layer> layers=new List<Layer>();
        internal void Highlight(string model){if(HighlightedModel==model)return;HighlightedModel=model;AutomationProperties.SetItemStatus(this,model==null?"全部模型":"高亮："+model);InvalidateVisual();}
        internal int Selected=-1,Hovered=-1;
        internal DateTime HoverPausedUntil;
        private Point lastPointer=new Point(Double.NaN,Double.NaN);
        internal event Action<int,bool> Pick;
        internal event Action Leave;
        private readonly ToolTip tip=new ToolTip{Background=Theme.Surface,Foreground=Theme.Ink,BorderBrush=Theme.Line,Padding=new Thickness(10),MaxWidth=360};
        private readonly Brush[] levels=Theme.HeatLevels;
        private int themeRevision=-1;
        private Rect plot;
        private DrawingGroup cachedDrawing;
        private Size cachedSize;
        private string signature;
        private readonly Dictionary<string,double[]> series=new Dictionary<string,double[]>();
        internal static string Signature(DailyUsage[] days)
        {
            return String.Join("|",(days??new DailyUsage[0]).Select(d=>d.Date+":"+d.Tokens+":"+d.Requests+":"+d.Input+":"+d.Output+":"+d.CacheRead+":"+d.CacheWrite+":"+String.Join(",",d.Models.Select(m=>m.Model+"/"+m.Effort+"/"+m.Tokens+"/"+m.EquivalentUsd))));
        }
        private int offset,columns,first;
        private double pitch,cell,max=1;
        internal UsageChart(bool heatmap)
        {
            Theme.Watch(this);
            IsHeatmap=heatmap;Height=heatmap?160:190;Focusable=true;ClipToBounds=true;Cursor=Cursors.Cross;ToolTip=tip;
            AutomationProperties.SetName(this,heatmap?"每日用量热度图":"每日 Token 趋势图");
            MouseMove+=delegate(object s,MouseEventArgs e)
            {
                if(DateTime.UtcNow<HoverPausedUntil)return;
                var screen=PointToScreen(e.GetPosition(this));
                // Scrolling/layout can synthesize MouseMove at the same screen pixel. It is not a new hover.
                if(screen==lastPointer)return;lastPointer=screen;Choose(Hit(e.GetPosition(this)),false);
            };
            MouseLeave+=delegate{Hovered=-1;tip.IsOpen=false;InvalidateVisual();if(Leave!=null)Leave();};
            MouseLeftButtonDown+=delegate(object s,MouseButtonEventArgs e){Focus();Choose(Hit(e.GetPosition(this)),true);};
            KeyDown+=delegate(object s,KeyEventArgs e)
            {
                if(Days.Length==0)return;int i=Selected>=0?Selected:Days.Length-1;
                if(e.Key==Key.Left)i-=IsHeatmap?7:1;else if(e.Key==Key.Right)i+=IsHeatmap?7:1;else if(e.Key==Key.Up)i--;else if(e.Key==Key.Down)i++;else return;
                Choose(Math.Max(first,Math.Min((IsHourly?through:Days.Length)-1,i)),true);e.Handled=true;
            };
        }
        protected override AutomationPeer OnCreateAutomationPeer(){return new ChartPeer(this);}
        private sealed class ChartPeer : FrameworkElementAutomationPeer
        {
            internal ChartPeer(UsageChart owner):base(owner){}
            protected override string GetClassNameCore(){return "UsageChart";}
            protected override AutomationControlType GetAutomationControlTypeCore(){return AutomationControlType.Custom;}
            protected override bool IsControlElementCore(){return true;}
            protected override bool IsContentElementCore(){return true;}
        }
        internal void SetData(DailyUsage[] days,int count,bool hourly=false,int availableHours=24,bool cost=false)
        {
            string hoveredDate=Hovered>=0&&Hovered<Days.Length?Days[Hovered].Date:null;
            int oldCount=WindowDays;Days=days??new DailyUsage[0];IsHourly=hourly;IsCost=cost;through=hourly?Math.Max(0,Math.Min(Days.Length,availableHours)):Days.Length;string next=hourly+"/"+through+"/"+cost+"/"+Signature(Days);WindowDays=count;
            bool dataChanged=next!=signature;if(!dataChanged&&oldCount==count)return;signature=next;cachedDrawing=null;layers.Clear();
            // A different visible period does not change the underlying model series.
            if(dataChanged)
            {
                series.Clear();foreach(string model in Days.SelectMany(d=>d.Models).Select(m=>m.Model).Distinct().OrderBy(m=>m))series[model]=new double[Days.Length];
                for(int i=0;i<Days.Length;i++)foreach(var m in Days[i].Models)series[m.Model][i]+=cost?(double)m.EquivalentUsd:m.Tokens;
            }
            AutomationProperties.SetName(this,IsHeatmap?(cost?"每日 API 估算费用热度图":"每日用量热度图"):(cost?"API 估算费用趋势 · USD":"Token 用量趋势"));
            if(Selected>=Days.Length)Selected=-1;Hovered=hoveredDate==null?-1:Array.FindIndex(Days,d=>d.Date==hoveredDate);if(Hovered<0)tip.IsOpen=false;else UpdateTip(Days[Hovered]);if(IsHeatmap&&ActualWidth>0)Height=HeatHeight(ActualWidth);InvalidateVisual();
        }
        internal double HeatHeight(double width)
        {
            int n=27;if(Days.Length>0){var start=DateTime.ParseExact(Days[0].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture);n=(int)Math.Ceiling((Days.Length+((int)start.DayOfWeek+6)%7)/7.0);}
            return Math.Max(5,(width-22)/n)*7+51;
        }
        internal void ClearPointer(){Selected=-1;Hovered=-1;tip.IsOpen=false;InvalidateVisual();}
        internal void Choose(int index,bool clicked)
        {
            if(index<0||index>=Days.Length){if(Hovered!=-1){Hovered=-1;tip.IsOpen=false;InvalidateVisual();}return;}
            if(!clicked&&Hovered==index)return;Hovered=index;if(clicked)Selected=index;
            DailyUsage day=Days[index];UpdateTip(day);tip.PlacementTarget=this;tip.Placement=PlacementMode.Mouse;tip.IsOpen=IsMouseOver;
            AutomationProperties.SetHelpText(this,day.Date+"，"+(IsCost?ChartValue.Money(ChartValue.Of(day,true))+" USD · API 估算":TokenText.Full(day.Tokens)+" Tokens"));if(Pick!=null)Pick(index,clicked);InvalidateVisual();
        }
        private void UpdateTip(DailyUsage day){string tokens=TokenText.Compact(day.Tokens)+" Tokens",cost=ChartValue.Money(ChartValue.Of(day,true))+" USD · API 估算";tip.Content=day.Date+"\n"+(IsCost?cost+"\n"+tokens:tokens+"\n"+cost)+"\n点击固定下方明细";}

        internal int Hit(Point p)
        {
            if(Days.Length==0||!plot.Contains(p))return -1;
            if(IsHeatmap)
            {
                int c=(int)((p.X-plot.Left)/pitch),r=(int)((p.Y-plot.Top)/pitch);
                if(c>=columns||r>=7||(p.X-plot.Left)%pitch>cell||(p.Y-plot.Top)%pitch>cell)return -1;
                int i=c*7+r-offset;return i>=0&&i<Days.Length?i:-1;
            }
            if(IsHourly){int hour=(int)((p.X-plot.Left)/plot.Width*24);return hour>=0&&hour<through?hour:-1;}
            int count=Days.Length-first;return Math.Max(first,Math.Min(Days.Length-1,first+(int)Math.Round((p.X-plot.Left)/plot.Width*Math.Max(1,count-1))));
        }
        internal Point PointFor(int index)
        {
            if(IsHeatmap){int position=index+offset;return new Point(plot.Left+(position/7)*pitch+cell/2,plot.Top+(position%7)*pitch+cell/2);}
            return new Point(IsHourly?plot.Left+(index+.5)*plot.Width/24:plot.Left+(index-first)*plot.Width/Math.Max(1,Days.Length-first-1),plot.Bottom-((double)ChartValue.Of(Days[index],IsCost)/max)*plot.Height);
        }
        private void Text(DrawingContext dc,string text,double x,double y,double size,Brush color)
        {
            dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI, Microsoft YaHei UI"),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
            if(Days.Length==0){Text(dc,"等待当前来源的数据",12,40,11,Theme.Muted);return;}
            // Reuse all grid lines, labels, paths and cells for pointer movement. Only the cursor overlay changes.
            var size=new Size(ActualWidth,ActualHeight);
            if(cachedDrawing==null||cachedSize!=size||themeRevision!=Theme.Revision){themeRevision=Theme.Revision;cachedSize=size;cachedDrawing=new DrawingGroup();using(var baseContext=cachedDrawing.Open()){if(IsHeatmap)DrawHeatmap(baseContext);else DrawTrend(baseContext);}cachedDrawing=(DrawingGroup)cachedDrawing.GetCurrentValueAsFrozen();}
            dc.DrawDrawing(cachedDrawing);if(!IsHeatmap)DrawAreas(dc);
            if(IsHeatmap){foreach(int i in new[]{Selected,Hovered})if(i>=0&&i<Days.Length){Point p=PointFor(i);double radius=Math.Min(3.5,cell*.24);dc.DrawRoundedRectangle(null,new Pen(i==Hovered?Theme.Ink:Theme.Accent,1.5),new Rect(p.X-cell/2,p.Y-cell/2,cell,cell),radius,radius);}}
            else
            {
                int active=Hovered>=first?Hovered:Selected>=first?Selected:-1;
                if(active>=first&&active<through){Point point=PointFor(active);dc.DrawLine(new Pen(Theme.Muted,.8),new Point(point.X,plot.Top),new Point(point.X,plot.Bottom));
                    foreach(var layer in layers){if(HighlightedModel!=null&&HighlightedModel!=layer.Model)continue;point.Y=plot.Bottom-layer.Upper[active-first]/max*plot.Height;dc.DrawEllipse(ModelColors.For(layer.Model),new Pen(Theme.Ink,.8),point,3,3);}}
            }
        }
        private void DrawHeatmap(DrawingContext dc)
        {
            first=0;DateTime start=DateTime.ParseExact(Days[0].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture);offset=((int)start.DayOfWeek+6)%7;columns=(int)Math.Ceiling((Days.Length+offset)/7.0);
            pitch=Math.Max(5,(ActualWidth-22)/columns);cell=Math.Max(3,pitch-3);plot=new Rect(20,24,pitch*columns,pitch*7);max=Days.Max(d=>(double)ChartValue.Of(d,IsCost));if(max<=0)max=1;
            int previousMonth=-1;double lastLabel=-100;
            for(int i=0;i<Days.Length;i++)
            {
                Point p=PointFor(i);DateTime date=start.AddDays(i);int column=(i+offset)/7;
                if(date.Month!=previousMonth){if(column*pitch-lastLabel>28){Text(dc,date.Month+"月",20+column*pitch,2,10,Theme.Muted);lastLabel=column*pitch;}previousMonth=date.Month;}
                double amount=(double)ChartValue.Of(Days[i],IsCost);int level=amount<=0?0:Math.Min(4,1+(int)Math.Floor(3.999*Math.Sqrt(amount/max)));
                var rect=new Rect(p.X-cell/2,p.Y-cell/2,cell,cell);double radius=Math.Min(3.5,cell*.24);dc.DrawRoundedRectangle(levels[level],null,rect,radius,radius);
                if(amount>0&&series.Count>0)
                {
                    // Keep model proportions secondary to total usage. Inset the thin strip so it
                    // does not turn every day into a multicolored tile, including at narrow widths.
                    double inset=Math.Min(2,cell*.12),stripe=Math.Max(.8,Math.Min(2,cell*.09));
                    var track=new Rect(rect.Left+inset,rect.Bottom-inset-stripe,cell-2*inset,stripe);
                    dc.PushClip(new RectangleGeometry(track,stripe/2,stripe/2));double x=track.Left;
                    foreach(var pair in series){double part=pair.Value[i];if(part<=0)continue;double width=track.Width*part/amount;dc.DrawRectangle(ModelColors.For(pair.Key),null,new Rect(x,track.Top,width,stripe));x+=width;}
                    dc.Pop();
                }
            }
            Text(dc,"一",0,24,9,Theme.Muted);Text(dc,"四",0,24+3*pitch,9,Theme.Muted);Text(dc,"日",0,24+6*pitch,9,Theme.Muted);
            double y=plot.Bottom+8;Text(dc,"少",20,y,9,Theme.Muted);for(int i=0;i<5;i++)dc.DrawRoundedRectangle(levels[i],null,new Rect(39+i*13,y+2,10,10),2,2);Text(dc,"多",107,y,9,Theme.Muted);if(ActualWidth>350)Text(dc,IsCost?"色阶：估算费用 · 细条：模型占比":"色阶：用量 · 细条：模型占比",140,y,9,Theme.Muted);
        }
        private void DrawTrend(DrawingContext dc)
        {
            first=IsHourly?0:Math.Max(0,Days.Length-WindowDays);int count=through-first;
            double left=IsCost?72:46;plot=new Rect(left,14,Math.Max(1,ActualWidth-left-8),Math.Max(40,ActualHeight-44));
            max=NiceMax(count>0?Days.Skip(first).Take(count).Max(d=>(double)ChartValue.Of(d,IsCost)):0);
            var dashed=new Pen(Theme.Line,.7){DashStyle=new DashStyle(new double[]{3,4},0)};
            for(int line=0;line<=4;line++){double y=plot.Bottom-plot.Height*line/4;dc.DrawLine(dashed,new Point(plot.Left,y),new Point(plot.Right,y));Text(dc,ChartValue.Axis(max*line/4,IsCost),0,y-6,9,Theme.Muted);}
            if(IsHourly)
            {
                foreach(int hour in new[]{0,6,12,18,23}){double x=plot.Left+hour*plot.Width/24;Text(dc,hour.ToString("00")+":00",Math.Min(plot.Right-28,x),plot.Bottom+8,9,Theme.Muted);}
                if(through<24){double x=plot.Left+through*plot.Width/24;dc.DrawRectangle(Theme.Background,null,new Rect(x,plot.Top,plot.Right-x,plot.Height));if(plot.Right-x>80)Text(dc,"尚未发生",x+10,plot.Top+8,10,Theme.Muted);}
            }
            else if(count>0)
            {
                int labels=Math.Max(2,Math.Min(7,(int)(plot.Width/65)));for(int i=0;i<labels;i++){int index=first+(int)Math.Round((count-1)*i/(double)(labels-1));Point point=PointFor(index);Text(dc,Days[index].Date.Substring(5).Replace('-', '/'),Math.Max(plot.Left,Math.Min(plot.Right-31,point.X-15)),plot.Bottom+8,9,Theme.Muted);}
            }
            layers.Clear();GeometryBuilds++;if(count<=0)return;
            double[] lower=new double[count];var names=series.Keys.OrderBy(ModelOrder).ThenBy(m=>m).ToArray();
            foreach(string name in names)
            {
                if(!series[name].Skip(first).Take(count).Any(v=>v>0))continue;
                double[] upper=new double[count];for(int i=0;i<count;i++)upper[i]=lower[i]+series[name][first+i];
                layers.Add(new Layer{Model=name,Upper=upper,Area=Band(upper,lower)});lower=upper;
            }
            if(layers.Count==0&&Days.Skip(first).Take(count).Any(d=>ChartValue.Of(d,IsCost)>0)){double[] total=Days.Skip(first).Take(count).Select(d=>(double)ChartValue.Of(d,IsCost)).ToArray();layers.Add(new Layer{Model="unknown",Upper=total,Area=Band(total,new double[count])});}
        }
        private static int ModelOrder(string name){switch(name){case "gpt-6-astra":return 0;case "gpt-5.6-sol":case "gpt-5.6":return 1;case "gpt-5.5":return 2;case "gpt-5.6-terra":return 3;case "gpt-5.6-luna":return 4;default:return 5;}}
        private void DrawAreas(DrawingContext dc)
        {
            // Hover changes opacity only. Shared frozen paths keep highlighting fast even at 180 days.
            foreach(var layer in layers){bool active=HighlightedModel==layer.Model;dc.PushOpacity(HighlightedModel==null?.92:active?1:.15);dc.DrawGeometry(ModelColors.For(layer.Model),active?new Pen(Theme.Ink,1.1):null,layer.Area);dc.Pop();}
        }
        private StreamGeometry Band(double[] upper,double[] lower)
        {
            Func<double[],Point[]> points=values=>
            {
                var list=new List<Point>();for(int i=0;i<values.Length;i++){var p=PointFor(first+i);p.Y=plot.Bottom-values[i]/max*plot.Height;list.Add(p);}
                if(IsHourly){list.Insert(0,new Point(plot.Left,list[0].Y));list.Add(new Point(plot.Left+through*plot.Width/24,list[list.Count-1].Y));}return list.ToArray();
            };
            Point[] top=points(upper),bottom=points(lower);var shape=new StreamGeometry();
            using(var context=shape.Open())
            {
                context.BeginFigure(top[0],true,true);for(int i=1;i<top.Length;i++)Segment(context,top[i-1],top[i]);
                context.LineTo(bottom[bottom.Length-1],true,false);for(int i=bottom.Length-2;i>=0;i--)Segment(context,bottom[i+1],bottom[i]);
            }
            shape.Freeze();return shape;
        }
        private static void Segment(StreamGeometryContext context,Point start,Point end)
        {
            // The same nonnegative smoothstep weights are used at every boundary. Layers cannot cross or create negative usage.
            double dx=(end.X-start.X)/3;context.BezierTo(new Point(start.X+dx,start.Y),new Point(end.X-dx,end.Y),end,true,false);
        }
        internal static double NiceMax(double value)
        {
            if(value<=0)return 4;double power=Math.Pow(10,Math.Floor(Math.Log10(value)));double n=value/power;return (n<=1?1:n<=2?2:n<=4?4:n<=6?6:n<=8?8:10)*power;
        }
    }

    // Each chart owns its pinned date, hover state and coalescing timer; refreshing one cannot overwrite the other.
    internal sealed class ChartDetailSelection
    {
        private readonly UsageChart chart;
        private readonly UsageDetails detail;
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(75)};
        private DailyUsage[] points=new DailyUsage[0];
        private int first,end;
        private string selected,hovered,countLabel,ignoredHover;
        private readonly bool automaticDefault,hoverPreview;
        internal void PauseForScroll(){timer.Stop();chart.HoverPausedUntil=DateTime.UtcNow.AddMilliseconds(350);}
        internal ChartDetailSelection(UsageChart graph,UsageDetails view,bool selectLatest=true,bool previewOnHover=true)
        {
            chart=graph;detail=view;automaticDefault=selectLatest;hoverPreview=previewOnHover;
            graph.Pick+=Pick;graph.Leave+=delegate{if(!hoverPreview)return;if(DateTime.UtcNow<chart.HoverPausedUntil)return;hovered=null;ignoredHover=null;Show();};
            graph.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape&&!automaticDefault){Clear();e.Handled=true;}};
            timer.Tick+=delegate{timer.Stop();Show();};
            graph.Unloaded+=delegate{timer.Stop();hovered=null;};
        }
        internal void Reset(){timer.Stop();selected=null;hovered=null;ignoredHover=null;chart.ClearPointer();}
        internal void Clear()
        {
            string previous=chart.Hovered>=0&&chart.Hovered<points.Length?points[chart.Hovered].Date:null;
            Reset();ignoredHover=previous;Show();
        }
        internal void SetData(DailyUsage[] data,int start,int through,string label,bool reset=false)
        {
            if(reset)Reset();points=data??new DailyUsage[0];first=Math.Max(0,Math.Min(points.Length,start));end=Math.Max(first,Math.Min(points.Length,through));countLabel=label;Show();
        }
        private int Index(string date){return date==null?-1:Array.FindIndex(points,first,end-first,d=>d.Date==date);}
        private void Pick(int index,bool clicked)
        {
            // Trend hover only paints the chart/tooltip; expensive detail changes require explicit selection.
            if(index<first||index>=end||(!clicked&&!hoverPreview))return;
            // Esc must stay cleared while the pointer remains on the same point (including synthetic layout moves).
            if(!clicked&&points[index].Date==ignoredHover){chart.ClearPointer();return;}ignoredHover=null;
            hovered=points[index].Date;
            if(clicked){selected=hovered;Show();}else if(!timer.IsEnabled)timer.Start();
        }
        private void Show()
        {
            timer.Stop();int pinned=Index(selected),active=Index(hovered);
            if(pinned<0)selected=null;if(active<0)hovered=null;
            if(active<0)active=pinned>=0?pinned:automaticDefault?end-1:-1;
            if(active<first){detail.Clear(end>first&&!automaticDefault?"未选择时段 · 点击曲线查看明细":"等待当前来源的数据");chart.Selected=-1;}else{detail.Apply(points[active],points[active].Date==selected,countLabel);chart.Selected=pinned;}
            chart.InvalidateVisual();
        }
    }

    // Reuses 42 day buttons and only repaints when the visible month or selected
    // dates change. This keeps the picker responsive on low-performance devices.
    internal sealed class RangeMonthCalendar : Grid
    {
        private readonly TextBlock monthLabel;
        private readonly List<Button> dayButtons=new List<Button>();
        private DateTime month,from,to,active;
        internal event Action<DateTime> Picked;
        internal RangeMonthCalendar(DateTime start,DateTime end,DateTime selected)
        {
            RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});RowDefinitions.Add(new RowDefinition());
            var header=new Grid{Margin=new Thickness(0,0,0,7)};header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});Children.Add(header);
            var previous=Theme.Button("‹","上个月",34);previous.Height=30;previous.FontSize=18;previous.Padding=new Thickness(0);header.Children.Add(previous);
            monthLabel=Theme.Text("",15,Theme.Ink);monthLabel.FontWeight=FontWeights.SemiBold;monthLabel.HorizontalAlignment=HorizontalAlignment.Center;Grid.SetColumn(monthLabel,1);header.Children.Add(monthLabel);
            var next=Theme.Button("›","下个月",34);next.Height=30;next.FontSize=18;next.Padding=new Thickness(0);Grid.SetColumn(next,2);header.Children.Add(next);
            previous.Click+=delegate{month=month.AddMonths(-1);Render();};next.Click+=delegate{month=month.AddMonths(1);Render();};
            var weekdays=new UniformGrid{Columns=7,Margin=new Thickness(0,0,0,3)};Grid.SetRow(weekdays,1);Children.Add(weekdays);
            foreach(string name in new[]{"日","一","二","三","四","五","六"}){var label=Theme.Text(name,10,Theme.Muted);label.Height=24;label.TextAlignment=TextAlignment.Center;weekdays.Children.Add(label);}
            var days=new UniformGrid{Columns=7,Rows=6};Grid.SetRow(days,2);Children.Add(days);
            for(int i=0;i<42;i++)
            {
                var day=Theme.Button("","选择日期",32);day.Height=34;day.Margin=new Thickness(2);day.Padding=new Thickness(0);day.FontSize=11;day.FocusVisualStyle=null;
                day.Click+=delegate(object sender,RoutedEventArgs e){var value=(DateTime)((Button)sender).Tag;if(Picked!=null)Picked(value);};
                dayButtons.Add(day);days.Children.Add(day);
            }
            AutomationProperties.SetAutomationId(this,"CustomRangeCalendar");SetState(start,end,selected,true);
        }
        internal void SetState(DateTime start,DateTime end,DateTime selected,bool reveal)
        {
            start=start.Date;end=end.Date;selected=selected.Date;
            bool changed=start!=from||end!=to||selected!=active;from=start;to=end;active=selected;
            if(month==default(DateTime)||reveal&&(month.Year!=selected.Year||month.Month!=selected.Month)){month=new DateTime(selected.Year,selected.Month,1);changed=true;}
            if(changed)Render();
        }
        private void Render()
        {
            monthLabel.Text=month.ToString("yyyy年M月",CultureInfo.CurrentCulture);
            DateTime first=new DateTime(month.Year,month.Month,1),begin=first.AddDays(-(int)first.DayOfWeek);DateTime low=from<=to?from:to,high=from<=to?to:from;
            for(int i=0;i<dayButtons.Count;i++)
            {
                DateTime date=begin.AddDays(i);Button button=dayButtons[i];bool inMonth=date.Month==month.Month&&date.Year==month.Year,selected=date==active,endpoint=date==from||date==to,inside=date>=low&&date<=high;
                button.Tag=date;button.Content=date.Day.ToString(CultureInfo.InvariantCulture);button.Opacity=inMonth?1:.38;button.Foreground=selected?Theme.OnAccent:inMonth?Theme.Ink:Theme.Muted;button.Background=selected?Theme.Accent:inside?Theme.Hover:Brushes.Transparent;
                button.BorderBrush=endpoint||date==DateTime.Today?Theme.Accent:Theme.Line;button.BorderThickness=selected?new Thickness(0):endpoint||date==DateTime.Today?new Thickness(1):new Thickness(0);button.Resources["ThemeHover"]=selected?Theme.Accent:Theme.Hover;
                AutomationProperties.SetName(button,date.ToString("yyyy年M月d日",CultureInfo.CurrentCulture));
            }
        }
    }

    internal sealed class DateTimeRangePicker : Grid
    {
        private readonly Border startCard,endCard;
        private readonly TextBlock startTitle,endTitle,startDate,endDate,error;
        private readonly TextBox startTime,endTime;
        private readonly CheckBox followNow;
        private readonly RangeMonthCalendar calendar;
        private readonly DispatcherTimer clock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private DateTime from,to;
        private bool editingStart=true;
        internal readonly Button CancelButton,ApplyButton;
        internal DateTimeRangePicker(DateTime initialFrom,DateTime initialTo)
        {
            from=Second(initialFrom);to=Second(initialTo);Margin=new Thickness(18,0,18,18);
            ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(304)});ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(18)});ColumnDefinitions.Add(new ColumnDefinition());
            var left=new StackPanel();Children.Add(left);var guide=Theme.Text("支持日期与时间 · 精确到秒",11,Theme.Muted);guide.Margin=new Thickness(0,0,0,9);left.Children.Add(guide);
            startCard=TimeCard("开始时间",true,out startTitle,out startDate,out startTime);left.Children.Add(startCard);
            endCard=TimeCard("结束时间",false,out endTitle,out endDate,out endTime);endCard.Margin=new Thickness(0,9,0,0);left.Children.Add(endCard);
            followNow=new CheckBox{Content="结束时间跟随当前时刻",Foreground=Theme.Muted,FontSize=11,Margin=new Thickness(0,11,0,0),Cursor=Cursors.Hand};left.Children.Add(followNow);AutomationProperties.SetAutomationId(followNow,"CustomRangeFollowNow");
            error=Theme.Text("",10,Theme.Warning);error.TextWrapping=TextWrapping.Wrap;error.MinHeight=34;error.Margin=new Thickness(0,8,0,0);left.Children.Add(error);AutomationProperties.SetAutomationId(error,"CustomRangeError");
            var actions=new Grid{Margin=new Thickness(0,8,0,0)};actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(152)});left.Children.Add(actions);
            CancelButton=Theme.Button("取消","取消自定义时间范围",82);CancelButton.Height=38;CancelButton.Margin=new Thickness(0,0,9,0);actions.Children.Add(CancelButton);
            ApplyButton=Theme.Button("确定","应用自定义时间范围",152);ApplyButton.Height=38;ApplyButton.Background=Theme.Accent;ApplyButton.Foreground=Theme.OnAccent;ApplyButton.Resources["ThemeHover"]=Theme.Accent;Grid.SetColumn(ApplyButton,1);actions.Children.Add(ApplyButton);
            calendar=new RangeMonthCalendar(from,to,from);calendar.Picked+=Pick;var calendarCard=new Border{Background=Theme.Surface,BorderBrush=Theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(13),Padding=new Thickness(12),Child=calendar};Grid.SetColumn(calendarCard,2);Children.Add(calendarCard);
            startCard.PreviewMouseLeftButtonDown+=delegate{Activate(true);};endCard.PreviewMouseLeftButtonDown+=delegate{if(followNow.IsChecked!=true)Activate(false);};startTime.GotKeyboardFocus+=delegate{Activate(true);};endTime.GotKeyboardFocus+=delegate{if(followNow.IsChecked!=true)Activate(false);};
            followNow.Checked+=delegate{editingStart=true;to=Second(DateTime.Now);endTime.Text=to.ToString("HH:mm:ss",CultureInfo.InvariantCulture);endTime.IsReadOnly=true;clock.Start();Refresh(true);};
            followNow.Unchecked+=delegate{clock.Stop();endTime.IsReadOnly=false;Refresh(false);};
            clock.Tick+=delegate{DateTime previous=to;to=Second(DateTime.Now);endDate.Text=to.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture);endTime.Text=to.ToString("HH:mm:ss",CultureInfo.InvariantCulture);if(previous.Date!=to.Date)calendar.SetState(from,to,from,false);};
            Unloaded+=delegate{clock.Stop();};Refresh(true);
        }
        private Border TimeCard(string caption,bool start,out TextBlock title,out TextBlock date,out TextBox time)
        {
            var card=new Border{Background=Theme.Surface,BorderBrush=Theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(11),Padding=new Thickness(12,10,12,11),Cursor=Cursors.Hand};var stack=new StackPanel();card.Child=stack;
            title=Theme.Text(caption,11,Theme.Muted);title.FontWeight=FontWeights.SemiBold;stack.Children.Add(title);
            var row=new Grid{Margin=new Thickness(0,8,0,0)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(92)});stack.Children.Add(row);
            var calendarIcon=Icon(false);calendarIcon.Margin=new Thickness(0,0,7,0);row.Children.Add(calendarIcon);date=Theme.Text("",13,Theme.Ink);Grid.SetColumn(date,1);row.Children.Add(date);
            var clockIcon=Icon(true);clockIcon.Margin=new Thickness(9,0,6,0);Grid.SetColumn(clockIcon,2);row.Children.Add(clockIcon);
            time=Theme.Input((start?from:to).ToString("HH:mm:ss",CultureInfo.InvariantCulture));time.Height=29;time.Padding=new Thickness(6,4,6,4);time.TextAlignment=TextAlignment.Center;time.Background=Theme.Background;Grid.SetColumn(time,3);row.Children.Add(time);
            AutomationProperties.SetAutomationId(card,start?"CustomRangeStart":"CustomRangeEnd");AutomationProperties.SetName(card,caption);return card;
        }
        private static FrameworkElement Icon(bool clockIcon)
        {
            string data=clockIcon?"M 8,2 A 6,6 0 1 1 7.99,2 M 8,5 L 8,8 L 11,10":"M 2,4 L 14,4 L 14,14 L 2,14 Z M 2,7 L 14,7 M 5,2 L 5,5 M 11,2 L 11,5";
            var geometry=Geometry.Parse(data);geometry.Freeze();return new System.Windows.Shapes.Path{Data=geometry,Stroke=Theme.Muted,StrokeThickness=1.4,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=16,Height=16,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center};
        }
        private static DateTime Second(DateTime value)
        {
            if(value.Kind==DateTimeKind.Utc)value=value.ToLocalTime();return new DateTime(value.Year,value.Month,value.Day,value.Hour,value.Minute,value.Second,DateTimeKind.Local);
        }
        private void Activate(bool start){editingStart=start;Refresh(true);}
        private void Pick(DateTime date)
        {
            TimeSpan time;if(editingStart){time=ReadClock(startTime,from.TimeOfDay);from=DateTime.SpecifyKind(date.Date.Add(time),DateTimeKind.Local);}else{time=ReadClock(endTime,to.TimeOfDay);to=DateTime.SpecifyKind(date.Date.Add(time),DateTimeKind.Local);}Refresh(true);
        }
        private void Refresh(bool reveal)
        {
            startDate.Text=from.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture);endDate.Text=to.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture);
            startCard.BorderBrush=editingStart?Theme.Accent:Theme.Line;startCard.BorderThickness=new Thickness(1);startTitle.Foreground=editingStart?Theme.Accent:Theme.Muted;
            endCard.BorderBrush=!editingStart&&followNow.IsChecked!=true?Theme.Accent:Theme.Line;endCard.BorderThickness=new Thickness(1);endTitle.Foreground=!editingStart&&followNow.IsChecked!=true?Theme.Accent:Theme.Muted;endCard.Opacity=followNow.IsChecked==true?.58:1;
            calendar.SetState(from,to,editingStart?from:to,reveal);error.Text="";
        }
        private static TimeSpan ReadClock(TextBox box,TimeSpan fallback)
        {
            DateTime parsed;return DateTime.TryParseExact(box.Text.Trim(),new[]{"H:mm:ss","HH:mm:ss","H:mm","HH:mm"},CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)?parsed.TimeOfDay:fallback;
        }
        internal bool TryRead(out DateTime selectedFrom,out DateTime selectedTo)
        {
            selectedFrom=from;selectedTo=to;DateTime clockFrom,clockTo;string[] formats={"H:mm:ss","HH:mm:ss","H:mm","HH:mm"};
            if(!DateTime.TryParseExact(startTime.Text.Trim(),formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out clockFrom)){error.Text="开始时间格式应为 HH:mm:ss，例如 09:30:00。";return false;}
            if(followNow.IsChecked==true)selectedTo=Second(DateTime.Now);else if(!DateTime.TryParseExact(endTime.Text.Trim(),formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out clockTo)){error.Text="结束时间格式应为 HH:mm:ss，例如 18:00:00。";return false;}else selectedTo=DateTime.SpecifyKind(to.Date.Add(clockTo.TimeOfDay),DateTimeKind.Local);
            selectedFrom=DateTime.SpecifyKind(from.Date.Add(clockFrom.TimeOfDay),DateTimeKind.Local);
            if(selectedTo<selectedFrom){error.Text="结束时间不能早于开始时间。";return false;}if(selectedTo>DateTime.Now.AddSeconds(5)){error.Text="结束时间不能晚于当前时间。";return false;}
            from=selectedFrom;to=selectedTo;error.Text="";return true;
        }
    }

    internal sealed class HistoryPanel : StackPanel
    {
        internal static readonly int[] Periods={1,7,14,30,60,90,180};
        private readonly Border heatCard,trendCard;
        private readonly UsageChart heat,trend;
        private readonly UsageDetails heatDetail=new UsageDetails("Heatmap","热度图 · 每日明细"),trendDetail=new UsageDetails("Trend","曲线图 · 时段明细");
        private readonly ChartDetailSelection heatSelection,trendSelection;
        private readonly TextBlock sourceLabel,sum,average,value,timing,summaryLabel,averageLabel,valueLabel,heatTitle,heatHint,trendTitle,comparisonText;
        private readonly UniformGrid summary;
        private readonly Button tokensMetric,costMetric;
        private readonly Border coverage;
        private readonly TextBlock coverageText;
        private readonly Dictionary<int,Button> ranges=new Dictionary<int,Button>();
        private readonly Button customRangeButton;
        private readonly Dictionary<string,Button> legendItems=new Dictionary<string,Button>();
        private readonly Dictionary<string,string> modelChoices=new Dictionary<string,string>{{"","全部模型"}};
        private readonly ChoiceButton modelFilter;
        private readonly WrapPanel legend=new WrapPanel{Margin=new Thickness(0,6,0,8)};
        private UsageSnapshot original,snapshot;
        private UsageSnapshot customSnapshot;
        private UsageRangeSpec customRange;
        private string customScope;
        private bool customLoading;
        private string modelKey="",viewSignature,pinnedModel;
        private int days=30;
        private bool rangeInitialized;
        private bool cost;
        internal bool ShowCoverage=true;
        internal event Action<int> RangeChanged;
        internal event Action<DateTime,DateTime> CustomRangeRequested;
        internal HistoryPanel()
        {
            Background=Brushes.Transparent;
            sourceLabel=Theme.Text("近 180 天 · 等待数据",10,Theme.Muted);sourceLabel.TextWrapping=TextWrapping.Wrap;sourceLabel.Margin=new Thickness(0,12,0,8);AutomationProperties.SetAutomationId(sourceLabel,"ChartSource");Children.Add(sourceLabel);
            modelFilter=new ChoiceButton(modelChoices,"图表模型筛选");modelFilter.Select("");Children.Add(modelFilter);
            modelFilter.Changed+=SetModel;
            var metricButtons=new WrapPanel{Margin=new Thickness(-2,6,0,0)};Children.Add(metricButtons);
            tokensMetric=Theme.Button("Tokens","按 Tokens 查看图表",74);costMetric=Theme.Button("API 估算 · USD","按 API 估算费用查看图表",132);
            tokensMetric.Margin=costMetric.Margin=new Thickness(2,2,4,2);tokensMetric.FontSize=costMetric.FontSize=11;tokensMetric.Click+=delegate{SetMetric(false);};costMetric.Click+=delegate{SetMetric(true);};metricButtons.Children.Add(tokensMetric);metricButtons.Children.Add(costMetric);
            AutomationProperties.SetAutomationId(tokensMetric,"ChartMetricTokens");AutomationProperties.SetAutomationId(costMetric,"ChartMetricCost");
            var heatBody=new StackPanel();heatCard=Card(heatBody);heatCard.Margin=new Thickness(0,8,0,0);Children.Add(heatCard);
            heatTitle=Theme.Text("每日用量热度图",13,Theme.Ink);heatTitle.TextWrapping=TextWrapping.Wrap;heatBody.Children.Add(heatTitle);heatHint=Theme.Text("色阶表示总量 · 细条表示模型占比",10,Theme.Muted);heatHint.TextWrapping=TextWrapping.Wrap;heatHint.Margin=new Thickness(0,5,0,6);heatBody.Children.Add(heatHint);
            heat=new UsageChart(true);heatBody.Children.Add(heat);heatBody.Children.Add(DetailViewport(heatDetail));
            var trendBody=new StackPanel();trendCard=Card(trendBody);trendCard.Margin=new Thickness(0,9,0,0);Children.Add(trendCard);
            var trendHeader=new DockPanel();trendBody.Children.Add(trendHeader);
            var clearSelection=Theme.Button("清除选择","清除曲线时段选择",78);clearSelection.FontSize=11;clearSelection.Height=27;clearSelection.ToolTip="清除固定时段（Esc）";DockPanel.SetDock(clearSelection,Dock.Right);trendHeader.Children.Add(clearSelection);
            clearSelection.Click+=delegate{trendSelection.Clear();};trendTitle=Theme.Text("Token 用量趋势",13,Theme.Ink);trendTitle.TextWrapping=TextWrapping.Wrap;trendHeader.Children.Add(trendTitle);AutomationProperties.SetAutomationId(trendTitle,"TrendMetricTitle");
            // Wrap period buttons as the window shrinks; a horizontal StackPanel would clip the last options.
            var buttons=new WrapPanel{Margin=new Thickness(-2,9,0,8)};trendBody.Children.Add(buttons);
            foreach(int n in Periods){int v=n;var button=Theme.Button(n==1?"当天":n+"D",n==1?"当天趋势":n+" 天趋势",n==1?44:39);button.Height=27;button.FontSize=11;button.Margin=new Thickness(2,2,2,2);button.Click+=delegate{SetRange(v);if(RangeChanged!=null)RangeChanged(v);};ranges[n]=button;buttons.Children.Add(button);}
            customRangeButton=Theme.Button("自定义","选择开始和结束日期时间",64);customRangeButton.Height=27;customRangeButton.FontSize=11;customRangeButton.Margin=new Thickness(2,2,2,2);customRangeButton.Click+=delegate{OpenCustomRange();};buttons.Children.Add(customRangeButton);
            summary=new UniformGrid{Columns=3,Margin=new Thickness(0,3,0,2)};trendBody.Children.Add(summary);
            sum=Summary(summary,"区间用量",out summaryLabel);average=Summary(summary,"日均",out averageLabel);value=Summary(summary,"API 估算 · USD",out valueLabel);
            AutomationProperties.SetAutomationId(sum,"TrendMetricTotal");AutomationProperties.SetAutomationId(average,"TrendMetricAverage");AutomationProperties.SetAutomationId(value,"TrendSecondaryTotal");
            timing=Theme.Text("色块厚度表示模型用量 · 悬停图例高亮",10,Theme.Muted);timing.TextWrapping=TextWrapping.Wrap;timing.Margin=new Thickness(0,5,0,0);trendBody.Children.Add(timing);
            var comparisonTitle=Theme.Text("完整周期对比 · 不含今日",11,Theme.Ink);comparisonTitle.Margin=new Thickness(0,10,0,4);comparisonTitle.TextWrapping=TextWrapping.Wrap;trendBody.Children.Add(comparisonTitle);
            comparisonText=Theme.Text("等待当前来源的数据",10,Theme.Muted);comparisonText.TextWrapping=TextWrapping.Wrap;comparisonText.LineHeight=16;trendBody.Children.Add(comparisonText);AutomationProperties.SetAutomationId(comparisonText,"TrendComparison");
            trendBody.Children.Add(legend);trend=new UsageChart(false);trendBody.Children.Add(trend);AutomationProperties.SetAutomationId(trend,"TrendChart");AutomationProperties.SetAutomationId(heat,"HeatmapChart");
            trendBody.Children.Add(DetailViewport(trendDetail));
            coverageText=Theme.Text("",10,Theme.Warning);coverageText.TextWrapping=TextWrapping.Wrap;coverageText.Margin=new Thickness(3,7,3,3);
            var coverageBody=new StackPanel();coverageText.Visibility=Visibility.Collapsed;var toggleCoverage=Theme.Button("ⓘ 数据覆盖说明  ⌄","展开数据覆盖说明",30);toggleCoverage.Foreground=Theme.Warning;toggleCoverage.HorizontalAlignment=HorizontalAlignment.Left;toggleCoverage.FontSize=10;
            toggleCoverage.Click+=delegate{coverageText.Visibility=coverageText.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;};coverageBody.Children.Add(toggleCoverage);coverageBody.Children.Add(coverageText);
            coverage=new Border{Child=coverageBody,Margin=new Thickness(2,10,0,0),Visibility=Visibility.Collapsed};Children.Add(coverage);
            heatSelection=new ChartDetailSelection(heat,heatDetail);trendSelection=new ChartDetailSelection(trend,trendDetail,false,false);
            PreviewMouseWheel+=delegate{heatSelection.PauseForScroll();trendSelection.PauseForScroll();};
            SizeChanged+=delegate{heat.Height=heat.HeatHeight(Math.Max(180,ActualWidth-24));trend.Height=ActualWidth<420?280:ActualWidth<760?330:380;summary.Columns=ActualWidth<360?2:3;};SetRange(30);SetMetric(false);
        }
        private static FrameworkElement DetailViewport(UsageDetails detail)
        {
            // One page, one scrollbar. Real-pointer filtering and PauseForScroll keep heatmap
            // hover from selecting a new day merely because the page moved under the cursor.
            return new Border{Child=detail,Margin=new Thickness(0,6,0,0)};
        }
        private static Border Card(UIElement child){return new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Child=child};}
        private static TextBlock Summary(Panel parent,string label,out TextBlock caption)
        {
            var stack=new StackPanel{Margin=new Thickness(0,0,7,0)};caption=Theme.Text(label,9,Theme.Muted);stack.Children.Add(caption);
            var text=Theme.Text("—",14,Theme.Ink);text.FontWeight=FontWeights.SemiBold;text.TextTrimming=TextTrimming.CharacterEllipsis;text.Margin=new Thickness(0,5,0,3);stack.Children.Add(text);parent.Children.Add(stack);return text;
        }
        internal void SetRange(int count)
        {
            int next=Periods.Contains(count)?count:30;if(rangeInitialized&&days==next&&customRange==null)return;rangeInitialized=true;if(days!=next||customRange!=null)trendSelection.Reset();
            days=next;customRange=null;customSnapshot=null;customScope=null;customLoading=false;customRangeButton.Content="自定义";foreach(var pair in ranges){pair.Value.Foreground=pair.Key==days?Theme.Accent:Theme.Muted;pair.Value.Background=pair.Key==days?Theme.Hover:Brushes.Transparent;}customRangeButton.Foreground=Theme.Muted;customRangeButton.Background=Brushes.Transparent;
            // The selected preset changes the visible window even when the source snapshot
            // is unchanged, so force the lightweight view pass to update totals and details.
            viewSignature=null;ApplyView(original,sourceLabel.Tag as string);
        }
        internal void SetModel(string model)
        {modelKey=modelChoices.ContainsKey(model??"")?model??"":"";viewSignature=null;ApplyView(customRange!=null&&customSnapshot!=null?customSnapshot:original,sourceLabel.Tag as string);}
        internal void SetMetric(bool showCost)
        {
            cost=showCost;tokensMetric.Background=cost?Brushes.Transparent:Theme.Hover;tokensMetric.Foreground=cost?Theme.Muted:Theme.Accent;costMetric.Background=cost?Theme.Hover:Brushes.Transparent;costMetric.Foreground=cost?Theme.Accent:Theme.Muted;
            AutomationProperties.SetItemStatus(tokensMetric,cost?"未选中":"已选中");AutomationProperties.SetItemStatus(costMetric,cost?"已选中":"未选中");
            heatTitle.Text=cost?"每日 API 估算费用 · USD":"每日用量热度图";heatHint.Text=cost?"色阶表示估算费用 · 非实际账单 · 缺价格按 0":"色阶表示 Tokens · 细条表示模型占比";
            trendTitle.Text=cost?"API 估算费用趋势 · USD":"Token 用量趋势";
            heat.SetData(snapshot==null?null:snapshot.Daily,180,false,24,cost);UpdateTrend();
        }
        internal void Configure(bool showHeat,bool showTrend,int count)
        {
            heatCard.Visibility=showHeat?Visibility.Visible:Visibility.Collapsed;trendCard.Visibility=showTrend?Visibility.Visible:Visibility.Collapsed;Visibility=showHeat||showTrend?Visibility.Visible:Visibility.Collapsed;SetRange(count);
        }
        private static DailyUsage[] Filter(DailyUsage[] data,string model)
        {
            if(model=="")return data;var filtered=new List<DailyUsage>();
            foreach(var d in data){var day=new DailyUsage{Date=d.Date};foreach(var m in d.Models.Where(m=>m.Model==model)){day.Add(m.Input,m.Output,m.CacheRead,m.CacheWrite,m.Requests,0,0);day.Models.Add(m);}filtered.Add(day);}return filtered.ToArray();
        }
        internal void Apply(UsageSnapshot data,string scope)
        {
            bool changed=!String.Equals(scope,sourceLabel.Tag as string,StringComparison.Ordinal);original=data;
            // A refresh with no snapshot means the selected source is being rebuilt.
            // Clear a previous custom result here so it cannot survive a source/app switch.
            if(changed||data==null){customRange=null;customSnapshot=null;customScope=null;customLoading=false;customRangeButton.Content="自定义";customRangeButton.Foreground=Theme.Muted;customRangeButton.Background=Brushes.Transparent;}
            ApplyView(customRange!=null&&customSnapshot!=null&&String.Equals(customScope,scope,StringComparison.Ordinal)?customSnapshot:data,scope);
        }
        internal void BeginCustomRange(DateTime from,DateTime to,string scope)
        {
            customRange=UsageRangeSpec.Create(from,to);customScope=scope;customSnapshot=null;customLoading=true;snapshot=null;viewSignature=null;trendSelection.Reset();sourceLabel.Tag=scope;customRangeButton.Content="读取中…";customRangeButton.Foreground=Theme.Accent;customRangeButton.Background=Theme.Hover;sourceLabel.Text=scope+" · "+customRange.Label+" · 读取中";comparisonText.Text="正在读取自定义时间范围…";heatDetail.Clear("正在读取自定义时间范围…");trendDetail.Clear("正在读取自定义时间范围…");heat.SetData(null,180,false,24,cost);heatSelection.SetData(new DailyUsage[0],0,0,"用量记录");UpdateTrend();
        }
        internal void ApplyCustomRange(UsageSnapshot data,string scope,DateTime from,DateTime to)
        {
            customRange=UsageRangeSpec.Create(from,to);customScope=scope;customSnapshot=data;customLoading=false;customRangeButton.Content="自定义";customRangeButton.Foreground=Theme.Accent;customRangeButton.Background=Theme.Hover;ApplyView(data,scope);
        }
        internal bool MatchesCustomRange(DateTime from,DateTime to,string scope)
        {
            return customRange!=null&&String.Equals(customScope,scope,StringComparison.Ordinal)&&customRange.From==DateTime.SpecifyKind(from,DateTimeKind.Local)&&customRange.To==DateTime.SpecifyKind(to,DateTimeKind.Local);
        }
        internal void FailCustomRange(string message,string scope)
        {
            customRange=null;customSnapshot=null;customScope=null;customLoading=false;customRangeButton.Content="自定义";customRangeButton.Foreground=Theme.Muted;customRangeButton.Background=Brushes.Transparent;ApplyView(original,scope);comparisonText.Text="自定义范围读取失败："+(String.IsNullOrWhiteSpace(message)?"请检查数据来源和路径。":message);
        }
        private void ApplyView(UsageSnapshot data,string scope)
        {
            var names=data==null?new string[0]:data.Daily.SelectMany(d=>d.Models).Select(m=>m.Model).Distinct().OrderBy(m=>m).ToArray();
            if(modelKey!=""&&!names.Contains(modelKey))modelKey="";
            modelChoices.Clear();modelChoices.Add("","全部模型");foreach(string name in names)modelChoices[name]=name;modelFilter.Select(modelKey);
            string next=scope+"/"+modelKey+"/"+(customRange==null?"preset":customRange.Key)+"/"+(data==null?"none":data.Warning+"/"+data.CoverageWarnings+"/"+data.HourlyThrough+"/"+UsageChart.Signature(data.Daily)+"/"+UsageChart.Signature(data.Hourly));
            if(next==viewSignature)return;viewSignature=next;
            bool reset=snapshot==null||data==null||scope!=(sourceLabel.Tag as string)||(data.Daily.Length>0&&snapshot.Daily.Length>0&&data.Daily[0].Date!=snapshot.Daily[0].Date);
            snapshot=data==null?null:new UsageSnapshot{Daily=Filter(data.Daily,modelKey),Hourly=Filter(data.Hourly,modelKey),HourlyThrough=data.HourlyThrough,Warning=data.Warning,CoverageWarnings=data.CoverageWarnings,CountLabel=data.CountLabel};
            if(snapshot!=null&&snapshot.Daily.Length>0)snapshot.HourlyUnallocatedTokens=Math.Max(0,snapshot.Daily.Last().Tokens-snapshot.Hourly.Sum(h=>h.Tokens));
            sourceLabel.Tag=scope;sourceLabel.Text=scope+(modelKey==""?"":" · "+modelKey)+" · "+(customRange!=null&&String.Equals(customScope,scope,StringComparison.Ordinal)?customRange.Label:"近 180 天");
            coverageText.Text=data==null?"":data.Warning;coverage.Visibility=ShowCoverage&&!String.IsNullOrEmpty(coverageText.Text)?Visibility.Visible:Visibility.Collapsed;
            if(reset){heatSelection.Reset();trendSelection.Reset();}
            heat.SetData(snapshot==null?null:snapshot.Daily,180,false,24,cost);heatSelection.SetData(heat.Days,0,heat.Days.Length,snapshot==null?"用量记录":snapshot.CountLabel);UpdateTrend();
        }
        private void UpdateTrend()
        {
            bool custom=customRange!=null;DailyUsage[] data=snapshot==null?new DailyUsage[0]:custom?snapshot.Daily:days==1?snapshot.Hourly:snapshot.Daily;int window=custom?data.Length:days;bool hourly=!custom&&days==1;
            trend.SetData(data,window,hourly,snapshot==null?0:snapshot.HourlyThrough,cost);
            var visible=custom?data:hourly?data.Take(snapshot==null?0:snapshot.HourlyThrough).ToArray():data.Skip(Math.Max(0,data.Length-days)).ToArray();
            long total=visible.Sum(d=>d.Tokens);decimal amount=visible.Sum(d=>ChartValue.Of(d,cost));sum.Text=cost?ChartValue.Money(amount):TokenText.Compact(total);sum.ToolTip=sum.Text+(cost?" USD · API 估算":" Tokens");
            summaryLabel.Text=(custom?"自定义区间":days==1?"今日已记录":"近 "+days+" 天")+(cost?" · USD":" · Tokens");averageLabel.Text=custom?"按日均值":days==1?"已记录小时均值":"已展示日期均值";
            int divisor=Math.Max(1,visible.Length);average.Text=cost?ChartValue.Money(amount/divisor):TokenText.Compact(total/divisor);average.ToolTip=average.Text+(cost?" USD":" Tokens")+"；含未结束时段";
            var models=visible.SelectMany(d=>d.Models).ToArray();string price=ChartValue.Money(models.Sum(m=>m.EquivalentUsd));valueLabel.Text=cost?"区间 Tokens":"API 估算 · USD";value.Text=cost?TokenText.Compact(total):price;value.ToolTip=value.Text;
            bool includesToday=visible.Any(d=>d.Date==DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            timing.Text=custom?(customLoading?"正在读取 · 边界精确到秒":"边界精确到秒 · 曲线按日分组"):hourly?"按小时统计 · 当前小时未结束":includesToday?"曲线含今日未结束数据":"曲线按已提供日期绘制";timing.Text+=(cost?" · 费用是估算，非账单；缺价格按 0":" · 色块表示模型用量")+" · 悬停图例高亮";
            if(hourly&&snapshot!=null&&snapshot.HourlyUnallocatedTokens>0)timing.Text+="\n另有 "+TokenText.Compact(snapshot.HourlyUnallocatedTokens)+" Tokens 只有日汇总，未分摊到小时";
            comparisonText.Text=snapshot==null?(customLoading?"正在读取自定义时间范围…":"等待当前来源的数据"):custom?"自定义区间："+customRange.Label+"\n边界按秒过滤，图表按日分组。": "按当前已读记录；对比不含今日。\n"+ChartComparison.Calculate(snapshot.Daily,days,DateTime.Today,cost,snapshot.CoverageWarnings>0).Message;
            trendSelection.SetData(data,custom||days==1?0:Math.Max(0,data.Length-days),custom?data.Length:days==1?(snapshot==null?0:snapshot.HourlyThrough):data.Length,snapshot==null?"用量记录":snapshot.CountLabel);
            UpdateLegend(models);
        }
        private void UpdateLegend(ModelUsage[] models)
        {
            var totals=models.GroupBy(m=>m.Model).ToDictionary(g=>g.Key,g=>g.Sum(m=>m.Tokens));
            if(!legendItems.Keys.OrderBy(k=>k).SequenceEqual(totals.Keys.OrderBy(k=>k)))
            {
                legend.Children.Clear();legendItems.Clear();
                foreach(string model in totals.Keys.OrderBy(k=>k))
                {
                    string key=model;var item=Theme.Button("","突出模型 "+key,40);item.Height=27;item.Margin=new Thickness(0,3,6,0);item.Background=Theme.Background;item.Padding=new Thickness(7,3,7,3);item.ToolTip="悬停高亮，点击固定 / 取消";
                    item.MouseEnter+=delegate{Highlight(key);};item.MouseLeave+=delegate{Highlight(pinnedModel);};item.GotKeyboardFocus+=delegate{Highlight(key);};item.LostKeyboardFocus+=delegate{Highlight(pinnedModel);};
                    item.Click+=delegate{pinnedModel=pinnedModel==key?null:key;Highlight(pinnedModel);};legendItems[key]=item;legend.Children.Add(item);
                }
                if(pinnedModel!=null&&!totals.ContainsKey(pinnedModel))pinnedModel=null;Highlight(pinnedModel);
            }
            foreach(var pair in totals){if(legendItems[pair.Key].Content is TextBlock)continue;var label=Theme.Text("● "+pair.Key,10,ModelColors.For(pair.Key));legendItems[pair.Key].Content=label;}
        }
        private void OpenCustomRange()
        {
            DateTime now=DateTime.Now;DateTime initialFrom=customRange==null?now.AddHours(-1):customRange.From;DateTime initialTo=customRange==null?now:customRange.To;
            var picker=new DateTimeRangePicker(initialFrom,initialTo);
            var dialog=new StyledWindow{Title="自定义时间范围",Width=760,Height=430,MinWidth=720,MinHeight=410,ResizeMode=ResizeMode.NoResize,Owner=Window.GetWindow(this),ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};dialog.SetBody(picker,"选择日期和时间","CUSTOM RANGE",false);
            DateTime selectedFrom=initialFrom,selectedTo=initialTo;
            Func<bool> read=delegate{return picker.TryRead(out selectedFrom,out selectedTo);};
            picker.CancelButton.Click+=delegate{WindowInteraction.Close(dialog);};picker.ApplyButton.Click+=delegate{WindowInteraction.CompleteDialog(dialog,true,read);};
            bool? result=dialog.ShowDialog();if(result==true){BeginCustomRange(selectedFrom,selectedTo,sourceLabel.Tag as string);if(CustomRangeRequested!=null)CustomRangeRequested(selectedFrom,selectedTo);else if(original!=null)ApplyCustomRange(original,sourceLabel.Tag as string,selectedFrom,selectedTo);}
        }
        private void Highlight(string model){trend.Highlight(model);foreach(var pair in legendItems)pair.Value.Opacity=model==null||pair.Key==model?1:.45;}
    }
}
