# Shared build inputs only. Entry scripts retain their output and probe behavior.
function Get-CodexProject {
    param([Parameter(Mandatory=$true)][string]$Root, [switch]$ValidateSourceSet)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $manifest = Import-PowerShellDataFile -LiteralPath (Join-Path $rootPath 'build/Project.psd1')
    $framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw '.NET Framework 4.8 C# compiler not found.'
    }

    $sources = @($manifest.SourceGroups | ForEach-Object {
        foreach ($relative in $_.Files) { Join-Path $rootPath $relative }
    })
    if (@($sources | Select-Object -Unique).Count -ne $sources.Count) {
        throw 'Duplicate application source in build/Project.psd1.'
    }
    if ($ValidateSourceSet) {
        # Enumeration is for consistency checking, never automatic inclusion.
        $actual = @(Get-ChildItem -LiteralPath (Join-Path $rootPath 'src') -Filter '*.cs' -Recurse | ForEach-Object FullName)
        $listed = @($sources | ForEach-Object { [IO.Path]::GetFullPath($_) })
        $difference = @(Compare-Object -ReferenceObject $listed -DifferenceObject $actual)
        if ($difference.Count -gt 0) {
            throw ('Source manifest differs from src: ' + (($difference | ForEach-Object InputObject) -join ', '))
        }
    }

    $managed = @($manifest.ManagedLibraries | ForEach-Object { Join-Path $rootPath $_ })
    # The pinned dependency manifest supplies exact names; stale DLLs do not join
    # compilation or distribution merely by being present in vendor/ssh.
    $packages = Get-Content -LiteralPath (Join-Path $rootPath $manifest.RemotePackages) -Raw -Encoding UTF8 | ConvertFrom-Json
    $managed += @($packages | ForEach-Object {
        foreach ($asset in $_.files) { Join-Path $rootPath ('vendor/ssh/' + [IO.Path]::GetFileName($asset)) }
    })
    $runtime = @($managed) + @($manifest.NativeLibraries | ForEach-Object { Join-Path $rootPath $_ })
    $references = @($manifest.FrameworkReferences | ForEach-Object { Join-Path $framework $_ }) + $managed
    $resources = @($manifest.Resources | ForEach-Object { Join-Path $rootPath $_.Path })
    foreach ($path in @($sources) + $references + $runtime + $resources) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw ('Build input missing: ' + $path + '. Check the manifest or run restore-dependencies.ps1.')
        }
    }
    [PSCustomObject]@{
        Root = $rootPath
        Framework = $framework
        Compiler = $compiler
        Sources = $sources
        ReferenceArguments = @($references | ForEach-Object { '/r:' + $_ })
        ResourceArguments = @($manifest.Resources | ForEach-Object { '/resource:' + (Join-Path $rootPath $_.Path) + ',' + $_.Name })
        RuntimeFiles = $runtime
        SourceGroups = $manifest.SourceGroups
    }
}

function Copy-CodexRuntimeFiles {
    param([Parameter(Mandatory=$true)]$Project, [Parameter(Mandatory=$true)][string]$Destination)
    foreach ($path in $Project.RuntimeFiles) {
        Copy-Item -LiteralPath $path -Destination $Destination -Force
    }
}
