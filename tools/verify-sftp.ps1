param([Parameter(Mandatory=$true)][string]$Python)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$run=Join-Path $root ('.build/sftp-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
. (Join-Path $root 'build/Project.ps1')
$project=Get-CodexProject -Root $root
$framework=$project.Framework
$refs=@($project.ReferenceArguments)+@($project.ResourceArguments)
Copy-CodexRuntimeFiles -Project $project -Destination $run
$binary=Join-Path $run 'SftpTransportProbe.exe'
$sources=@($project.Sources)
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
