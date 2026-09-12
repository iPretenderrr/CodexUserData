using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUserData
{
    internal sealed class QuotaHistorySeries
    {
        internal string Key;
        internal QuotaSample[] Points;
        internal static QuotaHistorySeries[] Prepare(QuotaSample[] samples)
        {
            return samples.Where(s=>s.Supported).GroupBy(s=>s.Key).OrderBy(g=>g.First().Minutes).Select(g=>new QuotaHistorySeries{Key=g.Key,Points=g.OrderBy(s=>s.Time).ToArray()}).ToArray();
        }
    }
    // Cached drawing plus logarithmic hit testing: pointer movement never rebuilds curves.
    internal sealed class QuotaHistoryChart : FrameworkElement
    {
        private QuotaHistorySeries[] series=new QuotaHistorySeries[0];
        private readonly HashSet<string> hidden=new HashSet<string>();
        private readonly ToolTip tip=new ToolTip{Padding=new Thickness(9),Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse};
        private DrawingGroup drawing;
        private Size drawingSize;
        private int revision=-1;
        private long from,to,hover=-1,selected=-1;
        private Rect plot;
        internal int GeometryBuilds {get;private set;}
        internal event Action<QuotaSample[]> Pick;
        internal QuotaHistoryChart()
        {
            Height=340;Focusable=true;ClipToBounds=true;AutomationProperties.SetAutomationId(this,"QuotaHistoryChart");
            MouseMove+=delegate(object sender,MouseEventArgs e){long time=Hit(e.GetPosition(this));if(time==hover)return;hover=time;tip.IsOpen=false;if(time>=0){var points=At(time);if(points.Length>0){tip.Background=Theme.Surface;tip.Foreground=Theme.Ink;tip.BorderBrush=Theme.Line;tip.Content=Describe(points,false);tip.PlacementTarget=this;tip.IsOpen=true;}}InvalidateVisual();};
            MouseLeave+=delegate{ClearHover();};IsVisibleChanged+=delegate{if(!IsVisible)ClearHover();};Unloaded+=delegate{ClearHover();};
            MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){Focus();long time=Hit(e.GetPosition(this));Select(time==selected?-1:time);e.Handled=true;};
            KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){Select(-1);e.Handled=true;}};
        }
        internal void ClearHover(){hover=-1;tip.IsOpen=false;InvalidateVisual();}
        internal void Clear(){Select(-1);ClearHover();}
        private void Select(long time){selected=time;InvalidateVisual();if(Pick!=null)Pick(time<0?new QuotaSample[0]:At(time));}
        internal void SetData(QuotaSample[] samples,long start,long end)
        {SetSeries(QuotaHistorySeries.Prepare(samples),start,end);}
        internal void SetSeries(QuotaHistorySeries[] prepared,long start,long end)
        {
            from=start;to=Math.Max(start+1,end);series=prepared;
            drawing=null;ClearHover();if(selected>=0&&Pick!=null)Pick(At(selected));InvalidateVisual();
        }
        internal static Brush Color(long minutes)
        {
            // A window keeps its color even when the other window is absent.
            if(minutes==10080)return Theme.Accent;
            Color c=Theme.Accent.Color;
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(c.R*.3+20*.7),(byte)(c.G*.3+170*.7),(byte)(c.B*.3+140*.7)));
        }
        internal void Toggle(string key){if(!hidden.Add(key))hidden.Remove(key);drawing=null;ClearHover();if(selected>=0&&Pick!=null)Pick(At(selected));InvalidateVisual();}
        internal bool IsHidden(string key){return hidden.Contains(key);}
        internal static int Nearest(QuotaSample[] points,long time)
        {
            int lo=0,hi=points.Length;while(lo<hi){int mid=lo+(hi-lo)/2;if(points[mid].Time<time)lo=mid+1;else hi=mid;}
            if(lo==points.Length)return lo-1;if(lo>0&&time-points[lo-1].Time<=points[lo].Time-time)return lo-1;return lo;
        }
        private QuotaSample[] At(long time)
        {
            return series.Where(s=>!hidden.Contains(s.Key)).Select(s=>{int n=Nearest(s.Points,time);return n>=0&&Math.Abs(s.Points[n].Time-time)<=75?s.Points[n]:null;}).Where(s=>s!=null).ToArray();
        }
        private long Hit(Point p)
        {
            if(!plot.Contains(p))return -1;long time=from+(long)((p.X-plot.Left)/plot.Width*(to-from));
            var candidates=series.Where(s=>!hidden.Contains(s.Key)).Select(s=>{int n=Nearest(s.Points,time);return n<0?null:s.Points[n];}).Where(s=>s!=null).OrderBy(s=>Math.Abs(s.Time-time)).ToArray();
            // Never snap across a failed query / offline gap just because there is a distant point.
            return candidates.Length==0||Math.Abs(candidates[0].Time-time)>75?-1:candidates[0].Time;
        }
        internal static string Describe(QuotaSample[] points,bool resets)
        {
            if(points.Length==0)return "";
            return DateTimeOffset.FromUnixTimeSeconds(points[0].Time).ToLocalTime().ToString("MM-dd HH:mm:ss")+"\n"+String.Join("\n",points.Select(p=>p.Label+"  "+p.Remaining.ToString("0.#",CultureInfo.InvariantCulture)+"%"+(resets&&p.Reset>0?" · 重置 "+QuotaBucket.Timestamp(p.Reset):"")));
        }
        // Keep first/last and extrema per pixel column. Gap boundaries and quota resets
        // are retained explicitly, so decimation cannot draw a false continuous balance.
        internal static QuotaSample[] Reduce(QuotaSample[] points,long start,long end,int width)
        {
            if(points.Length<=width*4)return points;
            var keep=new SortedSet<int>();int first=0,min=0,max=0;long column=-1;
            for(int i=0;i<points.Length;i++)
            {
                long x=(points[i].Time-start)*Math.Max(1,width)/Math.Max(1,end-start);
                if(x!=column){if(i>0){keep.Add(first);keep.Add(i-1);keep.Add(min);keep.Add(max);}first=min=max=i;column=x;}
                if(points[i].Remaining<points[min].Remaining)min=i;if(points[i].Remaining>points[max].Remaining)max=i;
                if(i>0&&(points[i].Time-points[i-1].Time>90||points[i].Reset!=points[i-1].Reset)){keep.Add(i-1);keep.Add(i);}
            }
            if(points.Length>0){keep.Add(first);keep.Add(points.Length-1);keep.Add(min);keep.Add(max);}return keep.Select(i=>points[i]).ToArray();
        }
        private FormattedText Text(string text,Brush color){return new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);}
        private Point Position(QuotaSample p){return new Point(plot.Left+(p.Time-from)*plot.Width/(to-from),plot.Bottom-p.Remaining/100*plot.Height);}
        internal static StreamGeometry SmoothCurve(Point[] points,bool[] gaps,bool[] jumps)
        {
            var curve=new StreamGeometry();var slopes=new double[points.Length];
            // Minmod tangents keep each cubic within its observed endpoints. This gives
            // C1 continuity at normal samples without overshooting 0/100 or rounding resets.
            for(int i=0;i<points.Length;i++)
            {
                bool left=i>0&&!gaps[i]&&!jumps[i],right=i+1<points.Length&&!gaps[i+1]&&!jumps[i+1];
                double a=left?(points[i].Y-points[i-1].Y)/Math.Max(.000001,points[i].X-points[i-1].X):0;
                double b=right?(points[i+1].Y-points[i].Y)/Math.Max(.000001,points[i+1].X-points[i].X):0;
                slopes[i]=left&&right?(a*b<=0?0:Math.Sign(a)*Math.Min(Math.Abs(a),Math.Abs(b))):left?a:right?b:0;
            }
            using(var g=curve.Open())for(int i=0;i<points.Length;i++)
            {
                Point p=points[i];
                if(i==0||gaps[i])g.BeginFigure(p,false,false);
                else if(jumps[i]){g.LineTo(new Point(p.X,points[i-1].Y),true,false);g.LineTo(p,true,false);}
                else{Point previous=points[i-1];double dx=(p.X-previous.X)/3;g.BezierTo(new Point(previous.X+dx,previous.Y+slopes[i-1]*dx),new Point(p.X-dx,p.Y-slopes[i]*dx),p,true,true);}
            }
            curve.Freeze();return curve;
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);if(ActualWidth<100||ActualHeight<80)return;
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
            if(drawing==null||drawingSize!=RenderSize||revision!=Theme.Revision)
            {
                revision=Theme.Revision;drawingSize=RenderSize;GeometryBuilds++;plot=new Rect(38,16,Math.Max(1,ActualWidth-52),ActualHeight-47);
                drawing=new DrawingGroup();using(var c=drawing.Open())
                {
                    for(int n=0;n<=4;n++){double y=plot.Bottom-n*plot.Height/4;c.DrawLine(new Pen(Theme.Line,.6),new Point(plot.Left,y),new Point(plot.Right,y));c.DrawText(Text((n*25)+"%",Theme.Muted),new Point(0,y-7));}
                    int ticks=ActualWidth<440?2:4;for(int n=0;n<=ticks;n++){long time=from+(to-from)*n/ticks;var label=Text(DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString(to-from<=90000?"HH:mm":"MM-dd"),Theme.Muted);c.DrawText(label,new Point(Math.Max(plot.Left,Math.Min(plot.Right-label.Width,plot.Left+plot.Width*n/ticks-label.Width/2)),plot.Bottom+10));}
                    foreach(var s in series)
                    {
                        Brush color=Color(s.Points[0].Minutes);if(hidden.Contains(s.Key))continue;var points=Reduce(s.Points,from,to,(int)plot.Width);
                        var gaps=new bool[points.Length];var jumps=new bool[points.Length];
                        for(int i=1;i<points.Length;i++)
                        {
                            // Decimation retains raw samples on both sides of gaps / resets.
                            int raw=Nearest(s.Points,points[i].Time);gaps[i]=raw>0&&s.Points[raw].Time-s.Points[raw-1].Time>90;
                            jumps[i]=points[i].Reset!=points[i-1].Reset;
                        }
                        var geometry=SmoothCurve(points.Select(Position).ToArray(),gaps,jumps);var pen=new Pen(color,2){LineJoin=PenLineJoin.Round};c.DrawGeometry(null,pen,geometry);
                        if(points.Length>0)c.DrawEllipse(color,null,Position(points[points.Length-1]),2.5,2.5);
                    }
                    if(series.Length==0)c.DrawText(Text("暂无记录 · 在线额度查询成功后自动记录",Theme.Muted),new Point(plot.Left+8,plot.Top+20));
                }
                drawing=(DrawingGroup)drawing.GetCurrentValueAsFrozen();
            }
            dc.DrawDrawing(drawing);long marker=hover>=0?hover:selected;
            if(marker>=from&&marker<=to){double x=plot.Left+(marker-from)*plot.Width/(to-from);dc.DrawLine(new Pen(Theme.Muted,1),new Point(x,plot.Top),new Point(x,plot.Bottom));foreach(var p in At(marker))dc.DrawEllipse(Theme.Surface,new Pen(Color(p.Minutes),2),Position(p),3.5,3.5);}
        }
    }

    internal sealed class QuotaHistoryPanel : StackPanel
    {
        private readonly QuotaHistoryStore store;
        private readonly Func<string> scope,error;
        private readonly QuotaHistoryChart chart=new QuotaHistoryChart();
        private readonly TextBlock note=Theme.Text("每分钟记录 · 保留 180 天",10,Theme.Muted),detail=Theme.Text("",12,Theme.Ink);
        private readonly WrapPanel legend=new WrapPanel();
        private readonly Dictionary<int,Button> ranges=new Dictionary<int,Button>();
        internal static readonly int[] PeriodHours={1,3,6,12,24,168,336,720,1440,2160,4320};
        private int hours=24,generation;
        private double viewportHeight;
        private bool loading,pending;
        private string legendKey="",shownScope;
        internal QuotaHistoryPanel(QuotaHistoryStore history,Func<string> getScope,Func<string> getError=null)
        {
            store=history;scope=getScope;error=getError??(()=>"");Margin=new Thickness(20,10,20,20);
            Children.Add(Theme.Text("剩余额度趋势",15,Theme.Ink));note.Margin=new Thickness(0,8,0,10);note.TextWrapping=TextWrapping.Wrap;Children.Add(note);
            var toolbar=new DockPanel{Margin=new Thickness(0,0,0,4)};Children.Add(toolbar);
            var clear=Theme.Button("清除选择","清除额度时间选择",84);clear.HorizontalAlignment=HorizontalAlignment.Right;clear.VerticalAlignment=VerticalAlignment.Top;
            DockPanel.SetDock(clear,Dock.Right);clear.Click+=delegate{chart.Clear();};toolbar.Children.Add(clear);AutomationProperties.SetAutomationId(clear,"QuotaClearSelection");
            var buttons=new WrapPanel{Margin=new Thickness(0,0,8,0)};toolbar.Children.Add(buttons);
            foreach(int count in PeriodHours){int n=count;var b=Theme.Button(n<24?n+"h":n/24+"d","查看最近 "+(n<24?n+" 小时":n/24+" 天"),42);b.Margin=new Thickness(0,0,3,6);b.FontSize=11;b.Click+=delegate{if(hours==n)return;hours=n;chart.Clear();generation++;StyleRanges();Refresh();};ranges[n]=b;buttons.Children.Add(b);}
            legend.Margin=new Thickness(0,4,0,8);Children.Add(legend);Children.Add(chart);detail.TextWrapping=TextWrapping.Wrap;detail.Margin=new Thickness(0,12,0,0);detail.Visibility=Visibility.Collapsed;Children.Add(detail);
            chart.Pick+=rows=>{detail.Text=QuotaHistoryChart.Describe(rows,true);detail.Visibility=rows.Length==0?Visibility.Collapsed:Visibility.Visible;};
            IsVisibleChanged+=delegate{if(IsVisible)Refresh();else{generation++;chart.ClearHover();}};Unloaded+=delegate{generation++;};
            PreviewMouseWheel+=delegate{chart.ClearHover();};SizeChanged+=delegate{UpdateHeight();};StyleRanges();
        }
        internal static long RangeStart(long end,int hours){return end-hours*3600L;}
        internal void SetViewportHeight(double height){viewportHeight=height;UpdateHeight();}
        private void UpdateHeight(){chart.Height=Math.Max(ActualWidth<420?280:ActualWidth<760?330:380,viewportHeight-160);}
        private void StyleRanges(){foreach(var pair in ranges){pair.Value.Background=pair.Key==hours?Theme.Hover:Brushes.Transparent;pair.Value.Foreground=pair.Key==hours?Theme.Accent:Theme.Muted;}}
        internal async void Refresh()
        {
            if(!IsVisible)return;if(loading){pending=true;return;}loading=true;int version=generation;string current=scope();
            long to=LocalCodexUsage.Unix(DateTime.UtcNow),from=RangeStart(to,hours);
            try
            {
                if(shownScope!=current){chart.Clear();chart.SetSeries(new QuotaHistorySeries[0],from,to);legend.Children.Clear();legendKey="";shownScope=current;}
                var data=await store.ReadAsync(current,from,to);
                var prepared=await Task.Run(()=>QuotaHistorySeries.Prepare(data));
                if(!IsVisible||version!=generation||current!=scope()){pending=IsVisible;return;}
                chart.SetSeries(prepared,from,to);
                string key=Theme.Revision+"/"+String.Join("|",prepared.Select(g=>g.Key));
                if(key!=legendKey)
                {
                    legendKey=key;legend.Children.Clear();
                    foreach(var g in prepared){string id=g.Key;var b=Theme.Button("● "+g.Points[0].Label,"点击显示 / 隐藏这条额度曲线",64);b.Foreground=QuotaHistoryChart.Color(g.Points[0].Minutes);b.Margin=new Thickness(0,0,6,4);b.Opacity=chart.IsHidden(id)?.4:1;b.Click+=delegate{chart.Toggle(id);b.Opacity=chart.IsHidden(id)?.4:1;};legend.Children.Add(b);}
                }
                note.Text=(String.IsNullOrEmpty(error())?"":"历史记录写入失败，请检查磁盘空间与目录权限。\n")+"每分钟记录 · 保留 180 天 · 点击图例显隐曲线"+(data.Length==0?"":"\n最近记录 "+DateTimeOffset.FromUnixTimeSeconds(data[data.Length-1].Time).ToLocalTime().ToString("MM-dd HH:mm"));
            }
            catch(System.IO.IOException){note.Text="额度记录暂时无法读取，请稍后重新打开。";}
            catch(UnauthorizedAccessException){note.Text="额度记录目录无法访问，请检查目录权限。";}
            finally{loading=false;if(pending){pending=false;Refresh();}}
        }
    }
}
