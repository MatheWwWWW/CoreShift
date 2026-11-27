[CmdletBinding()]
param(
    [string] $DotNet = 'dotnet',
    [string] $InnoCompiler,
    [ValidatePattern('^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$')]
    [string] $Version,
    [string[]] $PackageSource,
    [switch] $WithoutShifter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $desktopProject = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Desktop\CoreShift.Desktop.csproj'))
    $Version = $desktopProject.Project.PropertyGroup.Version
}
$dotNetExe = (Get-Command -Name $DotNet -CommandType Application).Source
$sources = foreach ($source in $PackageSource) {
    if (Test-Path -LiteralPath $source) { (Resolve-Path -LiteralPath $source).Path }
    else { $source }
}

if (-not $InnoCompiler) {
    $command = Get-Command -Name ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        $InnoCompiler = $command.Source
    } else {
        $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
            throw 'ISCC.exe was not found. Pass -InnoCompiler with the path to Inno Setup 6.6 or newer.'
        }
    }
}
$innoExe = (Resolve-Path -LiteralPath $InnoCompiler).Path

$stagingRoot = (New-Item -ItemType Directory -Path (Join-Path $repoRoot 'obj\installer') -Force).FullName
$workDir = (New-Item -ItemType Directory -Path (Join-Path $stagingRoot ([guid]::NewGuid().ToString('N')))).FullName
$outputDir = Join-Path $repoRoot 'dist'
$installerName = "CoreShift-Setup-$Version-x64.exe"
$publishDir = Join-Path $workDir 'publish'

Push-Location -LiteralPath $repoRoot
try {
    $publishArgs = @(
        'publish', 'Desktop\CoreShift.Desktop.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', '--disable-build-servers', '-m:1',
        '--artifacts-path', (Join-Path $workDir 'artifacts'), '--output', $publishDir,
        "-p:Version=$Version",
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-p:EnableSingleFileAnalyzer=false',
        "-p:RestorePackagesPath=$(Join-Path $stagingRoot 'packages')",
        "-p:NuGetLockFilePath=$(Join-Path $workDir 'packages.lock.json')",
        '-p:RestorePackagesWithLockFile=false'
    )
    foreach ($source in $sources) {
        $publishArgs += @('--source', $source)
    }
    & $dotNetExe @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw ".NET publish exited with code $LASTEXITCODE."
    }

    $innoArgs = @('/Qp', "/O$workDir", "/DAppVersion=$Version", "/DPublishDir=$publishDir")
    if (-not $WithoutShifter) {
        $shifterDir = Join-Path $workDir 'shifter'
        & (Join-Path $PSScriptRoot 'Prepare-Shifter.ps1') -Destination $shifterDir
        $innoArgs += "/DShifterDir=$shifterDir"
    }
    & $innoExe @innoArgs (Join-Path $PSScriptRoot 'CoreShift.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup exited with code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    $installerPath = Join-Path $outputDir $installerName
    Move-Item -LiteralPath (Join-Path $workDir $installerName) -Destination $installerPath -Force
    Get-Item -LiteralPath $installerPath
} finally {
    Pop-Location
    $resolvedWorkDir = (Resolve-Path -LiteralPath $workDir).Path
    if ((Split-Path -Parent $resolvedWorkDir) -eq $stagingRoot) {
        Remove-Item -LiteralPath $resolvedWorkDir -Recurse -Force
    }
}
