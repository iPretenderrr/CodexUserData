using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

[assembly: System.Reflection.AssemblyTitle("CodexUserData")]
[assembly: System.Reflection.AssemblyProduct("CodexUserData")]
[assembly: System.Reflection.AssemblyVersion("1.8.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.2.0")]

namespace CodexUserData
{
    internal sealed class BallPlacement
    {
        public double X {get;set;} public double Y {get;set;} public string Dock {get;set;}
        internal static BallPlacement Capture(Rect bounds,Rect work,string dock)
        {return new BallPlacement{X=Theme.Bound((bounds.Left-work.Left)/Math.Max(1,work.Width-bounds.Width),0,1,0),Y=Theme.Bound((bounds.Top-work.Top)/Math.Max(1,work.Height-bounds.Height),0,1,0),Dock=dock};}
        internal Rect Restore(Size pixels,Rect work)
        {return WindowInteraction.FitBounds(new Rect(work.Left+X*Math.Max(0,work.Width-pixels.Width),work.Top+Y*Math.Max(0,work.Height-pixels.Height),pixels.Width,pixels.Height),work,0);}
    }
    internal sealed class Preferences
    {
        public string App {get;set;} public string Range {get;set;} public string Database {get;set;}
        public bool Pinned {get;set;} public bool Collapsed {get;set;} public double Left {get;set;} public double Top {get;set;}
        public string Source {get;set;} public string CodexHome {get;set;}
        public double Width {get;set;} public double Height {get;set;} public double Opacity {get;set;}
        public int RefreshSeconds {get;set;} public string[] Metrics {get;set;}
        public bool ShowHeatmap {get;set;} public bool ShowTrend {get;set;} public int TrendDays {get;set;}
        public bool ShowQuota {get;set;} public bool LiveQuota {get;set;} public bool ShowModels {get;set;} public string QuotaCli {get;set;}
        public Dictionary<string,decimal[]> PriceOverrides {get;set;}
        public string[] KnownModels {get;set;}
        public bool BallMode {get;set;} public bool BallExpanded {get;set;}
        public string BallStyle {get;set;} public string OrbQuotaWindow {get;set;} public string OrbAnimation {get;set;}
        public double OrbSize {get;set;}
        public double AnimationSpeed {get;set;}
        public string[] OrbShortColors {get;set;} public string[] OrbLongColors {get;set;}
        public double OrbShortAngle {get;set;} public double OrbLongAngle {get;set;}
        public bool StartWithWindows {get;set;} public bool StartWithCodex {get;set;}
        public double BallOpacity {get;set;} public bool OrbFollowTheme {get;set;}
        public bool IslandFollowTheme {get;set;} public string[] IslandColors {get;set;} public string IslandMaterial {get;set;}
        public string CustomShape {get;set;}
        public bool CompletionFlash {get;set;}
        public bool CliNoticeShown {get;set;}
        public bool MinimizeToTray {get;set;} // Missing in older settings defaults to taskbar (false).
        public double BallLeft {get;set;} public double BallTop {get;set;} public string BallDock {get;set;}
        public bool BallPositionLocked {get;set;}
        public string BallMonitor {get;set;}
        public Dictionary<string,BallPlacement> BallPlacements {get;set;}
        public Dictionary<string,double[]> CustomShapeSizes {get;set;}
        public string ThemeMode {get;set;} public string ThemeBase {get;set;} public string GradientKind {get;set;} public string GradientSpread {get;set;}
        public double GradientSpan {get;set;}
        public string[] GradientColors {get;set;} public double[] GradientStops {get;set;}
        public double GradientAngle {get;set;} public double GradientCenterX {get;set;} public double GradientCenterY {get;set;} public double GradientRadius {get;set;}
        public double GradientStrength {get;set;} public double ThemeCardOpacity {get;set;}
        public Preferences()
        {
            string user=Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            BallLeft=Double.NaN;BallTop=Double.NaN;BallDock="";
            BallMonitor="";BallPlacements=new Dictionary<string,BallPlacement>(StringComparer.OrdinalIgnoreCase);CustomShapeSizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            BallOpacity=1;OrbFollowTheme=true;IslandFollowTheme=true;IslandMaterial="glass";IslandColors=new[]{"#3976FF","#35C8E2","#87F3BE","#C68BFF","#FF83C4"};CompletionFlash=true;BallStyle="orb";OrbQuotaWindow="auto";OrbAnimation="auto";OrbSize=84;AnimationSpeed=1;
            OrbShortColors=new[]{"#316BF1","#5F97FF","#89CDEC"};OrbLongColors=new[]{"#7965EA","#AB8DF0","#E2B0ED"};OrbShortAngle=OrbLongAngle=45;
            ThemeMode="dark";ThemeBase="auto";GradientKind="linear";GradientSpread="pad";GradientColors=new[]{"#F7BBE3","#E6D7FA","#AAF1ED"};GradientStops=new[]{0d,48d,100d};GradientAngle=120;GradientSpan=100;GradientCenterX=50;GradientCenterY=35;GradientRadius=80;GradientStrength=85;ThemeCardOpacity=82;
            PriceOverrides=new Dictionary<string,decimal[]>();KnownModels=new string[0];
            App="";Range="today";Pinned=true;Left=Double.NaN;Top=Double.NaN;
            Database=Path.Combine(user,".cc-switch","cc-switch.db"); CodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
            if(String.IsNullOrWhiteSpace(CodexHome))CodexHome=Path.Combine(user,".codex");
            Source=File.Exists(Database)&&!Directory.Exists(Path.Combine(CodexHome,"sessions"))?"ccswitch":"local";Width=380;Height=440;Opacity=1;RefreshSeconds=5;
            Metrics=new[]{"tokens","requests","cacheRate","cost","input","output"};
            ShowHeatmap=true;ShowTrend=true;TrendDays=30;
            ShowQuota=true;LiveQuota=true;ShowModels=true;QuotaCli=QuotaReader.DefaultExe;
        }
        internal Preferences Clone(){return Program.Json.Deserialize<Preferences>(Program.Json.Serialize(this));}
        internal void Validate()
        {
            Theme.Normalize(this);BallOpacity=Clamp(BallOpacity,.2,1,1);
            NormalizePlacement();
            OrbPalette.Normalize(this);
            IslandColors=OrbPalette.Clean(IslandColors,new[]{"#3976FF","#35C8E2","#87F3BE","#C68BFF","#FF83C4"});
            if(IslandMaterial!="classic")IslandMaterial="glass";
            if(BallStyle!="capsule"&&BallStyle!="island"&&BallStyle!="html")BallStyle="orb";
            if(!new[]{"auto","short","week"}.Contains(OrbQuotaWindow))OrbQuotaWindow="auto";
            if(!new[]{"auto","smooth","eco","off"}.Contains(OrbAnimation))OrbAnimation="auto";
            OrbSize=Clamp(OrbSize,56,128,84);
            if(!new[]{"today","week","month","all"}.Contains(Range))Range="today";
            if(Source!="local")Source="ccswitch";
            if(String.IsNullOrWhiteSpace(Database))Database=new Preferences().Database;
            if(String.IsNullOrWhiteSpace(CodexHome))CodexHome=new Preferences().CodexHome;
            if(!File.Exists(QuotaCli))QuotaCli=QuotaReader.DefaultExe;
            PriceOverrides=ApiPrices.Clean(PriceOverrides);KnownModels=(KnownModels??new string[0]).Where(m=>!String.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // An unrecognized saved form must not reopen a large or partially docked window.
            if(!new[]{"","left","right","top","bottom"}.Contains(BallDock)){BallDock="";BallExpanded=false;}
            App=App??""; Width=Clamp(Width,260,1100,380);Height=Clamp(Height,280,1000,440);Opacity=Clamp(Opacity,.2,1,1);
            RefreshSeconds=Math.Max(2,Math.Min(3600,RefreshSeconds));
            Metrics=(Metrics??new string[0]).Where(k=>Theme.MetricLabels.ContainsKey(k)).Distinct().ToArray();
            if(Metrics.Length==0)Metrics=new[]{"tokens"};
            if(!new[]{1,7,14,30,60,90,180}.Contains(TrendDays))TrendDays=30;
        }
        private static double Clamp(double v,double min,double max,double fallback){return Double.IsNaN(v)||Double.IsInfinity(v)?fallback:Math.Max(min,Math.Min(max,v));}
        internal static string ShapeSizeKey(string relative)
        {
            if(String.IsNullOrWhiteSpace(relative)||relative.Length>240||Path.IsPathRooted(relative))return null;
            var parts=relative.Replace('\\','/').Split('/');
            if(parts.Any(p=>p.Length==0||p=="."||p==".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))return null;
            return String.Join("/",parts);
        }
        internal void NormalizePlacement()
        {
            if(String.IsNullOrEmpty(BallMonitor)||BallMonitor.Length>128||BallMonitor.Any(Char.IsControl))BallMonitor="";
            var monitors=new Dictionary<string,BallPlacement>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in BallPlacements??new Dictionary<string,BallPlacement>())
            {
                if(monitors.Count>=8)break;
                if(String.IsNullOrWhiteSpace(pair.Key)||pair.Key.Length>128||pair.Key.Any(Char.IsControl)||pair.Value==null)continue;
                monitors[pair.Key]=new BallPlacement{X=Clamp(pair.Value.X,0,1,0),Y=Clamp(pair.Value.Y,0,1,0),Dock=new[]{"left","right","top","bottom"}.Contains(pair.Value.Dock)?pair.Value.Dock:""};
            }
            BallPlacements=monitors;
            var sizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in CustomShapeSizes??new Dictionary<string,double[]>())
            {
                if(sizes.Count>=32)break;string key=ShapeSizeKey(pair.Key);
                if(key==null||pair.Value==null||pair.Value.Length!=2)continue;
                sizes[key]=new[]{Clamp(pair.Value[0],64,800,240),Clamp(pair.Value[1],40,600,90)};
            }
            CustomShapeSizes=sizes;
        }
        internal bool RememberShapeSize(string relative,double width,double height)
        {
            string key=ShapeSizeKey(relative);if(key==null)return false;
            if(CustomShapeSizes==null)CustomShapeSizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            width=Clamp(width,64,800,240);height=Clamp(height,40,600,90);double[] previous;
            if(CustomShapeSizes.TryGetValue(key,out previous)&&previous!=null&&previous.Length==2&&Math.Abs(previous[0]-width)<.5&&Math.Abs(previous[1]-height)<.5)return false;
            if(!CustomShapeSizes.ContainsKey(key)&&CustomShapeSizes.Count>=32)CustomShapeSizes.Remove(CustomShapeSizes.Keys.First());
            CustomShapeSizes[key]=new[]{width,height};return true;
        }
    }
    internal static class Program
    {
        internal static readonly string Folder=AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string DataFolder=UserDataFolder();
        private static string UserDataFolder()
        {
            // In-process fixture hosts set this before loading the assembly; ordinary launches
            // always use Windows' per-user folder, without any version or installation suffix.
            string fixture=AppDomain.CurrentDomain.GetData("CodexUserData.TestDataFolder") as string;
            return String.IsNullOrEmpty(fixture)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexUserData"):Path.GetFullPath(fixture);
        }
        internal static readonly string SettingsPath=Path.Combine(DataFolder,"widget-settings.json");
        internal static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=64*1024*1024};
        internal const string WindowTitle="CodexUserData";
        internal static readonly int ShowMainMessage=(int)RegisterWindowMessage("CodexUserData.ShowMain.v1");
        private static bool saveWarningShown,recoveredSettings;
        private static readonly object settingsGate=new object();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string cls,string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h,int message,IntPtr wParam,IntPtr lParam);
        [STAThread] public static int Main(string[] args)
        {
            Mutex instance=null;
            try
            {
                if(args.Length>0&&args[0]=="--watch-codex")return CodexLaunchWatcher.Run();
                // Acquire the UI instance before migration. Launching a new version while an
                // older one is open must not copy a settings snapshot that can still change.
                bool diagnostic=args.Length>1&&new[]{"--quota-check","--check","--local-check","--preview"}.Contains(args[0]);
                if(!diagnostic)
                {
                    bool created;instance=new Mutex(true,"Local\\CodexUserData_v1",out created);
                    if(!created)
                    {
                        if(args.Contains("--codex-auto"))return 0;
                        // Let the existing process restore its own state. Native ShowWindow alone
                        // could expose the main window while leaving an active floating ball behind.
                        IntPtr h=IntPtr.Zero;for(int attempt=0;attempt<20&&h==IntPtr.Zero;attempt++){h=FindWindow(null,WindowTitle);if(h==IntPtr.Zero)Thread.Sleep(50);}
                        if(h!=IntPtr.Zero)PostMessage(h,ShowMainMessage,IntPtr.Zero,IntPtr.Zero);return 0;
                    }
                }
                PortableStore.Initialize(DataFolder,Path.Combine(Folder,"data"));Preferences p=ReadPreferences();ApiPrices.Configure(p.PriceOverrides);
                if(args.Length>1 && args[0]=="--quota-check")
                {
                    var quotas=QuotaReader.Query(args.Length>2?args[2]:p.QuotaCli,p.CodexHome).GetAwaiter().GetResult();
                    File.WriteAllText(args[1],Json.Serialize(quotas),new UTF8Encoding(false));return 0;
                }
                if(args.Length>1 && (args[0]=="--check" || args[0]=="--local-check"))
                {
                    var report=new Dictionary<string,object>();
                    LocalCodexUsage local=args[0]=="--local-check"?new LocalCodexUsage(args.Length>2?args[2]:p.CodexHome,Path.Combine(DataFolder,"local-codex-cache.json.gz")):null;
                    foreach(string range in new[]{"today","week","month","all"}) report[range]=local==null?UsageDatabase.Read(args.Length>2?args[2]:p.Database,range,"",DateTime.Now):local.Read(range,DateTime.Now,null,null);
                    if(local!=null)report["warmBytesRead"]=local.LastBytesRead;
                    File.WriteAllText(args[1],Json.Serialize(report),new UTF8Encoding(false));return 0;
                }
                if(args.Length>1 && args[0]=="--preview")
                {
                    p.Collapsed=args.Contains("collapsed");p.Opacity=1;
                    if(args.Length>3){double size;if(Double.TryParse(args[2],out size))p.Width=size;if(Double.TryParse(args[3],out size))p.Height=size;}
                    p.Source="ccswitch";var w=new WidgetWindow(p,true);
                    w.ApplySnapshot(UsageDatabase.Read(p.Database,p.Range,p.App,DateTime.Now));w.SavePreview(args[1]);return 0;
                }
                    if(p.StartWithWindows||p.StartWithCodex)try{StartupRegistration.Configure(p,Path.Combine(Folder,"CodexUserData.exe"));if(p.StartWithCodex)CodexLaunchWatcher.Ensure();}catch(Exception){MessageBox.Show("无法更新 Windows 自启动项。软件仍可使用，请在设置中重新保存自启动选项。",WindowTitle,MessageBoxButton.OK,MessageBoxImage.Information);}
                    var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};var main=new WidgetWindow(p,false);
                    main.Loaded+=delegate{if(p.LiveQuota&&!File.Exists(p.QuotaCli)&&!p.CliNoticeShown){p.CliNoticeShown=true;Save(p);MessageBox.Show("未找到 Codex CLI 可执行文件。请在设置中点击自动检测，或手动选择 codex.exe。本地用量统计仍可读取日志；在线额度需要可用且已登录的 CLI。",WindowTitle,MessageBoxButton.OK,MessageBoxImage.Information);}};
                    app.Run(main);
                return 0;
            }
            catch(Exception error)
            {
                if(args.Length>1 && args[0].StartsWith("--"))File.WriteAllText(args[1]+".error.json",Json.Serialize(new{error=error.Message}),new UTF8Encoding(false));
                else MessageBox.Show(error.Message,WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);
                return 1;
            }
            finally{if(instance!=null)instance.Dispose();}
        }
        internal static Preferences ReadPreferences()
        {
            lock(settingsGate)
            {
                recoveredSettings=false;Exception error=null;Preferences result;
                if(TryPreferences(SettingsPath,out result,out error))return result;
                Exception backupError;
                if(TryPreferences(SettingsPath+".bak",out result,out backupError))
                {
                    // Do not replace files merely by reading settings. The next successful save
                    // preserves the damaged primary separately instead of overwriting the good backup.
                    recoveredSettings=true;return result;
                }
                if(error!=null||backupError!=null)throw new IOException("设置及备份均无法读取，已保留原文件。请检查用户数据目录中的 widget-settings.json 和 .bak 备份。",error??backupError);
                return new Preferences();
            }
        }
        private static bool TryPreferences(string path,out Preferences value,out Exception error)
        {
            value=null;error=null;
            try
            {
                if(!File.Exists(path))return false;
                value=Json.Deserialize<Preferences>(File.ReadAllText(path,Encoding.UTF8));
                if(value==null)throw new IOException("Empty settings");value.Validate();return true;
            }
            catch(IOException ex){error=ex;}catch(UnauthorizedAccessException ex){error=ex;}
            catch(ArgumentException ex){error=ex;}catch(InvalidOperationException ex){error=ex;}
            return false;
        }
        internal static void Save(Preferences p)
        {
            lock(settingsGate)
            {
                string temp=SettingsPath+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    Directory.CreateDirectory(DataFolder);byte[] bytes=new UTF8Encoding(false).GetBytes(Json.Serialize(p));
                    using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                    if(File.Exists(SettingsPath))File.Replace(temp,SettingsPath,SettingsPath+(recoveredSettings?".corrupt":".bak"));else File.Move(temp,SettingsPath);
                    recoveredSettings=false;saveWarningShown=false;
                }
                catch(IOException){SaveWarning();}catch(UnauthorizedAccessException){SaveWarning();}
                finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
            }
        }
        private static void SaveWarning(){if(saveWarningShown)return;saveWarningShown=true;MessageBox.Show("设置暂未保存成功。请检查用户数据目录的写入权限和可用空间；已有设置文件会保留。",WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
