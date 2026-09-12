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
                try{if(args.Contains("--quota-only")){await QuotaHistoryStabilityProbe.Run(root);}else if(args.Contains("--island-only")){await IslandStabilityProbe.Run(root);await WindowMotionStabilityProbe.Run(root);}else await Run(root);Console.WriteLine("SOURCE CHECKS: "+checks);}
                catch(Exception ex){result=1;Console.Error.WriteLine(ex);}
                finally{foreach(Window window in app.Windows.Cast<Window>().ToArray())window.Close();app.Shutdown();}
            };
            app.Run();return result;
        }
        private static async Task Run(string root)
        {
            // Never use the default user profile or CLI in a GUI regression host.
            var home=Path.Combine(root,"codex");Directory.CreateDirectory(Path.Combine(home,"sessions"));
            var prefs=new Preferences{CodexHome=home,Database=Path.Combine(root,"missing.db"),Source="local",LiveQuota=false,ShowQuota=false,QuotaCli="",BallMode=false,BallStyle="capsule",BallDock="",OrbAnimation="smooth",Left=180,Top=160,BallLeft=400,BallTop=250,Collapsed=true};
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
            foreach(string name in new[]{"PlacementStabilityProbe","StatusStabilityProbe","ChartStabilityProbe","QuotaHistoryStabilityProbe","DiagnosticStabilityProbe","UpdateStabilityProbe","IslandStabilityProbe","WindowMotionStabilityProbe"})
            {
                var probe=typeof(StabilityProbe).Assembly.GetType("CodexUserData."+name);
                if(probe==null)throw new InvalidOperationException("Required verification component is missing: "+name);
                var pending=probe.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,new object[]{root}) as Task;if(pending!=null)await pending;
            }
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
