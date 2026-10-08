param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\startup-verification'),[switch]$HandoffOnly,[switch]$PreloadOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Security.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')){
 $path=Join-Path $root ('vendor/webview2/lib/net462/'+$name)
 $refs+=('/r:'+$path);Copy-Item -LiteralPath $path -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $root 'vendor/webview2/runtimes/win-x64/native/WebView2Loader.dll') -Destination $destination -Force
foreach($dll in Get-ChildItem -LiteralPath (Join-Path $root 'vendor/ssh') -Filter '*.dll'){
 $refs+=('/r:'+$dll.FullName);Copy-Item -LiteralPath $dll.FullName -Destination $destination -Force
}
$refs+=('/resource:'+(Join-Path $root 'model-palette.json')+',CodexUserData.ModelPalette.json')
foreach($name in @('index.html','cinema.webm','layers.webm','matrix.webm','prism.webm','chime.wav')){
 $refs+=('/resource:'+(Join-Path $root ('assets/startup/'+$name))+',CodexUserData.Startup.'+$name)
}
$binary=Join-Path $destination 'StartupStabilityProbe.exe'
Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config') -Force
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object {$_.FullName})
$sources+=Join-Path $PSScriptRoot 'StartupStabilityProbe.cs'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.StartupStabilityProbe ('/out:'+$binary) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Startup verifier compilation failed'}
if($PreloadOnly){
 & $binary (Join-Path $destination 'fixture-data') --preload-only
 if($LASTEXITCODE -ne 0){throw 'Startup preload verification failed'}
 return
}
if(-not $HandoffOnly){
 & $binary (Join-Path $destination 'fixture-data')
 if($LASTEXITCODE -ne 0){throw 'Startup verification failed'}
}
# A fresh process/profile models a real launch, rather than immediately reusing
# the controller whose intentionally broken media was just disposed above.
& $binary (Join-Path $destination 'handoff-fixture-data') --handoff-only
if($LASTEXITCODE -ne 0){throw 'Startup handoff verification failed'}
