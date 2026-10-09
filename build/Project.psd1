@{
    # Groups document ownership without changing the existing flat source paths.
    # New application files must be reviewed and listed here before compilation.
    SourceGroups = @(
        @{ Name = 'Application'; Files = @(
            'src/Program.cs', 'src/Preferences.cs', 'src/SettingsStore.cs',
            'src/ApplicationStartup.cs', 'src/PortableServices.cs',
            'src/StartupAssets.cs', 'src/StartupSplash.cs',
            'src/StartupChime.cs', 'src/StartupUsagePreload.cs',
            'src/ReleaseUpdate.cs', 'src/Diagnostics.cs'
        ) }
        @{ Name = 'UsageAndRemote'; Files = @(
            'src/ActivityJsonLine.cs', 'src/CodexActivity.cs', 'src/LocalCodexUsage.cs',
            'src/LocalUsageSession.cs',
            'src/UsageDatabase.cs', 'src/RemoteCodex.cs', 'src/RemoteMonitor.cs',
            'src/RemoteSettings.cs'
        ) }
        @{ Name = 'Quota'; Files = @(
            'src/QuotaReader.cs', 'src/QuotaStatus.cs', 'src/QuotaHistory.cs',
            'src/QuotaHistoryChart.cs', 'src/QuotaOrb.cs'
        ) }
        @{ Name = 'PricingAndModels'; Files = @(
            'src/PriceCatalog.cs', 'src/OfficialPrices.cs', 'src/PriceEditor.cs',
            'src/ModelPaletteCatalog.cs', 'src/ModelColors.cs',
            'src/ModelSharePanel.cs', 'src/ModelShareChart.cs'
        ) }
        @{ Name = 'ChartsAndMilestones'; Files = @(
            'src/Dashboard.cs', 'src/UsageCharts.cs', 'src/UsageDetails.cs',
            'src/Milestones.cs', 'src/MilestonePanel.cs', 'src/MilestoneCurve.cs'
        ) }
        @{ Name = 'WindowsAndShapes'; Files = @(
            'src/Widget.cs', 'src/SettingsWindow.cs', 'src/TrayFlyout.cs',
            'src/FloatingBall.cs', 'src/DynamicIsland.cs', 'src/DynamicIsland.Glass.cs',
            'src/DockTransition.cs', 'src/WindowInteraction.cs',
            'src/CustomShape.cs', 'src/OrbAppearance.cs'
        ) }
        @{ Name = 'Theme'; Files = @(
            'src/Theme.cs', 'src/ThemePalette.cs', 'src/ThemeEditor.cs'
        ) }
    )
    FrameworkReferences = @(
        'WPF/PresentationCore.dll', 'WPF/PresentationFramework.dll',
        'WPF/WindowsBase.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll',
        'System.IO.Compression.dll', 'System.Windows.Forms.dll',
        'System.Drawing.dll', 'System.Security.dll'
    )
    ManagedLibraries = @(
        'vendor/webview2/lib/net462/Microsoft.Web.WebView2.Core.dll',
        'vendor/webview2/lib/net462/Microsoft.Web.WebView2.Wpf.dll'
    )
    NativeLibraries = @('vendor/webview2/runtimes/win-x64/native/WebView2Loader.dll')
    RemotePackages = 'tools/remote-packages.json'
    # Production resources are an explicit allowlist, shared by source probes.
    # This is not the public ZIP allowlist: pack.ps1 keeps that privacy boundary.
    Resources = @(
        @{ Path = 'model-palette.json'; Name = 'CodexUserData.ModelPalette.json' }
        @{ Path = 'assets/startup/index.html'; Name = 'CodexUserData.Startup.index.html' }
        @{ Path = 'assets/startup/cinema.webm'; Name = 'CodexUserData.Startup.cinema.webm' }
        @{ Path = 'assets/startup/layers.webm'; Name = 'CodexUserData.Startup.layers.webm' }
        @{ Path = 'assets/startup/matrix.webm'; Name = 'CodexUserData.Startup.matrix.webm' }
        @{ Path = 'assets/startup/prism.webm'; Name = 'CodexUserData.Startup.prism.webm' }
        @{ Path = 'assets/startup/cinema.png'; Name = 'CodexUserData.Startup.cinema.png' }
        @{ Path = 'assets/startup/layers.png'; Name = 'CodexUserData.Startup.layers.png' }
        @{ Path = 'assets/startup/matrix.png'; Name = 'CodexUserData.Startup.matrix.png' }
        @{ Path = 'assets/startup/prism.png'; Name = 'CodexUserData.Startup.prism.png' }
        @{ Path = 'assets/startup/chime.wav'; Name = 'CodexUserData.Startup.chime.wav' }
    )
}
