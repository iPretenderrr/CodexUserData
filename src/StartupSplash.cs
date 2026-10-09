using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        private readonly Func<Task> continueStartup;
        private readonly Action startupShown;
        private readonly DispatcherTimer watchdog;
        private readonly StartupAssets assets=new StartupAssets();
        private readonly Image poster;
        private WebView2CompositionControl web;
        private readonly StartupChime chime;
        private bool finished,ready,started,closed;
        private bool handoffComplete;
        internal bool BrandStarted {get;private set;}
        internal string CompletionReason {get;private set;}
        internal bool PlaybackStarted {get{return started;}}
        internal bool FramePresented {get;private set;}

        internal static string NormalizeStyle(string value)
        {return value=="layers"||value=="matrix"||value=="prism"?value:"cinema";}

        internal static bool ShouldPlay(Preferences preferences)
        {return preferences.StartupAnimationEnabled&&SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast;}

        internal static void Launch(Preferences preferences,Action handoff)
        {Launch(preferences,delegate{handoff();return Task.FromResult(0);},null);}

        internal static void Launch(Preferences preferences,Func<Task> handoff,Action shown)
        {
            if(!ShouldPlay(preferences)){WithoutFilm(handoff,shown);return;}
            StartupSplash splash=null;
            try{splash=new StartupSplash(preferences.StartupAnimationStyle,handoff,preferences.StartupSoundEnabled,shown);}
            catch(Exception){WithoutFilm(handoff,shown);return;}
            try{splash.Show();}
            catch(Exception){if(splash.finished)throw;splash.Finish("window-failed");}
        }
        private static async void WithoutFilm(Func<Task> handoff,Action shown)
        {await handoff();if(shown!=null&&!Application.Current.Dispatcher.HasShutdownStarted)shown();}

        internal StartupSplash(string selectedStyle,Action handoff):this(selectedStyle,handoff,false){}
        internal StartupSplash(string selectedStyle,Action handoff,bool soundEnabled):this(selectedStyle,delegate{handoff();return Task.FromResult(0);},soundEnabled,null){}
        internal StartupSplash(string selectedStyle,Func<Task> handoff,bool soundEnabled,Action shown)
        {
            style=NormalizeStyle(selectedStyle);continueStartup=handoff;startupShown=shown;
            if(soundEnabled)chime=new StartupChime();
            // Keep the normal IPC title until the widget takes over, so a
            // second manual launch can skip this window instead of getting lost.
            Title=Program.WindowTitle;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
            ShowInTaskbar=false;ShowActivated=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            AllowsTransparency=true;Background=Brushes.Transparent;Opacity=1;
            double width=Math.Min(920,Math.Min(SystemParameters.WorkArea.Width*.85,SystemParameters.WorkArea.Height*.80*16/9));
            Width=Math.Max(160,width);Height=Width*9/16;
            var body=new Grid{Background=Brushes.Black};Content=body;
            // Border.CornerRadius alone does not clip a composed browser. Clip
            // the complete visual, with a transparent window behind its corners.
            body.SizeChanged+=delegate{body.Clip=new RectangleGeometry(new Rect(0,0,body.ActualWidth,body.ActualHeight),24,24);};
            web=new WebView2CompositionControl{DefaultBackgroundColor=System.Drawing.Color.Black};body.Children.Add(web);
            // WPF can paint this real film frame before Edge creates its process.
            // Keep the WebView visible underneath so its compositor is not throttled.
            poster=new Image{Source=StartupAssets.Poster(style),Stretch=Stretch.UniformToFill,IsHitTestVisible=false};body.Children.Add(poster);
            var skip=SkipButton();
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

        private static Button SkipButton()
        {
            var content=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            content.Children.Add(new TextBlock{Text="跳过",FontSize=11,FontWeight=FontWeights.Medium,VerticalAlignment=VerticalAlignment.Center});
            content.Children.Add(new Border{Width=1,Height=11,Background=new SolidColorBrush(Color.FromArgb(65,255,255,255)),Margin=new Thickness(10,0,8,0)});
            content.Children.Add(new TextBlock{Text="esc",FontFamily=new FontFamily("Segoe UI"),FontSize=10,Opacity=.68,VerticalAlignment=VerticalAlignment.Center});
            var button=new Button{Content=content,Height=32,Padding=new Thickness(14,0,13,0),Margin=new Thickness(20),
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Foreground=Brushes.White,
                Background=new LinearGradientBrush(Color.FromArgb(155,36,39,47),Color.FromArgb(120,18,20,27),90),
                BorderBrush=new SolidColorBrush(Color.FromArgb(65,255,255,255)),BorderThickness=new Thickness(1),
                Cursor=Cursors.Hand,FocusVisualStyle=null,FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI")};
            // A local template avoids Windows' square default button chrome.
            // No live blur or shadow filter is needed over the decoded film.
            var chrome=new FrameworkElementFactory(typeof(Border),"Chrome");
            chrome.SetValue(Border.CornerRadiusProperty,new CornerRadius(16));
            chrome.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);chrome.AppendChild(presenter);
            var template=new ControlTemplate(typeof(Button)){VisualTree=chrome};
            var hover=new Trigger{Property=IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(205,47,51,61)),"Chrome"));template.Triggers.Add(hover);
            var focus=new Trigger{Property=IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(200,255,255,255)),"Chrome"));template.Triggers.Add(focus);
            var pressed=new Trigger{Property=Button.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(UIElement.OpacityProperty,.70,"Chrome"));template.Triggers.Add(pressed);
            button.Template=template;AutomationProperties.SetName(button,"跳过开屏动画，Esc");return button;
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
            Stream stream=assets.Open(name);
            if(stream==null){e.Response=environment.CreateWebResourceResponse(null,404,"Not Found","Content-Length: 0\r\n");return;}
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
                // Decoded and presented are different states. Play underneath the
                // native poster; only the frame-presentation message retires it.
                watchdog.Stop();watchdog.Interval=TimeSpan.FromSeconds(3);watchdog.Start();
                try{web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"play\"}");}
                catch(InvalidOperationException){Finish("browser-failed");}
                catch(System.Runtime.InteropServices.COMException){Finish("browser-failed");}
            }
            else if(type=="started"&&ready&&!started)
            {
                started=true;watchdog.Stop();watchdog.Interval=TimeSpan.FromSeconds(style=="matrix"?14:16);watchdog.Start();
            }
            else if(type=="presented"&&started&&!FramePresented)
            {FramePresented=true;CompositionTarget.Rendering+=PresentFilm;}
            else if(type=="brand"&&started&&!BrandStarted)
            {BrandStarted=true;if(chime!=null)chime.PlayOnce();}
            else if(type=="ended"&&started)Finish("ended");
            else if(type=="error"||type=="skip")Finish(type);
        }

        internal void Finish(string reason)
        {
            if(finished)return;
            finished=true;CompletionReason=reason;watchdog.Stop();Title="CodexUserData · 开屏";
            CompositionTarget.Rendering-=PresentFilm;
            if(chime!=null)chime.Dispose();
            IsHitTestVisible=false;
            if(reason!="ended"&&web!=null)
            {
                try{if(web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"stop\"}");}
                catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
            }
            double visibleOpacity=Opacity;BeginAnimation(OpacityProperty,null);Opacity=visibleOpacity;
            // Finish once even when skip, timeout and a late WebView callback
            // arrive together. Leave the WebView callback before the widget can
            // open a dialog in Loaded: nested browser message loops are unsafe.
            if(Dispatcher.HasShutdownStarted){DisposeBrowser();return;}
            Dispatcher.BeginInvoke(new Action(async delegate
            {
                try{await continueStartup();}
                catch{CompleteHandoff(false);throw;}
                if(closed||Dispatcher.HasShutdownStarted||Opacity<.01||!Theme.MotionAllowed){CompleteHandoff();return;}
                // Keep the last video frame until the final saved form is ready,
                // then overlap its entrance with this short compositor fade.
                var fade=new DoubleAnimation(Opacity,0,Theme.MotionTime(reason=="ended"?300:170)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}};
                Timeline.SetDesiredFrameRate(fade,Theme.MotionFrameRate);
                fade.Completed+=delegate{CompleteHandoff();};BeginAnimation(OpacityProperty,fade);
            }));
        }

        private void PresentFilm(object sender,EventArgs e)
        {
            // Allow a native composition pass after the browser's frame callback.
            // This is a one-shot fade, with no permanent frame observer or blur.
            CompositionTarget.Rendering-=PresentFilm;
            if(finished||web==null)return;
            var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(120));Timeline.SetDesiredFrameRate(fade,30);
            fade.Completed+=delegate{poster.Visibility=Visibility.Collapsed;poster.Source=null;};
            poster.BeginAnimation(OpacityProperty,fade);
        }

        private void CompleteHandoff(bool notify=true)
        {
            if(handoffComplete)return;handoffComplete=true;
            if(!closed)Close();DisposeBrowser();
            if(notify&&startupShown!=null&&!Dispatcher.HasShutdownStarted)startupShown();
        }

        private void DisposeBrowser()
        {
            CompositionTarget.Rendering-=PresentFilm;
            poster.BeginAnimation(OpacityProperty,null);poster.Source=null;
            if(web!=null)
            {
                var old=web;web=null;
                // A failed browser process can invalidate its COM controller;
                // releasing it must not take down the already-started widget.
                try{old.Dispose();}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
            }
            assets.Dispose();
        }
    }
}
