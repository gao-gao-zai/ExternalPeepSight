[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Coverage,
    [switch]$Deep,
    [switch]$SkipNative
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$solution = Join-Path $root "ExternalPeepSight.sln"

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

function Get-CMakePath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Visual Studio Installer vswhere.exe was not found."
    }

    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        throw "Visual Studio C++ Build Tools installation was not found."
    }

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

function Get-VisualStudioPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Visual Studio Installer vswhere.exe was not found."
    }

    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        throw "Visual Studio C++ Build Tools installation was not found."
    }

    return $vsPath
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

Invoke-Step "Restore" $dotnet @("restore", $solution)
Invoke-Step "Format" $dotnet @("format", $solution, "--verify-no-changes", "--no-restore")
Invoke-Step "Managed build" $dotnet @(
    "build",
    $solution,
    "--configuration",
    $Configuration,
    "--no-restore"
)

$testArguments = @(
    "test",
    $solution,
    "--configuration",
    $Configuration,
    "--no-build",
    "--no-restore"
)

if ($Coverage) {
    $coverageDirectory = Join-Path $root "artifacts\coverage"
    New-Item -ItemType Directory -Force -Path $coverageDirectory | Out-Null
    $coverageFile = Join-Path $coverageDirectory "coverage.cobertura.xml"
    $testArguments += @(
        "/p:CollectCoverage=true",
        "/p:CoverletOutput=$coverageFile",
        "/p:CoverletOutputFormat=cobertura",
        "/p:Threshold=85",
        "/p:ThresholdType=line%2cbranch",
        "/p:ThresholdStat=total"
    )
}

Invoke-Step "Managed tests" $dotnet $testArguments

foreach ($project in Get-ChildItem -LiteralPath $root -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" }) {
    Invoke-Step "NuGet audit: $($project.Name)" $dotnet @(
        "package",
        "list",
        "--project",
        $project.FullName,
        "--vulnerable",
        "--include-transitive",
        "--no-restore"
    )
}

if (-not $SkipNative) {
    $cmake = Get-CMakePath
    $nativePreset = "windows-debug"
    $nativeBuild = Join-Path $root "build\native\windows-debug"

    Invoke-Step "Native configure" $cmake @(
        "--preset",
        $nativePreset
    )
    Invoke-Step "Native build" $cmake @(
        "--build",
        $nativeBuild,
        "--config",
        $Configuration
    )

    $ctest = Join-Path (Split-Path $cmake -Parent) "ctest.exe"
    if (-not (Test-Path -LiteralPath $ctest)) {
        $ctest = (Get-Command ctest -ErrorAction SilentlyContinue).Source
    }
    if (-not $ctest) {
        throw "CTest was not found beside CMake or on PATH."
    }

    Invoke-Step "Native tests" $ctest @(
        "--test-dir",
        $nativeBuild,
        "--build-config",
        $Configuration,
        "--output-on-failure"
    )

    $vsPath = Get-VisualStudioPath
    $clangFormat = Get-ChildItem `
        -LiteralPath (Join-Path $vsPath "VC\Tools\Llvm\x64\bin") `
        -Filter clang-format.exe `
        -Recurse `
        | Select-Object -First 1 -ExpandProperty FullName
    $cppFiles = Get-ChildItem `
        -LiteralPath $root `
        -Recurse `
        -File `
        | Where-Object {
            $_.Extension -in ".cpp", ".h", ".hpp" -and
            $_.FullName -notmatch "\\(build|artifacts|bin|obj)\\"
        }

    if ($clangFormat -and $cppFiles) {
        $formatArguments = @(
            "--dry-run",
            "--Werror",
            "--style=file"
        ) + @($cppFiles.FullName)
        Invoke-Step "Native format" $clangFormat $formatArguments
    }
}

if ($Deep -and -not $SkipNative) {
    $cmake = Get-CMakePath
    $ctest = Join-Path (Split-Path $cmake -Parent) "ctest.exe"

    $analyzeBuild = Join-Path $root "build\native\windows-analyze"
    Invoke-Step "MSVC analyze configure" $cmake @("--preset", "windows-analyze")
    Invoke-Step "MSVC analyze build" $cmake @("--build", $analyzeBuild, "--config", "Release")

    $asanBuild = Join-Path $root "build\native\windows-asan"
    Invoke-Step "ASan configure" $cmake @("--preset", "windows-asan")
    Invoke-Step "ASan build" $cmake @("--build", $asanBuild, "--config", "Debug")

    $vsPath = Get-VisualStudioPath
    $asanRuntime = Get-ChildItem `
        -LiteralPath (Join-Path $vsPath "VC\Tools\MSVC") `
        -Filter clang_rt.asan_dynamic-x86_64.dll `
        -Recurse `
        | Where-Object { $_.FullName -match "Hostx64\\x64" } `
        | Select-Object -First 1
    if (-not $asanRuntime) {
        throw "The x64 MSVC AddressSanitizer runtime was not found."
    }

    $previousPath = $env:PATH
    try {
        $env:PATH = "$($asanRuntime.DirectoryName);$env:PATH"
        Invoke-Step "ASan tests" $ctest @(
            "--test-dir",
            $asanBuild,
            "--build-config",
            "Debug",
            "--output-on-failure"
        )
    }
    finally {
        $env:PATH = $previousPath
    }
}

Write-Host "`nAll checks passed." -ForegroundColor Green
