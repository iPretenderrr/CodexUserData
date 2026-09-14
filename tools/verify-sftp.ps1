param([Parameter(Mandatory=$true)][string]$Python)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$run=Join-Path $root ('.build/sftp-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.IO.Compression.dll','System.Security.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
foreach($dll in @(Get-ChildItem (Join-Path $root 'vendor/ssh') -Filter '*.dll')+@(Get-ChildItem (Join-Path $root 'vendor/webview2/lib/net462') -Filter '*.dll')){$refs+=('/r:'+$dll.FullName);Copy-Item -LiteralPath $dll.FullName -Destination $run}
$binary=Join-Path $run 'SftpTransportProbe.exe'
$sources=@(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object {$_.FullName})
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /main:CodexUserData.SftpTransportProbe ('/out:'+$binary) @refs @sources (Join-Path $PSScriptRoot 'SftpTransportProbe.cs')
if($LASTEXITCODE -ne 0){throw 'SFTP probe compilation failed'}
Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config')
$server=Start-Process -FilePath $Python -ArgumentList @(('"'+(Join-Path $PSScriptRoot 'sftp-fixture.py')+'"'),('"'+$run+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'server.out') -RedirectStandardError (Join-Path $run 'server.err')
try{
 $config=Join-Path $run 'connection.json';$deadline=[DateTime]::UtcNow.AddSeconds(15)
 while(-not(Test-Path -LiteralPath $config)){if($server.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Fixture did not start'};Start-Sleep -Milliseconds 100}
 & $binary $config
 if($LASTEXITCODE -ne 0){throw 'SFTP transport checks failed'}
}finally{if(-not $server.HasExited){Stop-Process -Id $server.Id}}
