using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private WebView2CompositionControl web;
        private bool disposed,ready,started,failed;
        internal string LastError;
        internal string LoadStage="not-started";
        private string snapshot="{}",root,entry;
        private readonly Action restore,drag;
        private readonly Action<double,double> resize;
        private readonly DispatcherTimer watchdog;
        internal CustomShapeView(string manifest,Action showMain,Action move,Action<double,double> setSize)
        {
            restore=showMain;drag=move;resize=setSize;Background=Theme.B("#01000000");
            var spec=ShapeManifest.Read(manifest);root=Path.GetDirectoryName(manifest);entry=spec.entry.Replace('\\','/');
            watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(15)};
            watchdog.Tick+=delegate{Fail("形态加载超时。右键可返回主界面或重新载入。");};
            Loaded+=delegate{if(!started){started=true;Start();}};
            IsVisibleChanged+=async delegate
            {
                if(disposed||web==null||web.CoreWebView2==null)return;
                try{if(IsVisible){web.CoreWebView2.Resume();Send();}else await web.CoreWebView2.TrySuspendAsync();}catch(InvalidOperationException){}
            };
        }
        private async void Start()
        {
            try
            {
                web=new WebView2CompositionControl{DefaultBackgroundColor=System.Drawing.Color.Transparent};Children.Add(web);
                watchdog.Start();
                var options=new CoreWebView2EnvironmentOptions();
                options.AdditionalBrowserArguments="--disable-background-networking";
                LoadStage="environment";var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.DataFolder,"webview"),options);
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
                core.NavigationStarting+=delegate(object s,CoreWebView2NavigationStartingEventArgs e){if(!Allowed(e.Uri))e.Cancel=true;};
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
                    if(!Allowed(e.Source)||e.WebMessageAsJson.Length>1024)return;
                    try{var message=Program.Json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);object command;
                        if(message!=null&&message.TryGetValue("type",out command))
                        {if(Convert.ToString(command)=="ready")Send();else if(Convert.ToString(command)=="restoreMain")restore();else if(Convert.ToString(command)=="drag")drag();else if(Convert.ToString(command)=="resize"){object w,h;double width,height;if(message.TryGetValue("width",out w)&&message.TryGetValue("height",out h)&&Double.TryParse(Convert.ToString(w),out width)&&Double.TryParse(Convert.ToString(h),out height))resize(width,height);}}
                    }catch(ArgumentException){}
                };
                core.NavigationCompleted+=delegate(object s,CoreWebView2NavigationCompletedEventArgs e)
                {watchdog.Stop();if(!e.IsSuccess)Fail("形态加载失败，请检查 HTML 入口。");else{ready=true;Send();}};
                LoadStage="navigation";core.Navigate(Origin+"/"+entry);
            }
            catch(Exception ex){LastError=ex.ToString();if(!disposed)Fail("无法载入 HTML 形态。请确认已安装 Microsoft Edge WebView2 Runtime；也可右键返回内置形态。");}
        }
        internal static bool Allowed(string uri)
        {
            Uri parsed;return Uri.TryCreate(uri,UriKind.Absolute,out parsed)&&parsed.Scheme=="https"&&parsed.Host=="shape.codexuserdata.local"&&parsed.IsDefaultPort&&parsed.UserInfo.Length==0;
        }
        private void Resource(object sender,CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if(!Allowed(e.Request.Uri))throw new IOException();
                string path=ShapeManifest.SafeFile(root,Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath).TrimStart('/'));
                string ext=Path.GetExtension(path).ToLowerInvariant();
                var mime=new Dictionary<string,string>{{".html","text/html; charset=utf-8"},{".js","text/javascript; charset=utf-8"},{".css","text/css; charset=utf-8"},{".json","application/json"},{".svg","image/svg+xml"},{".png","image/png"},{".jpg","image/jpeg"},{".webp","image/webp"},{".gif","image/gif"},{".woff2","font/woff2"}};
                if(!mime.ContainsKey(ext)||new FileInfo(path).Length>8*1024*1024)throw new IOException();
                string headers="Content-Type: "+mime[ext]+"\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
                e.Response=web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(path)),200,"OK",headers);
            }
            catch(Exception){e.Response=web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Blocked","");}
        }
        internal void Apply(UsageSnapshot usage,QuotaBucket bucket,ActivityReport activity,Preferences preferences)
        {
            var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd"));
            long now=LocalCodexUsage.Unix(DateTime.Now);
            var windows=bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
            // Explicit data-only contract: no paths, account IDs, log text, or authentication tokens.
            snapshot=Program.Json.Serialize(new {type="snapshot",apiVersion=1,data=new{
                observedAt=now,source=preferences.Source,theme=preferences.ThemeMode,
                today=today==null?null:new{date=today.Date,tokens=today.Tokens,tokenText=TokenText.Compact(today.Tokens),input=today.Input,output=today.Output,cacheRead=today.CacheRead,cacheWrite=today.CacheWrite,requests=today.Requests,apiEquivalentUsd=today.Models.Sum(m=>m.EquivalentUsd),models=today.Models.Select(m=>new{model=m.Model,effort=m.Effort,tokens=m.Tokens,apiEquivalentUsd=m.EquivalentUsd})},
                quota=windows.Select(w=>new{minutes=w.Minutes,remainingPercent=w.RemainingPercent(bucket,now),resetsAt=w.ResetsAt,observedAt=bucket.ObservedAt}),
                activity=new{activeTasks=activity==null?0:activity.ActiveTasks,uncertainTasks=activity==null?0:activity.UncertainTasks,completedTasks=activity==null?0:activity.CompletedTasks,completionSerial=activity==null?0:activity.CompletionSerial},
                motion=preferences.OrbAnimation=="off"?"off":preferences.OrbAnimation=="eco"||RenderCapability.Tier==0?"eco":"smooth"
            }});Send();
        }
        private void Send(){if(ready&&!disposed&&web!=null&&IsVisible)try{web.CoreWebView2.PostWebMessageAsJson(snapshot);}catch(InvalidOperationException){}}
        private void Fail(string text)
        {
            if(disposed||failed)return;failed=true;LastError=LastError??(LoadStage+": "+text);watchdog.Stop();ready=false;
            if(web!=null){web.Dispose();web=null;}Children.Clear();
            var body=new StackPanel{Margin=new Thickness(10)};body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Theme.Ink,FontSize=11});
            var back=Theme.Button("返回主界面","返回主界面",110);back.Click+=delegate{restore();};body.Children.Add(back);
            Children.Add(new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(12),Child=body});
        }
        public void Dispose(){disposed=true;watchdog.Stop();if(web!=null){web.Dispose();web=null;}Children.Clear();}
    }
}
