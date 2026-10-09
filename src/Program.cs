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
[assembly: System.Reflection.AssemblyVersion("1.9.18.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.9.18.0")]

namespace CodexUserData
{
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
        private static readonly SettingsStore settings=new SettingsStore(SettingsPath,SaveWarning);
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
                PortableStore.Initialize(DataFolder,Path.Combine(Folder,"data"));
                Preferences p=ReadPreferences();
                // Diagnostic commands retain their synchronous, UI-free initialization.
                if(diagnostic)ApplicationStartup.InitializeCatalogs(p);
                if(args.Length>1 && args[0]=="--quota-check")
                {
                    var quotas=QuotaReader.Query(args.Length>2?args[2]:ApplicationStartup.ResolveQuotaCli(p.QuotaCli),p.CodexHome).GetAwaiter().GetResult();
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
                return ApplicationStartup.Run(p);
            }
            catch(Exception error)
            {
                if(args.Length>1 && args[0].StartsWith("--"))File.WriteAllText(args[1]+".error.json",Json.Serialize(new{error=error.Message}),new UTF8Encoding(false));
                else MessageBox.Show(error.Message,WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);
                return 1;
            }
            finally{if(instance!=null)instance.Dispose();}
        }
        // Stable facade for existing views; persistence and recovery have one owner.
        internal static Preferences ReadPreferences(){return settings.Read();}
        internal static void Save(Preferences p){settings.Save(p);}
        private static void SaveWarning()
        {MessageBox.Show("设置暂未保存成功。请检查用户数据目录的写入权限和可用空间；已有设置文件会保留。",WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
