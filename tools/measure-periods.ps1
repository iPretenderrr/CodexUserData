param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\period-performance'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$out=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Security.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@(Get-ChildItem (Join-Path $root 'vendor/ssh/*.dll'),(Join-Path $root 'vendor/webview2/lib/net462/*.dll') | ForEach-Object {'/r:'+$_.FullName})
$sources=@(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object {$_.FullName})
$binary=Join-Path $out 'PeriodPerformanceProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.PeriodPerformanceProbe ('/out:'+$binary) @refs @sources (Join-Path $PSScriptRoot 'PeriodPerformanceProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Performance probe compilation failed'}
# Input is generated in .build and contains only simulated quota measurements.
& $binary (Join-Path $out 'synthetic-quota')
if($LASTEXITCODE -ne 0){throw 'Performance measurement failed'}
