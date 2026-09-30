using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    internal static class StabilityProbe
    {
        private static int checks;
        internal static void Check(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);checks++;Console.WriteLine("PASS "+message);}
        internal static object Call(object target,string method,params object[] args)
        {return target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args);}
        internal static T Field<T>(object target,string name)
        {return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
        internal static async Task Until(Func<bool> condition,string message,int milliseconds)
        {var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(!condition()){if(DateTime.UtcNow>=end)throw new TimeoutException(message);await Task.Delay(25);}}
        [STAThread] public static int Main(string[] args)
        {
            string root=Path.Combine(Path.GetFullPath(args[0]),"fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",root);
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int result=0;
            app.Startup+=async delegate
            {
                try{if(args.Contains("--color-only")){ModelColorStabilityProbe.Run(root);}else if(args.Contains("--model-share-only")){await ModelShareStabilityProbe.Run(root);await ChartStabilityProbe.Run(root);await ModelShareIntegration(root);await HistoryWindowStates(root);}else if(args.Contains("--milestone-only")){MilestoneStabilityProbe.Run(root);MilestoneDataStabilityProbe.Run(root);await MilestoneUiStabilityProbe.Run(root);await MilestoneCurveStabilityProbe.Run(root);await MilestoneStabilityProbe.Integration(root);}else if(args.Contains("--price-only")){PriceCatalogStabilityProbe.Run(root);}else if(args.Contains("--period-only")){await PeriodStabilityProbe.Run(root);await ChartStabilityProbe.Run(root);}else if(args.Contains("--remote-only")){await RemoteStabilityProbe.Run(root);DataStabilityProbe.Run(root);StatusStabilityProbe.Run(root);}else if(args.Contains("--html-only")){await HtmlStabilityProbe.Run(root);}else if(args.Contains("--floating-only")){await FloatingEffectsStabilityProbe.Run(root);await IslandStabilityProbe.Run(root);await HtmlStabilityProbe.Run(root);}else if(args.Contains("--quota-only")){await QuotaHistoryStabilityProbe.Run(root);}else if(args.Contains("--island-only")){await IslandStabilityProbe.Run(root);await WindowMotionStabilityProbe.Run(root);}else await Run(root);Console.WriteLine("SOURCE CHECKS: "+checks);}
                catch(Exception ex){result=1;Console.Error.WriteLine(ex);}
                finally{foreach(Window window in app.Windows.Cast<Window>().ToArray())window.Close();app.Shutdown();}
            };
            app.Run();return result;
        }
        private static Button FindButton(DependencyObject root,string caption)
        {
            var button=root as Button;if(button!=null&&Object.Equals(button.Content,caption))return button;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=FindButton(VisualTreeHelper.GetChild(root,i),caption);if(found!=null)return found;}return null;
        }
        private static async Task HistoryWindowStates(string root)
        {
            Theme.Apply(new Preferences{ThemeMode="light",OrbAnimation="auto"});
            var bounds=WindowInteraction.MaximizedBounds(new Rect(-1920,-200,1920,1080),new Rect(-1872,-160,1872,1040));Check(bounds==new Rect(48,40,1872,1040),"maximize uses monitor-relative physical work bounds including left/top taskbars and negative screen coordinates");
            int shareReads=0,stageReads=0,curveReads=0;var data=new UsageSnapshot{Daily=DailyUsage.Empty(DateTime.Today,30)};
            var milestone=new MilestoneEngine(Path.Combine(root,"resize-fixture")).Get(new MilestoneInput(),"resize","Fixture",LocalCodexUsage.Unix(DateTime.Now),System.Threading.CancellationToken.None);
            var share=new ModelSharePanel((a,b,c)=>{shareReads++;return Task.FromResult(data);},()=>"resize");share.Apply(data,"resize","Fixture");
            var stages=new MilestonePanel(c=>{stageReads++;return Task.FromResult(milestone);},()=>"resize",MilestoneEngine.Base,v=>{});
            var curve=new MilestoneCurvePanel(c=>{curveReads++;return Task.FromResult(milestone);},()=>"resize",MilestoneEngine.Base,v=>{});
            var grid=new Grid();grid.Children.Add(share);grid.Children.Add(stages);grid.Children.Add(curve);var window=new StyledWindow{UseNativeFrame=true,Width=880,Height=680,ShowActivated=false,ShowInTaskbar=false};window.SetBody(grid,"Fixture","RESIZE",true);
            try
            {
                window.WindowState=WindowState.Minimized;window.Show();share.SetRange(DateTime.Today.AddDays(-7),DateTime.Now);await Task.Delay(80);Check(shareReads==0&&stageReads==0&&curveReads==0,"pages loaded in a minimized owner defer data reads");window.WindowState=WindowState.Normal;await Task.Delay(180);window.UpdateLayout();Check(shareReads>0&&stageReads>0&&curveReads>0,"pages loaded while minimized start reading on first restore");int a=shareReads,b=stageReads,c=curveReads;
                var restoreBounds=new Rect(window.Left,window.Top,window.ActualWidth,window.ActualHeight);WindowInteraction.ToggleMaximize(window);Check(window.WindowState==WindowState.Maximized,"maximize applies native bounds immediately without waiting for a fade");await Task.Delay(200);
                var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;var work=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;NativeWindowRect actual;Check(GetVisibleWindowRect(handle,out actual)&&Math.Abs(actual.Left-work.Left)<=1&&Math.Abs(actual.Top-work.Top)<=1&&Math.Abs(actual.Right-work.Right)<=1&&Math.Abs(actual.Bottom-work.Bottom)<=1,"maximized visible frame fills the work area without covering the taskbar");
                var shell=(Border)window.Content;Check(!window.AllowsTransparency&&System.Windows.Shell.WindowChrome.GetWindowChrome(window)!=null&&shell.CornerRadius==new CornerRadius(0),"history uses a native frame and square corners when maximized");
                var topLeft=shell.PointToScreen(new Point(0,0));var bottomRight=shell.PointToScreen(new Point(shell.ActualWidth,shell.ActualHeight));Check(Math.Abs(topLeft.X-work.Left)<=1&&Math.Abs(topLeft.Y-work.Top)<=1&&Math.Abs(bottomRight.X-work.Right)<=1&&Math.Abs(bottomRight.Y-work.Bottom)<=1,"maximized content fits the visible frame without clipping chart edges");
                ModelShareStabilityProbe.Capture(window,Path.Combine(root,"history-maximized.png"));
                WindowInteraction.ToggleMaximize(window);Check(window.WindowState==WindowState.Normal,"restore applies native bounds immediately");await Task.Delay(200);
                Check(Math.Abs(window.Left-restoreBounds.Left)<1&&Math.Abs(window.Top-restoreBounds.Top)<1&&Math.Abs(window.ActualWidth-restoreBounds.Width)<1&&Math.Abs(window.ActualHeight-restoreBounds.Height)<1,"restore returns to the previous normal position and size");
                Check(shell.CornerRadius==new CornerRadius(14),"restored history recovers its rounded corners");
                ModelShareStabilityProbe.Capture(window,Path.Combine(root,"history-restored.png"));
                WindowInteraction.ToggleMaximize(window);bool stable=true;for(int frame=0;frame<12;frame++){stable&=shell.RenderTransform.Value.IsIdentity&&shell.Opacity==1;await Task.Delay(16);}Check(stable,"maximize never runs a second content shift, scale, or fade");WindowInteraction.ToggleMaximize(window);await Task.Delay(180);
                Check(shareReads==a&&stageReads==b&&curveReads==c,"maximize and restore do not reread model or milestone data");
                WindowInteraction.ToggleMaximize(window);WindowInteraction.ToggleMaximize(window);await Task.Delay(220);Check(window.WindowState==WindowState.Normal&&((FrameworkElement)window.Content).Opacity==1,"rapid maximize reversal ends in the latest state without fading the chart tree");
                window.WindowState=WindowState.Minimized;await Task.Delay(60);window.WindowState=WindowState.Normal;await Task.Delay(180);Check(shareReads>a&&stageReads>b&&curveReads>c,"restoring from minimized still resumes data refresh");
                var modes=Field<System.Collections.Generic.Dictionary<string,Button>>(share,"modes");Check(((FrameworkElement)modes["daily"].Parent).HorizontalAlignment==HorizontalAlignment.Right,"model share aggregation controls align right");
            }
            finally{share.Dispose();stages.Dispose();curve.Dispose();window.Close();}
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeWindowRect{public int Left,Top,Right,Bottom;}
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeWindowRect bounds);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region,out NativeWindowRect bounds);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        private static bool GetVisibleWindowRect(IntPtr window,out NativeWindowRect bounds)
        {
            if(!GetWindowRect(window,out bounds))return false;
            // Native maximized frames include an invisible resize border outside
            // the work area. WindowChrome clips that border with a rectangular HRGN.
            var region=CreateRectRgn(0,0,0,0);try{NativeWindowRect clip;if(GetWindowRgn(window,region)!=2||GetRgnBox(region,out clip)!=2)return false;int x=bounds.Left,y=bounds.Top;bounds=new NativeWindowRect{Left=x+clip.Left,Top=y+clip.Top,Right=x+clip.Right,Bottom=y+clip.Bottom};return true;}finally{DeleteObject(region);}
        }
        private static async Task ModelShareIntegration(string root)
        {
            string home=Path.Combine(root,"share-widget"),logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);DateTime at=DateTime.Now.AddMinutes(-10);string id=Guid.NewGuid().ToString();
            var json=new System.Web.Script.Serialization.JavaScriptSerializer();File.WriteAllText(Path.Combine(logs,"rollout-"+id+".jsonl"),json.Serialize(new{type="session_meta",timestamp=at.ToString("o"),payload=new{id=id,timestamp=at.ToString("o")}})+"\n"+json.Serialize(new{type="turn_context",timestamp=at.ToString("o"),payload=new{model="fixture-model"}})+"\n"+json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type="token_count",info=new{last_token_usage=new{input_tokens=80,output_tokens=20}}}})+"\n");
            var prefs=new Preferences{CodexHome=home,Source="local",Database=Path.Combine(root,"unused.db"),QuotaCli="",LiveQuota=false,OrbAnimation="off"};var widget=new WidgetWindow(prefs,true);
            try
            {
                var data=await (Task<UsageSnapshot>)Call(widget,"ReadChartRange",at.AddMinutes(-1),DateTime.Now,System.Threading.CancellationToken.None,null);Check(data.TotalTokens==100&&data.Timeline.Sum(d=>d.Tokens)==100,"model share and existing custom charts use the same local numeric range query");
                widget.ApplySnapshot(data);widget.Show();Call(widget,"OpenHistory");var window=Field<Window>(widget,"historyWindow");window.UpdateLayout();var page=Field<ModelSharePanel>(widget,"modelSharePanel");
                Check(FindButton(window,"模型占比")!=null&&!page.IsVisible,"model share is a hidden independent history tab, not a home chart");FindButton(window,"模型占比").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();await Task.Delay(30);
                Check(page.IsVisible&&!Field<HistoryPanel>(widget,"largeHistory").IsVisible,"model share tab replaces only the visible history subpage");
                var original=Field<HistoryPanel>(widget,"largeHistory");string scope=(string)Call(widget,"Scope");DateTime end=DateTime.Now;
                original.BeginCustomRange(at.AddMinutes(-1),end,scope);Call(widget,"RequestCustomRange",original,at.AddMinutes(-1),end,false);await Until(()=>!Field<bool>(original,"customLoading"),"shared range loader broke the original usage chart",5000);
                Check(Field<UsageSnapshot>(original,"customSnapshot").TotalTokens==100,"existing usage custom range still completes through the shared provider");
                using(var cancellation=new System.Threading.CancellationTokenSource()){cancellation.Cancel();bool stopped=false;try{await (Task<UsageSnapshot>)Call(widget,"ReadChartRange",at,DateTime.Now,cancellation.Token,null);}catch(OperationCanceledException){stopped=true;}Check(stopped,"canceled chart requests cannot publish a numeric result");}
                prefs.Remote.Enabled=true;prefs.UsageView="remote";var remoteOnly=await (Task<UsageSnapshot>)Call(widget,"ReadChartRange",at.AddMinutes(-1),DateTime.Now,System.Threading.CancellationToken.None,null);
                Check(remoteOnly.TotalTokens==0&&remoteOnly.DataUnavailable&&!String.IsNullOrEmpty(remoteOnly.Warning),"an unready server never displays local usage under a remote label");
                using(var monitor=new RemoteMonitor(()=>{throw new InvalidOperationException("A fixture must never connect");},Path.Combine(root,"remote-chart"),true))
                {
                    var remoteField=typeof(WidgetWindow).GetField("remote",BindingFlags.Instance|BindingFlags.NonPublic);remoteField.SetValue(widget,monitor);
                    try{remoteOnly=await (Task<UsageSnapshot>)Call(widget,"ReadChartRange",at.AddMinutes(-1),end,System.Threading.CancellationToken.None,null);Check(remoteOnly.DataUnavailable&&remoteOnly.TotalTokens==0,"an existing monitor with an empty unpublished ledger remains unavailable");prefs.UsageView="combined";var combined=await (Task<UsageSnapshot>)Call(widget,"ReadChartRange",at.AddMinutes(-1),end,System.Threading.CancellationToken.None,null);Check(!combined.DataUnavailable&&combined.TotalTokens==100&&!String.IsNullOrEmpty(combined.Warning),"combined custom view retains local usage while disclosing the unready server");}
                    finally{remoteField.SetValue(widget,null);}
                }
                prefs.Remote.Enabled=false;prefs.UsageView="combined";
                var previewField=typeof(WidgetWindow).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic);
                previewField.SetValue(widget,false);try{Call(widget,"RefreshUsage",true);await Until(()=>!Field<bool>(widget,"busy"),"initial fixture refresh did not finish",5000);}finally{previewField.SetValue(widget,true);}
                // Point only the fixture reader at an absent directory to exercise
                // real discovery failure without moving a watched/open directory.
                var reader=Field<LocalCodexUsage>(widget,"local");var rootField=typeof(LocalCodexUsage).GetField("root",BindingFlags.Instance|BindingFlags.NonPublic);rootField.SetValue(reader,Path.Combine(home,"unavailable"));
                previewField.SetValue(widget,false);try{Call(widget,"RefreshUsage",true);await Until(()=>!Field<bool>(widget,"busy"),"fixture refresh did not finish",5000);}finally{previewField.SetValue(widget,true);}
                Check(Field<TextBlock>(page,"status").Text.Contains("上次结果")&&Field<UsageChart>(page,"chart").Days.Any(d=>d.Tokens>0),"real source refresh failure reaches the model page without discarding its curve");
                Check((bool)Call(widget,"ApplyCachedRange")&&Field<TextBlock>(page,"status").Text.Contains("上次结果"),"replaying a cached range keeps source failure visible");
                window.Close();Call(widget,"OpenHistory");var reopened=Field<ModelSharePanel>(widget,"modelSharePanel");Check(!Object.ReferenceEquals(page,reopened),"closing and reopening history creates a new model share page");FindButton(Field<Window>(widget,"historyWindow"),"模型占比").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Field<TextBlock>(reopened,"status").Text.Contains("上次结果"),"reopening history retains the current source failure notice");rootField.SetValue(reader,home);
                previewField.SetValue(widget,false);try{Call(widget,"RefreshUsage",true);await Until(()=>!Field<bool>(widget,"busy"),"fixture recovery did not finish",5000);}finally{previewField.SetValue(widget,true);}
                Check(Field<DockPanel>(reopened,"state").Visibility==Visibility.Collapsed,"actual source recovery through the widget clears the failure notice");
            }
            finally{widget.Close();}
        }
        private static async Task Run(string root)
        {
            // Never use the default user profile or CLI in a GUI regression host.
            var home=Path.Combine(root,"codex");Directory.CreateDirectory(Path.Combine(home,"sessions"));
            var prefs=new Preferences{CodexHome=home,Database=Path.Combine(root,"missing.db"),Source="local",LiveQuota=false,ShowQuota=false,QuotaCli="",BallMode=false,BallStyle="capsule",BallDock="",OrbAnimation="smooth",Left=180,Top=160,BallLeft=400,BallTop=250,Collapsed=true};
            // Run color recovery before any WPF view can initialize the process-wide registry.
            PriceCatalogStabilityProbe.Run(root);
            Theme.Apply(prefs);
            var work=new Rect(0,0,1920,1080);var near=new Rect(40,400,80,16);
            Check(FloatingBall.DockEdge(near,work,1)==-1&&FloatingBall.DockEdge(near,work,1,0)==0,"dock hysteresis retains the edge without attracting an undocked window");
            var fitted=WindowInteraction.FitBounds(new Rect(1800,900,1600,1200),new Rect(1000,0,900,700),8);
            Check(fitted.Width<=884&&fitted.Height<=684&&fitted.Right<=1892&&fitted.Bottom<=692,"oversized windows fit a smaller target work area");
            var widget=new WidgetWindow(prefs,false);widget.Show();
            await Task.Delay(350);
            Call(widget,"OpenBall");await Until(()=>!widget.IsVisible,"main did not hide",2500);
            var ball=Field<FloatingBall>(widget,"ball");await Until(()=>ball.IsVisible,"ball did not show",2500);
            Call(widget,"RestoreWindow");Call(widget,"RestoreWindow");
            await Until(()=>widget.IsVisible&&!ball.IsVisible,"duplicate restore did not settle",3000);
            Check(!prefs.Collapsed&&widget.Height>=480,"repeated ball restore retains the full main layout");
            Call(widget,"OpenBall");await Task.Delay(35);Call(widget,"RestoreWindow");
            await Task.Delay(1100);
            Check(widget.IsVisible&&!ball.IsVisible,"reversing an in-flight main-to-ball switch leaves only the main window");
            Call(widget,"OpenBall");await Until(()=>ball.IsVisible&&!widget.IsVisible,"ball switch did not settle",3000);
            ball.SetExpanded(true);ball.SetOrb();ball.SetExpanded(false);await Task.Delay(1000);
            Check(!ball.IsPillar&&!ball.IsOrb&&!ball.Expanded,"latest rapid shape request wins");
            prefs.BallOpacity=.55;ball.Apply(null,null,"fixture","");
            ball.SetExpanded(true);await Task.Delay(25);ball.Apply(null,null,"fixture","");await Task.Delay(800);
            Check(Math.Abs(ball.Opacity-.55)<.01,"data refresh preserves configured ball opacity after a transition");
            Call(widget,"RestoreWindow");await Task.Delay(1000);
            Call(widget,"SetCoverageNotice","Synthetic coverage explanation",false,true);Call(widget,"OpenCoverage");
            var explanation=Field<Window>(widget,"coverageWindow");await Task.Delay(300);
            WindowInteraction.Close(explanation);Call(widget,"OpenCoverage");await Task.Delay(450);
            var reopened=Field<Window>(widget,"coverageWindow");
            Check(reopened!=null&&reopened.IsVisible&&(!Theme.MotionAllowed||Object.ReferenceEquals(explanation,reopened)),"reopening coverage during dismissal cancels its close and keeps the requested explanation visible");
            if(reopened!=null)reopened.Close();widget.Close();
            Call(widget,"RestoreWindow");Call(widget,"OpenHistory");Call(widget,"OpenSettings");
            Check(!widget.IsVisible,"late window commands after close do not reopen UI");
            var dataProbe=typeof(StabilityProbe).Assembly.GetType("CodexUserData.DataStabilityProbe");
            if(dataProbe!=null)dataProbe.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,new object[]{root});
            VerifyDialogCompletion();
            var performanceProbe=typeof(StabilityProbe).Assembly.GetType("CodexUserData.PerformanceStabilityProbe");
            if(performanceProbe!=null)
            {
                var pending=performanceProbe.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,new object[]{root}) as Task;
                if(pending!=null)await pending;
            }
            await HtmlStabilityProbe.Run(root);
            foreach(string name in new[]{"PlacementStabilityProbe","StatusStabilityProbe","PeriodStabilityProbe","ChartStabilityProbe","QuotaHistoryStabilityProbe","DiagnosticStabilityProbe","UpdateStabilityProbe","IslandStabilityProbe","WindowMotionStabilityProbe"})
            {
                var probe=typeof(StabilityProbe).Assembly.GetType("CodexUserData."+name);
                if(probe==null)throw new InvalidOperationException("Required verification component is missing: "+name);
                var pending=probe.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,new object[]{root}) as Task;if(pending!=null)await pending;
            }
            MilestoneStabilityProbe.Run(root);MilestoneDataStabilityProbe.Run(root);await MilestoneUiStabilityProbe.Run(root);await MilestoneCurveStabilityProbe.Run(root);await MilestoneStabilityProbe.Integration(root);
            await GuideStabilityProbe.Run(root);
        }
        private static void VerifyDialogCompletion()
        {
            var dialog=new Window{Width=280,Height=160,ShowInTaskbar=false,ShowActivated=false,Content=new Border{Background=Theme.Surface}};
            WindowInteraction.EnableMotion(dialog);int commits=0;bool timedOut=false;
            var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
            timeout.Tick+=delegate{timedOut=true;timeout.Stop();dialog.Close();};
            dialog.Loaded+=delegate
            {
                WindowInteraction.CompleteDialog(dialog,true,delegate{commits++;return true;});
                WindowInteraction.CompleteDialog(dialog,false);WindowInteraction.Close(dialog);
            };
            timeout.Start();bool? accepted=dialog.ShowDialog();timeout.Stop();
            Check(!timedOut&&accepted==true&&commits==1,"accepted dialog commits once despite immediate cancel and close requests");
            var retry=new Window{Width=280,Height=160,ShowInTaskbar=false,ShowActivated=false,Content=new Border{Background=Theme.Surface}};
            WindowInteraction.EnableMotion(retry);bool rejected=false,recovered=false;int saved=0;
            var deadline=DateTime.UtcNow.AddSeconds(3);var retryTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            retryTimer.Tick+=delegate
            {
                if(rejected&&retry.IsEnabled){recovered=true;retryTimer.Stop();WindowInteraction.CompleteDialog(retry,true,()=>{saved++;return true;});}
                else if(DateTime.UtcNow>deadline){retryTimer.Stop();retry.Close();}
            };
            retry.Loaded+=delegate{WindowInteraction.CompleteDialog(retry,true,()=>{rejected=true;return false;});};
            retryTimer.Start();accepted=retry.ShowDialog();retryTimer.Stop();
            Check(recovered&&accepted==true&&saved==1,"failed dialog commit restores interaction and permits a later save");
            if(Theme.MotionAllowed)
            {
                var interrupted=new Window{Width=280,Height=160,ShowInTaskbar=false,ShowActivated=false,Content=new Border{Background=Theme.Surface}};
                WindowInteraction.EnableMotion(interrupted);int applied=0;
                interrupted.Loaded+=delegate{WindowInteraction.CompleteDialog(interrupted,true,()=>{applied++;return true;});interrupted.Close();};
                interrupted.ShowDialog();Check(applied==0,"native dialog destruction during its exit does not commit external settings");
            }
        }
    }
}
