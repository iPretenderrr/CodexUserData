using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
        // One outer ScrollViewer; the optional record list is built only when expanded.
    internal sealed class MilestonePanel : StackPanel, IDisposable
    {
        private readonly Func<CancellationToken,Task<MilestoneSnapshot>> load;
        private readonly Func<string> currentScope;
        private readonly Action<long> rememberStep;
        private readonly Dictionary<long,Button> steps=new Dictionary<long,Button>();
        private readonly TextBlock source=Label("",11),origin=Label("",11),status=Label("",11),currentTitle=Label("",12),currentText=Label("",12),currentAmount=Label("",30),pageText=Label("",11);
        private readonly Grid current=new Grid();
        private readonly StackPanel history=new StackPanel(),rows=new StackPanel();
        private readonly MilestoneProgressRing progress=new MilestoneProgressRing();
        private readonly Expander records=new Expander{Header="展开记录",Margin=new Thickness(0,12,0,0),Foreground=Theme.Muted,FontSize=12};
        private readonly Border explanation=new Border{Visibility=Visibility.Collapsed,Background=Theme.Surface,CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,8,0,12)};
        private readonly TextBlock empty=Label("暂无用量记录",12);
        private readonly Border detail=new Border{Visibility=Visibility.Collapsed,Background=Theme.Surface,CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,8,0,4)};
        private readonly MilestoneDurationChart chart=new MilestoneDurationChart();
        private readonly Button older,newer,clear;
        private readonly DispatcherTimer timer;
        private MilestoneSnapshot snapshot;
        private CancellationTokenSource cancellation;
        private Window owner;
        private bool loading,pending,disposed,narrow;
        private int generation;
        private long step,selected,pageEnd;
        private List<MilestoneStage> visible=new List<MilestoneStage>();
        internal MilestonePanel(Func<CancellationToken,Task<MilestoneSnapshot>> loader,Func<string> scope,long selectedStep,Action<long> remember)
        {
            load=loader;currentScope=scope;rememberStep=remember;step=Array.IndexOf(MilestoneEngine.Steps,selectedStep)>=0?selectedStep:MilestoneEngine.Steps[0];
            var choices=new WrapPanel{Margin=new Thickness(0,0,0,9)};
            foreach(long value in MilestoneEngine.Steps)
            {
                long choice=value;var button=Theme.Button("每 "+StepText(value),"每 "+StepText(value)+" 一个阶段",68);button.Margin=new Thickness(0,0,5,5);
                button.Click+=delegate{if(step==choice)return;SetStep(choice);if(rememberStep!=null)rememberStep(step);};steps.Add(value,button);choices.Children.Add(button);
            }
            Children.Add(choices);
            var metadata=new DockPanel();var help=Theme.Button("ⓘ","查看统计口径",28);help.ToolTip="统计说明";help.Click+=delegate{explanation.Visibility=explanation.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;};DockPanel.SetDock(help,Dock.Right);metadata.Children.Add(help);
            status.Margin=new Thickness(8,0,5,0);status.TextWrapping=TextWrapping.NoWrap;DockPanel.SetDock(status,Dock.Right);metadata.Children.Add(status);source.TextWrapping=TextWrapping.NoWrap;source.TextTrimming=TextTrimming.CharacterEllipsis;metadata.Children.Add(source);Children.Add(metadata);
            var notes=new StackPanel();notes.Children.Add(origin);notes.Children.Add(Label("全部可用历史 · 耗时包含离线和未使用时间。\n旧记录增减后会重新计算；一次记录跨越多个节点时，无法拆分的耗时显示未知。\n日期汇总以范围线表示可能耗时。",11));explanation.Child=notes;Children.Add(explanation);
            current.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});current.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            progress.Margin=new Thickness(0,0,20,0);current.Children.Add(progress);var summary=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(summary,1);current.Children.Add(summary);
            currentAmount.Foreground=Theme.Ink;currentAmount.FontWeight=FontWeights.SemiBold;currentAmount.Margin=new Thickness(0,4,0,6);summary.Children.Add(currentTitle);summary.Children.Add(currentAmount);summary.Children.Add(currentText);current.Margin=new Thickness(0,18,0,22);Children.Add(current);
            Children.Add(empty);Children.Add(history);
            var chartHeader=new DockPanel();var hint=Label("越低越快",11);DockPanel.SetDock(hint,Dock.Right);chartHeader.Children.Add(hint);var chartTitle=Label("每段耗时",15);chartTitle.Foreground=Theme.Ink;chartHeader.Children.Add(chartTitle);history.Children.Add(chartHeader);history.Children.Add(chart);
            var pager=new DockPanel{Margin=new Thickness(0,10,0,6)};var buttons=new StackPanel{Orientation=Orientation.Horizontal};
            older=Theme.Button("←","查看较早的 30 个阶段",30);newer=Theme.Button("→","查看较新的 30 个阶段",30);buttons.Children.Add(older);buttons.Children.Add(newer);DockPanel.SetDock(buttons,Dock.Right);pager.Children.Add(buttons);pager.Children.Add(pageText);history.Children.Add(pager);
            older.Click+=delegate{if(visible.Count==0)return;pageEnd=visible[0].Number-1;selected=0;Render(true);};
            newer.Click+=delegate{if(snapshot==null)return;long count=snapshot.Completed(step);pageEnd=pageEnd>=count-30?0:pageEnd+30;selected=0;Render(true);};
            clear=Theme.Button("清除","清除选中阶段",48);clear.HorizontalAlignment=HorizontalAlignment.Right;clear.Click+=delegate{Select(0);};
            history.Children.Add(detail);records.Content=rows;history.Children.Add(records);records.Expanded+=delegate{records.Header="收起记录";RenderRows();};records.Collapsed+=delegate{records.Header="展开记录";rows.Children.Clear();};chart.Pick+=Select;
            SizeChanged+=delegate{bool value=ActualWidth<480;if(narrow!=value){narrow=value;progress.Width=progress.Height=narrow?84:108;progress.Margin=new Thickness(0,0,narrow?12:20,0);currentAmount.FontSize=narrow?22:30;RenderRows();RenderDetail();}};
            timer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMinutes(1)};timer.Tick+=delegate{UpdateCurrent();Refresh();};
            Loaded+=delegate{if(disposed)return;owner=Window.GetWindow(this);if(owner!=null)owner.StateChanged+=OwnerStateChanged;Refresh();};Unloaded+=delegate{Suspend();DetachOwner();};
            IsVisibleChanged+=delegate{if(IsVisible){if(!disposed)Refresh();}else Suspend();};
            Render(true);
        }
        private static TextBlock Label(string text,double size)
        {var label=Theme.Text(text,size,Theme.Muted);label.TextWrapping=TextWrapping.Wrap;return label;}
        private static long Now(){return LocalCodexUsage.Unix(DateTime.UtcNow);}
        internal void SetStep(long value)
        {if(disposed||step==value)return;MilestoneEngine.CheckStep(value);step=value;pageEnd=selected=0;Render(true);}
        internal static string StepText(long value){return (value/100000000L).ToString(CultureInfo.InvariantCulture)+"亿";}
        internal static string Duration(long seconds)
        {
            if(seconds<60)return "<1分钟";long days=seconds/86400,hours=seconds%86400/3600,minutes=seconds%3600/60;
            return (days>0?days+"天 ":"")+(hours>0?hours+"小时 ":"")+(minutes>0?minutes+"分钟":"");
        }
        internal static string DurationText(MilestoneStage stage)
        {
            if(!stage.DurationKnown)return stage.SameBatch?"未知 · 同批记录跨越多个阶段":"未知 · 起止时间无法确定";
            return stage.DurationMin==stage.DurationMax?Duration(stage.DurationMin):Duration(stage.DurationMin)+" ～ "+Duration(stage.DurationMax);
        }
        internal static string BoundaryText(MilestoneBoundary boundary)
        {
            if(boundary==null)return "暂无记录";
            if(boundary.Precision==1)return "时间未知（推断记录）";
            string from=DateTimeOffset.FromUnixTimeSeconds(boundary.From).ToLocalTime().ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
            if(boundary.Precision==2)return from+" ～ "+DateTimeOffset.FromUnixTimeSeconds(boundary.To).ToLocalTime().ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)+"（日期范围）";
            return from;
        }
        internal void SourceChanged()
        {
            if(disposed)return;generation++;if(cancellation!=null)cancellation.Cancel();snapshot=null;pageEnd=selected=0;Render(true);Refresh();
        }
        private void Suspend()
        {generation++;pending=false;timer.Stop();chart.ClearHover();if(cancellation!=null)cancellation.Cancel();}
        private bool Active {get{return !disposed&&IsLoaded&&IsVisible&&(owner==null||owner.WindowState!=WindowState.Minimized);}}
        private void OwnerStateChanged(object sender,EventArgs e){if(Active)Refresh();else Suspend();}
        private void DetachOwner(){if(owner!=null)owner.StateChanged-=OwnerStateChanged;owner=null;}
        internal void Refresh()
        {
            if(!Active)return;
            timer.Start();if(snapshot!=null&&!String.Equals(snapshot.Scope,currentScope(),StringComparison.Ordinal)){SourceChanged();return;}
            pending=true;if(!loading)Pump();
        }
        // Cancellation alone cannot reject a provider that completes late. Generation and scope
        // checks cover source switches, hidden tabs and a closed/reopened owning window.
        private async void Pump()
        {
            if(!pending||!Active)return;
            pending=false;loading=true;int version=generation;string scope=currentScope();var request=new CancellationTokenSource();cancellation=request;
            if(snapshot==null)SetStatus("正在读取…",null);
            MilestoneSnapshot result=null;Exception error=null;
            try{result=await load(request.Token);}catch(OperationCanceledException){}catch(Exception ex){error=ex;}
            Action finish=delegate
            {
                bool accept=Active&&version==generation&&!request.IsCancellationRequested&&String.Equals(scope,currentScope(),StringComparison.Ordinal);
                if(accept)
                {
                    if(error!=null)SetStatus("同步失败 ⓘ",error.Message);
                    else if(result!=null&&String.Equals(result.Scope,scope,StringComparison.Ordinal))
                    {
                        bool changed=snapshot==null||snapshot.Signature!=result.Signature||snapshot.TotalTokens!=result.TotalTokens;
                        snapshot=result;Render(changed);
                    }
                    else SetStatus("等待同步…",null);
                }
                if(Object.ReferenceEquals(cancellation,request))cancellation=null;request.Dispose();loading=false;
                if(pending&&Active)Pump();
            };
            if(Dispatcher.HasShutdownStarted){request.Dispose();return;}
            if(Dispatcher.CheckAccess())finish();else try{Dispatcher.Invoke(finish);}catch(OperationCanceledException){request.Dispose();}
        }
        private void Render(bool rebuild)
        {
            foreach(var choice in steps){choice.Value.Background=choice.Key==step?Theme.Hover:Brushes.Transparent;choice.Value.Foreground=choice.Key==step?Theme.Accent:Theme.Muted;}
            source.Text=snapshot==null?"全部历史":snapshot.Source+" · 全部历史";origin.Text="统计起点："+BoundaryText(snapshot==null?null:snapshot.Origin);
            SetStatus(snapshot==null?"等待同步…":!String.IsNullOrEmpty(snapshot.Warning)?"统计提示 ⓘ":DateTimeOffset.FromUnixTimeSeconds(snapshot.ObservedAt).ToLocalTime().ToString("HH:mm",CultureInfo.InvariantCulture)+" 更新",snapshot==null?null:snapshot.Warning);
            bool has=snapshot!=null&&snapshot.TotalTokens>0;current.Visibility=has?Visibility.Visible:Visibility.Collapsed;empty.Visibility=has||snapshot==null?Visibility.Collapsed:Visibility.Visible;
            long completed=has?snapshot.Completed(step):0;history.Visibility=completed>0?Visibility.Visible:Visibility.Collapsed;
            if(has)
                UpdateCurrent();
            if(rebuild)
            {
                visible=new List<MilestoneStage>();long end=pageEnd==0?completed:Math.Min(pageEnd,completed);if(pageEnd>completed)pageEnd=0;
                for(long number=Math.Max(1,end-29);number<=end;number++)visible.Add(snapshot.Stage(step,number,Now()));
                if(visible.Count==0||selected<visible[0].Number||selected>visible[visible.Count-1].Number)selected=0;
                chart.SetStages(visible);RenderRows();
            }
            older.IsEnabled=visible.Count>0&&visible[0].Number>1;newer.IsEnabled=pageEnd>0&&pageEnd<completed;
            pageText.Text=visible.Count==0?"":"第 "+visible[0].Number+"–"+visible[visible.Count-1].Number+" 段 · 已完成 "+completed+" 段";
            RenderDetail();
        }
        private void SetStatus(string text,string warning)
        {status.Text=text;status.Foreground=String.IsNullOrEmpty(warning)?Theme.Muted:Theme.Warning;status.ToolTip=String.IsNullOrEmpty(warning)?null:warning;AutomationProperties.SetHelpText(status,warning??"");}
        private void UpdateCurrent()
        {
            if(snapshot==null||snapshot.TotalTokens==0)return;
            var stage=snapshot.Stage(step,snapshot.Completed(step)+1,Now());currentTitle.Text="第 "+stage.Number+" 段 · 进行中";
            currentAmount.Text=TokenText.Compact(stage.Tokens)+" / "+StepText(step);currentAmount.ToolTip=stage.Tokens.ToString("N0",CultureInfo.InvariantCulture)+" / "+step.ToString("N0",CultureInfo.InvariantCulture)+" Tokens";
            currentText.Text="已用时  "+(stage.DurationKnown?DurationText(stage):"未知");currentText.ToolTip=stage.DurationKnown?"自然经过时间，包含未使用和离线时间":DurationText(stage);progress.Set(stage.Tokens/(double)step);
        }
        private void RenderRows()
        {
            rows.Children.Clear();
            if(!records.IsExpanded)return;
            for(int i=visible.Count-1;i>=0;i--)
            {
                var stage=visible[i];long number=stage.Number;var button=Theme.Button("","查看第 "+number+" 阶段详情",0);button.Height=Double.NaN;button.MinHeight=34;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Padding=new Thickness(8,7,8,7);button.Margin=new Thickness(0,1,0,1);button.Tag=number;
                button.Background=number==selected?Theme.Hover:Brushes.Transparent;
                var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(narrow?75:90)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                var title=Label("第 "+number+" 阶段",12);title.Foreground=Theme.Ink;grid.Children.Add(title);
                var duration=Label(DurationText(stage),12);Grid.SetColumn(duration,1);grid.Children.Add(duration);
                button.Content=grid;button.Click+=delegate{Select(number);};rows.Children.Add(button);
            }
        }
        private void Select(long number)
        {selected=selected==number?0:number;chart.SetSelected(selected);foreach(Button row in rows.Children)row.Background=(long)row.Tag==selected?Theme.Hover:Brushes.Transparent;RenderDetail();}
        private void RenderDetail()
        {
            chart.SetSelected(selected);detail.Visibility=selected==0||snapshot==null?Visibility.Collapsed:Visibility.Visible;if(detail.Visibility==Visibility.Collapsed){detail.Child=null;return;}
            var stage=snapshot.Stage(step,selected,Now());var body=new StackPanel();var heading=new DockPanel();DockPanel.SetDock(clear,Dock.Right);var old=clear.Parent as Panel;if(old!=null)old.Children.Remove(clear);heading.Children.Add(clear);var title=Label("第 "+selected+" 段 · "+TokenText.Compact(stage.FromTokens)+" → "+TokenText.Compact(stage.ToTokens),12);title.Foreground=Theme.Ink;heading.Children.Add(title);body.Children.Add(heading);
            var duration=Label(stage.DurationKnown?DurationText(stage):"耗时未知",20);duration.Foreground=Theme.Accent;duration.Margin=new Thickness(0,7,0,7);body.Children.Add(duration);
            body.Children.Add(Label(BoundaryText(stage.Start)+(narrow?"\n→ ":"  →  ")+BoundaryText(stage.End),11));if(!stage.DurationKnown)body.Children.Add(Label(stage.SameBatch?"同一批记录跨过多个节点，无法拆分耗时。":"起止记录为推断用量，无法确定耗时。",11));detail.Child=body;
        }
        public void Dispose(){if(disposed)return;disposed=true;Suspend();DetachOwner();snapshot=null;}
    }

    internal sealed class MilestoneProgressRing : FrameworkElement
    {
        private double fraction;
        private Geometry arc;
        internal MilestoneProgressRing(){Width=Height=108;Theme.Watch(this);AutomationProperties.SetName(this,"当前阶段完成比例");}
        internal void Set(double value)
        {
            value=Math.Max(0,Math.Min(1,value));if(fraction==value)return;fraction=value;
            // Unit geometry is reused across layout changes; there is no animation clock.
            var path=new StreamGeometry();using(var c=path.Open()){c.BeginFigure(new Point(0,-1),false,false);double angle=value*Math.PI*2;c.ArcTo(new Point(Math.Sin(angle),-Math.Cos(angle)),new Size(1,1),0,value>.5,SweepDirection.Clockwise,true,false);}path.Freeze();arc=path;
            AutomationProperties.SetItemStatus(this,(fraction*100).ToString("0.#",CultureInfo.InvariantCulture)+"%");InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            double radius=Math.Min(ActualWidth,ActualHeight)/2-6;var center=new Point(ActualWidth/2,ActualHeight/2);dc.DrawEllipse(null,new Pen(Theme.Line,7),center,radius,radius);
            var pen=new Pen(Theme.Accent,7){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            if(fraction>=.999999)dc.DrawEllipse(null,pen,center,radius,radius);
            else if(fraction>0&&arc!=null){dc.PushTransform(new TranslateTransform(center.X,center.Y));dc.PushTransform(new ScaleTransform(radius,radius));dc.DrawGeometry(null,new Pen(Theme.Accent,7/radius){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},arc);dc.Pop();dc.Pop();}
            string value=(fraction*100).ToString("0.#",CultureInfo.InvariantCulture)+"%";var face=new Typeface("Segoe UI, Microsoft YaHei UI");double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var number=new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,ActualWidth<100?21:26,Theme.Ink,dpi);dc.DrawText(number,new Point(center.X-number.Width/2,center.Y-number.Height/2-6));
            var caption=new FormattedText("本段完成",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,face,10,Theme.Muted,dpi);dc.DrawText(caption,new Point(center.X-caption.Width/2,center.Y+15));
        }
    }

    internal sealed class MilestoneDurationChart : FrameworkElement
    {
        private List<MilestoneStage> stages=new List<MilestoneStage>();
        private DrawingGroup drawing;
        private Size drawingSize;
        private int revision=-1,hover=-1;
        private long selected;
        private readonly ToolTip tip=new ToolTip();
        internal event Action<long> Pick;
        internal int DrawingBuilds {get;private set;}
        internal MilestoneDurationChart()
        {
            Height=246;ClipToBounds=true;Focusable=true;Cursor=Cursors.Hand;Margin=new Thickness(0,12,0,4);Theme.Watch(this);AutomationProperties.SetName(this,"每段耗时柱图，左右方向键选择，Esc清除");
            MouseMove+=delegate(object sender,MouseEventArgs e){int index=Hit(e.GetPosition(this));if(index==hover)return;hover=index;tip.IsOpen=false;if(index>=0){tip.Content="第 "+stages[index].Number+" 阶段 · "+MilestonePanel.DurationText(stages[index]);tip.Background=Theme.Surface;tip.Foreground=Theme.Ink;tip.BorderBrush=Theme.Line;tip.PlacementTarget=this;tip.IsOpen=true;}InvalidateVisual();};
            MouseLeave+=delegate{ClearHover();};MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){int index=Hit(e.GetPosition(this));if(index>=0&&Pick!=null){Focus();Pick(stages[index].Number);e.Handled=true;}};
            KeyDown+=delegate(object sender,KeyEventArgs e){if(Pick==null)return;if(e.Key==Key.Escape){Pick(0);e.Handled=true;}else if((e.Key==Key.Left||e.Key==Key.Right)&&stages.Count>0){int at=stages.FindIndex(s=>s.Number==selected);int next=at<0?(e.Key==Key.Left?stages.Count-1:0):Math.Max(0,Math.Min(stages.Count-1,at+(e.Key==Key.Left?-1:1)));if(at!=next)Pick(stages[next].Number);e.Handled=true;}};
            IsVisibleChanged+=delegate{if(!IsVisible)ClearHover();};Unloaded+=delegate{ClearHover();};
        }
        internal void ClearHover(){hover=-1;tip.IsOpen=false;InvalidateVisual();}
        internal void SetSelected(long value){if(selected==value)return;selected=value;InvalidateVisual();}
        internal void SetStages(List<MilestoneStage> value){stages=value;drawing=null;ClearHover();}
        private int Hit(Point p){if(stages.Count==0||p.X<48||p.X>=ActualWidth-8||p.Y<22||p.Y>ActualHeight-22)return -1;return Math.Min(stages.Count-1,(int)((p.X-48)/Math.Max(1,ActualWidth-56)*stages.Count));}
        private static SolidColorBrush Copy(SolidColorBrush brush){var copy=new SolidColorBrush(brush.Color);copy.Freeze();return copy;}
        private void Text(DrawingContext dc,string text,Point at,Brush brush,double size){dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI, Microsoft YaHei UI"),size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip),at);}
        private static string TickText(double seconds){return seconds>=86400?(seconds/86400).ToString("0.#")+"天":seconds>=3600?(seconds/3600).ToString("0.#")+"时":seconds>=60?(seconds/60).ToString("0.#")+"分":seconds.ToString("0.#")+"秒";}
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);if(!IsVisible||ActualWidth<60||stages.Count==0)return;
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
            double left=48,bottom=ActualHeight-28,height=ActualHeight-58,width=ActualWidth-56,slot=width/stages.Count;
            if(drawing==null||drawingSize!=RenderSize||revision!=Theme.Revision)
            {
                double max=1;bool known=false;foreach(var stage in stages)if(stage.DurationKnown){known=true;max=Math.Max(max,stage.DurationMax);}
                double unit=max>=172800?86400:max>=7200?3600:max>=120?60:1,raw=max/unit/3,power=Math.Pow(10,Math.Floor(Math.Log10(raw))),scaled=raw/power;
                double tick=(scaled<=1?1:scaled<=2?2:scaled<=5?5:10)*power*unit;max=Math.Ceiling(max/tick)*tick;
                var accent=Copy(Theme.Accent);var muted=Copy(Theme.Muted);var line=Copy(Theme.Line);var soft=Color.FromArgb(110,accent.Color.R,accent.Color.G,accent.Color.B);var fill=new LinearGradientBrush(accent.Color,soft,new Point(0,0),new Point(0,1));fill.Freeze();drawing=new DrawingGroup();
                using(var context=drawing.Open())
                {
                    if(known)for(double value=0;value<=max+tick*.01;value+=tick){double y=bottom-height*value/max;context.DrawLine(new Pen(line,.6),new Point(left,y),new Point(left+width,y));Text(context,TickText(value),new Point(0,y-7),muted,10);}
                    else{context.DrawLine(new Pen(line,1),new Point(left,bottom),new Point(left+width,bottom));Text(context,"这些阶段暂时无法确定耗时",new Point(left,6),muted,11);}
                    bool unknown=false,interval=false;int labels=Math.Max(2,(int)(width/65)),stride=Math.Max(1,(int)Math.Ceiling(stages.Count/(double)labels));
                    for(int i=0;i<stages.Count;i++)
                    {
                        var stage=stages[i];double w=Math.Min(42,Math.Max(3,slot*.64)),x=left+i*slot+(slot-w)/2;
                        if(!stage.DurationKnown){unknown=true;context.DrawEllipse(null,new Pen(muted,1),new Point(x+w/2,bottom-6),3,3);}
                        else
                        {
                            double low=height*stage.DurationMin/max,high=height*stage.DurationMax/max;
                            // Unknown times never become zero bars. Known sub-minute observations
                            // retain a visible minimum; ranges show lower bound plus an upper whisker.
                            context.DrawRoundedRectangle(fill,null,new Rect(x,bottom-Math.Max(2,low),w,Math.Max(2,low)),3,3);
                            if(stage.DurationMax!=stage.DurationMin){interval=true;double center=x+w/2;context.DrawLine(new Pen(accent,1.5),new Point(center,bottom-high),new Point(center,bottom-low));context.DrawLine(new Pen(accent,1.5),new Point(x,bottom-high),new Point(x+w,bottom-high));}
                            if(stages.Count<=8)Text(context,TickText(stage.DurationMax),new Point(x-2,Math.Max(7,bottom-high-19)),muted,10);
                        }
                        if(i%stride==0||i==stages.Count-1&&stages.Count-1-(stages.Count-1)/stride*stride>stride/2)Text(context,stage.Number.ToString(CultureInfo.InvariantCulture),new Point(x,bottom+8),muted,10);
                    }
                    Text(context,(unknown?"○ 耗时未知   ":"")+(interval?"↕ 时间范围":""),new Point(left,2),muted,10);
                }
                drawing.Freeze();drawingSize=RenderSize;revision=Theme.Revision;DrawingBuilds++;
            }
            dc.DrawDrawing(drawing);
            for(int i=0;i<stages.Count;i++)if(i==hover||stages[i].Number==selected){var color=Theme.Accent.Color;var shade=new SolidColorBrush(Color.FromArgb(stages[i].Number==selected?(byte)35:(byte)18,color.R,color.G,color.B));dc.DrawRoundedRectangle(shade,null,new Rect(left+i*slot,22,slot,bottom-20),4,4);if(stages[i].Number==selected)dc.DrawEllipse(Theme.Accent,null,new Point(left+(i+.5)*slot,bottom+25),2,2);}
        }
    }
}
