param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\period-performance'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$out=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) | Out-Null
. (Join-Path $root 'build/Project.ps1')
$project=Get-CodexProject -Root $root
$framework=$project.Framework
$refs=@($project.ReferenceArguments)+@($project.ResourceArguments)
Copy-CodexRuntimeFiles -Project $project -Destination $out
$sources=@($project.Sources)
$binary=Join-Path $out 'PeriodPerformanceProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.PeriodPerformanceProbe ('/out:'+$binary) @refs @sources (Join-Path $PSScriptRoot 'PeriodPerformanceProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Performance probe compilation failed'}
# Input is generated in .build and contains only simulated quota measurements.
& $binary (Join-Path $out 'synthetic-quota')
if($LASTEXITCODE -ne 0){throw 'Performance measurement failed'}
