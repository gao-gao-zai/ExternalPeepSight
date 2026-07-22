[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipNative,
    [switch]$SkipManaged
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not $SkipManaged) {
    dotnet restore (Join-Path $root "ExternalPeepSight.sln")
    dotnet build (Join-Path $root "ExternalPeepSight.sln") --configuration $Configuration --no-restore
}

if (-not $SkipNative) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        throw "Visual Studio C++ Build Tools installation was not found."
    }

    $cmake = Get-ChildItem -LiteralPath (Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake") -Filter cmake.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
    if (-not $cmake) {
        throw "CMake bundled with Visual Studio was not found."
    }

    $nativeBuild = Join-Path $root "build\native"
    & $cmake -S $root -B $nativeBuild -G "Visual Studio 17 2022" -A x64
    & $cmake --build $nativeBuild --config $Configuration
}
