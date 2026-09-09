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

    // One lightweight drawing surface per graph, with arithmetic hit testing.
    // Hundreds of squares/points do not require hundreds of WPF controls or timers.
    internal sealed class UsageChart : FrameworkElement
    {
        internal bool IsHeatmap;
        internal DailyUsage[] Days=new DailyUsage[0];
        internal int WindowDays=30;
        internal bool IsHourly;
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
        private readonly Dictionary<string,long[]> series=new Dictionary<string,long[]>();
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
        internal void SetData(DailyUsage[] days,int count,bool hourly=false,int availableHours=24)
        {
            string hoveredDate=Hovered>=0&&Hovered<Days.Length?Days[Hovered].Date:null;
            Days=days??new DailyUsage[0];IsHourly=hourly;through=hourly?Math.Max(0,Math.Min(Days.Length,availableHours)):Days.Length;string next=count+"/"+hourly+"/"+through+"/"+Signature(Days);WindowDays=count;
            if(next==signature)return;signature=next;cachedDrawing=null;series.Clear();layers.Clear();
            foreach(string model in Days.SelectMany(d=>d.Models).Select(m=>m.Model).Distinct().OrderBy(m=>m))series[model]=new long[Days.Length];
            for(int i=0;i<Days.Length;i++)foreach(var m in Days[i].Models)series[m.Model][i]+=m.Tokens;
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
            AutomationProperties.SetHelpText(this,day.Date+"，"+TokenText.Full(day.Tokens)+" Tokens");if(Pick!=null)Pick(index,clicked);InvalidateVisual();
        }
        private void UpdateTip(DailyUsage day){tip.Content=day.Date+"\n"+TokenText.Compact(day.Tokens)+" Tokens · "+ModelColors.Money(day.Models.Sum(m=>m.EquivalentUsd),day.Models.Sum(m=>m.UnpricedTokens),day.Tokens)+"\n点击固定下方明细";}

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
            return new Point(IsHourly?plot.Left+(index+.5)*plot.Width/24:plot.Left+(index-first)*plot.Width/Math.Max(1,Days.Length-first-1),plot.Bottom-(Days[index].Tokens/max)*plot.Height);
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
            pitch=Math.Max(5,(ActualWidth-22)/columns);cell=Math.Max(3,pitch-3);plot=new Rect(20,24,pitch*columns,pitch*7);max=Math.Max(1,Days.Max(d=>(double)d.Tokens));
            int previousMonth=-1;double lastLabel=-100;
            for(int i=0;i<Days.Length;i++)
            {
                Point p=PointFor(i);DateTime date=start.AddDays(i);int column=(i+offset)/7;
                if(date.Month!=previousMonth){if(column*pitch-lastLabel>28){Text(dc,date.Month+"月",20+column*pitch,2,10,Theme.Muted);lastLabel=column*pitch;}previousMonth=date.Month;}
                int level=Days[i].Tokens<=0?0:Math.Min(4,1+(int)Math.Floor(3.999*Math.Sqrt(Days[i].Tokens/max)));
                var rect=new Rect(p.X-cell/2,p.Y-cell/2,cell,cell);double radius=Math.Min(3.5,cell*.24);dc.DrawRoundedRectangle(levels[level],null,rect,radius,radius);
                if(Days[i].Tokens>0&&series.Count>0)
                {
                    // Keep model proportions secondary to total usage. Inset the thin strip so it
                    // does not turn every day into a multicolored tile, including at narrow widths.
                    double inset=Math.Min(2,cell*.12),stripe=Math.Max(.8,Math.Min(2,cell*.09));
                    var track=new Rect(rect.Left+inset,rect.Bottom-inset-stripe,cell-2*inset,stripe);
                    dc.PushClip(new RectangleGeometry(track,stripe/2,stripe/2));double x=track.Left;
                    foreach(var pair in series){long tokens=pair.Value[i];if(tokens<=0)continue;double width=track.Width*tokens/(double)Days[i].Tokens;dc.DrawRectangle(ModelColors.For(pair.Key),null,new Rect(x,track.Top,width,stripe));x+=width;}
                    dc.Pop();
                }
            }
            Text(dc,"一",0,24,9,Theme.Muted);Text(dc,"四",0,24+3*pitch,9,Theme.Muted);Text(dc,"日",0,24+6*pitch,9,Theme.Muted);
            double y=plot.Bottom+8;Text(dc,"少",20,y,9,Theme.Muted);for(int i=0;i<5;i++)dc.DrawRoundedRectangle(levels[i],null,new Rect(39+i*13,y+2,10,10),2,2);Text(dc,"多",107,y,9,Theme.Muted);if(ActualWidth>350)Text(dc,"色阶：用量 · 细条：模型占比",140,y,9,Theme.Muted);
        }
        private void DrawTrend(DrawingContext dc)
        {
            first=IsHourly?0:Math.Max(0,Days.Length-WindowDays);int count=through-first;
            plot=new Rect(46,14,Math.Max(1,ActualWidth-54),Math.Max(40,ActualHeight-44));
            max=NiceMax(count>0?Days.Skip(first).Take(count).Max(d=>(double)d.Tokens):0);
            var dashed=new Pen(Theme.Line,.7){DashStyle=new DashStyle(new double[]{3,4},0)};
            for(int line=0;line<=4;line++){double y=plot.Bottom-plot.Height*line/4;dc.DrawLine(dashed,new Point(plot.Left,y),new Point(plot.Right,y));Text(dc,TokenText.Axis(max*line/4),0,y-6,9,Theme.Muted);}
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
            if(layers.Count==0&&Days.Skip(first).Take(count).Any(d=>d.Tokens>0)){double[] total=Days.Skip(first).Take(count).Select(d=>(double)d.Tokens).ToArray();layers.Add(new Layer{Model="unknown",Upper=total,Area=Band(total,new double[count])});}
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
        internal static double Monotone(double a,double b){return a*b<=0?0:2*a*b/(a+b);}
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

    internal sealed class HistoryPanel : StackPanel
    {
        internal static readonly int[] Periods={1,7,14,30,60,90,180};
        private readonly Border heatCard,trendCard;
        private readonly UsageChart heat,trend;
        private readonly UsageDetails heatDetail=new UsageDetails("Heatmap","热度图 · 每日明细"),trendDetail=new UsageDetails("Trend","曲线图 · 时段明细");
        private readonly ChartDetailSelection heatSelection,trendSelection;
        private readonly TextBlock sourceLabel,sum,average,value,timing,summaryLabel,averageLabel;
        private readonly Border coverage;
        private readonly TextBlock coverageText;
        private readonly Dictionary<int,Button> ranges=new Dictionary<int,Button>();
        private readonly Dictionary<string,Button> legendItems=new Dictionary<string,Button>();
        private readonly Dictionary<string,string> modelChoices=new Dictionary<string,string>{{"","全部模型"}};
        private readonly ChoiceButton modelFilter;
        private readonly WrapPanel legend=new WrapPanel{Margin=new Thickness(0,6,0,8)};
        private UsageSnapshot original,snapshot;
        private string modelKey="",viewSignature,pinnedModel;
        private int days=30;
        internal bool ShowCoverage=true;
        internal event Action<int> RangeChanged;
        internal HistoryPanel()
        {
            Background=Brushes.Transparent;
            sourceLabel=Theme.Text("近 180 天 · 等待数据",10,Theme.Muted);sourceLabel.TextWrapping=TextWrapping.Wrap;sourceLabel.Margin=new Thickness(0,12,0,8);AutomationProperties.SetAutomationId(sourceLabel,"ChartSource");Children.Add(sourceLabel);
            modelFilter=new ChoiceButton(modelChoices,"图表模型筛选");modelFilter.Select("");Children.Add(modelFilter);
            modelFilter.Changed+=delegate(string key){modelKey=key;viewSignature=null;Apply(original,sourceLabel.Tag as string);};
            var heatBody=new StackPanel();heatCard=Card(heatBody);heatCard.Margin=new Thickness(0,8,0,0);Children.Add(heatCard);
            heatBody.Children.Add(Theme.Text("每日用量热度图",13,Theme.Ink));var hint=Theme.Text("色阶表示总量 · 细条表示模型占比",10,Theme.Muted);hint.Margin=new Thickness(0,5,0,6);heatBody.Children.Add(hint);
            heat=new UsageChart(true);heatBody.Children.Add(heat);heatBody.Children.Add(DetailViewport(heatDetail));
            var trendBody=new StackPanel();trendCard=Card(trendBody);trendCard.Margin=new Thickness(0,9,0,0);Children.Add(trendCard);
            var trendHeader=new DockPanel();trendBody.Children.Add(trendHeader);
            var clearSelection=Theme.Button("清除选择","清除曲线时段选择",78);clearSelection.FontSize=11;clearSelection.Height=27;clearSelection.ToolTip="清除固定时段（Esc）";DockPanel.SetDock(clearSelection,Dock.Right);trendHeader.Children.Add(clearSelection);
            clearSelection.Click+=delegate{trendSelection.Clear();};trendHeader.Children.Add(Theme.Text("Token 用量趋势",13,Theme.Ink));
            // Wrap period buttons as the window shrinks; a horizontal StackPanel would clip the last options.
            var buttons=new WrapPanel{Margin=new Thickness(-2,9,0,8)};trendBody.Children.Add(buttons);
            foreach(int n in Periods){int v=n;var button=Theme.Button(n==1?"当天":n+"D",n==1?"当天趋势":n+" 天趋势",n==1?44:39);button.Height=27;button.FontSize=11;button.Margin=new Thickness(2,2,2,2);button.Click+=delegate{SetRange(v);if(RangeChanged!=null)RangeChanged(v);};ranges[n]=button;buttons.Children.Add(button);}
            var summary=new UniformGrid{Columns=3,Margin=new Thickness(0,3,0,2)};trendBody.Children.Add(summary);
            sum=Summary(summary,"区间用量",out summaryLabel);average=Summary(summary,"日均",out averageLabel);TextBlock valueLabel;value=Summary(summary,"API 等效 · USD",out valueLabel);
            timing=Theme.Text("色块厚度表示模型用量 · 悬停图例高亮",10,Theme.Muted);timing.TextWrapping=TextWrapping.Wrap;timing.Margin=new Thickness(0,5,0,0);trendBody.Children.Add(timing);
            trendBody.Children.Add(legend);trend=new UsageChart(false);trendBody.Children.Add(trend);
            trendBody.Children.Add(DetailViewport(trendDetail));
            coverageText=Theme.Text("",10,Theme.Warning);coverageText.TextWrapping=TextWrapping.Wrap;coverageText.Margin=new Thickness(3,7,3,3);
            var coverageBody=new StackPanel();coverageText.Visibility=Visibility.Collapsed;var toggleCoverage=Theme.Button("ⓘ 数据覆盖说明  ⌄","展开数据覆盖说明",30);toggleCoverage.Foreground=Theme.Warning;toggleCoverage.HorizontalAlignment=HorizontalAlignment.Left;toggleCoverage.FontSize=10;
            toggleCoverage.Click+=delegate{coverageText.Visibility=coverageText.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;};coverageBody.Children.Add(toggleCoverage);coverageBody.Children.Add(coverageText);
            coverage=new Border{Child=coverageBody,Margin=new Thickness(2,10,0,0),Visibility=Visibility.Collapsed};Children.Add(coverage);
            heatSelection=new ChartDetailSelection(heat,heatDetail);trendSelection=new ChartDetailSelection(trend,trendDetail,false,false);
            PreviewMouseWheel+=delegate{heatSelection.PauseForScroll();trendSelection.PauseForScroll();};
            SizeChanged+=delegate{heat.Height=heat.HeatHeight(Math.Max(180,ActualWidth-24));trend.Height=ActualWidth<420?280:ActualWidth<760?330:380;};SetRange(30);
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
            int next=Periods.Contains(count)?count:30;if(days!=next)trendSelection.Reset();
            days=next;foreach(var pair in ranges){pair.Value.Foreground=pair.Key==days?Theme.Accent:Theme.Muted;pair.Value.Background=pair.Key==days?Theme.Hover:Brushes.Transparent;}
            UpdateTrend();
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
            original=data;var names=data==null?new string[0]:data.Daily.SelectMany(d=>d.Models).Select(m=>m.Model).Distinct().OrderBy(m=>m).ToArray();
            if(modelKey!=""&&!names.Contains(modelKey))modelKey="";
            modelChoices.Clear();modelChoices.Add("","全部模型");foreach(string name in names)modelChoices[name]=name;modelFilter.Select(modelKey);
            string next=scope+"/"+modelKey+"/"+(data==null?"none":data.Warning+"/"+data.HourlyThrough+"/"+UsageChart.Signature(data.Daily)+"/"+UsageChart.Signature(data.Hourly));
            if(next==viewSignature)return;viewSignature=next;
            bool reset=snapshot==null||data==null||scope!=(sourceLabel.Tag as string)||(data.Daily.Length>0&&snapshot.Daily.Length>0&&data.Daily[0].Date!=snapshot.Daily[0].Date);
            snapshot=data==null?null:new UsageSnapshot{Daily=Filter(data.Daily,modelKey),Hourly=Filter(data.Hourly,modelKey),HourlyThrough=data.HourlyThrough,Warning=data.Warning,CountLabel=data.CountLabel};
            if(snapshot!=null&&snapshot.Daily.Length>0)snapshot.HourlyUnallocatedTokens=Math.Max(0,snapshot.Daily.Last().Tokens-snapshot.Hourly.Sum(h=>h.Tokens));
            sourceLabel.Tag=scope;sourceLabel.Text=scope+(modelKey==""?"":" · "+modelKey)+" · 近 180 天";
            coverageText.Text=data==null?"":data.Warning;coverage.Visibility=ShowCoverage&&!String.IsNullOrEmpty(coverageText.Text)?Visibility.Visible:Visibility.Collapsed;
            if(reset){heatSelection.Reset();trendSelection.Reset();}
            heat.SetData(snapshot==null?null:snapshot.Daily,180);heatSelection.SetData(heat.Days,0,heat.Days.Length,snapshot==null?"用量记录":snapshot.CountLabel);UpdateTrend();
        }
        private void UpdateTrend()
        {
            DailyUsage[] data=snapshot==null?new DailyUsage[0]:days==1?snapshot.Hourly:snapshot.Daily;
            trend.SetData(data,days,days==1,snapshot==null?0:snapshot.HourlyThrough);
            var visible=days==1?data.Take(snapshot==null?0:snapshot.HourlyThrough).ToArray():data.Skip(Math.Max(0,data.Length-days)).ToArray();
            long total=visible.Sum(d=>d.Tokens);sum.Text=TokenText.Compact(total);sum.ToolTip=sum.Text;
            summaryLabel.Text=days==1?"今日 · 已记录小时":"近 "+days+" 天";averageLabel.Text=days==1?"小时均值":"日均";
            average.Text=TokenText.Compact(total/Math.Max(1,days==1?visible.Length:days));average.ToolTip=average.Text;
            var models=visible.SelectMany(d=>d.Models).ToArray();string price=ModelColors.Money(models.Sum(m=>m.EquivalentUsd),models.Sum(m=>m.UnpricedTokens),total);value.Text=price;value.ToolTip=price;
            timing.Text=days==1?"按小时统计 · 当前小时未结束 · 悬停图例高亮":"色块厚度表示模型用量 · 含今日 · 悬停图例高亮";
            if(days==1&&snapshot!=null&&snapshot.HourlyUnallocatedTokens>0)timing.Text+="\n另有 "+TokenText.Compact(snapshot.HourlyUnallocatedTokens)+" Tokens 只有日汇总，未分摊到小时";
            trendSelection.SetData(data,days==1?0:Math.Max(0,data.Length-days),days==1?(snapshot==null?0:snapshot.HourlyThrough):data.Length,snapshot==null?"用量记录":snapshot.CountLabel);
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
        private void Highlight(string model){trend.Highlight(model);foreach(var pair in legendItems)pair.Value.Opacity=model==null||pair.Key==model?1:.45;}
    }
}
