using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexUserData
{
    internal sealed class ShapeManifest
    {
        public int apiVersion {get;set;}
        public string name {get;set;}
        public string entry {get;set;}
        public double width {get;set;}
        public double height {get;set;}
        internal static ShapeManifest Read(string path)
        {
            if(new FileInfo(path).Length>16384)throw new IOException("形态配置文件过大。");
            var m=Program.Json.Deserialize<ShapeManifest>(File.ReadAllText(path));
            if(m==null||m.apiVersion!=1)throw new IOException("形态接口版本不支持，需要 apiVersion: 1。");
            if(String.IsNullOrWhiteSpace(m.entry))m.entry="index.html";
            string file=SafeFile(Path.GetDirectoryName(path),m.entry);
            if(!File.Exists(file)||Path.GetExtension(file).ToLowerInvariant()!=".html")throw new IOException("找不到形态 HTML 入口。");
            m.width=Theme.Bound(m.width,64,800,240);m.height=Theme.Bound(m.height,40,600,90);return m;
        }
        internal static string SafeFile(string root,string relative)
        {
            if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.IndexOf(':')>=0)throw new IOException("形态需要文件夹内的相对路径。");
            string folder=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.Combine(folder,relative));
            if(!full.StartsWith(folder,StringComparison.OrdinalIgnoreCase))throw new IOException("形态文件必须位于形态文件夹内。");
            // Do not let a junction or symbolic link escape the selected asset directory.
            for(string p=full;p!=null&&p.Length>=folder.Length-1;p=Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("形态不支持链接文件夹或符号链接。");
            return full;
        }
        internal static string Resolve(string relative){return SafeFile(Program.DataFolder,relative??"");}
        internal static string Import(string path)
        {
            Read(path);string root=Path.GetDirectoryName(Path.GetFullPath(path));
            string relative=Path.Combine("skins",Guid.NewGuid().ToString("N")),target=Resolve(relative);
            var files=new List<string>();Collect(root,files,0);
            if(files.Count>256||files.Sum(f=>new FileInfo(f).Length)>25*1024*1024)throw new IOException("形态最多 256 个文件、25 MB。");
            Directory.CreateDirectory(target);
            foreach(string file in files)
            {
                string name=file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                string to=SafeFile(target,name);Directory.CreateDirectory(Path.GetDirectoryName(to));File.Copy(file,to,false);
            }
            return Path.Combine(relative,Path.GetFileName(path));
        }
        private static void Collect(string root,List<string> files,int depth)
        {
            if(depth>12||files.Count>256)throw new IOException("形态目录过大。");
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("形态目录不能包含链接。");
            foreach(string file in Directory.GetFiles(root)){SafeFile(root,Path.GetFileName(file));files.Add(file);}
            foreach(string folder in Directory.GetDirectories(root))Collect(folder,files,depth+1);
        }
    }
    // CompositionControl participates in WPF mouse routing. The parent intercepts right-clicks
    // before web content receives them, including pages calling preventDefault or running bad JS.
    internal sealed class CustomShapeView : Grid,IDisposable
    {
        private const string Origin="https://shape.codexuserdata.local";
        private static readonly object environmentGate=new object();
        private static Task<CoreWebView2Environment> sharedEnvironment;
        private static Task<CoreWebView2Environment> warmedEnvironment;
        private WebView2CompositionControl web;
        private bool disposed,ready,started,failed,reconcilingVisibility,presented,snapshotDirty;
        private int visibilityRevision,presentationRevision,requestedFrame=-1;
        private ulong navigationId;
        private TaskCompletionSource<bool> presentation=new TaskCompletionSource<bool>();
        internal string LastError;
        internal string LoadStage="not-started";
        internal string PresentationStage="loading";
        private string dataJson,lastSentData,root,entry;
        private UsageSnapshot latestUsage;
        private QuotaBucket latestBucket;
        private ActivityReport latestActivity;
        private Preferences latestPreferences;
        private bool completionPending;
        internal void SetCompletionPending(bool value)
        {if(completionPending==value)return;completionPending=value;snapshotDirty=true;Send();}
        private readonly Action restore,drag;
        private readonly Action<double,double> resize;
        private readonly DispatcherTimer watchdog,frameFallback;
        private readonly Border loading;
        internal CustomShapeView(string manifest,Action showMain,Action move,Action<double,double> setSize)
        {
            restore=showMain;drag=move;resize=setSize;Background=Theme.B("#01000000");
            var spec=ShapeManifest.Read(manifest);root=Path.GetDirectoryName(manifest);entry=spec.entry.Replace('\\','/');
            loading=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(12),Child=new TextBlock{Text="正在载入形态…",Foreground=Theme.Ink,FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
            Panel.SetZIndex(loading,1);Children.Add(loading);
            watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(15)};
            watchdog.Tick+=delegate{Fail("形态加载超时。右键可返回主界面或重新载入。");};
            frameFallback=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1000)};
            frameFallback.Tick+=delegate{frameFallback.Stop();if(!disposed&&ready&&IsVisible)CompletePresentation("frame-timeout");};
            Loaded+=delegate{if(!started){started=true;Start();}};
            IsVisibleChanged+=delegate
            {
                if(disposed)return;
                visibilityRevision++;InvalidatePresentation();ReconcileVisibility();
            };
        }
        private async void Start()
        {
            try
            {
                web=new WebView2CompositionControl{DefaultBackgroundColor=System.Drawing.Color.Transparent};Children.Add(web);
                watchdog.Start();
                LoadStage="environment";Task<CoreWebView2Environment> environment=Environment();CoreWebView2Environment env;
                try{env=await environment;}catch{lock(environmentGate)if(Object.ReferenceEquals(sharedEnvironment,environment))sharedEnvironment=null;throw;}
                if(disposed||failed)return;
                LoadStage="controller";await web.EnsureCoreWebView2Async(env);
                if(disposed||failed)return;
                var core=web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;
                core.Settings.AreBrowserAcceleratorKeysEnabled=false;core.Settings.IsStatusBarEnabled=false;
                core.Settings.AreDefaultScriptDialogsEnabled=false;
                core.Settings.AreHostObjectsAllowed=false;core.Settings.IsZoomControlEnabled=false;
                core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
                core.PermissionRequested+=delegate(object s,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
                core.NewWindowRequested+=delegate(object s,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
                core.DownloadStarting+=delegate(object s,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;};
                core.NavigationStarting+=delegate(object s,CoreWebView2NavigationStartingEventArgs e)
                {
                    if(!Allowed(e.Uri)){e.Cancel=true;return;}
                    navigationId=e.NavigationId;ready=false;visibilityRevision++;InvalidatePresentation();loading.Visibility=Visibility.Visible;watchdog.Start();
                };
                core.FrameNavigationStarting+=delegate(object s,CoreWebView2NavigationStartingEventArgs e){e.Cancel=true;};
                core.ProcessFailed+=delegate(object sender,CoreWebView2ProcessFailedEventArgs e)
                {
                    LastError="navigation: WebView2 "+e.ProcessFailedKind+" / "+e.Reason+" / exit "+e.ExitCode;
                    Fail("形态渲染已停止，可右键重新载入或返回主界面。");
                };
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=Resource;
                core.WebMessageReceived+=delegate(object s,CoreWebView2WebMessageReceivedEventArgs e)
                {
                    if(disposed||failed||!Object.ReferenceEquals(web.CoreWebView2,core)||!Allowed(e.Source)||e.WebMessageAsJson.Length>1024)return;
                    try{var message=Program.Json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);object command;
                        if(message!=null&&message.TryGetValue("type",out command))
                        {
                            string type=Convert.ToString(command);
                            if(type=="__hostPresented")
                            {
                                object value;int revision;
                                if(message.TryGetValue("revision",out value)&&Int32.TryParse(Convert.ToString(value),out revision)&&revision==presentationRevision&&ready&&IsVisible)CompletePresentation("frame");
                            }
                            else if(type=="ready")Send(true);
                            else if(IsVisible&&type=="restoreMain")restore();
                            else if(IsVisible&&type=="drag")drag();
                            else if(IsVisible&&type=="resize"){object w,h;double width,height;if(message.TryGetValue("width",out w)&&message.TryGetValue("height",out h)&&Double.TryParse(Convert.ToString(w),out width)&&Double.TryParse(Convert.ToString(h),out height))resize(width,height);}
                        }
                    }catch(ArgumentException){}
                };
                core.NavigationCompleted+=delegate(object s,CoreWebView2NavigationCompletedEventArgs e)
                {if(disposed||failed||e.NavigationId!=navigationId)return;watchdog.Stop();if(!e.IsSuccess)Fail("形态加载失败，请检查 HTML 入口。");else{ready=true;visibilityRevision++;ReconcileVisibility();}};
                LoadStage="navigation";core.Navigate(Origin+"/"+entry);
            }
            catch(Exception ex){if(disposed||failed)return;LastError=ex.ToString();Fail("无法载入 HTML 形态。请确认已安装 Microsoft Edge WebView2 Runtime；也可右键返回内置形态。");}
        }
        private static Task<CoreWebView2Environment> Environment()
        {
            lock(environmentGate)
            {
                if(sharedEnvironment==null)
                {
                    var options=new CoreWebView2EnvironmentOptions();options.AdditionalBrowserArguments="--disable-background-networking";
                    // One browser environment serves every custom form. Controllers remain isolated,
                    // while repeated form switches avoid another browser-process startup.
                    sharedEnvironment=CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.DataFolder,"webview"),options);
                }
                return sharedEnvironment;
            }
        }
        internal static void WarmEnvironment()
        {
            Task<CoreWebView2Environment> environment;
            // Preloading is optional; an unavailable runtime must not break the native dock bar.
            try{environment=Environment();}catch(Exception){return;}
            lock(environmentGate){if(Object.ReferenceEquals(warmedEnvironment,environment))return;warmedEnvironment=environment;}
            environment.ContinueWith(delegate(Task<CoreWebView2Environment> failed)
            {
                // Observe a failed preload and allow the visible form to retry later.
                var ignored=failed.Exception;lock(environmentGate)if(Object.ReferenceEquals(sharedEnvironment,environment))sharedEnvironment=null;
            },TaskContinuationOptions.OnlyOnFaulted);
        }
        private void InvalidatePresentation()
        {
            frameFallback.Stop();presentationRevision++;presented=false;requestedFrame=-1;PresentationStage=ready?"resuming":"loading";
            var previous=presentation;presentation=new TaskCompletionSource<bool>();previous.TrySetResult(false);
        }
        // A hide/show can arrive while TrySuspendAsync is outstanding. Only this loop owns
        // suspension, and it rechecks the latest visibility after every asynchronous step.
        private async void ReconcileVisibility()
        {
            if(reconcilingVisibility||disposed||failed||!ready||web==null||web.CoreWebView2==null)return;
            reconcilingVisibility=true;
            try
            {
                int revision;
                do
                {
                    revision=visibilityRevision;var core=web.CoreWebView2;
                    if(IsVisible)
                    {
                        core.Resume();Send(true);RequestFrame(core);
                    }
                    else
                    {
                        // A controller may reject suspension briefly. Retry once, then leave it
                        // hidden without a busy retry timer; the next visibility change reconciles it.
                        for(int attempt=0;attempt<2&&!IsVisible;attempt++)
                        {
                            if(core.IsSuspended)break;
                            try{await core.TrySuspendAsync();}
                            catch(InvalidOperationException ex){if(!IsControllerVisibilityRace(ex))throw;}
                            catch(System.Runtime.InteropServices.COMException ex)
                            {
                                // WPF visibility can change before the native controller catches up.
                                // ERROR_INVALID_STATE means it is still visible; allow one delayed retry.
                                if(!IsControllerVisibilityRace(ex))throw;
                            }
                            if(disposed||failed||web==null||!Object.ReferenceEquals(web.CoreWebView2,core))return;
                            if(core.IsSuspended||IsVisible)break;
                            if(attempt==0)await Task.Delay(80);
                            if(disposed||failed||!ready||web==null||!Object.ReferenceEquals(web.CoreWebView2,core))return;
                        }
                    }
                }while(!disposed&&!failed&&ready&&web!=null&&revision!=visibilityRevision);
            }
            catch(InvalidOperationException ex){if(!disposed&&!failed){LastError=ex.ToString();Fail("形态恢复失败，可右键重新载入或返回主界面。");}}
            catch(System.Runtime.InteropServices.COMException ex){if(!disposed&&!failed){LastError=ex.ToString();Fail("形态恢复失败，可右键重新载入或返回主界面。");}}
            finally{reconcilingVisibility=false;}
        }
        internal static bool IsControllerVisibilityRace(Exception error)
        {
            // The managed SDK wraps ERROR_INVALID_STATE in InvalidOperationException with
            // a misleading "disposed" message. Inspect the HRESULT, never the translated text.
            for(var current=error;current!=null;current=current.InnerException)
                if(current is System.Runtime.InteropServices.COMException&&current.HResult==unchecked((int)0x8007139F))return true;
            return false;
        }
        private async void RequestFrame(CoreWebView2 core)
        {
            if(presented||requestedFrame==presentationRevision||!ready||!IsVisible)return;
            int revision=presentationRevision;requestedFrame=revision;frameFallback.Start();
            try
            {
                // Host-owned acknowledgement also works with existing API v1 pages. The two
                // animation frames let snapshot handlers apply DOM changes before revealing.
                await core.ExecuteScriptAsync("requestAnimationFrame(function(){requestAnimationFrame(function(){window.chrome.webview.postMessage({type:'__hostPresented',revision:"+revision+"});});});");
            }
            catch(InvalidOperationException){if(!disposed&&revision==presentationRevision)CompletePresentation("frame-unavailable");}
            catch(System.Runtime.InteropServices.COMException){if(!disposed&&revision==presentationRevision)CompletePresentation("frame-unavailable");}
        }
        private void CompletePresentation(string stage)
        {
            if(disposed)return;frameFallback.Stop();presented=true;PresentationStage=stage;loading.Visibility=Visibility.Collapsed;presentation.TrySetResult(true);
        }
        internal async Task WaitForPresentationAsync()
        {
            if(disposed||failed||!IsVisible||presented)return;
            // Cold pages retain the native loading view when the bounded wait expires. They
            // still have the navigation watchdog and the always-available native context menu.
            using(var timeout=new System.Threading.CancellationTokenSource())
            {
                // Reuse one deadline across navigation/visibility revisions and release its
                // timer immediately on success/disposal instead of leaving delayed tasks behind.
                var deadline=Task.Delay(1200,timeout.Token);
                try
                {
                    while(!disposed&&!failed&&IsVisible&&!presented)
                    {
                        ReconcileVisibility();var pending=presentation.Task;
                        if(await Task.WhenAny(pending,deadline)!=pending)return;
                    }
                }
                finally{timeout.Cancel();}
            }
        }
        internal static bool Allowed(string uri)
        {
            Uri parsed;return Uri.TryCreate(uri,UriKind.Absolute,out parsed)&&parsed.Scheme=="https"&&parsed.Host=="shape.codexuserdata.local"&&parsed.IsDefaultPort&&parsed.UserInfo.Length==0;
        }
        private void Resource(object sender,CoreWebView2WebResourceRequestedEventArgs e)
        {
            if(disposed||failed||web==null||web.CoreWebView2==null)return;
            var environment=web.CoreWebView2.Environment;
            try
            {
                if(!Allowed(e.Request.Uri))throw new IOException();
                string path=ShapeManifest.SafeFile(root,Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath).TrimStart('/'));
                string ext=Path.GetExtension(path).ToLowerInvariant();
                var mime=new Dictionary<string,string>{{".html","text/html; charset=utf-8"},{".js","text/javascript; charset=utf-8"},{".css","text/css; charset=utf-8"},{".json","application/json"},{".svg","image/svg+xml"},{".png","image/png"},{".jpg","image/jpeg"},{".webp","image/webp"},{".gif","image/gif"},{".woff2","font/woff2"}};
                if(!mime.ContainsKey(ext)||new FileInfo(path).Length>8*1024*1024)throw new IOException();
                string headers="Content-Type: "+mime[ext]+"\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
                e.Response=environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(path)),200,"OK",headers);
            }
            catch(Exception){e.Response=environment.CreateWebResourceResponse(new MemoryStream(),403,"Blocked","");}
        }
        internal void Apply(UsageSnapshot usage,QuotaBucket bucket,ActivityReport activity,Preferences preferences)
        {
            if(disposed||failed)return;
            // Keep only the latest inputs while docked/hidden. Serialization and browser
            // messages are paid for when the shape is visible, not on every background tick.
            latestUsage=usage;latestBucket=bucket;latestActivity=activity;latestPreferences=preferences;snapshotDirty=true;Send();
        }
        private string SerializeData()
        {
            var usage=latestUsage;var bucket=latestBucket;var activity=latestActivity;var preferences=latestPreferences;
            var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd"));
            long now=LocalCodexUsage.Unix(DateTime.Now);
            int motionFps=Theme.ActivityFrameRate(preferences.OrbAnimation);
            var windows=bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
            // Explicit data-only contract: no paths, account IDs, log text, or authentication tokens.
            return Program.Json.Serialize(new {
                source=preferences.Source,theme=preferences.ThemeMode,
                today=today==null?null:new{date=today.Date,tokens=today.Tokens,tokenText=TokenText.Compact(today.Tokens),input=today.Input,output=today.Output,cacheRead=today.CacheRead,cacheWrite=today.CacheWrite,requests=today.Requests,apiEquivalentUsd=today.Models.Sum(m=>m.EquivalentUsd),models=today.Models.Select(m=>new{model=m.Model,effort=m.Effort,tokens=m.Tokens,apiEquivalentUsd=m.EquivalentUsd})},
                quota=windows.Select(w=>new{minutes=w.Minutes,remainingPercent=w.RemainingPercent(bucket,now),resetsAt=w.ResetsAt,observedAt=bucket.ObservedAt}),
                activity=new{activeTasks=activity==null?0:activity.ActiveTasks,uncertainTasks=activity==null?0:activity.UncertainTasks,completedTasks=activity==null?0:activity.CompletedTasks,completionSerial=activity==null?0:activity.CompletionSerial,
                    monitoringAvailable=activity!=null&&!activity.MonitoringUnavailable&&activity.ObservedAt>0&&activity.ObservedAt<=now+5&&now-activity.ObservedAt<=5},
                motion=motionFps==0?"off":motionFps<=30?"eco":"smooth",effectIntensity=preferences.EffectStrength,completionPending=completionPending
            });
        }
        private void Send(bool force=false)
        {
            if(!ready||disposed||failed||web==null||!IsVisible||latestPreferences==null)return;
            try
            {
                if(web.CoreWebView2.IsSuspended)return;
                if(snapshotDirty||force||dataJson==null){dataJson=SerializeData();snapshotDirty=false;}
                // Compare data before attaching the delivery timestamp, otherwise observedAt
                // makes identical periodic snapshots look different and wakes idle pages.
                if(!force&&dataJson==lastSentData)return;
                long now=LocalCodexUsage.Unix(DateTime.Now);
                string snapshot="{\"type\":\"snapshot\",\"apiVersion\":1,\"data\":"+dataJson.Insert(1,"\"observedAt\":"+now+",")+"}";
                web.CoreWebView2.PostWebMessageAsJson(snapshot);lastSentData=dataJson;
            }
            catch(InvalidOperationException){}
            catch(System.Runtime.InteropServices.COMException){}
        }
        private void Fail(string text)
        {
            if(disposed||failed)return;failed=true;LastError=LastError??(LoadStage+": "+text);watchdog.Stop();frameFallback.Stop();ready=false;visibilityRevision++;presentationRevision++;
            if(web!=null){web.Dispose();web=null;}Children.Clear();
            var body=new StackPanel{Margin=new Thickness(10)};body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Theme.Ink,FontSize=11});
            var back=Theme.Button("返回主界面","返回主界面",110);back.Click+=delegate{restore();};body.Children.Add(back);
            Children.Add(new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(12),Child=body});
            PresentationStage="failed";presentation.TrySetResult(false);
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;visibilityRevision++;presentationRevision++;watchdog.Stop();frameFallback.Stop();presentation.TrySetResult(false);
            latestUsage=null;latestBucket=null;latestActivity=null;latestPreferences=null;
            if(web!=null){web.Dispose();web=null;}Children.Clear();
        }
    }
}
