using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    // The provider owns the all-history index. Changing the viewport only changes
    // the drawing; it never rereads logs or resets the cumulative token baseline.
    internal sealed class MilestoneCurvePanel : StackPanel, IDisposable
    {
        private readonly Func<CancellationToken,Task<MilestoneSnapshot>> load;
        private readonly Func<string> currentScope;
        private readonly Action<long> rememberStep;
        private readonly Dictionary<long,Button> steps=new Dictionary<long,Button>();
        private readonly Dictionary<int,Button> ranges=new Dictionary<int,Button>();
        private Button customRangeButton;
        private int rangeDays=-1;
        private readonly TextBlock source=Theme.Text("累计 Tokens",11,Theme.Muted),status=Theme.Text("",11,Theme.Muted),detailText=Theme.Text("",12,Theme.Ink);
        private readonly MilestoneCurveChart chart=new MilestoneCurveChart();
        private readonly Border detail=new Border{Visibility=Visibility.Collapsed,Background=Theme.Surface,CornerRadius=new CornerRadius(7),Padding=new Thickness(8,6,8,6),Margin=new Thickness(0,4,0,0)};
        private readonly Button previous,next,clear;
        private readonly DispatcherTimer timer;
        private MilestoneSnapshot snapshot;
        private CancellationTokenSource cancellation;
        private Window owner;
        private string knownScope;
        private bool loading,pending,pumpQueued,disposed,followNow;
        private int generation;
        private long step,from,to=LocalCodexUsage.Unix(DateTime.UtcNow),selected,selectedFirst,selectedLast;

        internal MilestoneCurvePanel(Func<CancellationToken,Task<MilestoneSnapshot>> loader,Func<string> scope,long selectedStep,Action<long> saveStep)
        {
            load=loader;currentScope=scope;rememberStep=saveStep;step=Array.IndexOf(MilestoneEngine.Steps,selectedStep)>=0?selectedStep:MilestoneEngine.Base;
            var choices=new WrapPanel{Margin=new Thickness(0,0,0,4)};
            foreach(long value in MilestoneEngine.Steps)
            {
                long choice=value;var button=Theme.Button(MilestonePanel.StepText(value),"每 "+MilestonePanel.StepText(value)+" 显示一个节点",value>=100*MilestoneEngine.Base?52:46);button.Margin=new Thickness(0,0,4,4);
                button.Click+=delegate{if(step==choice)return;SetStep(choice);if(rememberStep!=null)rememberStep(step);};steps.Add(value,button);choices.Children.Add(button);
            }
            Children.Add(choices);
            var metadata=new DockPanel{Margin=new Thickness(0,0,0,3)};status.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(status,Dock.Right);metadata.Children.Add(status);source.TextTrimming=TextTrimming.CharacterEllipsis;metadata.Children.Add(source);Children.Add(metadata);Children.Add(chart);
            var row=new DockPanel();var controls=new StackPanel{Orientation=Orientation.Horizontal};
            previous=Theme.Button("‹","查看组内上一个阶段",25);next=Theme.Button("›","查看组内下一个阶段",25);clear=Theme.Button("×","清除所选阶段（Esc）",25);
            previous.Click+=delegate{if(selected>selectedFirst)Select(selected-1,selectedFirst,selectedLast);};next.Click+=delegate{if(selected<selectedLast)Select(selected+1,selectedFirst,selectedLast);};clear.Click+=delegate{ClearSelection();};controls.Children.Add(previous);controls.Children.Add(next);controls.Children.Add(clear);DockPanel.SetDock(controls,Dock.Right);row.Children.Add(controls);
            detailText.TextTrimming=TextTrimming.CharacterEllipsis;row.Children.Add(detailText);detail.Child=row;Children.Add(detail);
            chart.Pick+=Select;PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){ClearSelection();e.Handled=true;}};
            chart.SizeChanged+=delegate{RenderDetail();};
            timer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMinutes(1)};timer.Tick+=delegate{Refresh();};
            Loaded+=delegate{if(disposed)return;DetachOwner();owner=Window.GetWindow(this);if(owner!=null)owner.StateChanged+=OwnerStateChanged;Refresh();};
            Unloaded+=delegate{Suspend();DetachOwner();};IsVisibleChanged+=delegate{if(IsVisible){RenderDetail();Refresh();}else Suspend();};Render();
        }
        private bool Active {get{return !disposed&&IsLoaded&&IsVisible&&(owner==null||owner.WindowState!=WindowState.Minimized);}}
        internal void EnableRangeControls()
        {
            if(ranges.Count>0)return;rangeDays=0;chart.Height=380;SizeChanged+=delegate{chart.Height=ActualWidth<420?280:380;};
            var header=new Grid{Margin=new Thickness(0,0,0,8)};header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var choices=new WrapPanel();header.Children.Add(choices);
            foreach(int days in new[]{1,7,14,30,60,90,180,0})
            {
                int choice=days;var button=Theme.Button(days==0?"全部":days==1?"当天":days+"D",days==0?"查看全部历史":days==1?"查看当天":"查看近 "+days+" 天",42);button.Height=27;button.FontSize=11;button.Margin=new Thickness(0,0,3,4);
                button.Click+=delegate{SelectRange(choice);};ranges.Add(days,button);choices.Children.Add(button);
            }
            customRangeButton=Theme.Button("自定义","选择日期和时间范围",64);customRangeButton.Height=27;customRangeButton.FontSize=11;customRangeButton.Margin=new Thickness(0,0,3,4);customRangeButton.Click+=delegate{OpenRange();};choices.Children.Add(customRangeButton);
            var clearChoice=Theme.Button("清除选择","清除选中阶段（Esc）",70);clearChoice.Height=27;clearChoice.FontSize=11;clearChoice.VerticalAlignment=VerticalAlignment.Top;clearChoice.Click+=delegate{ClearSelection();};Grid.SetColumn(clearChoice,1);header.Children.Add(clearChoice);Children.Insert(0,header);
            var hint=Theme.Text("全部历史累计 · 点击节点查看该阶段耗时",11,Theme.Muted);hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,0,0,8);Children.Insert(1,hint);SelectRange(0);
        }
        private void SelectRange(int days)
        {
            if(!ranges.ContainsKey(days))return;rangeDays=days;UpdateRangeButtons();ApplyPresetRange();
        }
        private void UpdateRangeButtons()
        {
            foreach(var pair in ranges){bool active=pair.Key==rangeDays;pair.Value.Background=active?Theme.Hover:Brushes.Transparent;pair.Value.Foreground=active?Theme.Accent:Theme.Muted;}
            if(customRangeButton!=null){customRangeButton.Background=rangeDays==-2?Theme.Hover:Brushes.Transparent;customRangeButton.Foreground=rangeDays==-2?Theme.Accent:Theme.Muted;}
        }
        private void ApplyPresetRange()
        {
            if(rangeDays<0)return;DateTime now=DateTime.Now;
            // Presets only clip the immutable index; the independent page never
            // changes the home period or requests another range-specific ledger.
            long start=rangeDays==0&&snapshot!=null&&snapshot.Origin!=null?snapshot.Origin.From:LocalCodexUsage.Unix(now.Date.AddDays(rangeDays<=1?0:1-rangeDays));
            SetRange(start,Math.Max(start,LocalCodexUsage.Unix(now)),true);
        }
        private void OpenRange()
        {
            var picker=new DateTimeRangePicker(DateTimeOffset.FromUnixTimeSeconds(from).LocalDateTime,DateTimeOffset.FromUnixTimeSeconds(EffectiveEnd).LocalDateTime);
            var window=new StyledWindow{Title="累计里程碑时间范围",Width=760,Height=430,MinWidth=720,MinHeight=410,ResizeMode=ResizeMode.NoResize,Owner=Window.GetWindow(this),ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};window.SetBody(picker,"选择日期和时间","CUSTOM RANGE",false);
            DateTime start=DateTime.Now,end=start;picker.CancelButton.Click+=delegate{WindowInteraction.Close(window);};picker.ApplyButton.Click+=delegate{WindowInteraction.CompleteDialog(window,true,()=>picker.TryRead(out start,out end));};
            if(window.ShowDialog()!=true)return;rangeDays=-2;UpdateRangeButtons();SetRange(LocalCodexUsage.Unix(start),LocalCodexUsage.Unix(end));
        }
        internal void SetStep(long value)
        {if(disposed||step==value||Array.IndexOf(MilestoneEngine.Steps,value)<0)return;step=value;ClearSelection();Render();}
        private long EffectiveEnd {get{return followNow&&snapshot!=null?Math.Max(to,snapshot.ObservedAt):to;}}
        internal void SetRange(long start,long end,bool followNow=false)
        {
            if(disposed)return;if(end<start)throw new ArgumentOutOfRangeException("end");if(from==start&&to==end&&this.followNow==followNow)return;
            bool newWindow=from!=start||this.followNow!=followNow||!followNow&&to!=end;from=start;to=end;this.followNow=followNow;
            // The right edge of a live preset advances on ordinary data refresh.
            // Preserve the user's selection while that same window follows now.
            if(newWindow)ClearSelection();chart.SetData(snapshot,step,from,EffectiveEnd);RenderDetail();
        }
        internal void Refresh()
        {
            if(disposed)return;string scope=currentScope();
            if(!String.Equals(knownScope,scope,StringComparison.Ordinal))
            {
                knownScope=scope;generation++;if(cancellation!=null)cancellation.Cancel();snapshot=null;ClearSelection();Render();
            }
            if(!Active)return;timer.Start();pending=true;QueuePump();
        }
        private void QueuePump()
        {
            if(loading||pumpQueued||!pending||!Active)return;pumpQueued=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{pumpQueued=false;if(pending&&Active)Pump();}));
        }
        private async void Pump()
        {
            if(loading||!pending||!Active)return;pending=false;loading=true;int version=generation;string scope=knownScope;var request=new CancellationTokenSource();cancellation=request;
            if(snapshot==null)SetStatus("正在读取…",null);MilestoneSnapshot result=null;Exception error=null;
            try{result=await load(request.Token);}catch(OperationCanceledException){}catch(Exception ex){error=ex;}
            Action finish=delegate
            {
                // A provider may ignore cancellation. Scope + generation reject old
                // sources and hidden views. The current range is used at publication,
                // so a late all-history result cannot restore an older viewport.
                bool accept=Active&&version==generation&&!request.IsCancellationRequested&&String.Equals(scope,currentScope(),StringComparison.Ordinal);
                if(accept)
                {
                    if(error!=null)SetStatus("同步失败 ⓘ",error.Message);
                    else if(result!=null&&String.Equals(result.Scope,scope,StringComparison.Ordinal))
                    {
                        snapshot=result;if(selected>snapshot.Completed(step))ClearSelection();else selectedLast=Math.Min(selectedLast,snapshot.Completed(step));Render();
                    }
                    else SetStatus("等待同步…",null);
                }
                if(Object.ReferenceEquals(cancellation,request))cancellation=null;request.Dispose();loading=false;QueuePump();
            };
            if(Dispatcher.HasShutdownStarted){request.Dispose();return;}
            if(Dispatcher.CheckAccess())finish();else try{Dispatcher.Invoke(finish);}catch(OperationCanceledException){request.Dispose();}
        }
        private void Render()
        {
            ApplyPresetRange();
            foreach(var item in steps){item.Value.Background=item.Key==step?Theme.Hover:Brushes.Transparent;item.Value.Foreground=item.Key==step?Theme.Accent:Theme.Muted;}
            source.Text=snapshot==null?"累计 Tokens · 全部历史基线":snapshot.Source+" · 累计 Tokens";source.ToolTip="累计值包含窗口开始前的全部可用记录。节点按报送记录定位；耗时包含离线和未使用时间。";
            SetStatus(snapshot==null?"等待同步…":String.IsNullOrEmpty(snapshot.Warning)?"全部历史基线":"统计提示 ⓘ",snapshot==null?null:snapshot.Warning);
            chart.SetData(snapshot,step,from,EffectiveEnd);RenderDetail();
        }
        private void SetStatus(string text,string warning)
        {status.Text=text;status.Foreground=String.IsNullOrEmpty(warning)?Theme.Muted:Theme.Warning;status.ToolTip=String.IsNullOrEmpty(warning)?null:warning;AutomationProperties.SetHelpText(status,warning??"");}
        private void Select(long number,long first,long last)
        {
            if(snapshot==null||number<1||number>snapshot.Completed(step)){ClearSelection();return;}
            selected=number;selectedFirst=first;selectedLast=last;chart.SetSelected(number);RenderDetail();
        }
        internal void ClearSelection()
        {selected=selectedFirst=selectedLast=0;chart.SetSelected(0);detail.Visibility=Visibility.Collapsed;detailText.Text="";detail.ToolTip=null;}
        private void RenderDetail()
        {
            if(snapshot==null||selected==0){detail.Visibility=Visibility.Collapsed;return;}
            var group=chart.GroupFor(selected);selectedFirst=group==null?selected:group.First;selectedLast=group==null?selected:group.Last;
            var stage=snapshot.Stage(step,selected,snapshot.ObservedAt);string text="第"+selected+"个"+MilestonePanel.StepText(step)+" · 用时 "+MilestonePanel.DurationText(stage);detailText.Text=text;
            detail.ToolTip=text+"\n"+MilestonePanel.BoundaryText(stage.Start)+" → "+MilestonePanel.BoundaryText(stage.End)+"\n自然经过时间，包含离线和未使用时间。"+(selectedFirst==selectedLast?"":"\n当前节点组：第 "+selectedFirst+"–"+selectedLast+" 个；前后按钮逐个查看，曲线 Home / End 跳到组内首末阶段。");
            previous.Visibility=next.Visibility=selectedFirst==selectedLast?Visibility.Collapsed:Visibility.Visible;previous.IsEnabled=selected>selectedFirst;next.IsEnabled=selected<selectedLast;detail.Visibility=Visibility.Visible;AutomationProperties.SetItemStatus(chart,text);
        }
        private void Suspend(){generation++;pending=false;timer.Stop();chart.ClearHover();if(cancellation!=null)cancellation.Cancel();}
        private void OwnerStateChanged(object sender,EventArgs e){if(Active)Refresh();else Suspend();}
        private void DetachOwner(){if(owner!=null)owner.StateChanged-=OwnerStateChanged;owner=null;}
        public void Dispose(){if(disposed)return;disposed=true;Suspend();DetachOwner();snapshot=null;ClearSelection();chart.SetData(null,step,from,to);}
    }

    internal sealed class MilestoneCurveNode
    {
        internal long First,Last,Time,Total;
        internal Point Position;
        internal MilestoneBoundary Boundary;
    }

    internal sealed class MilestoneCurveChart : FrameworkElement
    {
        private sealed class Sample {internal long Time,Total;internal Sample(long time,long total){Time=time;Total=total;}}
        private MilestoneSnapshot snapshot;
        private long step=MilestoneEngine.Base,from,to,drawEnd,selected;
        private DrawingGroup drawing;
        private StreamGeometry curve;
        private Size drawingSize;
        private int revision=-1,hover=-1;
        private double left=52,top=40,bottom,width,height,yMin,yMax;
        private readonly List<MilestoneCurveNode> nodes=new List<MilestoneCurveNode>();
        private readonly ToolTip tip=new ToolTip();
        private readonly Typeface face=new Typeface("Segoe UI, Microsoft YaHei UI");
        internal event Action<long,long,long> Pick;
        internal int DrawingBuilds {get;private set;}
        internal int SampleCount {get;private set;}
        internal long Baseline {get;private set;}
        internal bool ShowsEndpoint {get;private set;}
        internal MilestoneCurveChart()
        {
            Height=280;ClipToBounds=true;Focusable=true;Cursor=Cursors.Hand;Theme.Watch(this);AutomationProperties.SetName(this,"累计 Tokens 曲线。左右键选择节点，上下键查看组内阶段，Home 或 End 跳到组内首末，Esc 清除。");
            MouseMove+=delegate(object sender,MouseEventArgs e){int hit=Hit(e.GetPosition(this));if(hit==hover)return;hover=hit;tip.IsOpen=false;if(hit>=0){tip.Content=NodeTip(nodes[hit]);tip.Background=Theme.Surface;tip.Foreground=Theme.Ink;tip.BorderBrush=Theme.Line;tip.PlacementTarget=this;tip.IsOpen=true;}};
            MouseLeave+=delegate{ClearHover();};MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){int hit=Hit(e.GetPosition(this));if(hit>=0&&Pick!=null){Focus();var node=nodes[hit];Pick(node.Last,node.First,node.Last);e.Handled=true;}};
            KeyDown+=OnKey;IsVisibleChanged+=delegate{if(!IsVisible)ClearHover();};Unloaded+=delegate{ClearHover();};
        }
        protected override AutomationPeer OnCreateAutomationPeer(){return new FrameworkElementAutomationPeer(this);}
        internal void SetData(MilestoneSnapshot value,long interval,long start,long end)
        {
            long observed=value==null?end:Math.Min(end,value.ObservedAt);
            bool same=snapshot!=null&&value!=null&&Object.ReferenceEquals(snapshot.Curve,value.Curve)&&Object.ReferenceEquals(snapshot.Marks,value.Marks)&&snapshot.TotalTokens==value.TotalTokens&&step==interval&&from==start&&to==end&&drawEnd==observed&&ShowsCurrent(snapshot,start,end)==ShowsCurrent(value,start,end);
            snapshot=value;step=interval;from=start;to=end;drawEnd=observed;
            if(same)return;drawing=null;curve=null;nodes.Clear();ClearHover();InvalidateVisual();
        }
        private static bool ShowsCurrent(MilestoneSnapshot value,long start,long end){return value!=null&&value.TotalTokens>0&&start<=value.ObservedAt&&value.ObservedAt<=end;}
        internal void SetSelected(long number)
        {if(selected==number)return;selected=number;AutomationProperties.SetItemStatus(this,number==0?"未选中节点":"第 "+number+" 个"+MilestonePanel.StepText(step));InvalidateVisual();}
        internal MilestoneCurveNode GroupFor(long number)
        {
            // Pixel groups can change on resize or a live-range advance while the
            // selected stage stays pinned. Resolve navigation against this drawing.
            if(!IsLoaded||!IsVisible||ActualWidth<80||ActualHeight<80)return null;
            if(drawing==null||drawingSize!=RenderSize||revision!=Theme.Revision){ClearHover();Build();}
            foreach(var node in nodes)if(number>=node.First&&number<=node.Last)return node;return null;
        }
        internal void ClearHover(){hover=-1;tip.IsOpen=false;tip.Content=null;}
        private void OnKey(object sender,KeyEventArgs e)
        {
            if(Pick==null)return;if(e.Key==Key.Escape){Pick(0,0,0);e.Handled=true;return;}if(nodes.Count==0)return;
            int at=-1;for(int i=0;i<nodes.Count;i++)if(selected>=nodes[i].First&&selected<=nodes[i].Last){at=i;break;}
            if(e.Key==Key.Left||e.Key==Key.Right)
            {int index=at<0?(e.Key==Key.Left?nodes.Count-1:0):Math.Max(0,Math.Min(nodes.Count-1,at+(e.Key==Key.Left?-1:1)));var node=nodes[index];Pick(node.Last,node.First,node.Last);e.Handled=true;}
            else if(at>=0&&(e.Key==Key.Up||e.Key==Key.Down||e.Key==Key.Home||e.Key==Key.End))
            {var node=nodes[at];long number=e.Key==Key.Home?node.First:e.Key==Key.End?node.Last:Math.Max(node.First,Math.Min(node.Last,selected+(e.Key==Key.Up?-1:1)));Pick(number,node.First,node.Last);e.Handled=true;}
        }
        // All searches operate on immutable sorted indexes. Work is bounded by
        // viewport pixels, not report count (at most 768 time buckets per draw).
        private static int Lower(long[] times,long time)
        {int lo=0,hi=times.Length;while(lo<hi){int mid=lo+(hi-lo)/2;if(times[mid]<time)lo=mid+1;else hi=mid;}return lo;}
        private static int Upper(long[] times,long time)
        {int lo=0,hi=times.Length;while(lo<hi){int mid=lo+(hi-lo)/2;if(times[mid]<=time)lo=mid+1;else hi=mid;}return lo;}
        private int MarkLower(long time)
        {int lo=0,hi=snapshot.Marks.Count;while(lo<hi){int mid=lo+(hi-lo)/2;if(snapshot.Marks[mid].From<time)lo=mid+1;else hi=mid;}return lo;}
        private int MarkUpper(long time)
        {int lo=0,hi=snapshot.Marks.Count;while(lo<hi){int mid=lo+(hi-lo)/2;if(snapshot.Marks[mid].From<=time)lo=mid+1;else hi=mid;}return lo;}
        private MilestoneMark MarkAt(long index)
        {int lo=0,hi=snapshot.Marks.Count-1;while(lo<=hi){int mid=lo+(hi-lo)/2;var mark=snapshot.Marks[mid];if(index<mark.First)hi=mid-1;else if(index>mark.Last)lo=mid+1;else return mark;}return null;}
        private long TotalAt(long time){var series=snapshot.Curve;int index=Upper(series.Times,time)-1;return index<0?0:series.Totals[index];}
        private double X(long time){return left+width*((double)time-from)/Math.Max(1,(double)to-from);}
        private double Y(long total){return bottom-height*((double)total-yMin)/Math.Max(1,yMax-yMin);}
        private long TimeAt(double fraction){return from+(long)Math.Floor(((double)to-from)*fraction);}
        private static SolidColorBrush Copy(SolidColorBrush brush){var copy=new SolidColorBrush(brush.Color);copy.Freeze();return copy;}
        private FormattedText Text(string value,double size,Brush brush,double limit)
        {var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,face,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);if(limit>0){text.MaxTextWidth=limit;text.Trimming=TextTrimming.CharacterEllipsis;}return text;}
        private void Write(DrawingContext dc,string value,double x,double y,Brush brush,double size,double limit){dc.DrawText(Text(value,size,brush,limit),new Point(x,y));}
        private static string DateText(long time,bool shortRange)
        {return DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString(shortRange?"HH:mm":"MM-dd",CultureInfo.InvariantCulture);}
        private void BuildNodes()
        {
            nodes.Clear();if(snapshot.Marks.Count==0)return;int count=Math.Max(1,Math.Min(64,(int)(width/18)));long unit=step/MilestoneEngine.Base;
            for(int i=0;i<count;i++)
            {
                long start=TimeAt(i/(double)count),end=i==count-1?to:TimeAt((i+1)/(double)count);if(start>drawEnd)break;end=Math.Min(end,drawEnd);
                int begin=MarkLower(start),finish=i==count-1||end==drawEnd?MarkUpper(end):MarkLower(end);if(begin>=finish)continue;
                long a=snapshot.Marks[begin].First,b=snapshot.Marks[finish-1].Last,first=a/unit+(a%unit==0?0:1),last=b/unit;if(first>last)continue;
                var boundary=MarkAt(last*unit);if(boundary==null)continue;long total=TotalAt(boundary.From);var node=new MilestoneCurveNode{First=first,Last=last,Time=boundary.From,Total=total,Boundary=boundary,Position=new Point(X(boundary.From),Y(total))};
                if(nodes.Count>0&&node.Position.X-nodes[nodes.Count-1].Position.X<18)
                {var prior=nodes[nodes.Count-1];prior.Last=node.Last;prior.Time=node.Time;prior.Total=node.Total;prior.Boundary=node.Boundary;prior.Position=node.Position;}
                else nodes.Add(node);
            }
        }
        private List<Sample> Samples()
        {
            var series=snapshot.Curve;var result=new List<Sample>();result.Add(new Sample(from,Baseline));int count=Math.Max(1,Math.Min(768,(int)Math.Ceiling(width)));int consumed=Lower(series.Times,from);
            for(int i=0;i<count;i++)
            {
                long end=i==count-1?to:TimeAt((i+1)/(double)count);end=Math.Min(end,drawEnd);int finish=i==count-1||end==drawEnd?Upper(series.Times,end):Lower(series.Times,end);
                if(finish>consumed){result.Add(new Sample(series.Times[consumed],series.Totals[consumed]));if(finish-1>consumed)result.Add(new Sample(series.Times[finish-1],series.Totals[finish-1]));consumed=finish;}
                if(end==drawEnd)break;
            }
            result.Add(new Sample(drawEnd,TotalAt(drawEnd)));
            // Include each displayed node's actual report value so even dense
            // grouped markers sit on the monotone polyline, never an interpolated crossing.
            foreach(var node in nodes)result.Add(new Sample(node.Time,node.Total));
            result.Sort(delegate(Sample a,Sample b){int order=a.Time.CompareTo(b.Time);return order==0?a.Total.CompareTo(b.Total):order;});SampleCount=result.Count;return result;
        }
        private void Build()
        {
            drawingSize=RenderSize;revision=Theme.Revision;DrawingBuilds++;drawing=new DrawingGroup();curve=null;nodes.Clear();SampleCount=0;Baseline=0;ShowsEndpoint=ShowsCurrent(snapshot,from,to);
            left=ActualWidth<340?46:54;top=42;bottom=ActualHeight-32;width=Math.Max(1,ActualWidth-left-12);height=Math.Max(1,bottom-top);
            var muted=Copy(Theme.Muted);var line=Copy(Theme.Line);var accent=Copy(Theme.Accent);var surface=Copy(Theme.Surface);
            using(var dc=drawing.Open())
            {
                Write(dc,"累计 Tokens",left,8,muted,11,Math.Max(1,width*.43));
                if(snapshot==null||snapshot.Curve==null||snapshot.Curve.Times.Length==0||drawEnd<from)
                {ShowsEndpoint=false;Write(dc,snapshot==null?"等待读取用量…":snapshot.TotalTokens==0?"暂无用量记录":"此时间范围内暂无记录",left,112,muted,12,width);}
                else
                {
                    int prefix=Lower(snapshot.Curve.Times,from)-1;Baseline=prefix<0?0:snapshot.Curve.Totals[prefix];long maximum=TotalAt(drawEnd);
                    double range=(double)maximum-Baseline;if(range<=0)range=Math.Max(1,maximum*.08);
                    // Near Int64.MaxValue, a token-sized double increment cannot
                    // advance a tick. Use representable spacing and a bounded loop.
                    range=Math.Max(range,Math.Max(1,Math.Abs((double)maximum)*1e-12));double raw=range/3,power=Math.Pow(10,Math.Floor(Math.Log10(raw))),scaled=raw/power,tick=(scaled<=1?1:scaled<=2?2:scaled<=5?5:10)*power;
                    yMin=Math.Max(0,Math.Floor(Baseline/tick)*tick);yMax=Math.Ceiling(maximum/tick)*tick;if(yMax<=yMin)yMax=yMin+tick;
                    int ticks=Math.Min(8,(int)Math.Ceiling((yMax-yMin)/tick));for(int i=0;i<=ticks;i++)
                    {double value=yMin+i*tick;if(value>yMax+tick*.01)break;double y=bottom-height*(value-yMin)/(yMax-yMin);dc.DrawLine(new Pen(line,.7),new Point(left,y),new Point(left+width,y));Write(dc,TokenText.Axis(value),0,y-7,muted,10,left-5);}
                    bool shortRange=(double)to-from<=86400;int labels=width<350?2:width<580?3:5;
                    for(int i=0;i<labels;i++){long time=TimeAt(i/(double)(labels-1));var label=Text(DateText(time,shortRange),10,muted,0);double x=i==0?left:i==labels-1?left+width-label.Width:X(time)-label.Width/2;dc.DrawText(label,new Point(x,bottom+10));}
                    BuildNodes();var samples=Samples();curve=new StreamGeometry();
                    using(var path=curve.Open()){path.BeginFigure(new Point(X(samples[0].Time),Y(samples[0].Total)),false,false);for(int i=1;i<samples.Count;i++)path.LineTo(new Point(X(samples[i].Time),Y(samples[i].Total)),true,false);}curve.Freeze();
                    var fill=new StreamGeometry();using(var path=fill.Open()){path.BeginFigure(new Point(X(samples[0].Time),bottom),true,true);foreach(var sample in samples)path.LineTo(new Point(X(sample.Time),Y(sample.Total)),true,false);path.LineTo(new Point(X(drawEnd),bottom),true,false);}fill.Freeze();
                    var wash=new LinearGradientBrush(Color.FromArgb(38,accent.Color.R,accent.Color.G,accent.Color.B),Color.FromArgb(2,accent.Color.R,accent.Color.G,accent.Color.B),new Point(0,0),new Point(0,1));wash.Freeze();dc.DrawGeometry(wash,null,fill);dc.DrawGeometry(null,new Pen(accent,1.8){LineJoin=PenLineJoin.Round},curve);
                    double labelEnd=left-6;
                    foreach(var node in nodes)
                    {
                        double radius=node.First==node.Last?3.7:5;dc.DrawEllipse(surface,new Pen(accent,1.5),node.Position,radius,radius);
                        if(node.Boundary.Precision!=0)dc.DrawEllipse(accent,null,node.Position,1.2,1.2);
                        string caption=(node.First==node.Last?"":TokenText.Compact(node.First*step)+"–")+TokenText.Compact(node.Last*step);var label=Text(caption,10,muted,Math.Min(width,130));double x=Math.Max(left,Math.Min(left+width-label.Width,node.Position.X-label.Width/2));
                        if(x>=labelEnd+10&&node.Position.Y-top>24){dc.DrawText(label,new Point(x,node.Position.Y-22));labelEnd=x+label.Width;}
                    }
                    if(ShowsEndpoint)
                    {
                        dc.DrawEllipse(accent,null,new Point(X(snapshot.ObservedAt),Y(snapshot.TotalTokens)),3.5,3.5);
                        string progress="第"+(snapshot.Completed(step)+1)+"段 · "+((snapshot.TotalTokens%step)*100.0/step).ToString("0.#",CultureInfo.InvariantCulture)+"%";var label=Text(progress,11,accent,width*.57);dc.DrawText(label,new Point(left+width-label.Width,8));
                    }
                }
            }
            drawing.Freeze();
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);if(!IsVisible||ActualWidth<80||ActualHeight<80)return;dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
            if(drawing==null||drawingSize!=RenderSize||revision!=Theme.Revision){ClearHover();Build();}dc.DrawDrawing(drawing);
            if(selected==0||snapshot==null||curve==null||selected>snapshot.Completed(step))return;var stage=snapshot.Stage(step,selected,snapshot.ObservedAt);if(stage.Start==null||stage.End==null)return;
            double x1=Math.Max(left,X(stage.Start.From)),x2=Math.Min(left+width,X(stage.End.From));var color=Theme.Accent.Color;
            if(x2>x1)
            {dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(15,color.R,color.G,color.B)),null,new Rect(x1,top,x2-x1,height));dc.PushClip(new RectangleGeometry(new Rect(x1,top-5,x2-x1,height+10)));dc.DrawGeometry(null,new Pen(Theme.Accent,3.5){LineJoin=PenLineJoin.Round},curve);dc.Pop();}
            if(stage.End.From>=from&&stage.End.From<=drawEnd){var point=new Point(X(stage.End.From),Y(TotalAt(stage.End.From)));dc.DrawEllipse(Theme.Surface,new Pen(Theme.Accent,2),point,7,7);dc.DrawEllipse(Theme.Accent,null,point,3,3);}
        }
        private int Hit(Point point)
        {
            int lo=0,hi=nodes.Count;while(lo<hi){int mid=lo+(hi-lo)/2;if(nodes[mid].Position.X<point.X)lo=mid+1;else hi=mid;}
            for(int i=Math.Max(0,lo-1);i<=Math.Min(nodes.Count-1,lo);i++){Vector delta=point-nodes[i].Position;if(delta.LengthSquared<=144)return i;}return -1;
        }
        private string NodeTip(MilestoneCurveNode node)
        {
            string title=node.First==node.Last?"第"+node.Last+"个"+MilestonePanel.StepText(step):"第 "+node.First+"–"+node.Last+" 个"+MilestonePanel.StepText(step)+"（"+(node.Last-node.First+1)+"个节点）";
            string precision=node.Boundary.Precision==2?"\n日期汇总按范围起点定位；具体跨越时刻不确定。":node.Boundary.Precision==1?"\n推断记录：图中位置为记录锚点，真实跨越时刻未知。":"";
            return title+"\n"+(node.First==node.Last?"报送时间：":"末节点报送时间：")+MilestonePanel.BoundaryText(node.Boundary)+precision+"\n该时间点累计 "+node.Total.ToString("N0",CultureInfo.InvariantCulture)+" Tokens（同秒记录合并）"+(node.First==node.Last?"\n用时 "+MilestonePanel.DurationText(snapshot.Stage(step,node.Last,snapshot.ObservedAt)):"\n相邻或同批节点已合并；点击后可逐个查看耗时。");
        }
    }
}
