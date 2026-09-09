using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CodexUserData
{
    internal static class PortableStore
    {
        // One-time return from v1.4 portable storage to the stable per-user directory.
        // Originals survive migration; newer collisions are replaced atomically with backups.
        internal static void Initialize(string target,string legacy)
        {
            Directory.CreateDirectory(target);
            string check=Path.Combine(target,".write-"+Guid.NewGuid().ToString("N"));
            try{File.WriteAllText(check,"");File.Delete(check);}
            catch(Exception ex){throw new IOException("用户数据目录无法写入。请检查当前用户 LocalAppData 下 CodexUserData 文件夹的权限与可用空间；已有数据不会被清空。",ex);}
            Directory.CreateDirectory(Path.Combine(target,"skins"));
            string marker=Path.Combine(target,"storage-user-v2.json");
            if(File.Exists(marker)||!Directory.Exists(legacy))return;
            if(String.Equals(Path.GetFullPath(target).TrimEnd('\\'),Path.GetFullPath(legacy).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return;
            string backup=Path.Combine(target,"migration-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
            // Install referenced skins before selecting the migrated settings. Browser cache is
            // regenerated instead of copying a potentially locked Chromium profile.
            CopySkins(Path.Combine(legacy,"skins"),Path.Combine(target,"skins"),Path.Combine(backup,"skins"),0);
            foreach(string name in new[]{"local-codex-cache.json.gz","widget-settings.json.bak","widget-settings.json"})
            {
                string from=Path.Combine(legacy,name),to=Path.Combine(target,name);
                if(name=="widget-settings.json"&&File.Exists(from)&&(!File.Exists(to)||File.GetLastWriteTimeUtc(from)>File.GetLastWriteTimeUtc(to)))
                {
                    // A damaged portable settings file must never replace a working user file.
                    try{if(Program.Json.Deserialize<Preferences>(File.ReadAllText(from))==null)throw new IOException();}
                    catch(Exception ex){throw new IOException("旧 data 中的设置无法读取，迁移已停止，原文件均保留。请先检查或恢复旧设置。",ex);}
                }
                CopyNewer(from,to,Path.Combine(backup,name));
            }
            File.WriteAllText(marker,"{\"version\":2}");
        }
        private static void CopyNewer(string from,string to,string backup)
        {
            if(!File.Exists(from))return;
            if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)throw new IOException("迁移不支持链接文件。");
            if(File.Exists(to)&&File.GetLastWriteTimeUtc(from)<=File.GetLastWriteTimeUtc(to))return;
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            string temporary=to+".migrate-"+Guid.NewGuid().ToString("N");File.Copy(from,temporary);
            if(File.Exists(to)){Directory.CreateDirectory(Path.GetDirectoryName(backup));File.Replace(temporary,to,backup);}else File.Move(temporary,to);
        }
        private static void CopySkins(string from,string to,string backup,int depth)
        {
            if(!Directory.Exists(from))return;
            if(depth>16||(File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)throw new IOException("自定义形态目录包含不支持的链接或过深的路径。");
            foreach(string file in Directory.GetFiles(from))CopyNewer(file,Path.Combine(to,Path.GetFileName(file)),Path.Combine(backup,Path.GetFileName(file)));
            foreach(string folder in Directory.GetDirectories(from))CopySkins(folder,Path.Combine(to,Path.GetFileName(folder)),Path.Combine(backup,Path.GetFileName(folder)),depth+1);
        }
    }
    internal static class StartupRegistration
    {
        private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static void Configure(Preferences p,string executable)
        {
            using(var key=Registry.CurrentUser.CreateSubKey(Key))ConfigureValue(key,p.StartWithWindows,p.StartWithCodex,executable);
        }
        internal static void ConfigureValue(RegistryKey key,bool windows,bool codex,string executable)
        {
            ApplyValue(key,windows&&!codex,executable);
            if(codex)key.SetValue("CodexUserData.WatchCodex","\""+Path.GetFullPath(executable)+"\" --watch-codex",RegistryValueKind.String);
            else key.DeleteValue("CodexUserData.WatchCodex",false);
        }
        internal static void ApplyValue(RegistryKey key,bool enabled,string executable)
        {
            if(enabled)key.SetValue("CodexUserData","\""+Path.GetFullPath(executable)+"\"",RegistryValueKind.String);
            else key.DeleteValue("CodexUserData",false);
        }
    }
    internal static class CodexLaunchWatcher
    {
        private static DateTime lastLaunch;
        internal static bool UiRunning(){System.Threading.Mutex mutex;if(!System.Threading.Mutex.TryOpenExisting("Local\\CodexUserData_v1",out mutex))return false;mutex.Dispose();return true;}
        internal static void Ensure()
        {
            System.Threading.Mutex mutex;if(System.Threading.Mutex.TryOpenExisting("Local\\CodexUserData_WatchCodex",out mutex)){mutex.Dispose();return;}
            if((DateTime.UtcNow-lastLaunch).TotalSeconds<5)return;lastLaunch=DateTime.UtcNow;
            using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=Path.Combine(Program.Folder,"CodexUserData.exe"),Arguments="--watch-codex",UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden})){}
        }
        internal static bool IsCodexRunning()
        {
            // No command lines, tokens or logs are read. Restrict discovery to this Windows session.
            int session;using(var current=System.Diagnostics.Process.GetCurrentProcess())session=current.SessionId;
            foreach(var process in System.Diagnostics.Process.GetProcessesByName("codex"))using(process){try{if(process.SessionId==session)return true;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
            return false;
        }
        internal static bool ShouldLaunch(bool wasRunning,bool running,bool uiRunning){return running&&!wasRunning&&!uiRunning;}
        private static string RegisteredExecutable()
        {
            // An already-running watcher follows a new installation path after an update.
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                string command=key==null?null:key.GetValue("CodexUserData.WatchCodex") as string;
                if(command!=null&&command.StartsWith("\"")){int end=command.IndexOf('"',1);if(end>1&&command.Substring(end+1)==" --watch-codex"){string path=command.Substring(1,end-1);if(File.Exists(path))return path;}}
            }
            return Path.Combine(Program.Folder,"CodexUserData.exe");
        }
        internal static int Run()
        {
            bool created;using(var mutex=new System.Threading.Mutex(true,"Local\\CodexUserData_WatchCodex",out created))
            {
                if(!created)return 0;bool seen=false;Preferences p=null;DateTime settingsStamp=DateTime.MinValue;
                while(true)
                {
                    // Read the small settings file so disabling the option stops the watcher,
                    // even when the UI has moved to a newer program directory.
                    try{var stamp=File.GetLastWriteTimeUtc(Program.SettingsPath);if(p==null||stamp!=settingsStamp){p=Program.ReadPreferences();settingsStamp=stamp;}}catch(IOException){System.Threading.Thread.Sleep(2000);continue;}
                    if(!p.StartWithCodex)return 0;
                    bool running=IsCodexRunning();if(ShouldLaunch(seen,running,UiRunning()))
                    {
                        try{using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=RegisteredExecutable(),Arguments="--codex-auto",UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden})){} }catch(System.ComponentModel.Win32Exception){}
                    }
                    seen=running;System.Threading.Thread.Sleep(2000);
                }
            }
        }
    }
}
