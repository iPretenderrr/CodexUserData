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
[assembly: System.Reflection.AssemblyVersion("1.6.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.6.2.0")]

namespace CodexUserData
{
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
        public string[] OrbShortColors {get;set;} public string[] OrbLongColors {get;set;}
        public double OrbShortAngle {get;set;} public double OrbLongAngle {get;set;}
        public bool StartWithWindows {get;set;} public bool StartWithCodex {get;set;}
        public double BallOpacity {get;set;} public bool OrbFollowTheme {get;set;}
        public string CustomShape {get;set;}
        public bool CompletionFlash {get;set;}
        public bool CliNoticeShown {get;set;}
        public bool MinimizeToTray {get;set;} // Missing in older settings defaults to taskbar (false).
        public double BallLeft {get;set;} public double BallTop {get;set;} public string BallDock {get;set;}
        public string ThemeMode {get;set;} public string ThemeBase {get;set;} public string GradientKind {get;set;} public string GradientSpread {get;set;}
        public double GradientSpan {get;set;}
        public string[] GradientColors {get;set;} public double[] GradientStops {get;set;}
        public double GradientAngle {get;set;} public double GradientCenterX {get;set;} public double GradientCenterY {get;set;} public double GradientRadius {get;set;}
        public double GradientStrength {get;set;} public double ThemeCardOpacity {get;set;}
        public Preferences()
        {
            string user=Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            BallLeft=Double.NaN;BallTop=Double.NaN;BallDock="";
            BallOpacity=1;OrbFollowTheme=true;CompletionFlash=true;BallStyle="orb";OrbQuotaWindow="auto";OrbAnimation="auto";OrbSize=84;
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
            OrbPalette.Normalize(this);
            if(BallStyle!="capsule"&&BallStyle!="html")BallStyle="orb";
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
        private static bool saveWarningShown;
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string cls,string name);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int n);
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
                    if(!created){if(args.Contains("--codex-auto"))return 0;IntPtr h=FindWindow(null,WindowTitle);if(h!=IntPtr.Zero){ShowWindow(h,9);SetForegroundWindow(h);}return 0;}
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
            try{if(File.Exists(SettingsPath)){var p=Json.Deserialize<Preferences>(File.ReadAllText(SettingsPath,Encoding.UTF8));if(p==null)throw new IOException("Empty settings");p.Validate();return p;}}catch(Exception ex){throw new IOException("设置文件无法读取，已保留原文件。请先备份用户数据目录，并检查 widget-settings.json 或其 .bak 备份。",ex);}
            return new Preferences();
        }
        internal static void Save(Preferences p)
        {
            string temp=SettingsPath+".tmp";
            try{Directory.CreateDirectory(DataFolder);File.WriteAllText(temp,Json.Serialize(p),new UTF8Encoding(false));if(File.Exists(SettingsPath))File.Replace(temp,SettingsPath,SettingsPath+".bak");else File.Move(temp,SettingsPath);saveWarningShown=false;}catch(IOException){SaveWarning();}catch(UnauthorizedAccessException){SaveWarning();}
        }
        private static void SaveWarning(){if(saveWarningShown)return;saveWarningShown=true;MessageBox.Show("设置暂未保存成功。请检查用户数据目录的写入权限和可用空间；已有设置文件会保留。",WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
