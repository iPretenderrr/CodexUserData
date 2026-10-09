param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\startup-verification'),[string]$ProtectedBinary,[switch]$HandoffOnly,[switch]$PreloadOnly,[switch]$FormsOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
. (Join-Path $root 'build/Project.ps1')
$project=Get-CodexProject -Root $root
$framework=$project.Framework
$refs=@($project.ReferenceArguments)+@($project.ResourceArguments)
Copy-CodexRuntimeFiles -Project $project -Destination $destination
if($ProtectedBinary){
 $binary=Join-Path $destination 'ProtectedStartupProbe.exe'
 & $project.Compiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+$binary) @($project.ReferenceArguments) (Join-Path $PSScriptRoot 'ProtectedStartupProbe.cs')
 if($LASTEXITCODE -ne 0){throw 'Protected startup verifier compilation failed'}
 Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config') -Force
 & $binary ([IO.Path]::GetFullPath($ProtectedBinary)) (Join-Path $destination 'protected-fixture-data')
 if($LASTEXITCODE -ne 0){throw 'Protected startup verification failed'}
 return
}
$binary=Join-Path $destination 'StartupStabilityProbe.exe'
Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config') -Force
$sources=@($project.Sources)
$sources+=Join-Path $PSScriptRoot 'StartupStabilityProbe.cs'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.StartupStabilityProbe ('/out:'+$binary) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Startup verifier compilation failed'}
if($FormsOnly){
 & $binary (Join-Path $destination 'fixture-data') --forms-only
 if($LASTEXITCODE -ne 0){throw 'Startup forms verification failed'}
 return
}
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
