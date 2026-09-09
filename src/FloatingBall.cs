using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUserData
{
    // A view of the existing ledger/quota state, never a second scanner or quota client.
    internal sealed class FloatingBall : Window, IDisposable
    {
        private readonly Func<Preferences> preferences;
        private readonly Action save,restore,exit;
        private readonly Border shell;
        private TextBlock tokenText;
        private Border dockTrack,dockFill;
        private TranslateTransform dockFlow;
        private int dockPulseFps;
        private readonly DispatcherTimer dockPulseClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private QuotaOrb orb;
        private CustomShapeView custom;
        internal bool IsCustom {get{return !IsPillar&&preferences().BallStyle=="html";}}
        private ActivityReport activity;
        internal Action SettingsRequested;
        private StackPanel quotaRows;
        private QuotaBucket bucket;
        private UsageSnapshot usage;
        private string source="",quotaStamp="",dock="",valuesKey;
        private bool dragging,disposing;
        private string builtShape;
        private double dockAnchor;
        internal bool Expanded {get{return preferences().BallStyle=="capsule"&&preferences().BallExpanded;}}
        internal bool IsOrb {get{return !IsPillar&&preferences().BallStyle=="orb";}}
        internal string DockSide {get{return dock;}}
        internal bool IsPillar {get{return dock.Length>0;}}
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] private struct Monitor {public int Size;public NativeRect Screen,Work;public uint Flags;}
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out NativeRect r);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h,ref Monitor info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        internal FloatingBall(Func<Preferences> get,Action persist,Action showMain,Action quit)
        {
            preferences=get;save=persist;restore=showMain;exit=quit;
            Title="今日用量悬浮球";ShowInTaskbar=false;ShowActivated=false;Topmost=true;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
            FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");UseLayoutRounding=true;Theme.InstallStyles(this);
            shell=new Border{Background=Theme.WindowBackground,BorderBrush=Theme.Frame,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(22),Padding=new Thickness(12,9,10,9)};Content=shell;
            var menu=Theme.Menu();
            AddMenu(menu,"光环 · 圆形额度球",SetOrb);
            AddMenu(menu,"小形态 · 仅今日 Tokens",()=>SetExpanded(false));AddMenu(menu,"大形态 · 额度与今日 Tokens",()=>SetExpanded(true));
            AddMenu(menu,"自定义 HTML 形态",SetCustom);AddMenu(menu,"重新载入自定义形态",()=>{if(IsCustom){builtShape=null;Build();}});
            AddMenu(menu,"形态设置",()=>{if(SettingsRequested!=null)SettingsRequested();});
            AddMenu(menu,"返回完整窗口",restore);AddMenu(menu,"退出软件",exit);shell.ContextMenu=menu;
            shell.MouseLeftButtonDown+=Drag;
            PreviewMouseDown+=delegate(object sender,MouseButtonEventArgs e)
            {
                if(e.ChangedButton==MouseButton.Right){e.Handled=true;menu.PlacementTarget=shell;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;}
                else if(IsCustom&&e.ChangedButton==MouseButton.Left&&(Keyboard.Modifiers&ModifierKeys.Alt)!=0){e.Handled=true;DragCustom();}
            };
            PreviewMouseRightButtonUp+=delegate(object sender,MouseButtonEventArgs e){e.Handled=true;};
            PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;restore();}};
            // Resolve the shape before the first frame; edge hover has no handlers or timers.
            var initial=preferences();dock=NormalizeDock(initial.BallDock);
            if(dock.Length>0||dock!=(initial.BallDock??""))initial.BallExpanded=false;
            SourceInitialized+=delegate
            {
                var p=preferences();if(Finite(p.BallLeft)&&Finite(p.BallTop))Place((int)p.BallLeft,(int)p.BallTop);
                NativeRect r;GetWindowRect(Handle,out r);dockAnchor=dock=="top"||dock=="bottom"?r.Left+Width*Scale/2:r.Top+Height*Scale/2;
                if(dock.Length>0)PositionDock();else Clamp();SavePosition();
            };
            Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(!disposing){e.Cancel=true;restore();}};
            IsVisibleChanged+=delegate{UpdateDockPulse();};dockPulseClock.Tick+=delegate{UpdateDockPulse();};
            Build();
        }
        private IntPtr Handle {get{return new WindowInteropHelper(this).Handle;}}
        private double Scale {get{uint dpi=Handle==IntPtr.Zero?0:GetDpiForWindow(Handle);return dpi>0?dpi/96.0:VisualTreeHelper.GetDpi(this).DpiScaleX;}}
        private static void AddMenu(ContextMenu menu,string name,Action action){var item=new MenuItem{Header=name};item.Click+=delegate{action();};menu.Items.Add(item);}
        internal void Apply(UsageSnapshot snapshot,QuotaBucket quota,string scope,string status)
        {
            Opacity=preferences().BallOpacity;usage=snapshot;bucket=quota;source=scope;quotaStamp=status;Build();
        }
        internal void ApplyActivity(ActivityReport report){activity=report;if(orb!=null)orb.ApplyActivity(report);if(custom!=null)custom.Apply(usage,bucket,activity,preferences());UpdateDockPulse();}
        internal bool DockPulseActive {get{return dockPulseFps>0;}}
        private void StopDockPulse()
        {
            dockPulseClock.Stop();dockPulseFps=0;
            if(dockTrack!=null)dockTrack.BeginAnimation(OpacityProperty,null);
            if(dockFill!=null){dockFill.BeginAnimation(OpacityProperty,null);var light=dockFill.Child as UIElement;if(light!=null)light.Opacity=0;}
            if(dockFlow!=null){dockFlow.BeginAnimation(TranslateTransform.XProperty,null);dockFlow.BeginAnimation(TranslateTransform.YProperty,null);}
        }
        private void UpdateDockPulse()
        {
            var p=preferences();long now=LocalCodexUsage.Unix(DateTime.Now);
            bool running=!disposing&&IsVisible&&IsPillar&&dockTrack!=null&&activity!=null&&activity.ActiveTasks>0&&activity.Until>now;
            int fps=!running||p.OrbAnimation=="off"||!SystemParameters.ClientAreaAnimation||SystemParameters.HighContrast?0:p.OrbAnimation=="eco"||(RenderCapability.Tier>>16)==0?20:30;
            if(fps==dockPulseFps)return;StopDockPulse();if(fps==0)return;dockPulseFps=fps;
            // Animate paint only: quota length, hit targets and native edge coordinates never move.
            var breath=new DoubleAnimation(1,.64,TimeSpan.FromSeconds(1.1)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever};Timeline.SetDesiredFrameRate(breath,fps);
            dockFill.BeginAnimation(OpacityProperty,breath);
            // Unknown/empty quota only breathes its neutral track; no fake remaining balance.
            if(dockFill.Visibility!=Visibility.Visible)dockTrack.BeginAnimation(OpacityProperty,breath);
            var light=dockFill.Child as UIElement;if(light!=null)light.Opacity=.55;
            bool horizontal=dock=="top"||dock=="bottom";
            var flow=new DoubleAnimation(horizontal?-1:1,horizontal?1:-1,TimeSpan.FromSeconds(1.9)){RepeatBehavior=RepeatBehavior.Forever};Timeline.SetDesiredFrameRate(flow,fps);
            if(dockFlow!=null)dockFlow.BeginAnimation(horizontal?TranslateTransform.XProperty:TranslateTransform.YProperty,flow);
            dockPulseClock.Start();
        }
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        private static string NormalizeDock(string side){return new[]{"left","right","top","bottom"}.Contains(side)?side:"";}
        internal void SetExpanded(bool value)
        {
            if(disposing||dragging)return;
            preferences().BallStyle="capsule";preferences().BallExpanded=value;dock="";Build();Clamp();SavePosition();
        }
        internal void SetOrb(){if(disposing||dragging)return;preferences().BallStyle="orb";preferences().BallExpanded=false;dock="";Build();Clamp();SavePosition();}
        internal void SetCustom(){if(disposing||dragging)return;preferences().BallStyle="html";preferences().BallExpanded=false;dock="";builtShape=null;Build();Clamp();SavePosition();}
        private void DragCustom(){if(disposing||dragging||Mouse.LeftButton!=MouseButtonState.Pressed)return;dragging=true;try{DragMove();}catch(InvalidOperationException){}finally{dragging=false;SnapToEdge();SavePosition();}}
        private void ResizeCustom(double width,double height){if(!IsCustom||dragging)return;Width=Theme.Bound(width,64,800,240);Height=Theme.Bound(height,40,600,90);Clamp();}
        private void Build()
        {
            string valid=NormalizeDock(dock);if(valid!=dock){dock=valid;preferences().BallExpanded=false;}
            bool pillar=IsPillar,large=!pillar&&Expanded,circle=IsOrb,horizontal=dock=="top"||dock=="bottom";
            double size=Theme.Bound(preferences().OrbSize,56,128,84);
            string shape=pillar?dock:IsCustom?"html:"+preferences().CustomShape:circle?"orb:"+size:large?"large":"small";
            // Repeated drops/data refreshes keep the same visual tree, avoiding needless layout and flashing.
            if(builtShape==shape){UpdateValues();UpdateDockPulse();return;}StopDockPulse();dockFlow=null;builtShape=shape;
            if(orb!=null){orb.Dispose();orb=null;}if(custom!=null){custom.Dispose();custom=null;}
            Width=pillar?(horizontal?80:16):circle?size:(large?340:174);Height=pillar?(horizontal?16:80):circle?size:(large?54:48);
            // Only the dock strip needs an extended grab area. Free-floating forms leave the
            // pixels outside their rounded border fully transparent, avoiding a rectangular halo.
            Background=pillar?Theme.B("#01000000"):Brushes.Transparent;shell.Background=pillar||circle?(Brush)Brushes.Transparent:Theme.WindowBackground;
            shell.BorderThickness=new Thickness(pillar||circle?0:1);shell.BorderBrush=pillar||circle?(Brush)Brushes.Transparent:Theme.Frame;
            // Put all invisible grab padding on the inward side, so the visible track touches the edge.
            shell.Padding=circle?new Thickness(0):pillar?(dock=="top"?new Thickness(0,0,0,10):dock=="bottom"?new Thickness(0,10,0,0):dock=="left"?new Thickness(0,0,10,0):new Thickness(10,0,0,0)):new Thickness(8,5,6,5);shell.CornerRadius=new CornerRadius(pillar?8:16);
            valuesKey=null;tokenText=null;quotaRows=null;dockTrack=null;dockFill=null;
            AutomationProperties.SetItemStatus(this,pillar?"贴边额度条":circle?"圆环剩余额度":large?"额度与今日 Tokens":"仅今日 Tokens");
            if(IsCustom)
            {
                shell.Padding=new Thickness(0);shell.BorderThickness=new Thickness(0);shell.Background=Brushes.Transparent;shell.ToolTip=null;ToolTipService.SetIsEnabled(shell,false);
                try{string path=ShapeManifest.Resolve(preferences().CustomShape);var manifest=ShapeManifest.Read(path);Width=manifest.width;Height=manifest.height;custom=new CustomShapeView(path,restore,DragCustom,ResizeCustom);shell.Child=custom;}
                catch(Exception){Width=260;Height=86;var text=Theme.Text("尚未选择可用的 HTML 形态。\n右键 → 形态设置，导入 shape.json；\n随时可右键返回完整窗口。",11,Theme.Ink);text.Margin=new Thickness(12);shell.Child=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(14),Child=text};}
            }
            else if(circle){ToolTipService.SetIsEnabled(shell,false);orb=new QuotaOrb();shell.Child=orb;}
            else if(pillar)
            {
                // The dock contains only a quota track. Unknown/stale quota leaves it empty, never falsely full.
                dockFill=new Border{Background=Theme.Accent,CornerRadius=new CornerRadius(4),HorizontalAlignment=horizontal?HorizontalAlignment.Left:HorizontalAlignment.Stretch,VerticalAlignment=horizontal?VerticalAlignment.Stretch:VerticalAlignment.Bottom};
                dockFlow=new TranslateTransform();var gloss=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=horizontal?new Point(1,0):new Point(0,1),RelativeTransform=dockFlow};
                gloss.GradientStops.Add(new GradientStop(Colors.Transparent,0));gloss.GradientStops.Add(new GradientStop(Colors.Transparent,.3));gloss.GradientStops.Add(new GradientStop(Colors.White,.5));gloss.GradientStops.Add(new GradientStop(Colors.Transparent,.7));gloss.GradientStops.Add(new GradientStop(Colors.Transparent,1));
                dockFill.Child=new Border{Background=gloss,CornerRadius=new CornerRadius(4),Opacity=0,IsHitTestVisible=false};
                dockTrack=new Border{Background=Theme.B("#50828F9E"),CornerRadius=new CornerRadius(4),Child=dockFill};shell.Child=dockTrack;
                AutomationProperties.SetAutomationId(dockTrack,"BallDockQuota");
                shell.ToolTip=null;ToolTipService.SetIsEnabled(shell,false);
            }
            else
            {
                ToolTipService.SetIsEnabled(shell,true);
                var row=new DockPanel{VerticalAlignment=VerticalAlignment.Center};shell.Child=row;
                var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
                var toggle=Theme.ToolbarButton(large?"collapse":"expand","切换悬浮球大小");toggle.Width=22;toggle.MinWidth=22;toggle.Height=24;toggle.Padding=new Thickness(2);toggle.Margin=new Thickness(1);toggle.Click+=delegate{SetExpanded(!Expanded);};actions.Children.Add(toggle);
                var back=Theme.ToolbarButton("restore","返回完整窗口");back.Width=22;back.MinWidth=22;back.Height=24;back.Padding=new Thickness(2);back.Margin=new Thickness(1);back.Click+=delegate{restore();};actions.Children.Add(back);
                var tokens=new StackPanel{Margin=new Thickness(large?10:2,0,6,0)};if(large)tokens.Width=94;DockPanel.SetDock(tokens,Dock.Right);row.Children.Add(tokens);
                tokens.Children.Add(Theme.Text("今日 Tokens",9,Theme.Muted));tokenText=Theme.Text("—",large?20:18,Theme.Ink);tokenText.FontWeight=FontWeights.SemiBold;
                tokens.Children.Add(new Viewbox{Child=tokenText,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Height=24});AutomationProperties.SetAutomationId(tokenText,"BallTodayTokens");
                if(large){quotaRows=new StackPanel{VerticalAlignment=VerticalAlignment.Center};row.Children.Add(quotaRows);}
            }
            UpdateValues();UpdateDockPulse();
        }
        private static double RemainingFraction(QuotaWindow window,long now)
        {
            if(window==null||!Finite(window.UsedPercent)||(window.ResetsAt>0&&now>=window.ResetsAt))return 0;
            return Math.Max(0,Math.Min(1,1-window.UsedPercent/100));
        }
        private void UpdateValues()
        {
            if(custom!=null)custom.Apply(usage,bucket,activity,preferences());
            if(orb!=null){var p=preferences();orb.ApplyActivity(activity);orb.Apply(p,bucket,usage,source+"|"+p.Source+"|"+p.CodexHome+"|"+p.Database);}
            long now=LocalCodexUsage.Unix(DateTime.Now);var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            string key=(today==null?"none":today.Date+":"+today.Tokens)+"/"+source+"/"+quotaStamp+"/"+OrbPalette.Key(preferences())+"/"+(bucket==null?"none":bucket.Origin+"/"+String.Join("|",new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).Select(w=>w.Label+":"+w.Remaining(now))));
            if(valuesKey==key)return;valuesKey=key;
            if(tokenText!=null){tokenText.Text=today==null?"—":TokenText.Compact(today.Tokens);tokenText.ToolTip="今日已记录 Tokens · "+source;}
            var windows=bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
            if(dockTrack!=null)
            {
                var main=preferences().BallStyle=="orb"?QuotaOrb.SelectWindow(bucket,preferences().OrbQuotaWindow):windows.LastOrDefault();double fraction=bucket==null||now-bucket.ObservedAt>300?0:RemainingFraction(main,now);bool horizontal=dock=="top"||dock=="bottom";
                if(horizontal)dockFill.Width=80*fraction;else dockFill.Height=80*fraction;
                var visibility=fraction>0?Visibility.Visible:Visibility.Collapsed;if(dockFill.Visibility!=visibility)StopDockPulse();dockFill.Visibility=visibility;
                dockFill.Background=OrbPalette.ForBar(preferences(),main!=null&&main.Minutes>=1440,!horizontal);
                AutomationProperties.SetName(dockTrack,main==null?"额度暂不可用":main.Label+" 剩余 "+main.Remaining(now));
            }
            if(quotaRows!=null)
            {
                quotaRows.Children.Clear();
                if(windows.Length==0)quotaRows.Children.Add(Theme.Text("额度暂不可用",10,Theme.Muted));
                foreach(var w in windows)
                {
                    var line=new DockPanel{Height=18};quotaRows.Children.Add(line);
                    var label=Theme.Text(w.Label,9,Theme.Muted);label.Width=24;label.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(label,Dock.Left);line.Children.Add(label);
                    var amount=Theme.Text(w.Remaining(now),10,Theme.Ink);amount.FontWeight=FontWeights.SemiBold;amount.MinWidth=32;amount.Margin=new Thickness(5,0,0,0);amount.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(amount,Dock.Right);line.Children.Add(amount);
                    double fraction=RemainingFraction(w,now);
                    var bar=new Grid{Height=5,VerticalAlignment=VerticalAlignment.Center};line.Children.Add(bar);
                    bar.Children.Add(new Border{Background=Theme.Line,CornerRadius=new CornerRadius(3)});
                    var fillGrid=new Grid();bar.Children.Add(fillGrid);fillGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(fraction,GridUnitType.Star)});fillGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1-fraction,GridUnitType.Star)});
                    fillGrid.Children.Add(new Border{CornerRadius=new CornerRadius(3),Background=OrbPalette.ForBar(preferences(),w.Minutes>=1440)});
                }
            }
            if(!IsPillar&&!IsOrb&&!IsCustom)shell.ToolTip=source+" · 今日 "+(today==null?"等待记录":TokenText.Compact(today.Tokens)+" Tokens")+"\n"+quotaStamp+"\n"+String.Join(" · ",windows.Select(w=>w.Label+" 剩余 "+w.Remaining(now)))+"\n拖动移动 · 双击切换大小 · 右键更多选项";
        }
        private void Drag(object sender,MouseButtonEventArgs e)
        {
            if(IsCustom)return;
            DependencyObject node=e.OriginalSource as DependencyObject;while(node!=null&&node!=shell){if(node is ButtonBase)return;var text=node as FrameworkContentElement;node=text!=null?text.Parent:VisualTreeHelper.GetParent(node);}
            if(e.ChangedButton!=MouseButton.Left)return;e.Handled=true;
            if(e.ClickCount==2){if(IsOrb)restore();else if(!IsPillar)SetExpanded(!Expanded);return;}
            NativeRect before;GetWindowRect(Handle,out before);bool circleClick=IsOrb;
            dragging=true;if(orb!=null)orb.SetPressed(true);
            // Keep the current shape throughout the native drag. Only settle once on release,
            // so grabbing a thin bar cannot jump the window or trigger a hover/resize loop.
            bool failed=false;
            try{DragMove();}catch(InvalidOperationException){failed=true;}
            finally
            {
                dragging=false;if(orb!=null)orb.SetPressed(false);
                if(failed){dock="";preferences().BallExpanded=false;Build();Clamp();}
                else{NativeRect after;GetWindowRect(Handle,out after);bool moved=Math.Abs(after.Left-before.Left)>3*Scale||Math.Abs(after.Top-before.Top)>3*Scale;if(circleClick&&!moved){if(orb!=null)orb.Pulse();}else SnapToEdge();}SavePosition();
            }
        }
        private NativeRect WorkArea()
        {
            var monitor=new Monitor{Size=Marshal.SizeOf(typeof(Monitor))};GetMonitorInfo(MonitorFromWindow(Handle,2),ref monitor);return monitor.Work;
        }
        internal void SnapToEdge()
        {
            if(Handle==IntPtr.Zero)return;NativeRect r;GetWindowRect(Handle,out r);var area=WorkArea();
            int side=DockEdge(new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),new Rect(area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top),Scale);
            bool wasDocked=IsPillar;
            dock=side>=0?new[]{"left","right","top","bottom"}[side]:"";
            // Docking forgets the large state; every undock deterministically returns to small.
            if(wasDocked||dock.Length>0)preferences().BallExpanded=false;
            dockAnchor=side<2?(r.Top+r.Bottom)/2.0:(r.Left+r.Right)/2.0;Build();if(dock.Length>0)PositionDock();else Clamp();
        }
        internal static int DockEdge(Rect window,Rect work,double scale)
        {
            // Overshooting a screen edge is still contact, not distance away from it.
            // Previously Abs() rejected these drops, then Clamp() left an undocked window flush with the edge.
            double[] distances={Math.Max(0,window.Left-work.Left),Math.Max(0,work.Right-window.Right),Math.Max(0,window.Top-work.Top),Math.Max(0,work.Bottom-window.Bottom)};
            int side=Array.IndexOf(distances,distances.Min());return distances[side]<=32*scale?side:-1;
        }
        private void PositionDock()
        {
            if(Handle==IntPtr.Zero||dock.Length==0)return;var area=WorkArea();int w=(int)Math.Round(Width*Scale),h=(int)Math.Round(Height*Scale);
            int x=dock=="left"?area.Left:dock=="right"?area.Right-w:(int)dockAnchor-w/2;
            int y=dock=="top"?area.Top:dock=="bottom"?area.Bottom-h:(int)dockAnchor-h/2;
            Place(Math.Max(area.Left,Math.Min(area.Right-w,x)),Math.Max(area.Top,Math.Min(area.Bottom-h,y)));
        }
        private void Clamp()
        {
            if(Handle==IntPtr.Zero)return;NativeRect r;GetWindowRect(Handle,out r);var area=WorkArea();int w=(int)Math.Round(Width*Scale),h=(int)Math.Round(Height*Scale);
            Place(Math.Max(area.Left,Math.Min(area.Right-w,r.Left)),Math.Max(area.Top,Math.Min(area.Bottom-h,r.Top)));
        }
        private void Place(int x,int y){if(Handle!=IntPtr.Zero)SetWindowPos(Handle,IntPtr.Zero,x,y,(int)Math.Round(Width*Scale),(int)Math.Round(Height*Scale),0x14);}
        private void SavePosition()
        {
            NativeRect r;if(Handle!=IntPtr.Zero&&GetWindowRect(Handle,out r)){var p=preferences();p.BallLeft=r.Left;p.BallTop=r.Top;p.BallDock=dock;save();}
        }
        public void Dispose(){disposing=true;StopDockPulse();if(orb!=null)orb.Dispose();if(custom!=null)custom.Dispose();Close();}
    }
}
