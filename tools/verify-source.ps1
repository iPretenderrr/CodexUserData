param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\source-verification'),[switch]$IslandOnly,[switch]$QuotaOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$web=Join-Path $root 'vendor\webview2'
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')){
 $path=Join-Path $web ('lib\net462\'+$name);$refs+=('/r:'+$path)
 Copy-Item -LiteralPath $path -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $web 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $destination -Force
$binary=Join-Path $destination 'StabilityProbe.exe'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'FakeQuotaHelper.cs')){
 & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+(Join-Path $destination 'FakeQuotaHelper.exe')) (Join-Path $PSScriptRoot 'FakeQuotaHelper.cs')
 if($LASTEXITCODE -ne 0){throw 'Fixture helper compilation failed'}
}
Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config') -Force
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object {$_.FullName})
$sources+=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*StabilityProbe.cs' | ForEach-Object {$_.FullName})
# Source checks avoid re-obfuscating the application for each small regression fix.
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.StabilityProbe ('/out:'+$binary) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Source regression compilation failed'}
if($QuotaOnly){& $binary $destination --quota-only}elseif($IslandOnly){& $binary $destination --island-only}else{& $binary $destination}
if($LASTEXITCODE -ne 0){throw 'Source regression failed'}
