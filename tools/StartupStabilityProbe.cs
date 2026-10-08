using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexUserData
{
    // This host never calls Program.Main or reads the user's settings/logs.
    internal static class StartupStabilityProbe
    {
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
        private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        [STAThread] public static int Main(string[] args)
        {
            try
            {
                string fixture=Path.GetFullPath(args[0]);Directory.CreateDirectory(fixture);
                AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",fixture);
                Settings();
                var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                if(args.Length>1&&args[1]=="--handoff-only")
                {Handoff(app);Console.WriteLine("STARTUP_HANDOFF=main-window lifetime and opacity passed");return 0;}
                Preload();
                if(args.Length>1&&args[1]=="--preload-only"){app.Shutdown();return 0;}
                int disabled=0;var preferences=new Preferences{StartupAnimationEnabled=false};
                StartupSplash.Launch(preferences,()=>disabled++);Require(disabled==1,"Disabled splash did not hand off");
                foreach(string style in new[]{"cinema","layers","matrix","prism"})RunFilm(style,false);
                RunFilm("cinema",true);
                // Closing during environment creation must not produce a second
                // callback or access a disposed control when its await resumes.
                int skipped=0;var loading=new StartupSplash("cinema",()=>skipped++);
                HideFixture(loading);loading.Show();loading.Finish("test-skip");loading.Finish("late-timeout");
                Pump(()=>false,1);Require(skipped==1,"Early skip was not idempotent");
                Require(app.Windows.Count==0,"Startup windows were retained");
                MediaFailure();app.Shutdown();
                Console.WriteLine("STARTUP_CHECKS=preferences, embedded resources, 4 WebView films, IPC skip, early skip, media failure, disposal passed");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
        private static void Settings()
        {
            var defaults=Program.Json.Deserialize<Preferences>("{}");
            Require(defaults.StartupAnimationEnabled&&defaults.StartupAnimationStyle=="cinema"&&defaults.StartupSoundEnabled,"Older settings did not get startup defaults");
            Require(StartupSplash.NormalizeStyle(null)=="cinema"&&StartupSplash.NormalizeStyle("../../secret")=="cinema","Style fallback failed");
            foreach(string style in new[]{"cinema","layers","matrix","prism"})
            {
                var preferences=new Preferences{StartupAnimationStyle=style,StartupSoundEnabled=style=="layers",LiveQuota=false,StartWithWindows=false,StartWithCodex=false};
                preferences.CodexHome=Path.Combine(Program.DataFolder,"codex");preferences.Database=Path.Combine(Program.DataFolder,"empty.db");
                Program.Save(preferences);var reloaded=Program.ReadPreferences();
                Require(reloaded.StartupAnimationStyle==style&&reloaded.Clone().StartupAnimationStyle==style,"Selection not persisted: "+style);
                Require(reloaded.StartupSoundEnabled==(style=="layers")&&reloaded.Clone().StartupSoundEnabled==reloaded.StartupSoundEnabled,"Sound setting not persisted");
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup."+style+".webm"))
                {Require(stream!=null&&stream.Length>100000,"Missing film: "+style);Require(stream.ReadByte()==0x1a&&stream.ReadByte()==0x45,"Not a WebM file: "+style);}
            }
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup.index.html"))Require(stream!=null,"Player missing");
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup.chime.wav"))
            {Require(stream!=null&&stream.Length>100000,"Chime missing");Require(stream.ReadByte()=='R'&&stream.ReadByte()=='I',"Invalid PCM resource");}
            // The probe is silent: disposal must cancel even a future request.
            var chime=new StartupChime();chime.Dispose();chime.PlayOnce();chime.Stop();chime.Dispose();
        }
        private static object Field(object value,string name)
        {return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
        private static void Preload()
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string home=Path.Combine(Program.DataFolder,"preload-logs-"+Guid.NewGuid().ToString("N")),sessions=Path.Combine(home,"sessions");Directory.CreateDirectory(sessions);
            const string id="11111111-1111-4111-8111-111111111111";
            string log=Path.Combine(sessions,"rollout-"+id+".jsonl"),at=DateTime.UtcNow.ToString("o");
            string header=Program.Json.Serialize(new{type="session_meta",timestamp=at,payload=new{id=id,timestamp=at}})+"\n";
            Func<int,string> record=tokens=>Program.Json.Serialize(new{type="event_msg",timestamp=at,payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=tokens,output_tokens=0,total_tokens=tokens}}}})+"\n";
            File.WriteAllText(log,header+record(100));
            var preload=new StartupUsagePreload(home,Path.Combine(Program.DataFolder,"preload-cache.gz"));
            Require(preload.Completion.Wait(10000)&&preload.Failure==null,"Preload failed");
            var prepared=(LocalCodexUsage)Field(preload,"reader");Require(prepared!=null&&prepared.LastBytesRead>0,"Preload did not parse fixture");
            // A delayed completion models early skip on a large log. The window
            // must keep pumping UI messages until the same reader is handed over.
            var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);typeof(StartupUsagePreload).GetProperty("Completion",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(preload,pending.Task,null);
            var p=new Preferences{CodexHome=home,Source="local",Range="today",LiveQuota=false,StartWithWindows=false,StartWithCodex=false,BallMode=false};
            var widget=new WidgetWindow(p,false,preload);
            // Do not show the widget or start network services in a fixture.
            ((OfficialPriceSync)Field(widget,"priceSync")).Dispose();
            var refresh=typeof(WidgetWindow).GetMethod("RefreshData",BindingFlags.Instance|BindingFlags.NonPublic);
            refresh.Invoke(widget,null);Pump(()=>false,.1);Require((bool)Field(widget,"startupRefreshWaiting")&&!(bool)Field(widget,"busy"),"Preload blocked the refresh slot");
            var chartRead=typeof(WidgetWindow).GetMethod("ReadChartRange",BindingFlags.Instance|BindingFlags.NonPublic);
            var chart=(Task<UsageSnapshot>)chartRead.Invoke(widget,new object[]{DateTime.Today,DateTime.Today.AddDays(1),System.Threading.CancellationToken.None,null});
            Pump(()=>false,.1);Require(!chart.IsCompleted,"Chart bypassed pending preload");
            p.Source="ccswitch";p.Database=Path.Combine(Program.DataFolder,"not-a-database.db");
            refresh.Invoke(widget,null);Pump(()=>!(bool)Field(widget,"busy"),3);
            Require(!(bool)Field(widget,"busy")&&(bool)Field(widget,"startupRefreshWaiting"),"Main database refresh waited on unrelated logs");
            var database=(Task<UsageSnapshot>)chartRead.Invoke(widget,new object[]{DateTime.Today,DateTime.Today.AddDays(1),System.Threading.CancellationToken.None,null});
            Pump(()=>database.IsCompleted,3);Require(database.IsCompleted,"Database needlessly waited for local logs");
            if(database.IsFaulted){var observed=database.Exception;}
            p.Source="local";
            File.AppendAllText(log,record(200));pending.SetResult(true);
            // A timer can arrive after completion but before the UI continuation.
            // It must coalesce rather than queue an immediate duplicate scan.
            refresh.Invoke(widget,null);
            Pump(()=>!(bool)Field(widget,"startupRefreshWaiting")&&!(bool)Field(widget,"busy")&&chart.IsCompleted,5);
            Require(chart.Status==TaskStatus.RanToCompletion&&chart.Result.TotalTokens==100,"Concurrent chart did not reuse preload");
            Require(Object.ReferenceEquals(Field(widget,"local"),prepared),"Widget created a duplicate reader");
            Require(((UsageSnapshot)Field(widget,"snapshot")).TotalTokens==100,"First refresh expected 100, got "+((UsageSnapshot)Field(widget,"snapshot")).TotalTokens+"; ledger "+prepared.Snapshot("all",DateTime.Now).TotalTokens);
            refresh.Invoke(widget,null);Pump(()=>!(bool)Field(widget,"busy"),5);
            Require(((UsageSnapshot)Field(widget,"snapshot")).TotalTokens==200,"Next refresh missed appended usage");
            widget.Close();
            var changed=new StartupUsagePreload(home,Path.Combine(Program.DataFolder,"changed-cache.gz"));changed.Completion.Wait();Require(changed.Take(home+"-other")==null&&changed.Take(home)==null,"Changed source reused obsolete data");
            var failed=new StartupUsagePreload(Path.Combine(home,"missing"),Path.Combine(Program.DataFolder,"failed-cache.gz"));failed.Completion.Wait();Require(failed.Failure!=null&&failed.Take(home)==null,"Failed preload was published");
            var cancelled=new StartupUsagePreload(home,Path.Combine(Program.DataFolder,"cancel-cache.gz"));cancelled.Cancel();cancelled.Completion.Wait();Require(cancelled.Take(home)==null,"Cancelled preload was published");
            Console.WriteLine("PRELOAD=async early handoff, same reader, no duplicate first scan, append refresh, source change, failure and cancellation passed");
        }
        private static void HideFixture(Window window)
        {
            window.Title="CodexUserData startup fixture";window.WindowStartupLocation=WindowStartupLocation.Manual;
            window.Left=-20000;window.Top=-20000;window.Width=640;window.Height=360;window.ShowActivated=false;
        }
        private static void RunFilm(string style,bool skip)
        {
            int finished=0;var splash=new StartupSplash(style,()=>finished++);HideFixture(splash);splash.Show();
            Require(splash.AllowsTransparency&&((SolidColorBrush)splash.Background).Color.A==0,"Opaque window corners");
            splash.UpdateLayout();var body=(Grid)splash.Content;var clip=body.Clip as RectangleGeometry;
            Require(clip!=null&&clip.RadiusX==24&&!clip.FillContains(new Point(1,1))&&clip.FillContains(new Point(320,180)),"Rounded browser clip missing");
            // An actual WPF render catches square black backing outside the clip.
            var pixels=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);pixels.Render(body);var corner=new byte[4];pixels.CopyPixels(new Int32Rect(0,0,1,1),corner,4,0);Require(corner[3]==0,"Splash corner pixels are opaque");
            if(skip)Require(PostMessage(new WindowInteropHelper(splash).Handle,Program.ShowMainMessage,IntPtr.Zero,IntPtr.Zero),"IPC send failed");
            Pump(()=>finished>0,25);
            Require(finished==1,"Startup handoff count: "+style);
            Require(splash.CompletionReason==(skip?"second-launch":"ended"),style+" completion: "+splash.CompletionReason);
            if(!skip)Require(splash.PlaybackStarted&&splash.BrandStarted,"Film or brand cue never played: "+style);
            else Require(!splash.BrandStarted,"Early skip fired brand cue");
            splash.Finish("late-callback");Require(finished==1,"Repeated completion: "+style);
            Console.WriteLine(style+": "+splash.CompletionReason);
        }
        private static void MediaFailure()
        {
            int finished=0;var splash=new StartupSplash("cinema",()=>finished++);HideFixture(splash);splash.Show();
            Pump(()=>splash.PlaybackStarted||finished>0,8);Require(splash.PlaybackStarted,"Failure fixture did not start");
            var web=(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl)typeof(StartupSplash).GetField("web",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(splash);
            web.CoreWebView2.ExecuteScriptAsync("document.getElementById('startup-video').src='missing.webm';document.getElementById('startup-video').load();");
            Pump(()=>finished>0,4);Require(finished==1&&splash.CompletionReason=="error","Bad media did not hand off");
        }
        private static void Handoff(Application app)
        {
            Window main=null;StartupSplash splash=null;double handoffOpacity=0;
            splash=new StartupSplash("cinema",delegate
            {
                handoffOpacity=splash.Opacity;
                main=new Window{Width=160,Height=90};HideFixture(main);
                app.MainWindow=main;app.ShutdownMode=ShutdownMode.OnMainWindowClose;main.Show();
            });
            HideFixture(splash);splash.Show();Pump(()=>splash.PlaybackStarted,8);
            Require(splash.PlaybackStarted,"Handoff fixture did not start: "+splash.CompletionReason);Pump(()=>false,.25);
            splash.Finish("test-handoff");
            Pump(()=>main!=null,2);
            Require(handoffOpacity>.95,"Splash went transparent before handoff");
            Require(main!=null&&main.IsVisible&&app.MainWindow==main&&app.Windows.Count==1,"Main-window lifetime not transferred");
            bool exited=false;var frame=new DispatcherFrame();
            app.Exit+=delegate{exited=true;frame.Continue=false;};
            app.Dispatcher.BeginInvoke(new Action(()=>main.Close()));Dispatcher.PushFrame(frame);
            Require(exited,"Closing the real main window did not exit");
        }
        private static void Pump(Func<bool> complete,double seconds)
        {
            var frame=new DispatcherFrame();var until=DateTime.UtcNow.AddSeconds(seconds);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            timer.Tick+=delegate{if(complete()||DateTime.UtcNow>=until)frame.Continue=false;};
            timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        }
    }
}
