using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexUserData
{
    // Playback uses one prerecorded, local film, not the preview's particle
    // renderer. The browser/decoder is released immediately after handoff.
    internal sealed class StartupSplash : Window
    {
        private const string Origin="https://codexuserdata-startup.local";
        private readonly string style;
        private readonly Action continueStartup;
        private readonly DispatcherTimer watchdog;
        private readonly List<Stream> resourceStreams=new List<Stream>();
        private WebView2CompositionControl web;
        private readonly StartupChime chime;
        private bool finished,ready,started,closed;
        internal bool BrandStarted {get;private set;}
        internal string CompletionReason {get;private set;}
        internal bool PlaybackStarted {get{return started;}}

        internal static string NormalizeStyle(string value)
        {return value=="layers"||value=="matrix"||value=="prism"?value:"cinema";}

        internal static bool ShouldPlay(Preferences preferences)
        {return preferences.StartupAnimationEnabled&&SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast;}

        internal static void Launch(Preferences preferences,Action handoff)
        {
            if(!ShouldPlay(preferences)){handoff();return;}
            StartupSplash splash=null;
            try{splash=new StartupSplash(preferences.StartupAnimationStyle,handoff,preferences.StartupSoundEnabled);}
            catch(Exception){handoff();return;}
            try{splash.Show();}
            catch(Exception){if(splash.finished)throw;splash.Finish("window-failed");}
        }

        internal StartupSplash(string selectedStyle,Action handoff):this(selectedStyle,handoff,false){}
        internal StartupSplash(string selectedStyle,Action handoff,bool soundEnabled)
        {
            style=NormalizeStyle(selectedStyle);continueStartup=handoff;
            if(soundEnabled)chime=new StartupChime();
            // Keep the normal IPC title until the widget takes over, so a
            // second manual launch can skip this window instead of getting lost.
            Title=Program.WindowTitle;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
            ShowInTaskbar=false;ShowActivated=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            AllowsTransparency=true;Background=Brushes.Transparent;Opacity=0;
            double width=Math.Min(920,Math.Min(SystemParameters.WorkArea.Width*.85,SystemParameters.WorkArea.Height*.80*16/9));
            Width=Math.Max(160,width);Height=Width*9/16;
            var body=new Grid{Background=Brushes.Black};Content=body;
            // Border.CornerRadius alone does not clip a composed browser. Clip
            // the complete visual, with a transparent window behind its corners.
            body.SizeChanged+=delegate{body.Clip=new RectangleGeometry(new Rect(0,0,body.ActualWidth,body.ActualHeight),24,24);};
            web=new WebView2CompositionControl{DefaultBackgroundColor=System.Drawing.Color.Black};body.Children.Add(web);
            var skip=new Button{Content="跳过",Padding=new Thickness(14,7,14,7),Margin=new Thickness(16),
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,
                Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(150,20,23,29)),
                BorderThickness=new Thickness(0),FontSize=12,Cursor=Cursors.Hand};
            AutomationProperties.SetAutomationId(skip,"StartupSkip");skip.Click+=delegate{Finish("skip");};body.Children.Add(skip);
            watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
            watchdog.Tick+=delegate{Finish(started?"playback-timeout":"load-timeout");};
            SourceInitialized+=delegate
            {
                var source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                source.AddHook(delegate(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
                {
                    if(message==Program.ShowMainMessage&&!finished){handled=true;Dispatcher.BeginInvoke(new Action(()=>Finish("second-launch")));}
                    return IntPtr.Zero;
                });
            };
            PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;Finish("skip");}};
            Loaded+=delegate{watchdog.Start();InitializeBrowser();};
            Closed+=delegate{closed=true;if(!finished)Finish("closed");else DisposeBrowser();};
        }

        private async void InitializeBrowser()
        {
            try
            {
                var options=new CoreWebView2EnvironmentOptions{AdditionalBrowserArguments="--disable-background-networking"};
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.DataFolder,"startup-webview"),options);
                if(finished)return;
                await web.EnsureCoreWebView2Async(environment);
                if(finished)return;
                var core=web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;
                core.Settings.AreBrowserAcceleratorKeysEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;
                core.Settings.AreHostObjectsAllowed=false;core.Settings.IsStatusBarEnabled=false;
                core.Settings.IsZoomControlEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;
                core.Settings.IsGeneralAutofillEnabled=false;
                core.PermissionRequested+=delegate(object sender,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
                core.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
                core.DownloadStarting+=delegate(object sender,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;};
                core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){if(!IsEntry(e.Uri))e.Cancel=true;};
                core.FrameNavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){e.Cancel=true;};
                core.ProcessFailed+=delegate{Finish("browser-failed");};
                core.NavigationCompleted+=delegate(object sender,CoreWebView2NavigationCompletedEventArgs e){if(!e.IsSuccess)Finish("navigation-failed");};
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=Resource;
                core.WebMessageReceived+=Message;
                core.Navigate(Origin+"/index.html?style="+style);
            }
            catch(Exception){if(!finished)Finish("browser-unavailable");}
        }

        private bool IsEntry(string url)
        {return String.Equals(url,Origin+"/index.html?style="+style,StringComparison.Ordinal);}

        private void Resource(object sender,CoreWebView2WebResourceRequestedEventArgs e)
        {
            try{ServeResource(e);}
            catch(InvalidOperationException){Finish("browser-failed");}
            catch(System.Runtime.InteropServices.COMException){Finish("browser-failed");}
            catch(IOException){Finish("resource-failed");}
        }

        private void ServeResource(CoreWebView2WebResourceRequestedEventArgs e)
        {
            if(finished||web==null||web.CoreWebView2==null)return;
            var environment=web.CoreWebView2.Environment;
            Uri uri;
            string name=null;
            if(Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="codexuserdata-startup.local"&&uri.IsDefaultPort)
            {
                if(uri.AbsolutePath=="/index.html")name="index.html";
                else if(uri.AbsolutePath=="/"+style+".webm")name=style+".webm";
            }
            // Unrecognized requests are answered locally; they never reach a
            // network endpoint or arbitrary path on the user's computer.
            if(name==null||e.Request.Method!="GET")
            {e.Response=environment.CreateWebResourceResponse(null,403,"Forbidden","Content-Length: 0\r\n");return;}
            Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup."+name);
            if(stream==null){e.Response=environment.CreateWebResourceResponse(null,404,"Not Found","Content-Length: 0\r\n");return;}
            resourceStreams.Add(stream);
            string type=name=="index.html"?"text/html; charset=utf-8":"video/webm";
            // No seeking UI is exposed. Returning the complete stream for a
            // Range request is valid HTTP and avoids copying a video into RAM.
            e.Response=environment.CreateWebResourceResponse(stream,200,"OK","Content-Type: "+type+"\r\nContent-Length: "+stream.Length+
                "\r\nAccept-Ranges: none\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        }

        private void Message(object sender,CoreWebView2WebMessageReceivedEventArgs e)
        {
            if(finished||!IsEntry(e.Source)||e.WebMessageAsJson.Length>128)return;
            string type;
            try
            {
                var message=Program.Json.Deserialize<Dictionary<string,string>>(e.WebMessageAsJson);
                if(message==null||!message.TryGetValue("type",out type))return;
            }
            catch(ArgumentException){return;}
            if(type=="ready"&&!ready)
            {
                ready=true;
                // NavigationCompleted alone can precede the first decoded
                // frame. Reveal only after the player confirms usable pixels.
                BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(140)));
                watchdog.Stop();watchdog.Interval=TimeSpan.FromSeconds(3);watchdog.Start();
                try{web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"play\"}");}
                catch(InvalidOperationException){Finish("browser-failed");}
                catch(System.Runtime.InteropServices.COMException){Finish("browser-failed");}
            }
            else if(type=="started"&&ready&&!started)
            {
                started=true;watchdog.Stop();watchdog.Interval=TimeSpan.FromSeconds(style=="matrix"?14:16);watchdog.Start();
            }
            else if(type=="brand"&&started&&!BrandStarted)
            {BrandStarted=true;if(chime!=null)chime.PlayOnce();}
            else if(type=="ended"&&started)Finish("ended");
            else if(type=="error"||type=="skip")Finish(type);
        }

        internal void Finish(string reason)
        {
            if(finished)return;
            finished=true;CompletionReason=reason;watchdog.Stop();Title="CodexUserData · 开屏";
            if(chime!=null)chime.Dispose();
            double visibleOpacity=Opacity;BeginAnimation(OpacityProperty,null);Opacity=visibleOpacity;
            // Finish once even when skip, timeout and a late WebView callback
            // arrive together. Leave the WebView callback before the widget can
            // open a dialog in Loaded: nested browser message loops are unsafe.
            if(Dispatcher.HasShutdownStarted){DisposeBrowser();return;}
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try{continueStartup();}
                finally{if(!closed)Close();DisposeBrowser();}
            }));
        }

        private void DisposeBrowser()
        {
            if(web!=null)
            {
                var old=web;web=null;
                // A failed browser process can invalidate its COM controller;
                // releasing it must not take down the already-started widget.
                try{old.Dispose();}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
            }
            foreach(var stream in resourceStreams)stream.Dispose();resourceStreams.Clear();
        }
    }
}
