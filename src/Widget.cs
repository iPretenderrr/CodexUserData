using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUserData
{
    internal sealed class WidgetWindow : Window
    {
        private Preferences prefs;
        private readonly bool preview;
        private bool busy,closed,ready,changingLayout;
        private double expandedRestoreHeight;
        private FloatingBall ball;
        private CodexActivity activity;
        private string activityRoot;
        private StackPanel toolbar;
        private int revision;
        private UsageSnapshot snapshot;
        private LocalCodexUsage local;
        private string localRoot;
        private readonly DispatcherTimer timer=new DispatcherTimer();
        private readonly DispatcherTimer activityStatusClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private ActivityReport activityReport;
        private readonly Dictionary<string,TextBlock> values=new Dictionary<string,TextBlock>();
        private readonly Dictionary<string,TextBlock> labels=new Dictionary<string,TextBlock>();
        private readonly Dictionary<string,Button> ranges=new Dictionary<string,Button>();
        private readonly ChoiceButton source,app;
        private readonly Button pin,fold,minimize;
        private readonly MenuItem minimizeItem;
        private bool restoreHistoryAfterMinimize;
        private readonly TextBlock brand;
        private readonly TextBlock heroLabel,heroValue,heroExact,heroNote,status,warning;
        private readonly TextBlock activityStatus,activityObservedAt;
        private readonly Button coverageToggle;
        private StyledWindow coverageWindow;
        private TextBlock coverageExplanation;
        private string coverageDetails="";
        private bool coverageReadFailed,coverageIncomplete;
        private readonly HistoryPanel history;
        private readonly ModelPanel modelPanel;
        private readonly QuotaStatus quota;
        private HistoryPanel largeHistory;
        private Window historyWindow;
        private SettingsWindow settingsWindow;
        private readonly StackPanel body;
        private readonly UniformGrid cards;
        private readonly ScrollViewer scroll;
        private readonly Border hero;
        private string heroKey="tokens";

        internal WidgetWindow(Preferences preferences,bool isPreview)
        {
            prefs=preferences;prefs.Validate();Theme.Apply(prefs);preview=isPreview;expandedRestoreHeight=Math.Max(480,prefs.Height);Title=Program.WindowTitle;Theme.InstallStyles(this);
            Width=prefs.Width;Height=prefs.Collapsed?305:prefs.Height;MinWidth=260;MinHeight=280;MaxWidth=1100;MaxHeight=1000;
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;AllowsTransparency=true;Background=Brushes.Transparent;Opacity=prefs.Opacity;
            Topmost=prefs.Pinned;ShowInTaskbar=true;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");UseLayoutRounding=true;SnapsToDevicePixels=true;
            if(!Double.IsNaN(prefs.Left)&&!Double.IsNaN(prefs.Top)){Left=prefs.Left;Top=prefs.Top;WindowStartupLocation=WindowStartupLocation.Manual;}else WindowStartupLocation=WindowStartupLocation.CenterScreen;
            // Transition opacity belongs to the outer layer; completion feedback owns the skin.
            var shell=new Border{Background=Theme.WindowBackground,BorderBrush=Theme.Frame,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(15),Padding=new Thickness(15,9,15,10)};Content=new Border{Child=shell};
            var context=Theme.Menu();var setupItem=new MenuItem{Header="悬浮窗设置"};setupItem.Click+=delegate{OpenSettings();};context.Items.Add(setupItem);
            var chooseDatabase=new MenuItem{Header="选择 CC Switch 数据库…"};chooseDatabase.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="SQLite 数据库|*.db|所有文件|*.*",FileName=prefs.Database};if(picker.ShowDialog(this)==true){prefs.Database=picker.FileName;prefs.Source="ccswitch";source.Select(prefs.Source);UpdateSource();SelectionChanged();}};context.Items.Add(chooseDatabase);
            var help=new MenuItem{Header="统计口径与使用说明"};help.Click+=delegate{MessageBox.Show(this,"CC Switch：按数据库记录归一化并去重。\n本地 Codex：读取本机日志，Tokens = 输入（含缓存）+ 输出；推理已含在输出。\n两个来源不相加。API 等效价值按模型、未缓存输入、缓存、输出分别计价，是标准短上下文基准估算，不是订阅实际扣费。\n\n设置中可调透明度、刷新时间、指标和路径。四边与四角都可拖动缩放。\n完整说明位于程序目录的 README.md。", "统计口径",MessageBoxButton.OK,MessageBoxImage.Information);};context.Items.Add(help);
            minimizeItem=new MenuItem{Header="最小化到任务栏"};minimizeItem.Click+=delegate{MinimizeWindow();};context.Items.Add(minimizeItem);
            var quit=new MenuItem{Header="退出软件"};quit.Click+=delegate{RequestClose();};context.Items.Add(quit);shell.ContextMenu=context;
            var grid=new Grid();shell.Child=grid;
            foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})grid.RowDefinitions.Add(new RowDefinition{Height=height});
            var header=new DockPanel{Margin=new Thickness(0,0,0,5),Background=Brushes.Transparent};grid.Children.Add(header);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal};toolbar=buttons;DockPanel.SetDock(buttons,Dock.Right);header.Children.Add(buttons);
            var charts=Theme.ToolbarButton("chart","放大查看用量图表");charts.Click+=delegate{OpenHistory();};buttons.Children.Add(charts);
            var settings=Theme.ToolbarButton("settings","设置");settings.Click+=delegate{OpenSettings();};buttons.Children.Add(settings);
            pin=Theme.ToolbarButton("pin","置顶");pin.Click+=delegate{prefs.Pinned=!prefs.Pinned;Topmost=prefs.Pinned;UpdateButtons();Persist();};buttons.Children.Add(pin);
            fold=Theme.ToolbarButton("collapse","折叠 / 展开");fold.Click+=delegate{ToggleCollapsed();};buttons.Children.Add(fold);
            var floating=Theme.ToolbarButton("bubble","切换为悬浮球");floating.Click+=delegate{OpenBall();};buttons.Children.Add(floating);
            minimize=Theme.ToolbarButton("minimize","最小化到任务栏");minimize.Click+=delegate{MinimizeWindow();};buttons.Children.Add(minimize);
            var close=Theme.ToolbarButton("close","退出软件");close.Click+=delegate{RequestClose();};buttons.Children.Add(close);
            brand=Theme.Text("●  CodexUserData",11,Theme.Ink);brand.FontWeight=FontWeights.SemiBold;brand.ToolTip="拖动这里移动窗口";header.Children.Add(brand);
            WindowInteraction.Header(this,header,ToggleCollapsed,()=>{ClampToMonitor();Persist();});
            WindowInteraction.Attach(this,()=>{if(!prefs.Collapsed)expandedRestoreHeight=Math.Max(480,Height);ClampToMonitor();Persist();},()=>{if(!prefs.Collapsed)expandedRestoreHeight=Math.Max(480,Height);});
            SourceInitialized+=delegate
            {
                var hwndSource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);hwndSource.AddHook(delegate(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
                {
                    if(closed)return IntPtr.Zero;
                    if(message==0x007E||message==0x02E0)Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(!closed)ClampToMonitor();}));
                    if(message!=Program.ShowMainMessage)return IntPtr.Zero;handled=true;Dispatcher.BeginInvoke(new Action(RestoreWindow));return IntPtr.Zero;
                });
            };
            var filters=new Grid{Margin=new Thickness(0,0,0,9)};filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});var filtersAndQuota=new StackPanel();Grid.SetRow(filtersAndQuota,1);grid.Children.Add(filtersAndQuota);filtersAndQuota.Children.Add(filters);
            quota=new QuotaStatus(()=>prefs,()=>{if(!IsVisible||WindowState==WindowState.Minimized)RestoreWindow();else WindowInteraction.Hide(this);},RequestClose,preview,()=>RestoreWindowThen(RefreshData),()=>RestoreWindowThen(OpenHistory));filtersAndQuota.Children.Add(quota);quota.Changed+=UpdateBall;
            source=new ChoiceButton(new Dictionary<string,string>{{"ccswitch","CC Switch"},{"local","本地 Codex"}},"数据来源");source.Select(prefs.Source);filters.Children.Add(source);
            app=new ChoiceButton(new Dictionary<string,string>{{"","全部应用"},{"claude","Claude"},{"codex","Codex"},{"gemini","Gemini"},{"opencode","OpenCode"},{"grokbuild","Grok"},{"hermes","Hermes"},{"pi","Pi"}},"应用筛选");app.Select(prefs.App);app.Margin=new Thickness(7,0,0,0);Grid.SetColumn(app,1);filters.Children.Add(app);
            source.Changed+=delegate(string v){prefs.Source=v;UpdateSource();SelectionChanged();};app.Changed+=delegate(string v){prefs.App=v;SelectionChanged();};
            scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly};Grid.SetRow(scroll,2);grid.Children.Add(scroll);
            body=new StackPanel();scroll.Content=body;
            hero=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(11),Padding=new Thickness(13,10,13,10)};body.Children.Add(hero);
            WindowInteraction.Header(this,hero,null,()=>{ClampToMonitor();Persist();});
            var heroStack=new StackPanel();hero.Child=heroStack;
            var activityLine=new DockPanel{Margin=new Thickness(0,0,0,8)};heroStack.Children.Add(activityLine);
            activityObservedAt=Theme.Text("",9,Theme.Muted);activityObservedAt.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(activityObservedAt,Dock.Right);activityLine.Children.Add(activityObservedAt);
            AutomationProperties.SetAutomationId(activityObservedAt,"ActivityObservedAt");
            activityStatus=Theme.Text("Codex · 任务状态未知",10,Theme.Muted);activityStatus.TextTrimming=TextTrimming.CharacterEllipsis;activityLine.Children.Add(activityStatus);AutomationProperties.SetAutomationId(activityStatus,"ActivityStatus");
            heroLabel=Theme.Text("今日 · 真实消耗 Tokens",11,Theme.Muted);heroStack.Children.Add(heroLabel);
            heroValue=Theme.Text("—",38,Theme.Ink);heroValue.FontWeight=FontWeights.SemiBold;heroValue.Margin=new Thickness(0,1,0,1);AutomationProperties.SetAutomationId(heroValue,"totalTokens");heroStack.Children.Add(heroValue);
            heroExact=Theme.Text("",11,Theme.Muted);heroExact.TextWrapping=TextWrapping.Wrap;AutomationProperties.SetAutomationId(heroExact,"ExactTokens");heroExact.Visibility=Visibility.Collapsed;heroStack.Children.Add(heroExact);
            heroNote=Theme.Text("等待数据",10,Theme.Muted);heroNote.TextTrimming=TextTrimming.CharacterEllipsis;heroStack.Children.Add(heroNote);
            var period=new UniformGrid{Columns=4,Margin=new Thickness(0,10,0,9)};body.Children.Add(period);
            foreach(var entry in new Dictionary<string,string>{{"today","今日"},{"week","7 天"},{"month","30 天"},{"all","全部"}}){string key=entry.Key;var b=Theme.Button(entry.Value,"时间范围："+entry.Value,20);b.Margin=new Thickness(1,0,1,0);b.Click+=delegate{prefs.Range=key;UpdateButtons();SelectionChanged();};ranges[key]=b;period.Children.Add(b);}
            cards=new UniformGrid{Columns=3,Margin=new Thickness(-3,0,-3,0)};body.Children.Add(cards);
            modelPanel=new ModelPanel();body.Children.Add(modelPanel);
            history=new HistoryPanel{ShowCoverage=false};history.Configure(prefs.ShowHeatmap,prefs.ShowTrend,prefs.TrendDays);history.RangeChanged+=SetTrendRange;body.Children.Add(history);
            var footer=new Grid{Margin=new Thickness(0,7,0,0)};footer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});footer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Grid.SetRow(footer,3);grid.Children.Add(footer);
            warning=Theme.Text("",10,Theme.Warning);warning.TextTrimming=TextTrimming.CharacterEllipsis;
            coverageToggle=Theme.Button("","查看统计说明与处理建议",0);coverageToggle.Content=warning;coverageToggle.Height=24;coverageToggle.Padding=new Thickness(3,1,3,1);coverageToggle.HorizontalContentAlignment=HorizontalAlignment.Stretch;coverageToggle.Visibility=Visibility.Collapsed;coverageToggle.Click+=delegate{OpenCoverage();};AutomationProperties.SetAutomationId(coverageToggle,"CoverageToggle");footer.Children.Add(coverageToggle);
            var footRow=new DockPanel();Grid.SetRow(footRow,1);footer.Children.Add(footRow);
            var refresh=Theme.Button("","立即刷新 (F5)",32);refresh.Height=32;refresh.Padding=new Thickness(7);refresh.Content=Theme.Icon("refresh",Theme.Muted);refresh.Click+=delegate{RefreshData();};DockPanel.SetDock(refresh,Dock.Right);footRow.Children.Add(refresh);
            status=Theme.Text("准备读取数据",10,Theme.Muted);status.TextTrimming=TextTrimming.CharacterEllipsis;AutomationProperties.SetAutomationId(status,"RefreshStatus");footRow.Children.Add(status);
            SizeChanged+=delegate{Reflow();if(ready&&!preview&&!changingLayout&&WindowState==WindowState.Normal)AdaptToSize();};KeyDown+=delegate(object s,KeyEventArgs e){if(e.Key==Key.F5)RefreshData();};
            Loaded+=delegate{ready=true;if(!preview)AdaptToSize();ClampToMonitor();Persist();if(!preview){timer.Interval=TimeSpan.FromSeconds(prefs.RefreshSeconds);timer.Start();quota.CompletionChanged+=UpdateCompletionViews;PreviewMouseMove+=delegate{quota.AcknowledgeCompletion();};quota.Start();EnsureActivity();RefreshData();if(prefs.BallMode)OpenBall();}};
            timer.Tick+=delegate{RefreshData();};StateChanged+=delegate{if(WindowState==WindowState.Minimized&&prefs.MinimizeToTray){MinimizeWindow();return;}if(WindowState==WindowState.Normal){RestoreHistory();if(snapshot!=null)ApplySnapshot(snapshot);RefreshData();}};
            activityStatusClock.Tick+=delegate{UpdateActivityStatus();};IsVisibleChanged+=delegate{UpdateActivityClock();};StateChanged+=delegate{UpdateActivityClock();};
            Closing+=delegate{closed=true;timer.Stop();activityStatusClock.Stop();if(activity!=null)activity.Dispose();if(ball!=null)ball.Dispose();quota.Dispose();Persist();if(historyWindow!=null)historyWindow.Close();if(coverageWindow!=null)coverageWindow.Close();};
            BuildCards();UpdateButtons();UpdateSource();Reflow();
        }
        private void BuildCards()
        {
            cards.Children.Clear();values.Clear();labels.Clear();heroKey=prefs.Metrics.Contains("tokens")?"tokens":prefs.Metrics[0];
            foreach(string key in prefs.Metrics.Where(k=>k!=heroKey))
            {
                var card=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(8),Padding=new Thickness(10,10,7,10),Margin=new Thickness(3,3,3,3),MinHeight=67};
                var stack=new StackPanel();var label=Theme.Text(Theme.MetricLabels[key],10,Theme.Muted);label.TextTrimming=TextTrimming.CharacterEllipsis;var value=Theme.Text("—",19,Theme.Ink);value.FontWeight=FontWeights.SemiBold;value.Margin=new Thickness(0,5,0,0);value.TextTrimming=TextTrimming.CharacterEllipsis;
                AutomationProperties.SetAutomationId(value,key);stack.Children.Add(label);stack.Children.Add(value);card.Child=stack;cards.Children.Add(card);values[key]=value;labels[key]=label;
            }
            heroLabel.Text=PeriodName()+" · "+LabelFor(heroKey);if(snapshot!=null)ApplySnapshot(snapshot);
        }
        private string PeriodName(){return prefs.Range=="today"?"今日":prefs.Range=="week"?"近 7 天":prefs.Range=="month"?"近 30 天":"全部时间";}
        private string LabelFor(string key){return key=="requests"?(prefs.Source=="local"?"用量记录":"请求数"):key=="cost"&&prefs.Source=="local"?"实际费用 · USD":Theme.MetricLabels[key];}
        private void UpdateSource(){app.Visibility=prefs.Source=="local"?Visibility.Collapsed:Visibility.Visible;Grid.SetColumnSpan(source,prefs.Source=="local"?2:1);heroLabel.Text=PeriodName()+" · "+LabelFor(heroKey);}
        private void UpdateButtons()
        {
            string minimizeName=prefs.MinimizeToTray?"最小化到托盘":"最小化到任务栏";
            AutomationProperties.SetName(minimize,minimizeName);minimize.ToolTip=minimizeName+" · 后台继续统计";minimizeItem.Header=minimizeName;
            pin.Tag=prefs.Pinned?"selected":null;pin.Foreground=prefs.Pinned?Theme.Accent:Theme.Muted;pin.ToolTip=prefs.Pinned?"已置顶 · 点击取消":"未置顶 · 点击置顶";
            AutomationProperties.SetItemStatus(pin,prefs.Pinned?"已置顶":"未置顶");fold.Content=Theme.ToolbarIcon(prefs.Collapsed?"expand":"collapse");fold.ToolTip=prefs.Collapsed?"展开完整界面":"收起为精简窗口";AutomationProperties.SetItemStatus(fold,prefs.Collapsed?"已收起":"已展开");
            foreach(var entry in ranges){entry.Value.Background=entry.Key==prefs.Range?Theme.Hover:Brushes.Transparent;entry.Value.Foreground=entry.Key==prefs.Range?Theme.Accent:Theme.Muted;}
            for(int n=1;n<body.Children.Count;n++)body.Children[n].Visibility=prefs.Collapsed?Visibility.Collapsed:Visibility.Visible;
            heroNote.Visibility=prefs.Collapsed?Visibility.Collapsed:Visibility.Visible;
            heroExact.Visibility=!prefs.Collapsed&&heroKey=="tokens"&&snapshot!=null?Visibility.Visible:Visibility.Collapsed;
            history.Visibility=!prefs.Collapsed&&(prefs.ShowHeatmap||prefs.ShowTrend)?Visibility.Visible:Visibility.Collapsed;
            modelPanel.Visibility=!prefs.Collapsed&&prefs.ShowModels?Visibility.Visible:Visibility.Collapsed;
        }
        private void ToggleCollapsed()
        {
            WindowInteraction.ChangeShape(this,delegate
            {
                changingLayout=true;
                try{if(!prefs.Collapsed)expandedRestoreHeight=Math.Max(480,Height);prefs.Collapsed=!prefs.Collapsed;Height=prefs.Collapsed?305:expandedRestoreHeight;UpdateButtons();if(!prefs.Collapsed&&snapshot!=null)ApplySnapshot(snapshot);Reflow();}
                finally{changingLayout=false;}Persist();
            });
        }
        internal void AdaptToSize()
        {
            // Hysteresis: resizing near one boundary never flips the layout back and forth.
            bool compact=prefs.Collapsed?Height<440:Height<=350;
            if(compact==prefs.Collapsed)return;prefs.Collapsed=compact;UpdateButtons();
            if(!compact&&snapshot!=null)ApplySnapshot(snapshot);Reflow();
        }
        private void Reflow()
        {
            // Change the number of columns, not a whole-window scale transform: text remains legible on small displays.
            double width=ActualWidth>0?ActualWidth:Width;if(toolbar!=null)foreach(Button button in toolbar.Children){button.MinWidth=width<330?28:32;button.Width=width<330?28:32;}brand.Visibility=width<510?Visibility.Collapsed:Visibility.Visible;
            int desired=width<420?2:width<740?3:4,count=Math.Max(1,cards.Children.Count),rows=(int)Math.Ceiling(count/(double)desired);cards.Columns=(int)Math.Ceiling(count/(double)rows);
            activityObservedAt.Visibility=width<350||prefs.Collapsed?Visibility.Collapsed:Visibility.Visible;
            heroValue.FontSize=prefs.Collapsed?30:Math.Max(27,Math.Min(44,(width-58)/8.5));
            var measured=new FormattedText(heroValue.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(heroValue.FontFamily,FontStyles.Normal,heroValue.FontWeight,FontStretches.Normal),heroValue.FontSize,Theme.Ink,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double available=Math.Max(160,width-74);if(measured.Width>available)heroValue.FontSize=Math.Max(20,heroValue.FontSize*available/measured.Width);
        }
        private void SelectionChanged()
        {
            revision++;snapshot=null;quota.AcceptUsage(null,Scope());heroValue.Text="—";heroExact.Text="";heroExact.Visibility=Visibility.Collapsed;heroLabel.Text=PeriodName()+" · "+LabelFor(heroKey);foreach(var text in values.Values)text.Text="—";SetCoverageNotice("",false,false);heroNote.Text="等待当前范围的数据";modelPanel.Apply(null);history.Apply(null,Scope());if(largeHistory!=null)largeHistory.Apply(null,Scope());Persist();RefreshData();
        }
        private void OpenSettings()
        {
            if(closed)return;
            if(settingsWindow!=null){settingsWindow.Activate();return;}
            Persist();double original=Opacity;var settings=new SettingsWindow(prefs,v=>Opacity=v,()=>Diagnostics.Create(prefs,snapshot,activityReport,quota.MainBucket)){Owner=this};
            settingsWindow=settings;bool? accepted;
            try{accepted=settings.ShowDialog();}finally{settingsWindow=null;}
            if(closed)return;
            if(accepted==true)
            {
                bool newAccount=prefs.CodexHome!=settings.Result.CodexHome||prefs.QuotaCli!=settings.Result.QuotaCli;prefs=settings.Result;Program.Save(prefs);if(prefs.StartWithCodex)CodexLaunchWatcher.Ensure();quota.Configure(newAccount);EnsureActivity();Opacity=prefs.Opacity;source.Select(prefs.Source);app.Select(prefs.App);timer.Interval=TimeSpan.FromSeconds(prefs.RefreshSeconds);history.Configure(prefs.ShowHeatmap,prefs.ShowTrend,prefs.TrendDays);UpdateSource();BuildCards();UpdateButtons();SelectionChanged();
            }
            else Opacity=original;
            Theme.Apply(prefs);quota.ApplyTheme();UpdateCompletionViews();
        }
        private string Scope(){return prefs.Source=="local"?"本地 Codex": "CC Switch · "+(String.IsNullOrEmpty(prefs.App)?"全部应用":prefs.App);}
        private void SetTrendRange(int value){prefs.TrendDays=value;history.SetRange(value);if(largeHistory!=null)largeHistory.SetRange(value);Persist();}
        private void RequestClose()
        {
            if(closed)return;
            // The main window is hidden in floating mode; dismiss the visible form first.
            if(ball!=null&&ball.IsVisible){ball.CancelTransition();WindowInteraction.Hide(ball,()=>WindowInteraction.Close(this));}
            else WindowInteraction.Close(this);
        }
        internal void MinimizeWindow()
        {
            if(closed)return;
            Persist();
            if(prefs.MinimizeToTray)
            {
                // Keep scanners/quota timers running; hide owned charts too, preserving their selection.
                if(historyWindow!=null&&historyWindow.IsVisible){restoreHistoryAfterMinimize=true;WindowInteraction.Hide(historyWindow);}
                ShowInTaskbar=false;WindowInteraction.Hide(this);
            }
            else WindowState=WindowState.Minimized;
        }
        private void RestoreHistory()
        {
            if(!restoreHistoryAfterMinimize)return;restoreHistoryAfterMinimize=false;
            if(historyWindow!=null)historyWindow.Show();
        }
        private void RestoreWindow(){RestoreWindowThen(null);}
        private void RestoreWindowThen(Action ready)
        {
            if(closed)return;
            if(!prefs.BallMode&&IsVisible&&WindowState==WindowState.Normal){WindowInteraction.ResumeReveal(this);Activate();if(ready!=null)ready();return;}
            // A second restore can arrive before the first animation has hidden the ball.
            bool fromBall=prefs.BallMode||(ball!=null&&ball.IsVisible);prefs.BallMode=false;if(ball!=null)ball.CancelTransition();
            WindowInteraction.ShowFrom(this,ball,delegate
            {
                ShowInTaskbar=true;WindowState=WindowState.Normal;
                if(fromBall){changingLayout=true;try{prefs.Collapsed=false;Height=Math.Max(480,expandedRestoreHeight);UpdateButtons();}finally{changingLayout=false;}}
                if(snapshot!=null)ApplySnapshot(snapshot);
            },delegate{RestoreHistory();Activate();Persist();if(ready!=null)ready();});
        }
        private void OpenBall()
        {
            if(preview||closed)return;Persist();prefs.BallMode=true;
            if(ball==null){ball=new FloatingBall(()=>prefs,Persist,RestoreWindow,RequestClose);ball.PreviewMouseMove+=delegate{quota.AcknowledgeCompletion();};ball.SettingsRequested=()=>RestoreWindowThen(OpenSettings);if(Double.IsNaN(prefs.BallLeft)){ball.Left=Left+20;ball.Top=Top+20;}}
            ball.CancelTransition();if(historyWindow!=null)WindowInteraction.Close(historyWindow);if(coverageWindow!=null)WindowInteraction.Close(coverageWindow);WindowInteraction.ShowFrom(ball,this,UpdateBall,Persist);
        }
        private void UpdateBall()
        {
            if(closed)return;
            EnsureActivity();if(ball==null)return;
            ball.Apply(snapshot,quota.MainBucket,Scope(),quota.StatusText);
        }
        private void UpdateCompletionViews(){CompletionFeedback.Set(this,quota.CompletionPending);if(ball!=null)CompletionFeedback.Set(ball,quota.CompletionPending);UpdateActivityStatus();}
        internal void ApplyActivityReport(ActivityReport report)
        {
            if(closed)return;activityReport=report;quota.ApplyActivity(report);if(ball!=null)ball.ApplyActivity(report);UpdateCompletionViews();
        }
        private void UpdateActivityClock(){if(!preview&&!closed&&IsVisible&&WindowState!=WindowState.Minimized){activityStatusClock.Start();UpdateActivityStatus();}else activityStatusClock.Stop();}
        private void UpdateActivityStatus()
        {
            if(closed||activityStatus==null)return;long now=LocalCodexUsage.Unix(DateTime.Now),at=activityReport==null?0:activityReport.ObservedAt;
            bool fresh=at>0&&at<=now+5&&now-at<=5;
            string text="Codex · "+QuotaStatus.ActivityLabel(activityReport,quota.CompletionPending,now);
            if(activityStatus.Text!=text)activityStatus.Text=text;
            activityStatus.Foreground=fresh&&activityReport.ActiveTasks>0||quota.CompletionPending?Theme.Accent:Theme.Muted;
            bool unavailable=activityReport!=null&&activityReport.MonitoringUnavailable;
            string observed=unavailable?"监测受限":fresh?DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime.ToString("HH:mm:ss")+" 更新":at>0?"状态过期":"未读取";
            if(activityObservedAt.Text!=observed)activityObservedAt.Text=observed;
            AutomationProperties.SetItemStatus(activityStatus,unavailable?"部分 Codex 日志无法读取，不能据此确认空闲或全部完成":fresh?"近期本地任务事件":"缺少近期任务事件，不能确认正在运行或已经结束");
        }
        private void EnsureActivity()
        {
            if(!preview&&(activity==null||!String.Equals(activityRoot,prefs.CodexHome,StringComparison.OrdinalIgnoreCase)))
            {
                if(activity!=null)activity.Dispose();activityRoot=prefs.CodexHome;
                var current=new CodexActivity(activityRoot,false);activity=current;activityReport=null;UpdateActivityStatus();if(ball!=null)ball.ApplyActivity(new ActivityReport());
                current.Changed+=report=>
                {
                    if(closed)return;
                    try{Dispatcher.BeginInvoke(new Action(()=>{if(closed||!Object.ReferenceEquals(activity,current))return;ApplyActivityReport(report);}));}catch(InvalidOperationException){} // Dispatcher may already be shutting down.
                };
                current.Start();
            }
        }
        private void OpenHistory()
        {
            if(closed)return;
            if(historyWindow!=null){WindowInteraction.ResumeReveal(historyWindow);if(!historyWindow.IsVisible)historyWindow.Show();if(historyWindow.WindowState==WindowState.Minimized)historyWindow.WindowState=WindowState.Normal;historyWindow.Activate();return;}
            largeHistory=new HistoryPanel{Margin=new Thickness(20,10,20,20)};largeHistory.RangeChanged+=SetTrendRange;largeHistory.Configure(true,true,prefs.TrendDays);largeHistory.Apply(snapshot,Scope());
            var scroller=new ScrollViewer{Content=largeHistory,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            var styled=new StyledWindow{Title="每日用量与 Token 趋势",Width=880,Height=850,MinWidth=340,MinHeight=420,MaxHeight=SystemParameters.WorkArea.Height,Background=Theme.Background,Foreground=Theme.Ink,FontFamily=FontFamily,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
            styled.SetBody(scroller,"每日用量与趋势","USAGE INSIGHTS",true);historyWindow=styled;historyWindow.Loaded+=delegate{if(historyWindow!=null)ClampWindow(historyWindow);};historyWindow.Closed+=delegate{historyWindow=null;largeHistory=null;};historyWindow.Show();
        }
        private async void RefreshData()
        {
            if(preview||closed||busy)return;busy=true;int version=revision;string selected=prefs.Source,range=prefs.Range,application=prefs.App,db=prefs.Database,home=prefs.CodexHome;var priceOverrides=prefs.PriceOverrides;
            status.Text=selected=="local"?"更新本地日志…":"读取数据库…";
            try
            {
                UsageSnapshot result=await Task.Run(()=>
                {
                    ApiPrices.Configure(priceOverrides);
                    if(selected!="local")return UsageDatabase.Read(db,range,application,DateTime.Now);
                    if(local==null||localRoot!=home){local=new LocalCodexUsage(home,Path.Combine(Program.DataFolder,"local-codex-cache.json.gz"));localRoot=home;}
                    return local.Read(range,DateTime.Now,message=>Dispatcher.BeginInvoke(new Action(()=>{if(!closed&&version==revision)status.Text=message;})),()=>closed||version!=revision);
                });
                if(!closed&&version==revision){ApplySnapshot(result);status.Text="已更新 "+DateTime.Now.ToString("HH:mm:ss")+" · "+prefs.RefreshSeconds+" 秒";}
            }
            catch(OperationCanceledException){}
            catch(Exception ex)
            {
                if(!closed&&version==revision){status.Text="读取失败 · 保留上次数据";SetCoverageNotice(ex.Message,true,false);if(snapshot==null)heroNote.Text="当前来源尚未读取成功";}
            }
            finally{busy=false;if(!closed&&version!=revision)RefreshData();}
        }
        private string Format(string key,UsageSnapshot s,bool full)
        {
            long number;
            switch(key)
            {
                case "tokens":number=s.TotalTokens;break;case "requests":number=s.Requests;break;case "input":number=s.InputTokens;break;case "output":number=s.OutputTokens;break;case "cacheRead":number=s.CacheReadTokens;break;
                case "sessions":if(prefs.Source!="local")return "—";number=s.Sessions;break;
                case "reasoning":if(prefs.Source!="local")return "—";number=s.ReasoningTokens;break;
                case "cost":return s.CostAvailable?s.CostUsd.ToString(full?"0.000000":"0.0000",CultureInfo.InvariantCulture):prefs.Source=="local"?"日志未提供":"—";
                case "cacheRate":return s.CacheHitRate.ToString("0.0",CultureInfo.InvariantCulture)+"%";
                default:return "—";
            }
            return full?TokenText.Full(number):TokenText.Compact(number);
        }
        internal void ApplySnapshot(UsageSnapshot s)
        {
            snapshot=s;prefs.KnownModels=prefs.KnownModels.Concat(s.KnownModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();quota.Accept(s.Quotas);quota.AcceptUsage(s,Scope());UpdateBall();if(!preview&&(!IsVisible||WindowState==WindowState.Minimized))return;heroLabel.Text=PeriodName()+" · "+LabelFor(heroKey);heroValue.Text=Format(heroKey,s,false);heroValue.ToolTip=Format(heroKey,s,true);
            heroExact.Text=heroKey=="tokens"?"("+TokenText.Exact(s.TotalTokens)+")":"";heroExact.Visibility=heroKey=="tokens"&&!prefs.Collapsed?Visibility.Visible:Visibility.Collapsed;
            heroNote.Text="API 估算 "+ModelColors.Money(s.EquivalentUsd,s.UnpricedTokens,s.TotalTokens)+" · USD";
            heroNote.ToolTip=ApiPrices.Basis+"\n估算基准，不代表订阅实际扣费。";
            hero.ToolTip=prefs.Source=="local"?"Token = 输入（包含缓存）+ 输出；推理包含在输出中。\n最新记录："+s.LatestRecord:"Token = 未缓存输入 + 输出 + 缓存读取 + 缓存创建。\n最新记录："+s.LatestRecord;
            foreach(var item in values){bool unavailable=item.Key=="cost"&&!s.CostAvailable;item.Value.Text=Format(item.Key,s,false);item.Value.FontSize=unavailable&&prefs.Source=="local"?13:19;item.Value.ToolTip=unavailable?"日志未提供实际费用；上方 API 估算按模型单价计算。":Format(item.Key,s,true);labels[item.Key].Text=LabelFor(item.Key);}
            SetCoverageNotice(s.Warning,false,s.CoverageWarnings>0);Reflow();UpdateActivityStatus();
            if(!prefs.Collapsed){if(prefs.ShowModels)modelPanel.Apply(s);if(prefs.ShowHeatmap||prefs.ShowTrend)history.Apply(s,Scope());}if(largeHistory!=null)largeHistory.Apply(s,Scope());
        }
        private void SetCoverageNotice(string details,bool readFailed,bool incomplete)
        {
            coverageDetails=details??"";coverageReadFailed=readFailed;coverageIncomplete=incomplete;
            warning.Text=readFailed?"读取失败 · 查看处理建议":incomplete?"部分记录未计入 · 查看说明":"统计说明 · 查看详情";
            coverageToggle.Visibility=coverageDetails.Length==0?Visibility.Collapsed:Visibility.Visible;
            if(coverageExplanation!=null)coverageExplanation.Text=CoverageExplanationText();
        }
        private string CoverageExplanationText()
        {
            string impact=coverageReadFailed?"本次刷新没有完成，界面保留上次成功读取的结果，当前用量可能尚未更新。":coverageIncomplete?"部分文件或记录没有纳入统计，当前总量和模型明细可能不完整。":"统计已完成；部分记录按日志还原规则处理，具体说明如下。";
            string action=prefs.Source=="local"?"先尝试刷新；记录仍在写入时，可稍后重试。若提示持续出现，在设置的“数据与额度”中检查 Codex 数据目录是否正确且可读取。":"先尝试刷新；若提示持续出现，在设置的“数据与额度”中检查 CC Switch 数据库是否正确且可读取。";
            return "对数据的影响\n"+impact+"\n\n建议操作\n"+action+"\n保留原始记录，避免通过删除日志消除提示。\n\n具体说明\n"+(coverageDetails.Length==0?"当前没有待处理的统计提示。":coverageDetails);
        }
        private void OpenCoverage()
        {
            if(closed||coverageDetails.Length==0)return;
            if(coverageWindow!=null)
            {
                // Reopening during the exit animation cancels its pending Close callback.
                WindowInteraction.ResumeReveal(coverageWindow);
                if(coverageWindow.WindowState==WindowState.Minimized)coverageWindow.WindowState=WindowState.Normal;
                if(!coverageWindow.IsVisible)coverageWindow.Show();coverageWindow.Activate();return;
            }
            var window=new StyledWindow{Title="统计说明",Owner=this,Width=520,Height=420,MinWidth=320,MinHeight=280,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};coverageWindow=window;
            coverageExplanation=Theme.Text(CoverageExplanationText(),12,Theme.Ink);coverageExplanation.TextWrapping=TextWrapping.Wrap;coverageExplanation.LineHeight=20;AutomationProperties.SetAutomationId(coverageExplanation,"CoverageExplanation");
            var content=new ScrollViewer{Content=coverageExplanation,Margin=new Thickness(18,2,18,18),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            window.SetBody(content,"统计说明与处理建议","DATA STATUS",true);window.Loaded+=delegate{ClampWindow(window);};window.Closed+=delegate{coverageWindow=null;coverageExplanation=null;};window.Show();
        }
        private void Persist()
        {
            if(preview)return;
            // Minimized native coordinates are off-screen; persist the normal window bounds instead.
            Rect normal=WindowState==WindowState.Normal?new Rect(Left,Top,Width,Height):RestoreBounds;
            if(!normal.IsEmpty){prefs.Left=normal.Left;prefs.Top=normal.Top;prefs.Width=normal.Width;if(!prefs.Collapsed)prefs.Height=normal.Height;}
            Program.Save(prefs);
        }
        internal void SavePreview(string path)
        {
            var root=(FrameworkElement)Content;root.Measure(new Size(Width,Height));root.Arrange(new Rect(0,0,Width,Height));root.UpdateLayout();Reflow();root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Width,(int)Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))png.Save(output);
        }
        [StructLayout(LayoutKind.Sequential)] private struct RectNative{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo{public int Size;public RectNative Monitor,Work;public uint Flags;}
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out RectNative rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        private void ClampToMonitor()
        {
            if(!ready||preview)return;ClampWindow(this);
        }
        private static void ClampWindow(Window window)
        {
            IntPtr h=new WindowInteropHelper(window).Handle;var info=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};RectNative r;
            if(!GetWindowRect(h,out r)||!GetMonitorInfo(MonitorFromWindow(h,2),ref info))return;
            int width=Math.Min(r.Right-r.Left,info.Work.Right-info.Work.Left),height=Math.Min(r.Bottom-r.Top,info.Work.Bottom-info.Work.Top);
            int x=Math.Max(info.Work.Left,Math.Min(r.Left,info.Work.Right-width)),y=Math.Max(info.Work.Top,Math.Min(r.Top,info.Work.Bottom-height));
            if(x!=r.Left||y!=r.Top||width!=r.Right-r.Left||height!=r.Bottom-r.Top)SetWindowPos(h,IntPtr.Zero,x,y,width,height,0x14);
        }
    }
}
