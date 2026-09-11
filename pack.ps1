param([string]$Version='1.8.2')
# Keep this script UTF-8 with BOM: Windows PowerShell 5.1 must decode the Chinese allowlist paths correctly.
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Version must be major.minor.patch'}
$root=[IO.Path]::GetFullPath($PSScriptRoot)
& (Join-Path $root 'restore-dependencies.ps1')
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$package=Join-Path $root 'tools\obfuscar.2.2.50.nupkg'
$packageHash='8790E1E36D613613311EC512129A6701C63576428692F20A95988A351FDC3187'
# Fixed, locally cached tool. Never silently produce an unprotected fallback package.
if(-not(Test-Path -LiteralPath $package)){throw 'Pinned Obfuscar package missing from tools; restore the developer tools before packaging.'}
if((Get-FileHash -LiteralPath $package).Hash -ne $packageHash){throw 'Obfuscar package checksum mismatch'}
$webPackage=Join-Path $root 'vendor\webview2.1.0.3296.44.nupkg'
if((Get-FileHash -LiteralPath $webPackage).Hash -ne '5B1B19C266DDFF33C837ED7965A95BAC3DAE69B48B5B27F26E5D69F161E9F534'){throw 'WebView2 SDK checksum mismatch'}
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$run=Join-Path $root ('.build\protected-'+[Guid]::NewGuid().ToString('N'))
$inputDir=Join-Path $run 'input';$protected=Join-Path $run 'protected';$toolDir=Join-Path $run 'obfuscar'
New-Item -ItemType Directory -Path $inputDir,$protected -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($package,$toolDir)
& (Join-Path $root 'src\build.ps1') -OutputPath (Join-Path $inputDir 'CodexUserData.exe')
if(-not(Test-Path -LiteralPath (Join-Path $inputDir 'CodexUserData.exe'))){throw 'Compilation failed'}
$assemblyVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $inputDir 'CodexUserData.exe')).Version.ToString(3)
if($assemblyVersion -ne $Version){throw "Package version $Version does not match compiled application $assemblyVersion. Update the assembly version before packaging."}
$config=[IO.File]::ReadAllText((Join-Path $root 'obfuscation.rules.xml'))
foreach($pair in @(@('@INPUT@',$inputDir),@('@OUTPUT@',$protected),@('@FRAMEWORK@',$framework),@('@ASSEMBLY@',(Join-Path $inputDir 'CodexUserData.exe')))){$config=$config.Replace($pair[0],[Security.SecurityElement]::Escape($pair[1]))}
$configPath=Join-Path $run 'obfuscar.xml';[IO.File]::WriteAllText($configPath,$config)
& (Join-Path $toolDir 'tools\Obfuscar.Console.exe') $configPath
if($LASTEXITCODE -ne 0){throw 'Obfuscation failed; no ZIP was generated'}
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll')){Copy-Item -LiteralPath (Join-Path $inputDir $name) -Destination $protected -Force}
$binary=Join-Path $protected 'CodexUserData.exe';$map=Join-Path $protected 'Mapping.txt'
if(-not(Test-Path -LiteralPath $binary) -or -not(Test-Path -LiteralPath $map)){throw 'Obfuscation output missing'}
if((Get-FileHash -LiteralPath $binary).Hash -eq (Get-FileHash -LiteralPath (Join-Path $inputDir 'CodexUserData.exe')).Hash){throw 'Obfuscator did not transform the binary'}
$refs=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$probe=Join-Path $run 'ReleaseProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 ('/out:'+$probe) @refs (Join-Path $root 'tools\ReleaseProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Release verifier compilation failed'}
& $probe $binary $map (Join-Path $run 'verification')
if($LASTEXITCODE -ne 0){throw 'Protected binary verification failed; no ZIP was generated'}
# HTML uses its own renderer process: smoke-test the protected bridge and host-owned menu.
$htmlRefs=@($refs)+@(('/r:'+(Join-Path $protected 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $protected 'Microsoft.Web.WebView2.Wpf.dll')))
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll')){Copy-Item -LiteralPath (Join-Path $protected $name) -Destination $run -Force}
$htmlProbe=Join-Path $run 'HtmlReleaseProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 ('/out:'+$htmlProbe) @htmlRefs (Join-Path $root 'tools\HtmlReleaseProbe.cs')
if($LASTEXITCODE -ne 0){throw 'HTML release verifier compilation failed'}
& $htmlProbe $binary $map (Join-Path $run 'verification')
if($LASTEXITCODE -ne 0){throw 'Protected HTML verification failed; install WebView2 Runtime and check the browser host before publishing'}
# Product-only allowlist. Source, mapping files, PDBs, tools and private data cannot enter this archive.
$files=[ordered]@{'CodexUserData.exe'=$binary;'CodexUserData.exe.config'=(Join-Path $root 'CodexUserData.exe.config');'README.md'=(Join-Path $root 'README-release.md');'THIRD-PARTY-NOTICES.txt'=(Join-Path $root 'THIRD-PARTY-NOTICES.txt')}
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll')){$files[$name]=Join-Path $protected $name}
$files['LICENSE']=Join-Path $root 'LICENSE'
foreach($name in @('docs/HTML形态接口.md','docs/CodexUserData-使用说明书.html','docs/CodexUserData-使用说明书.pdf','examples/aurora/shape.json','examples/aurora/index.html','examples/aurora/codexuserdata.js')){$files[$name]=Join-Path $root $name}
$dist=Join-Path $root 'dist';New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zipPath=Join-Path $dist ('CodexUserData-'+$Version+'-win-x64-protected.zip');$temporary=Join-Path $run 'release.zip'
$archive=[IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($entry in $files.GetEnumerator()){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$entry.Value,('CodexUserData/'+$entry.Key),[IO.Compression.CompressionLevel]::Optimal) | Out-Null}}finally{$archive.Dispose()}
$check=[IO.Compression.ZipFile]::OpenRead($temporary)
try{
 if($check.Entries.Count -ne $files.Count){throw 'Unexpected archive content'}
 foreach($entry in $check.Entries){if($entry.FullName -notin @($files.Keys | ForEach-Object {'CodexUserData/'+$_})){throw 'Unexpected archive entry'}}
}finally{$check.Dispose()}
Copy-Item -LiteralPath $temporary -Destination $zipPath -Force
$hash=(Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zipPath+'.sha256'),$hash+'  '+[IO.Path]::GetFileName($zipPath)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Write-Output ('Share only: '+$zipPath)
Write-Output ('SHA256: '+$hash)
Write-Output ('Private build/mapping files remain in: '+$run)
