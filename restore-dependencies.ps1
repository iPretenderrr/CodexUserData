param([switch]$Offline)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)
Add-Type -AssemblyName System.IO.Compression.FileSystem
function HashFile([string]$path){
 $hash=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($path)
 try{return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','')}
 finally{$stream.Dispose();$hash.Dispose()}
}
function RestorePackage([string]$relative,[string]$url,[string]$sha){
 $path=Join-Path $root $relative
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
 if(Test-Path -LiteralPath $path){
  if((HashFile $path) -ne $sha){throw ('Cached package checksum mismatch: '+$relative)}
  return $path
 }
 if($Offline){throw ('Offline package missing: '+$relative)}
 # Download only pinned public NuGet packages; no local settings or credentials are sent.
 $temporary=$path+'.'+[Guid]::NewGuid().ToString('N')+'.download'
 [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
 $client=New-Object Net.WebClient
 try{
  $client.DownloadFile($url,$temporary)
  if((HashFile $temporary) -ne $sha){throw ('Downloaded package checksum mismatch: '+$relative)}
  [IO.File]::Move($temporary,$path)
 }finally{$client.Dispose();if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
 return $path
}
$web=RestorePackage 'vendor/webview2.1.0.3296.44.nupkg' 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3296.44/microsoft.web.webview2.1.0.3296.44.nupkg' '5B1B19C266DDFF33C837ED7965A95BAC3DAE69B48B5B27F26E5D69F161E9F534'
$null=RestorePackage 'tools/obfuscar.2.2.50.nupkg' 'https://api.nuget.org/v3-flatcontainer/obfuscar/2.2.50/obfuscar.2.2.50.nupkg' '8790E1E36D613613311EC512129A6701C63576428692F20A95988A351FDC3187'
$zip=[IO.Compression.ZipFile]::OpenRead($web)
try{
 # Only the three required SDK files are restored. Repeated runs keep identical files.
 foreach($name in @('lib/net462/Microsoft.Web.WebView2.Core.dll','lib/net462/Microsoft.Web.WebView2.Wpf.dll','runtimes/win-x64/native/WebView2Loader.dll')){
  $entry=$zip.GetEntry($name);if($null -eq $entry){throw ('SDK entry missing: '+$name)}
  $target=Join-Path $root ('vendor/webview2/'+$name)
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
  $temp=$target+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
  try{
   [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$temp,$false)
   if([IO.File]::Exists($target)){
    if((HashFile $temp) -ne (HashFile $target)){[IO.File]::Replace($temp,$target,$null)}
   }else{[IO.File]::Move($temp,$target)}
  }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
 }
}finally{$zip.Dispose()}
Write-Output 'Pinned dependencies verified and restored. Ready for src/build.ps1 or pack.ps1.'
