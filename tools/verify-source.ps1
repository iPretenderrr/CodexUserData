param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.build\source-verification'),[switch]$ArchitectureOnly,[switch]$IslandOnly,[switch]$QuotaOnly,[switch]$FloatingOnly,[switch]$HtmlOnly,[switch]$RemoteOnly,[switch]$PeriodOnly,[switch]$PriceOnly,[switch]$MilestoneOnly,[switch]$ModelShareOnly,[switch]$ColorOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
. (Join-Path $root 'build/Project.ps1')
$project=Get-CodexProject -Root $root
$framework=$project.Framework
$refs=@($project.ReferenceArguments)+@($project.ResourceArguments)
Copy-CodexRuntimeFiles -Project $project -Destination $destination
$binary=Join-Path $destination 'StabilityProbe.exe'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'FakeQuotaHelper.cs')){
 & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 ('/out:'+(Join-Path $destination 'FakeQuotaHelper.exe')) (Join-Path $PSScriptRoot 'FakeQuotaHelper.cs')
 if($LASTEXITCODE -ne 0){throw 'Fixture helper compilation failed'}
}
Copy-Item -LiteralPath (Join-Path $root 'CodexUserData.exe.config') -Destination ($binary+'.config') -Force
$sources=@($project.Sources)
$sources+=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*StabilityProbe.cs' | ForEach-Object {$_.FullName})
# Source checks avoid re-obfuscating the application for each small regression fix.
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:CodexUserData.StabilityProbe ('/out:'+$binary) @refs @sources
if($LASTEXITCODE -ne 0){throw 'Source regression compilation failed'}
if($ArchitectureOnly){& $binary $destination --architecture-only}elseif($ColorOnly){& $binary $destination --color-only}elseif($ModelShareOnly){& $binary $destination --model-share-only}elseif($MilestoneOnly){& $binary $destination --milestone-only}elseif($PriceOnly){& $binary $destination --price-only}elseif($PeriodOnly){& $binary $destination --period-only}elseif($RemoteOnly){& $binary $destination --remote-only}elseif($HtmlOnly){& $binary $destination --html-only}elseif($FloatingOnly){& $binary $destination --floating-only}elseif($QuotaOnly){& $binary $destination --quota-only}elseif($IslandOnly){& $binary $destination --island-only}else{& $binary $destination}
if($LASTEXITCODE -ne 0){throw 'Source regression failed'}
