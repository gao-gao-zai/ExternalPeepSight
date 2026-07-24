[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+$")]
    [string]$Version = "0.1.0",
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipChecks
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$solution = Join-Path $root "ExternalPeepSight.sln"
$uiProject = Join-Path $root "src\ExternalPeepSight.UI\ExternalPeepSight.UI.csproj"
$nativeBuild = Join-Path $root "build\native\windows-release"
$releaseRoot = Join-Path $root "artifacts\release"
$packageName = "ExternalPeepSight-v$Version-$Runtime"
$packageDirectory = Join-Path $releaseRoot $packageName
$uiPublishDirectory = Join-Path $packageDirectory "ui-publish"

function Invoke-Step {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "`n== $Name ==" -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Get-VisualStudioPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Visual Studio Installer vswhere.exe was not found."
    }

    $vsPath = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if (-not $vsPath) {
        throw "Visual Studio C++ Build Tools installation was not found."
    }

    return $vsPath
}

function Get-CMakePath {
    $vsPath = Get-VisualStudioPath
    $cmake = Get-ChildItem `
        -LiteralPath (Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake") `
        -Filter cmake.exe `
        -Recurse `
        | Select-Object -First 1 -ExpandProperty FullName
    if (-not $cmake) {
        throw "CMake bundled with Visual Studio was not found."
    }

    return $cmake
}

function Copy-MsvcRuntime {
    param(
        [Parameter(Mandatory)]
        [string]$VisualStudioPath,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    $runtimeDirectory = Get-ChildItem `
        -LiteralPath (Join-Path $VisualStudioPath "VC\Redist\MSVC") `
        -Directory `
        -Recurse `
        -Filter "Microsoft.VC143.CRT" `
        | Where-Object { $_.FullName -match "\\x64\\Microsoft\.VC143\.CRT$" } `
        | Sort-Object LastWriteTime -Descending `
        | Select-Object -First 1
    if (-not $runtimeDirectory) {
        throw "The x64 Microsoft Visual C++ runtime redistributable was not found."
    }

    foreach ($fileName in @(
        "msvcp140.dll",
        "msvcp140_atomic_wait.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll"
    )) {
        $source = Join-Path $runtimeDirectory.FullName $fileName
        if (-not (Test-Path -LiteralPath $source)) {
            throw "The MSVC runtime file was not found: $source"
        }

        Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $fileName)
    }
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$cmake = Get-CMakePath
$vsPath = Get-VisualStudioPath

if (-not $SkipChecks) {
    Write-Host "`n== Quality gates ==" -ForegroundColor Cyan
    & (Join-Path $root "check.ps1") -Coverage -Deep
    if (-not $?) {
        throw "Quality gates failed."
    }
}

if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $uiPublishDirectory | Out-Null

Invoke-Step "Native configure" $cmake @(
    "-S",
    $root,
    "-B",
    $nativeBuild,
    "-G",
    "Visual Studio 17 2022",
    "-A",
    "x64",
    "-DBUILD_TESTING=OFF"
)
Invoke-Step "Native release build" $cmake @(
    "--build",
    $nativeBuild,
    "--config",
    $Configuration,
    "--target",
    "ExternalPeepSight.Host"
)

$nativeHost = Join-Path $nativeBuild "src\ExternalPeepSight.Host\$Configuration\ExternalPeepSight.Host.exe"
if (-not (Test-Path -LiteralPath $nativeHost)) {
    throw "Native Host output was not found: $nativeHost"
}

Invoke-Step "Restore publish runtime" $dotnet @(
    "restore",
    $solution,
    "--runtime",
    $Runtime
)
Invoke-Step "Managed self-contained publish" $dotnet @(
    "publish",
    $uiProject,
    "--configuration",
    $Configuration,
    "--runtime",
    $Runtime,
    "--self-contained",
    "true",
    "--output",
    $uiPublishDirectory,
    "--no-restore",
    "/p:DebugType=None",
    "/p:DebugSymbols=false"
)

Copy-Item -LiteralPath $nativeHost `
    -Destination (Join-Path $uiPublishDirectory "ExternalPeepSight.Host.exe")
Copy-MsvcRuntime -VisualStudioPath $vsPath -Destination $uiPublishDirectory

Copy-Item -LiteralPath (Join-Path $root "README.md") `
    -Destination (Join-Path $packageDirectory "README.md")
Copy-Item -LiteralPath (Join-Path $root "LICENSE") `
    -Destination (Join-Path $packageDirectory "LICENSE")
@"
ExternalPeepSight $Version
Runtime: $Runtime
Build configuration: $Configuration
Build date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz")

Start ExternalPeepSight.UI.exe from this directory.
The native Host executable and the required x64 MSVC runtime files are beside it.

System requirement: Windows 10 or Windows 11 x64.
This package is portable and stores user configuration under:
%LOCALAPPDATA%\ExternalPeepSight

The first version still requires manual compatibility validation with target games,
display configurations, HDR, mixed DPI, and anti-cheat software.
"@ | Set-Content -LiteralPath (Join-Path $packageDirectory "RELEASE-NOTES.txt") -Encoding ascii

Rename-Item -LiteralPath $uiPublishDirectory -NewName "app"

$fileCount = (Get-ChildItem -LiteralPath (Join-Path $packageDirectory "app") -Recurse -File).Count
$packageSize = [math]::Round(
    ((Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB),
    2)

Write-Host "`nRelease package created:" -ForegroundColor Green
Write-Host $packageDirectory
Write-Host "Files: $fileCount"
Write-Host "Size: $packageSize MB"
