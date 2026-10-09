param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CodexUserData.exe'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'build/Project.ps1')
$project = Get-CodexProject -Root $root
$compilerPath = $project.Compiler
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null

# 图标直接绘制成小型本地资源，不下载图片或额外包。
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(21,25,30))
$iconBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(130,180,232))
$graphics.FillRectangle($iconBrush,14,34,8,16)
$graphics.FillRectangle($iconBrush,28,25,8,25)
$graphics.FillRectangle($iconBrush,42,14,8,36)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconStream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'widget.ico'))
try { $icon.Save($iconStream) } finally { $iconStream.Dispose(); $icon.Dispose(); $iconBrush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }

# The reviewed manifest is also used by every source-level verification tool.
$compilerArgs = @(
  '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
  ('/out:' + $OutputPath),
  ('/win32manifest:' + (Join-Path $PSScriptRoot 'widget.manifest')),
  ('/win32icon:' + (Join-Path $PSScriptRoot 'widget.ico'))
)
$compilerArgs += @($project.ReferenceArguments) + @($project.ResourceArguments) + @($project.Sources)
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Widget compilation failed.' }
$destination=Split-Path $OutputPath -Parent
Copy-CodexRuntimeFiles -Project $project -Destination $destination
if([IO.Path]::GetFullPath($OutputPath+'.config') -ne [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\CodexUserData.exe.config'))){Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\CodexUserData.exe.config') -Destination ($OutputPath+'.config') -Force}
Write-Output ('Built: ' + $OutputPath)
