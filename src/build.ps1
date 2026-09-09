param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CodexUserData.exe'))
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.8 C# compiler not found.' }

if(-not(Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\vendor\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll'))){throw 'Run restore-dependencies.ps1 from the repository root first.'}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null

# 图标直接绘制成小型本地资源，不下载图片或额外包。
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(21,25,30))
$iconBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(130,180,232))
$graphics.FillRectangle($iconBrush,14,34,8,16)
$graphics.FillRectangle($iconBrush,28,25,8,25)
$graphics.FillRectangle($iconBrush,42,14,8,36)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconStream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'widget.ico'))
try { $icon.Save($iconStream) } finally { $iconStream.Dispose(); $icon.Dispose(); $iconBrush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }

$compilerArgs = @(
  '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
  ('/out:' + $OutputPath),
  ('/win32manifest:' + (Join-Path $PSScriptRoot 'widget.manifest')),
  ('/win32icon:' + (Join-Path $PSScriptRoot 'widget.ico')),
  ('/r:' + (Join-Path $frameworkPath 'WPF\PresentationCore.dll')),
  ('/r:' + (Join-Path $frameworkPath 'WPF\PresentationFramework.dll')),
  ('/r:' + (Join-Path $frameworkPath 'WPF\WindowsBase.dll')),
  ('/r:' + (Join-Path $frameworkPath 'System.Xaml.dll')),
  ('/r:' + (Join-Path $frameworkPath 'System.Web.Extensions.dll')),
  ('/r:' + (Join-Path $frameworkPath 'System.IO.Compression.dll')),
  ('/r:' + (Join-Path $frameworkPath 'System.Windows.Forms.dll')),
  ('/r:' + (Join-Path $frameworkPath 'System.Drawing.dll')),
  ('/r:' + (Join-Path $PSScriptRoot '..\vendor\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll')),
  ('/r:' + (Join-Path $PSScriptRoot '..\vendor\webview2\lib\net462\Microsoft.Web.WebView2.Wpf.dll')),
  (Join-Path $PSScriptRoot 'CustomShape.cs'), (Join-Path $PSScriptRoot 'PortableServices.cs'),
  (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'Theme.cs'),
  (Join-Path $PSScriptRoot 'QuotaOrb.cs'),
  (Join-Path $PSScriptRoot 'CodexActivity.cs'), (Join-Path $PSScriptRoot 'ActivityJsonLine.cs'), (Join-Path $PSScriptRoot 'OrbAppearance.cs'),
  (Join-Path $PSScriptRoot 'ThemePalette.cs'), (Join-Path $PSScriptRoot 'ThemeEditor.cs'),
  (Join-Path $PSScriptRoot 'Widget.cs'), (Join-Path $PSScriptRoot 'SettingsWindow.cs'),
  (Join-Path $PSScriptRoot 'LocalCodexUsage.cs'), (Join-Path $PSScriptRoot 'UsageDatabase.cs')
  (Join-Path $PSScriptRoot 'UsageCharts.cs')
  (Join-Path $PSScriptRoot 'Dashboard.cs'), (Join-Path $PSScriptRoot 'QuotaReader.cs'), (Join-Path $PSScriptRoot 'QuotaStatus.cs')
  (Join-Path $PSScriptRoot 'FloatingBall.cs'), (Join-Path $PSScriptRoot 'WindowInteraction.cs'), (Join-Path $PSScriptRoot 'PriceEditor.cs'), (Join-Path $PSScriptRoot 'UsageDetails.cs'), (Join-Path $PSScriptRoot 'TrayFlyout.cs')
)
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Widget compilation failed.' }
$destination=Split-Path $OutputPath -Parent
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('..\vendor\webview2\lib\net462\'+$name)) -Destination $destination -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\vendor\webview2\runtimes\win-x64\native\WebView2Loader.dll') -Destination $destination -Force
if([IO.Path]::GetFullPath($OutputPath+'.config') -ne [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\CodexUserData.exe.config'))){Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\CodexUserData.exe.config') -Destination ($OutputPath+'.config') -Force}
Write-Output ('Built: ' + $OutputPath)
