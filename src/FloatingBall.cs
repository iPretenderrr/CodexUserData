using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
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
using System.Windows.Media.Imaging;
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
        private CapsuleActivityChrome capsuleChrome;
        private TranslateTransform dockFlow;
        private int activityMotionFps;
        private readonly DispatcherTimer activityMotionClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private QuotaOrb orb;
        private DynamicIsland island;
        private CustomShapeView custom;
        internal bool IsCustom {get{return !IsPillar&&preferences().BallStyle=="html";}}
        internal bool IsIsland {get{return !IsPillar&&preferences().BallStyle=="island";}}
        private ActivityReport activity;
        internal Action SettingsRequested;
        private StackPanel quotaRows;
        private QuotaBucket bucket;
        private UsageSnapshot usage;
        private string source="",quotaStamp="",dock="",valuesKey;
        private bool dragging,disposing;
        private bool completionPending;
        private DockTransition dockMotion;
        private int islandResizeGeneration;
        private bool islandResizeActive;
        private double islandTargetWidth,islandTargetHeight;
        private string builtShape;
        private string customManifest;
        private bool customResizeQueued;
        private double customResizeWidth,customResizeHeight;
        private int customGeneration;
        private string customSizeKey;
        private readonly DispatcherTimer customSizeSaveClock=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(750)};
        private bool customSizeDirty;
        private bool placementRepairPending,placementRepairQueued;
        private double dockAnchor;
        internal bool Expanded {get{return preferences().BallStyle=="capsule"&&preferences().BallExpanded;}}
        internal bool IslandExpanded {get{return preferences().BallStyle=="island"&&preferences().BallExpanded;}}
        internal bool IsOrb {get{return !IsPillar&&preferences().BallStyle=="orb";}}
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
            FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");UseLayoutRounding=true;Theme.InstallStyles(this);WindowInteraction.EnableMotion(this);
            // Keep completion breathing separate from visibility/shape transition clocks.
            shell=new Border{Background=Theme.WindowBackground,BorderBrush=Theme.Frame,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(22),Padding=new Thickness(12,9,10,9)};Content=new Border{Child=shell};
            var menu=Theme.Menu();
            AddMenu(menu,"光环 · 圆形额度球",SetOrb);
            AddMenu(menu,"小形态 · 仅今日 Tokens",()=>SetExpanded(false));AddMenu(menu,"大形态 · 额度与今日 Tokens",()=>SetExpanded(true));
            AddMenu(menu,"灵动岛 · 液态玻璃",()=>SetIslandMaterial("glass"));
            AddMenu(menu,"灵动岛 · 经典卡片",()=>SetIslandMaterial("classic"));
            AddMenu(menu,"自定义 HTML 形态",SetCustom);AddMenu(menu,"重新载入自定义形态",()=>{if(IsCustom)WindowInteraction.ChangeShape(this,ReloadCustom);});
            AddMenu(menu,"形态设置",()=>{if(SettingsRequested!=null)SettingsRequested();});
            var positionLock=new MenuItem{Header="锁定位置",IsCheckable=true};menu.Items.Add(positionLock);
            menu.Opened+=delegate{positionLock.IsChecked=preferences().BallPositionLocked;};
            positionLock.Click+=delegate{preferences().BallPositionLocked=positionLock.IsChecked;save();};
            AddMenu(menu,"返回完整窗口",restore);AddMenu(menu,"退出软件",exit);shell.ContextMenu=menu;
            shell.MouseLeftButtonDown+=Drag;PreviewMouseLeftButtonDown+=IslandMouseDown;PreviewMouseMove+=IslandMouseMove;PreviewMouseLeftButtonUp+=IslandMouseUp;
            LostMouseCapture+=delegate{islandPointer=false;if(island!=null)island.SetPressed(false);};
            PreviewMouseDown+=delegate(object sender,MouseButtonEventArgs e)
            {
                if(e.ChangedButton==MouseButton.Right){CancelIslandClick();e.Handled=true;menu.PlacementTarget=shell;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;}
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
                var preferred=PreferredScreen();
                if(preferred!=null)RestorePlacement(preferred);else if(dock.Length>0)PositionDock();else Clamp();
                // A disconnected preferred monitor remains remembered; fallback placement is temporary.
                SavePositionCore(String.IsNullOrEmpty(p.BallMonitor));
                HwndSource.FromHwnd(Handle).AddHook(delegate(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
                {
                    // Display, work-area and DPI changes can invalidate previously saved pixels.
                    if(message==0x7e||message==0x1a||message==0x2e0){placementRepairPending=true;QueuePlacementRepair();}
                    if(message==0x84&&islandResizeActive&&island!=null)
                    {
                        long point=lp.ToInt64();NativeRect rect;GetWindowRect(hwnd,out rect);
                        double x=((short)(point&0xffff)-rect.Left)/Scale,y=((short)((point>>16)&0xffff)-rect.Top)/Scale;
                        if(x>island.RenderWidth||y>island.RenderHeight){handled=true;return new IntPtr(-1);}
                    }
                    return IntPtr.Zero;
                });
            };
            Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(!disposing){e.Cancel=true;restore();}};
            IsVisibleChanged+=delegate{if(!IsVisible){CancelIslandClick();CancelIslandResize(false);CancelDockMotion();}else if(placementRepairPending)QueuePlacementRepair();UpdateActivityMotion();};activityMotionClock.Tick+=delegate{UpdateActivityMotion();};
            customSizeSaveClock.Tick+=delegate{FlushCustomSize();};
            Build();
        }
        private IntPtr Handle {get{return new WindowInteropHelper(this).Handle;}}
        private double Scale {get{uint dpi=Handle==IntPtr.Zero?0:GetDpiForWindow(Handle);return dpi>0?dpi/96.0:VisualTreeHelper.GetDpi(this).DpiScaleX;}}
        private static void AddMenu(ContextMenu menu,string name,Action action){var item=new MenuItem{Header=name};item.Click+=delegate{action();};menu.Items.Add(item);}
        internal void Apply(UsageSnapshot snapshot,QuotaBucket quota,string scope,string status)
        {
            if(disposing)return;WindowInteraction.SetOpacity(this,preferences().BallOpacity);usage=snapshot;bucket=quota;source=scope;quotaStamp=status;Build();
        }
        internal Task WaitForPresentationAsync(){return !disposing&&IsCustom&&custom!=null?custom.WaitForPresentationAsync():Task.FromResult(true);}
        internal void ApplyActivity(ActivityReport report){activity=report;if(orb!=null)orb.ApplyActivity(report);if(island!=null)island.ApplyActivity(report);if(custom!=null)custom.Apply(usage,bucket,activity,preferences());Build();UpdateActivityMotion();}
        private void StopActivityMotion()
        {
            activityMotionClock.Stop();activityMotionFps=0;
            if(dockTrack!=null)dockTrack.BeginAnimation(OpacityProperty,null);
            if(dockFill!=null){dockFill.BeginAnimation(OpacityProperty,null);var light=dockFill.Child as UIElement;if(light!=null)light.Opacity=0;}
            if(dockFlow!=null){dockFlow.BeginAnimation(TranslateTransform.XProperty,null);dockFlow.BeginAnimation(TranslateTransform.YProperty,null);}
            if(capsuleChrome!=null)capsuleChrome.Stop();
            if(island!=null)island.Stop();
        }
        private void UpdateActivityMotion()
        {
            var p=preferences();long now=LocalCodexUsage.Unix(DateTime.Now);
            bool capsule=!IsPillar&&!IsOrb&&!IsIsland&&!IsCustom&&capsuleChrome!=null,ready=IsPillar&&dockTrack!=null||capsule||IsIsland&&island!=null;
            bool running=!disposing&&dockMotion==null&&IsVisible&&ready&&activity!=null&&activity.ActiveTasks>0&&activity.Until>now;
            int fps=!disposing&&dockMotion==null&&IsVisible&&IsIsland&&island!=null?Theme.ActivityFrameRate(p.OrbAnimation):running?Theme.ActivityFrameRate(p.OrbAnimation):0;
            if(fps==activityMotionFps)return;StopActivityMotion();if(fps==0)return;activityMotionFps=fps;
            // Animate paint only: quota length, hit targets and native window coordinates never move.
            var breath=new DoubleAnimation(1,.58,TimeSpan.FromSeconds(1.05)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever};Timeline.SetDesiredFrameRate(breath,fps);
            if(IsPillar)
            {
                dockFill.BeginAnimation(OpacityProperty,breath);
                // Unknown quota breathes its neutral track without implying a remaining balance.
                if(dockFill.Visibility!=Visibility.Visible)dockTrack.BeginAnimation(OpacityProperty,breath);
                var light=dockFill.Child as UIElement;if(light!=null)light.Opacity=.55;
                bool horizontal=dock=="top"||dock=="bottom";
                var flow=new DoubleAnimation(horizontal?-1:1,horizontal?1:-1,TimeSpan.FromSeconds(1.9)){RepeatBehavior=RepeatBehavior.Forever};Timeline.SetDesiredFrameRate(flow,fps);
                if(dockFlow!=null)dockFlow.BeginAnimation(horizontal?TranslateTransform.XProperty:TranslateTransform.YProperty,flow);
            }
            else
            {
                if(island!=null)island.Start(fps);else capsuleChrome.Start(fps);
            }
            activityMotionClock.Start();
        }
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        private static string NormalizeDock(string side){return new[]{"left","right","top","bottom"}.Contains(side)?side:"";}
        internal void SetExpanded(bool value)
        {
            if(disposing||dragging)return;
            CancelIslandClick();CancelIslandResize(true);CancelDockMotion();WindowInteraction.ChangeShape(this,delegate{preferences().BallStyle="capsule";preferences().BallExpanded=value;dock="";Build();Clamp();SavePosition();});
        }
        internal void SetIsland(){if(disposing||dragging)return;CancelIslandClick();CancelIslandResize(true);CancelDockMotion();WindowInteraction.ChangeShape(this,delegate{preferences().BallStyle="island";preferences().BallExpanded=false;dock="";Build();Clamp();SavePosition();});}
        private void SetIslandMaterial(string material){if(disposing||dragging)return;preferences().IslandMaterial=material;SetIsland();}
        internal void SetIslandExpanded(bool value)
        {
            if(disposing||dragging||!IsIsland||IslandExpanded==value)return;CancelIslandClick();CancelDockMotion();preferences().BallExpanded=value;Build();QueueIslandSave();
        }
        internal void SetCompletionPending(bool value){if(disposing)return;if(completionPending==value){if(island!=null)island.SetCompletionPending(value);CompletionFeedback.Set(this,!IsIsland&&value);return;}completionPending=value;if(island!=null)island.SetCompletionPending(value);CompletionFeedback.Set(this,!IsIsland&&value);if(island!=null)Build();}
        internal void SetOrb(){if(disposing||dragging)return;CancelIslandClick();CancelIslandResize(true);CancelDockMotion();WindowInteraction.ChangeShape(this,delegate{preferences().BallStyle="orb";preferences().BallExpanded=false;dock="";Build();Clamp();SavePosition();});}
        internal void SetCustom(){if(disposing||dragging)return;CancelIslandClick();CancelIslandResize(true);CancelDockMotion();WindowInteraction.ChangeShape(this,delegate{preferences().BallStyle="html";preferences().BallExpanded=false;dock="";builtShape=null;Build();Clamp();SavePosition();});}
        private void DragCustom(){if(disposing||dragging||preferences().BallPositionLocked||Mouse.LeftButton!=MouseButtonState.Pressed)return;CancelTransition();RememberBeforeDrag();dragging=true;try{DragMove();}catch(InvalidOperationException){}finally{dragging=false;SnapToEdge();SavePosition();}}
        private void ReloadCustom()
        {
            if(disposing)return;customGeneration++;customResizeQueued=false;
            if(custom!=null){custom.Dispose();custom=null;}customManifest=null;builtShape=null;Build();
        }
        private void ResizeCustom(double width,double height)
        {
            if(disposing||!IsCustom||dragging)return;customResizeWidth=Theme.Bound(width,64,800,240);customResizeHeight=Theme.Bound(height,40,600,90);
            if(customSizeKey!=null&&preferences().RememberShapeSize(customSizeKey,customResizeWidth,customResizeHeight))
            {customSizeDirty=true;customSizeSaveClock.Stop();customSizeSaveClock.Start();}
            if(customResizeQueued)return;customResizeQueued=true;
            int generation=customGeneration;
            // A page may emit several resize messages during one JavaScript layout. Commit only
            // the newest dimensions on the render queue to avoid repeated native window work.
            Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(delegate
            {
                if(disposing||generation!=customGeneration)return;customResizeQueued=false;if(!IsCustom||dragging||dockMotion!=null)return;
                if(Math.Abs(Width-customResizeWidth)<.5&&Math.Abs(Height-customResizeHeight)<.5)return;
                Clamp();
            }));
        }
        private void Build()
        {
            if(disposing||dockMotion!=null)return; // Usage refreshes must not replace a shape during its transition.
            string valid=NormalizeDock(dock);if(valid!=dock){dock=valid;preferences().BallExpanded=false;}
            bool pillar=IsPillar,large=!pillar&&Expanded,circle=IsOrb,islandForm=IsIsland,horizontal=dock=="top"||dock=="bottom";
            long islandNow=LocalCodexUsage.Unix(DateTime.Now);bool islandRunning=islandForm&&activity!=null&&activity.ObservedAt>0&&activity.ObservedAt<=islandNow+5&&islandNow-activity.ObservedAt<=5&&activity.ActiveTasks>0&&activity.Until>islandNow,islandWide=islandRunning||completionPending;
            double islandFromWidth=island==null?Width:island.RenderWidth,islandFromHeight=island==null?Height:island.RenderHeight;bool animateIsland=islandForm&&island!=null&&IsVisible&&!pillar;
            bool glass=preferences().IslandMaterial!="classic";
            bool twoQuotas=bucket!=null&&bucket.Primary!=null&&bucket.Secondary!=null;
            islandTargetWidth=glass?(IslandExpanded?(twoQuotas?312:252):islandWide?252:224):(IslandExpanded?(twoQuotas?280:228):islandWide?216:196);
            islandTargetHeight=glass?(IslandExpanded?72:56):(IslandExpanded?76:48);
            if(pillar&&preferences().BallStyle=="html")CustomShapeView.WarmEnvironment();
            double size=Theme.Bound(preferences().OrbSize,56,128,84);
            string shape=pillar?dock:IsCustom?"html:"+preferences().CustomShape:circle?"orb:"+size:islandForm?preferences().IslandMaterial+(IslandExpanded?"island:expanded":islandWide?"island:running":"island:idle"):large?"large":"small";
            // Repeated drops/data refreshes keep the same visual tree, avoiding needless layout and flashing.
            if(builtShape==shape)
            {
                UpdateValues();UpdateActivityMotion();
                // A transfer may be reversed before its source ever becomes hidden. Resume
                // a cancelled island morph rather than retaining that intermediate size.
                if(islandForm&&island!=null&&!islandResizeActive&&(Math.Abs(island.RenderWidth-islandTargetWidth)>.5||Math.Abs(island.RenderHeight-islandTargetHeight)>.5))
                {
                    if(IsVisible){Width=Math.Max(island.RenderWidth,islandTargetWidth);Height=Math.Max(island.RenderHeight,islandTargetHeight);AnimateIslandResize(island.RenderWidth,island.RenderHeight,islandTargetWidth,islandTargetHeight);}
                    else{island.SetPresentationSize(islandTargetWidth,islandTargetHeight);Width=islandTargetWidth;Height=islandTargetHeight;}
                }
                return;
            }
            if(!animateIsland)StopActivityMotion();dockFlow=null;builtShape=shape;
            // Width/Height return the animated presentation value while a previous request is
            // reversing. Preserve that exact start, then clear the old clock before assigning
            // the new base target so the target cannot accidentally resolve to the old animation.
            if(animateIsland)CancelIslandResize(false);
            if(orb!=null){orb.Dispose();orb=null;}
            if(!islandForm&&island!=null){island.Dispose();island=null;}
            // Keep the browser controller alive while its HTML form is represented by an edge
            // strip. It is detached and suspended there, then reused immediately when undocked.
            if(custom!=null&&preferences().BallStyle!="html"){customGeneration++;customResizeQueued=false;custom.Dispose();custom=null;customManifest=null;}
            Width=pillar?(horizontal?80:16):circle?size:islandForm?(animateIsland?Math.Max(islandFromWidth,islandTargetWidth):islandTargetWidth):(large?340:174);Height=pillar?(horizontal?16:80):circle?size:islandForm?(animateIsland?Math.Max(islandFromHeight,islandTargetHeight):islandTargetHeight):(large?54:48);
            // Only the dock strip needs an extended grab area. Free-floating forms leave the
            // pixels outside their rounded border fully transparent, avoiding a rectangular halo.
            Background=pillar?Theme.B("#01000000"):Brushes.Transparent;shell.Background=pillar||circle||islandForm?(Brush)Brushes.Transparent:Theme.WindowBackground;
            shell.BorderThickness=new Thickness(pillar||circle||islandForm?0:1);shell.BorderBrush=pillar||circle||islandForm?(Brush)Brushes.Transparent:Theme.Frame;
            // Put all invisible grab padding on the inward side, so the visible track touches the edge.
            shell.Padding=circle||islandForm?new Thickness(0):pillar?(dock=="top"?new Thickness(0,0,0,10):dock=="bottom"?new Thickness(0,10,0,0):dock=="left"?new Thickness(0,0,10,0):new Thickness(10,0,0,0)):new Thickness(8,5,6,5);shell.CornerRadius=new CornerRadius(pillar?8:16);
            valuesKey=null;tokenText=null;quotaRows=null;dockTrack=null;dockFill=null;capsuleChrome=null;
            AutomationProperties.SetItemStatus(this,pillar?"贴边额度条":circle?"圆环剩余额度":islandForm?"灵动岛任务与额度":large?"额度与今日 Tokens":"仅今日 Tokens");
            if(IsCustom)
            {
                shell.Padding=new Thickness(0);shell.BorderThickness=new Thickness(0);shell.Background=Brushes.Transparent;shell.ToolTip=null;ToolTipService.SetIsEnabled(shell,false);
                try
                {
                    string path=ShapeManifest.Resolve(preferences().CustomShape);customSizeKey=Preferences.ShapeSizeKey(preferences().CustomShape);Size dimensions;
                    if(!RememberedCustomSize(out dimensions)){var manifest=ShapeManifest.Read(path);dimensions=new Size(manifest.width,manifest.height);}
                    Width=dimensions.Width;Height=dimensions.Height;
                    if(custom==null||!String.Equals(customManifest,path,StringComparison.OrdinalIgnoreCase))
                    {
                        customGeneration++;customResizeQueued=false;int generation=customGeneration;
                        if(custom!=null)custom.Dispose();customManifest=path;
                        // Ignore messages queued by a browser instance that has been replaced.
                        custom=new CustomShapeView(path,restore,DragCustom,(w,h)=>{if(generation==customGeneration)ResizeCustom(w,h);});
                    }
                    shell.Child=custom;
                }
                catch(Exception){if(custom!=null){custom.Dispose();custom=null;customManifest=null;}Width=260;Height=86;var text=Theme.Text("尚未选择可用的 HTML 形态。\n右键 → 形态设置，导入 shape.json；\n随时可右键返回完整窗口。",11,Theme.Ink);text.Margin=new Thickness(12);shell.Child=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(14),Child=text};}
            }
            else if(circle){ToolTipService.SetIsEnabled(shell,false);orb=new QuotaOrb();shell.Child=orb;}
            else if(islandForm)
            {
                ToolTipService.SetIsEnabled(shell,false);shell.ToolTip=null;
                if(island==null)island=new DynamicIsland();
                island.SetExpanded(IslandExpanded);island.Apply(usage,bucket,activity,source,quotaStamp,preferences());island.SetCompletionPending(completionPending);shell.Child=island;
                if(!animateIsland)island.SetPresentationSize(islandTargetWidth,islandTargetHeight);
            }
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
                // The chrome covers the full window beneath the controls; its rim is the outer border.
                shell.Padding=new Thickness(0);shell.BorderThickness=new Thickness(0);var stage=new Grid();shell.Child=stage;
                capsuleChrome=new CapsuleActivityChrome();stage.Children.Add(capsuleChrome);
                AutomationProperties.SetAutomationId(capsuleChrome,"CapsuleActivityChrome");
                var row=new DockPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,5,6,5)};stage.Children.Add(row);
                var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
                var toggle=Theme.ToolbarButton(large?"collapse":"expand","切换悬浮球大小");toggle.Width=22;toggle.MinWidth=22;toggle.Height=24;toggle.Padding=new Thickness(2);toggle.Margin=new Thickness(1);toggle.Click+=delegate{SetExpanded(!Expanded);};actions.Children.Add(toggle);
                var back=Theme.ToolbarButton("restore","返回完整窗口");back.Width=22;back.MinWidth=22;back.Height=24;back.Padding=new Thickness(2);back.Margin=new Thickness(1);back.Click+=delegate{restore();};actions.Children.Add(back);
                var tokens=new StackPanel{Margin=new Thickness(large?10:2,0,6,0)};if(large)tokens.Width=94;DockPanel.SetDock(tokens,Dock.Right);row.Children.Add(tokens);
                tokens.Children.Add(Theme.Text("今日 Tokens",9,Theme.Muted));tokenText=Theme.Text("—",large?20:18,Theme.Ink);tokenText.FontWeight=FontWeights.SemiBold;
                tokens.Children.Add(new Viewbox{Child=tokenText,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Height=24});AutomationProperties.SetAutomationId(tokenText,"BallTodayTokens");
                if(large){quotaRows=new StackPanel{VerticalAlignment=VerticalAlignment.Center};row.Children.Add(quotaRows);}
            }
            UpdateValues();UpdateActivityMotion();if(animateIsland)AnimateIslandResize(islandFromWidth,islandFromHeight,islandTargetWidth,islandTargetHeight);CompletionFeedback.Set(this,!IsIsland&&completionPending);
        }
        private void AnimateIslandResize(double fromWidth,double fromHeight,double toWidth,double toHeight)
        {
            int generation=++islandResizeGeneration;islandResizeActive=true;
            island.MorphTo(new Size(fromWidth,fromHeight),new Size(toWidth,toHeight),delegate
            {
                if(generation!=islandResizeGeneration||disposing||island==null)return;
                islandResizeActive=false;island.SetPresentationSize(toWidth,toHeight);Width=toWidth;Height=toHeight;Clamp();
                // Activity changes are not preference changes. Never flush settings to disk
                // on the UI thread at every automatic stretch/completion animation.
            });
        }
        private static double RemainingFraction(QuotaBucket owner,QuotaWindow window,long now)
        {
            double? value=window==null?null:window.RemainingPercent(owner,now);return value.HasValue?value.Value/100:0;
        }
        private void UpdateValues()
        {
            if(custom!=null)custom.Apply(usage,bucket,activity,preferences());
            if(island!=null)island.Apply(usage,bucket,activity,source,quotaStamp,preferences());
            if(orb!=null){var p=preferences();orb.ApplyActivity(activity);orb.Apply(p,bucket,usage,source+"|"+p.Source+"|"+p.CodexHome+"|"+p.Database);}
            long now=LocalCodexUsage.Unix(DateTime.Now);var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            string key=(today==null?"none":today.Date+":"+today.Tokens)+"/"+source+"/"+quotaStamp+"/"+OrbPalette.Key(preferences())+"/"+(bucket==null?"none":bucket.Origin+"/"+String.Join("|",new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).Select(w=>w.Label+":"+w.Remaining(bucket,now))));
            if(valuesKey==key)return;valuesKey=key;
            if(tokenText!=null){tokenText.Text=today==null?"—":TokenText.Compact(today.Tokens);tokenText.ToolTip="今日已记录 Tokens · "+source;}
            var windows=bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
            if(dockTrack!=null)
            {
                var main=preferences().BallStyle=="orb"?QuotaOrb.SelectWindow(bucket,preferences().OrbQuotaWindow):windows.LastOrDefault();double fraction=RemainingFraction(bucket,main,now);bool horizontal=dock=="top"||dock=="bottom";
                if(horizontal)dockFill.Width=80*fraction;else dockFill.Height=80*fraction;
                var visibility=fraction>0?Visibility.Visible:Visibility.Collapsed;if(dockFill.Visibility!=visibility)StopActivityMotion();dockFill.Visibility=visibility;
                // Provenance belongs to the track, leaving the fill's task pulse independent.
                dockTrack.Opacity=bucket!=null&&bucket.IsOnline?1:.72;
                dockFill.Background=OrbPalette.ForBar(preferences(),main!=null&&main.Minutes>=1440,!horizontal);
                AutomationProperties.SetName(dockTrack,main==null?"额度暂不可用":main.Label+" 剩余 "+main.Remaining(bucket,now));
            }
            if(quotaRows!=null)
            {
                quotaRows.Children.Clear();
                if(windows.Length==0)quotaRows.Children.Add(Theme.Text("额度暂不可用",10,Theme.Muted));
                foreach(var w in windows)
                {
                    var line=new DockPanel{Height=18};quotaRows.Children.Add(line);
                    var label=Theme.Text(w.Label,9,Theme.Muted);label.Width=24;label.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(label,Dock.Left);line.Children.Add(label);
                    var amount=Theme.Text(w.Remaining(bucket,now),10,Theme.Ink);amount.FontWeight=FontWeights.SemiBold;amount.MinWidth=32;amount.Margin=new Thickness(5,0,0,0);amount.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(amount,Dock.Right);line.Children.Add(amount);
                    double fraction=RemainingFraction(bucket,w,now);
                    var bar=new Grid{Height=5,VerticalAlignment=VerticalAlignment.Center};line.Children.Add(bar);
                    bar.Children.Add(new Border{Background=Theme.Line,CornerRadius=new CornerRadius(3)});
                    var fillGrid=new Grid{Opacity=bucket!=null&&bucket.IsOnline?1:.72};bar.Children.Add(fillGrid);fillGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(fraction,GridUnitType.Star)});fillGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1-fraction,GridUnitType.Star)});
                    fillGrid.Children.Add(new Border{CornerRadius=new CornerRadius(3),Background=OrbPalette.ForBar(preferences(),w.Minutes>=1440)});
                }
            }
            if(capsuleChrome!=null)capsuleChrome.ApplyPalette(preferences());
            if(!IsPillar&&!IsOrb&&!IsIsland&&!IsCustom)shell.ToolTip=source+" · 今日 "+(today==null?"等待记录":TokenText.Compact(today.Tokens)+" Tokens")+"\n"+quotaStamp+"\n"+String.Join(" · ",windows.Select(w=>w.Label+" 剩余 "+w.Remaining(bucket,now)))+"\n拖动移动 · 双击切换大小 · 右键更多选项";
        }
        private void Drag(object sender,MouseButtonEventArgs e)
        {
            if(disposing||IsCustom||IsIsland)return;
            DependencyObject node=e.OriginalSource as DependencyObject;while(node!=null&&node!=shell){if(node is ButtonBase)return;var text=node as FrameworkContentElement;node=text!=null?text.Parent:VisualTreeHelper.GetParent(node);}
            if(e.ChangedButton!=MouseButton.Left)return;e.Handled=true;
            if(e.ClickCount==2){if(IsOrb)restore();else if(!IsPillar)SetExpanded(!Expanded);return;}
            if(preferences().BallPositionLocked)return;
            CancelTransition();
            RememberBeforeDrag();
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
            var monitor=new Monitor{Size=Marshal.SizeOf(typeof(Monitor))};
            if(GetMonitorInfo(MonitorFromWindow(Handle,2),ref monitor))return monitor.Work;
            var fallback=System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;return new NativeRect{Left=fallback.Left,Top=fallback.Top,Right=fallback.Right,Bottom=fallback.Bottom};
        }
        private Point islandDown;private bool islandPointer,islandMoved,islandSavePending;
        private bool? islandTapBefore;private int islandTapAt;
        private DispatcherTimer islandSaveClock;
        private void IslandMouseDown(object sender,MouseButtonEventArgs e)
        {
            if(!IsIsland||e.ChangedButton!=MouseButton.Left)return;e.Handled=true;
            if(e.ClickCount>=2)
            {
                // A first tap responds immediately. A second tap belongs to the restore
                // gesture, so undo its preference change without reversing the visible morph.
                int elapsed=unchecked(Environment.TickCount-islandTapAt);
                if(islandTapBefore.HasValue&&elapsed>=0&&elapsed<=System.Windows.Forms.SystemInformation.DoubleClickTime+100)
                {preferences().BallExpanded=islandTapBefore.Value;builtShape=null;QueueIslandSave();}
                islandTapBefore=null;CancelIslandClick();restore();return;
            }
            islandDown=e.GetPosition(this);islandPointer=true;islandMoved=false;CaptureMouse();if(island!=null)island.SetPressed(true);
        }
        private void IslandMouseMove(object sender,MouseEventArgs e)
        {
            if(!islandPointer||!IsIsland||Mouse.LeftButton!=MouseButtonState.Pressed)return;
            Point p=e.GetPosition(this);if(Math.Abs(p.X-islandDown.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(p.Y-islandDown.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            islandPointer=false;islandMoved=true;if(preferences().BallPositionLocked)return;CancelTransition();RememberBeforeDrag();dragging=true;try{DragMove();}catch(InvalidOperationException){}finally{dragging=false;SnapToEdge();SavePosition();}
        }
        private void IslandMouseUp(object sender,MouseButtonEventArgs e)
        {
            if(!IsIsland||e.ChangedButton!=MouseButton.Left)return;e.Handled=true;
            bool activate=islandPointer&&!islandMoved;CancelIslandClick();if(activate){islandTapBefore=IslandExpanded;islandTapAt=Environment.TickCount;SetIslandExpanded(!IslandExpanded);}
        }
        private void CancelIslandClick(){islandPointer=false;islandMoved=false;if(Mouse.Captured==this)ReleaseMouseCapture();if(island!=null)island.SetPressed(false);}
        private void QueueIslandSave()
        {
            islandSavePending=true;
            if(islandSaveClock==null){islandSaveClock=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};islandSaveClock.Tick+=delegate{if(islandResizeActive)return;FlushIslandSave();};}
            islandSaveClock.Stop();islandSaveClock.Start();
        }
        private void FlushIslandSave(){if(islandSaveClock!=null)islandSaveClock.Stop();if(!islandSavePending)return;islandSavePending=false;save();}
        private void CancelIslandResize(bool preserveCurrent)
        {
            islandResizeGeneration++;bool resizing=islandResizeActive;islandResizeActive=false;
            if(island==null)return;Size current=island.StopMorph();
            if(preserveCurrent&&resizing){Width=current.Width;Height=current.Height;}
            else if(!IsVisible){island.SetPresentationSize(islandTargetWidth,islandTargetHeight);Width=islandTargetWidth;Height=islandTargetHeight;}
        }
        private System.Windows.Forms.Screen PreferredScreen()
        {string key=preferences().BallMonitor;return System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s=>String.Equals(s.DeviceName,key,StringComparison.OrdinalIgnoreCase));}
        private void RestorePlacement(System.Windows.Forms.Screen screen)
        {
            BallPlacement stored;var p=preferences();
            if(p.BallPlacements==null||!p.BallPlacements.TryGetValue(screen.DeviceName,out stored))
            {if(IsPillar)PositionDock();else Clamp();return;}
            string side=NormalizeDock(stored.Dock);if(dock!=side){dock=side;if(IsPillar)p.BallExpanded=false;Build();}
            var area=screen.WorkingArea;var work=new Rect(area.Left,area.Top,area.Width,area.Height);
            // The first placement selects the monitor; the second uses its resulting DPI.
            for(int pass=0;pass<2;pass++)
            {
                double scale=Scale;Size requested=new Size(Width,Height),remembered;if(IsCustom&&RememberedCustomSize(out remembered))requested=remembered;
                var bounds=stored.Restore(new Size(requested.Width*scale,requested.Height*scale),work);
                Width=bounds.Width/scale;Height=bounds.Height/scale;Place((int)bounds.Left,(int)bounds.Top);
            }
            NativeRect r;if(GetWindowRect(Handle,out r))dockAnchor=dock=="top"||dock=="bottom"?(r.Left+r.Right)/2.0:(r.Top+r.Bottom)/2.0;
            if(IsPillar)PositionDock();else Clamp();
        }
        private void QueuePlacementRepair()
        {
            if(disposing||placementRepairQueued||!IsVisible)return;placementRepairQueued=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(delegate
            {
                placementRepairQueued=false;if(disposing||!IsVisible||dragging)return;
                placementRepairPending=false;CancelDockMotion();UpdateLayout();
                var preferred=PreferredScreen();if(preferred!=null)RestorePlacement(preferred);else if(IsPillar)PositionDock();else Clamp();SavePositionCore(false);
            }));
        }
        internal void SnapToEdge()
        {
            if(Handle==IntPtr.Zero||disposing)return;CancelDockMotion();WindowInteraction.CompleteReveal(this);NativeRect r;GetWindowRect(Handle,out r);var area=WorkArea();
            int side=DockEdge(new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),new Rect(area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top),Scale,Array.IndexOf(new[]{"left","right","top","bottom"},dock));
            bool wasDocked=IsPillar;
            double anchor=side<2?(r.Top+r.Bottom)/2.0:(r.Left+r.Right)/2.0;
            Action settle=delegate
            {
                dockMotion=null;if(disposing)return;
                dock=side>=0?new[]{"left","right","top","bottom"}[side]:"";
                // Docking forgets the large state; every undock deterministically returns to small.
                if(wasDocked||dock.Length>0)preferences().BallExpanded=false;
                dockAnchor=anchor;Build();if(dock.Length>0)PositionDock();else Clamp();SavePosition();
            };
            int fps=Theme.ActivityFrameRate(preferences().OrbAnimation);
            if(side>=0&&!wasDocked&&IsVisible&&fps>0)
            {
                double scale=Scale;Rect final=DockBounds(side,anchor,new Rect(area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top),scale);
                Rect track=final;if(side==0)track.Width=6*scale;else if(side==1){track.X=final.Right-6*scale;track.Width=6*scale;}else if(side==2)track.Height=6*scale;else{track.Y=final.Bottom-6*scale;track.Height=6*scale;}
                dockMotion=new DockTransition(this,(FrameworkElement)Content,settle);StopActivityMotion();
                dockMotion.Begin(new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),track,side,DockPreview(side,scale),fps,IsCustom);
            }
            else if(wasDocked&&(side<0||dock!=new[]{"left","right","top","bottom"}[side]))WindowInteraction.ChangeShape(this,settle);
            else settle();
        }
        internal void CancelTransferMotion(){CancelIslandClick();CancelIslandResize(true);CancelDockMotion();}
        internal void CancelTransition(){CancelTransferMotion();WindowInteraction.CompleteReveal(this);}
        private void CancelDockMotion(){var motion=dockMotion;dockMotion=null;if(motion!=null)motion.Dispose();}
        private BitmapSource DockPreview(int side,double scale)
        {
            bool horizontal=side>=2;long now=LocalCodexUsage.Unix(DateTime.Now);
            var selected=preferences().BallStyle=="orb"?QuotaOrb.SelectWindow(bucket,preferences().OrbQuotaWindow):bucket==null?null:bucket.Secondary??bucket.Primary;
            double fraction=RemainingFraction(bucket,selected,now),width=horizontal?80:6,height=horizontal?6:80;
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
            {
                dc.PushOpacity(bucket!=null&&bucket.IsOnline?1:.72);
                dc.DrawRoundedRectangle(Theme.B("#50828F9E"),null,new Rect(0,0,width,height),4,4);
                if(fraction>0)dc.DrawRoundedRectangle(OrbPalette.ForBar(preferences(),selected!=null&&selected.Minutes>=1440,!horizontal),null,horizontal?new Rect(0,0,width*fraction,height):new Rect(0,height*(1-fraction),width,height*fraction),4,4);
                dc.Pop();
            }
            var image=new RenderTargetBitmap((int)Math.Ceiling(width*scale),(int)Math.Ceiling(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
        }
        internal static int DockEdge(Rect window,Rect work,double scale)
        {return DockEdge(window,work,scale,-1);}
        internal static int DockEdge(Rect window,Rect work,double scale,int previousSide)
        {
            // Overshooting a screen edge is still contact, not distance away from it.
            // Previously Abs() rejected these drops, then Clamp() left an undocked window flush with the edge.
            double[] distances={Math.Max(0,window.Left-work.Left),Math.Max(0,work.Right-window.Right),Math.Max(0,window.Top-work.Top),Math.Max(0,work.Bottom-window.Bottom)};
            // A docked bar needs a deliberate drag farther inward before it changes form.
            if(previousSide>=0&&previousSide<4&&distances[previousSide]<=48*scale)return previousSide;
            int side=Array.IndexOf(distances,distances.Min());return distances[side]<=32*scale?side:-1;
        }
        private void PositionDock()
        {
            if(Handle==IntPtr.Zero||dock.Length==0)return;var area=WorkArea();
            Rect target=DockBounds(Array.IndexOf(new[]{"left","right","top","bottom"},dock),dockAnchor,new Rect(area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top),Scale);Place((int)target.X,(int)target.Y);
        }
        internal static Rect DockBounds(int side,double anchor,Rect area,double scale)
        {
            bool horizontal=side>=2;int w=(int)Math.Round((horizontal?80:16)*scale),h=(int)Math.Round((horizontal?16:80)*scale);
            double x=side==0?area.Left:side==1?area.Right-w:(int)anchor-w/2;
            double y=side==2?area.Top:side==3?area.Bottom-h:(int)anchor-h/2;
            return new Rect(Math.Max(area.Left,Math.Min(area.Right-w,x)),Math.Max(area.Top,Math.Min(area.Bottom-h,y)),w,h);
        }
        private void Clamp()
        {
            if(Handle==IntPtr.Zero)return;NativeRect r;if(!GetWindowRect(Handle,out r))return;var area=WorkArea();double scale=Scale;
            Size requested=new Size(Width,Height),remembered;
            // Constrain the outer window, not the page's remembered request. Moving back to
            // a larger monitor restores its requested viewport without reloading the browser.
            if(IsCustom&&RememberedCustomSize(out remembered))requested=remembered;
            var bounds=WindowInteraction.FitBounds(new Rect(r.Left,r.Top,requested.Width*scale,requested.Height*scale),new Rect(area.Left,area.Top,area.Right-area.Left,area.Bottom-area.Top),0);
            if(Math.Abs(Width-bounds.Width/scale)>.01)Width=bounds.Width/scale;
            if(Math.Abs(Height-bounds.Height/scale)>.01)Height=bounds.Height/scale;
            Place((int)bounds.X,(int)bounds.Y);
        }
        private void Place(int x,int y){if(Handle!=IntPtr.Zero)SetWindowPos(Handle,IntPtr.Zero,x,y,(int)Math.Round(Width*Scale),(int)Math.Round(Height*Scale),0x14);}
        private bool RememberedCustomSize(out Size size)
        {
            size=new Size();double[] saved;var sizes=preferences().CustomShapeSizes;
            if(customSizeKey==null||sizes==null||!sizes.TryGetValue(customSizeKey,out saved)||saved==null||saved.Length!=2)return false;
            size=new Size(Theme.Bound(saved[0],64,800,240),Theme.Bound(saved[1],40,600,90));return true;
        }
        private void FlushCustomSize(){customSizeSaveClock.Stop();if(!customSizeDirty)return;customSizeDirty=false;save();}
        private void RememberBeforeDrag()
        {NativeRect r;if(Handle!=IntPtr.Zero&&GetWindowRect(Handle,out r))RememberMonitor(preferences(),r);}
        private void RememberMonitor(Preferences p,NativeRect r)
        {
            var screen=System.Windows.Forms.Screen.FromHandle(Handle);var work=screen.WorkingArea;
            if(p.BallPlacements==null)p.BallPlacements=new Dictionary<string,BallPlacement>(StringComparer.OrdinalIgnoreCase);
            if(!p.BallPlacements.ContainsKey(screen.DeviceName)&&p.BallPlacements.Count>=8)p.BallPlacements.Remove(p.BallPlacements.Keys.First());
            p.BallPlacements[screen.DeviceName]=BallPlacement.Capture(new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),new Rect(work.Left,work.Top,work.Width,work.Height),dock);p.BallMonitor=screen.DeviceName;
        }
        private void SavePosition(){SavePositionCore(true);}
        private void SavePositionCore(bool remember)
        {
            NativeRect r;if(Handle==IntPtr.Zero||!GetWindowRect(Handle,out r))return;
            var p=preferences();p.BallLeft=r.Left;p.BallTop=r.Top;p.BallDock=dock;
            if(remember)RememberMonitor(p,r);
            customSizeSaveClock.Stop();customSizeDirty=false;save();
        }
        public void Dispose(){if(disposing)return;disposing=true;CancelIslandClick();CancelIslandResize(false);FlushIslandSave();FlushCustomSize();customGeneration++;customResizeQueued=false;CancelDockMotion();WindowInteraction.CompleteReveal(this);StopActivityMotion();if(orb!=null)orb.Dispose();if(island!=null)island.Dispose();if(custom!=null)custom.Dispose();Close();}
    }

    // A single paint layer for both capsule sizes. Cached pens and perimeter samples avoid
    // layout, blur surfaces and per-frame geometry/brush allocations on low-end devices.
    internal sealed class CapsuleActivityChrome : FrameworkElement
    {
        internal static readonly DependencyProperty PhaseProperty=DependencyProperty.Register("Phase",typeof(double),typeof(CapsuleActivityChrome),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty BreathProperty=DependencyProperty.Register("Breath",typeof(double),typeof(CapsuleActivityChrome),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        private const int Samples=512,TailSteps=72;
        private int perimeterSamples=Samples,trailSteps=TailSteps;
        private bool lightweight;
        internal static int PerimeterSamplesFor(int fps){return fps<=30?128:Samples;}
        internal static int TrailStepsFor(int fps){return fps<=30?24:TailSteps;}
        private readonly Point[] perimeter=new Point[Samples+1];
        private readonly Pen[] trail=new Pen[TailSteps],halo=new Pen[TailSteps];
        private readonly TranslateTransform sweepPosition=new TranslateTransform();
        private readonly Pen frame=new Pen(Theme.Frame,1);
        private Brush tint,sweep;
        private Pen rim;
        private RectangleGeometry clip;
        private Rect bounds;
        private double tailFraction;
        private string paletteKey;
        private bool active;

        internal CapsuleActivityChrome(){IsHitTestVisible=false;}
        internal void ApplyPalette(Preferences p)
        {
            string key=OrbPalette.Key(p);if(key==paletteKey)return;paletteKey=key;
            var colors=OrbPalette.EffectiveColors(p,false).Select(c=>(Color)ColorConverter.ConvertFromString(c)).ToArray();
            tint=OrbPalette.ForBar(p,false);rim=new Pen(tint,1.2);rim.Freeze();
            var light=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,.3)};
            light.GradientStops.Add(new GradientStop(Colors.Transparent,0));
            for(int i=0;i<colors.Length;i++)light.GradientStops.Add(new GradientStop(colors[i],.25+.5*i/Math.Max(1,colors.Length-1)));
            light.GradientStops.Add(new GradientStop(Colors.Transparent,1));light.Freeze();sweep=light;
            for(int i=0;i<TailSteps;i++)
            {
                double t=(i+1.0)/TailSteps,index=t*(colors.Length-1);int a=(int)index,b=Math.Min(colors.Length-1,a+1);double mix=index-a;
                Color color=Color.FromRgb((byte)(colors[a].R+(colors[b].R-colors[a].R)*mix),(byte)(colors[a].G+(colors[b].G-colors[a].G)*mix),(byte)(colors[a].B+(colors[b].B-colors[a].B)*mix));
                // Overlapping round segments form one uninterrupted comet with a fading tail.
                trail[i]=MakePen(color,Math.Pow(t,1.6),2.1);halo[i]=MakePen(color,.17*Math.Pow(t,2),6);
            }
            InvalidateVisual();
        }
        private static Pen MakePen(Color color,double alpha,double width)
        {
            color.A=(byte)Math.Round(255*alpha);var pen=new Pen(new SolidColorBrush(color),width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};pen.Freeze();return pen;
        }
        internal void Start(int fps)
        {
            lightweight=fps<=30;trailSteps=TrailStepsFor(fps);int samples=PerimeterSamplesFor(fps);
            if(perimeterSamples!=samples){perimeterSamples=samples;CachePerimeter();}
            active=true;
            var phase=new DoubleAnimation(0,1,TimeSpan.FromSeconds(3.2)){RepeatBehavior=RepeatBehavior.Forever};Timeline.SetDesiredFrameRate(phase,fps);
            var breath=new DoubleAnimation(0,1,TimeSpan.FromSeconds(1.35)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};Timeline.SetDesiredFrameRate(breath,fps);
            BeginAnimation(PhaseProperty,phase);BeginAnimation(BreathProperty,breath);
        }
        internal void Stop(){active=false;BeginAnimation(PhaseProperty,null);BeginAnimation(BreathProperty,null);InvalidateVisual();}
        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);if(ActualWidth<4||ActualHeight<4)return;
            bounds=new Rect(1.05,1.05,ActualWidth-2.1,ActualHeight-2.1);
            clip=new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight),16,16);clip.Freeze();
            CachePerimeter();
        }
        private void CachePerimeter()
        {
            if(bounds.Width<=0||bounds.Height<=0)return;
            var path=new RectangleGeometry(bounds,14.95,14.95).GetFlattenedPathGeometry(lightweight?.18:.05,ToleranceType.Absolute);
            for(int i=0;i<=perimeterSamples;i++){Point tangent;path.GetPointAtFractionLength(i/(double)perimeterSamples,out perimeter[i],out tangent);}
            double length=2*(bounds.Width+bounds.Height-4*14.95)+2*Math.PI*14.95;
            tailFraction=Math.Min(150,Math.Max(85,length*.24))/length;
        }
        private Point At(double fraction)
        {
            fraction-=Math.Floor(fraction);double index=fraction*perimeterSamples;int a=(int)index;double t=index-a;
            return new Point(perimeter[a].X+(perimeter[a+1].X-perimeter[a].X)*t,perimeter[a].Y+(perimeter[a+1].Y-perimeter[a].Y)*t);
        }
        protected override void OnRender(DrawingContext dc)
        {
            if(clip==null)return;
            dc.DrawRoundedRectangle(null,frame,bounds,14.95,14.95);
            if(!active||tint==null)return;
            double phase=(double)GetValue(PhaseProperty),breath=(double)GetValue(BreathProperty);
            dc.PushClip(clip);
            // Brightness changes the whole background, never the text opacity or hit targets.
            dc.PushOpacity(.06+(Theme.IsLight?.25:.34)*breath);dc.DrawRectangle(tint,null,new Rect(RenderSize));dc.Pop();
            sweepPosition.X=(-1.05+2.1*phase)*ActualWidth;
            dc.PushOpacity(.14+.22*breath);dc.PushTransform(sweepPosition);
            dc.DrawRectangle(sweep,null,new Rect(0,0,ActualWidth,ActualHeight));dc.Pop();dc.Pop();
            dc.PushOpacity(.28+.40*breath);dc.DrawRoundedRectangle(null,rim,bounds,14.95,14.95);dc.Pop();
            dc.PushOpacity(.72+.28*breath);
            Point previous=At(phase-tailFraction);
            for(int i=0;i<trailSteps;i++)
            {
                Point next=At(phase-tailFraction+tailFraction*(i+1)/trailSteps);
                int pen=(i+1)*TailSteps/trailSteps-1;
                // Fewer connected segments preserve the continuous rim; eco removes the
                // extra halo pass rather than turning the perimeter into a dotted stroke.
                if(!lightweight)dc.DrawLine(halo[pen],previous,next);
                dc.DrawLine(trail[pen],previous,next);previous=next;
            }
            dc.Pop();dc.Pop();
        }
    }
}
