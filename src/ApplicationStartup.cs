using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace CodexUserData
{
    // Coordinates initialization, preload and window handoff. Views need neither
    // catalog-loading order nor knowledge of the temporary splash's lifetime.
    internal sealed class ApplicationStartup
    {
        private readonly Preferences preferences;
        private Task preparation;
        private volatile StartupUsagePreload preload;
        private volatile bool stopping;
        private bool registrationFailed;

        private ApplicationStartup(Preferences value){preferences=value;}

        internal static void InitializeCatalogs(Preferences value)
        {
            ModelPaletteCatalog.LoadCached();
            PriceCatalog.LoadCached();
            OfficialPriceSync.LoadCached();
            ApiPrices.Configure(value.PriceOverrides);
        }

        internal static string ResolveQuotaCli(string configured)
        {return !String.IsNullOrWhiteSpace(configured)&&File.Exists(configured)?configured:QuotaReader.DefaultExe;}

        private void Begin()
        {
            // Settings constructors/clones never enumerate PATH or extension folders.
            // Cached catalogs contain plain data; publish before constructing any view.
            preparation=Task.Run(delegate
            {
                InitializeCatalogs(preferences);
                if(stopping)return;
                if(preferences.Source=="local"||preferences.Remote.Enabled)
                {
                    preload=new StartupUsagePreload(preferences.CodexHome,Path.Combine(Program.DataFolder,"local-codex-cache.json.gz"));
                    if(stopping)preload.Cancel();
                }
                preferences.QuotaCli=ResolveQuotaCli(preferences.QuotaCli);
                if(stopping)return;
                if(preferences.StartWithWindows||preferences.StartWithCodex)
                {
                    try
                    {
                        StartupRegistration.Configure(preferences,Path.Combine(Program.Folder,"CodexUserData.exe"));
                        if(preferences.StartWithCodex)CodexLaunchWatcher.Ensure();
                    }
                    catch(Exception){registrationFailed=true;}
                }
            });
        }

        private async Task ShowMainAsync(Application app)
        {
            try
            {
                await preparation;
                if(stopping||app.Dispatcher.HasShutdownStarted)return;
                var main=new WidgetWindow(preferences,false,preload);
                app.MainWindow=main;app.ShutdownMode=ShutdownMode.OnMainWindowClose;
                await main.ShowStartupAsync();
            }
            catch(Exception error)
            {
                // Handle failures at the asynchronous handoff, with an explicit
                // exit code instead of relying on Dispatcher exception routing.
                Stop();MessageBox.Show(error.Message,Program.WindowTitle,MessageBoxButton.OK,MessageBoxImage.Warning);
                app.Shutdown(1);
            }
        }

        private void ShowNotices()
        {
            if(stopping)return;
            if(registrationFailed)MessageBox.Show("无法更新 Windows 自启动项。软件仍可使用，请在设置中重新保存自启动选项。",Program.WindowTitle,MessageBoxButton.OK,MessageBoxImage.Information);
            if(preferences.LiveQuota&&!File.Exists(preferences.QuotaCli)&&!preferences.CliNoticeShown)
            {
                preferences.CliNoticeShown=true;Program.Save(preferences);
                MessageBox.Show("未找到 Codex CLI 可执行文件。请在设置中点击自动检测，或手动选择 codex.exe。本地用量统计仍可读取日志；在线额度需要可用且已登录的 CLI。",Program.WindowTitle,MessageBoxButton.OK,MessageBoxImage.Information);
            }
        }

        private void Stop()
        {
            stopping=true;var current=preload;if(current!=null)current.Cancel();
        }

        internal static int Run(Preferences value)
        {
            // Splash never owns application lifetime. Begin disk/CLI work in the
            // background while its native poster and embedded video are presented.
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var startup=new ApplicationStartup(value);
            app.Exit+=delegate{startup.Stop();};
            app.Startup+=delegate
            {
                startup.Begin();
                StartupSplash.Launch(value,()=>startup.ShowMainAsync(app),startup.ShowNotices);
            };
            try{return app.Run();}finally{startup.Stop();}
        }
    }
}
