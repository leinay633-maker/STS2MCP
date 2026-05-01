<#
.SYNOPSIS
    Builds the STS2_MCP mod DLL.

.DESCRIPTION
    Compiles STS2_MCP.dll against the game's assemblies. Does NOT install
    the mod — copy the output files to the game's mods/ directory yourself.

.PARAMETER GameDir
    Path to the Slay the Spire 2 installation directory.
    Falls back to the STS2_GAME_DIR environment variable and then probes
    common Steam library locations if not specified.

.PARAMETER Configuration
    Build configuration (default: Release).

.EXAMPLE
    .\build.ps1 -GameDir "D:\steam\steamapps\common\Slay the Spire 2"
    .\build.ps1  # uses $env:STS2_GAME_DIR or probes common install locations
#>
param(
    [string]$GameDir,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Get-GameDirCandidates {
    param([string]$PreferredGameDir)

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @(
        $PreferredGameDir,
        $env:STS2_GAME_DIR,
        "D:\steam\steamapps\common\Slay the Spire 2",
        "D:\SteamLibrary\steamapps\common\Slay the Spire 2",
        "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
        "C:\Program Files\Steam\steamapps\common\Slay the Spire 2"
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and -not $candidates.Contains($candidate)) {
            $candidates.Add($candidate)
        }
    }

    return $candidates
}

function Resolve-GameDir {
    param([string]$PreferredGameDir)

    foreach ($candidate in Get-GameDirCandidates -PreferredGameDir $PreferredGameDir) {
        $dllPath = Join-Path $candidate "data_sts2_windows_x86_64\sts2.dll"
        if (Test-Path $dllPath) {
            return $candidate
        }
    }

    return $null
}

function Resolve-DotNetExe {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $homeDir = [Environment]::GetFolderPath("UserProfile")
    foreach ($candidate in @(
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT "dotnet.exe" }),
        $(if ($homeDir) { Join-Path $homeDir ".dotnet\dotnet.exe" })
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

# --- Resolve game directory ---
$requestedGameDir = $GameDir
$GameDir = Resolve-GameDir -PreferredGameDir $requestedGameDir
if (-not $GameDir) {
    $searched = (Get-GameDirCandidates -PreferredGameDir $requestedGameDir) -join "`n  "
    Write-Host @"
ERROR: Could not resolve the Slay the Spire 2 installation directory.

Pass it explicitly:
  .\build.ps1 -GameDir "D:\steam\steamapps\common\Slay the Spire 2"

Or set it once in your PowerShell profile:
  `$env:STS2_GAME_DIR = "D:\steam\steamapps\common\Slay the Spire 2"

Searched:
  $searched
"@ -ForegroundColor Red
    exit 1
}

# --- Check prerequisites ---
$dotnetExe = Resolve-DotNetExe
if (-not $dotnetExe) {
    Write-Host @"
ERROR: 'dotnet' not found on PATH or in the standard user install locations.

Install the .NET 9 SDK from:
  https://dotnet.microsoft.com/download/dotnet/9.0
"@ -ForegroundColor Red
    exit 1
}

# --- Build ---
$scriptDir = $PSScriptRoot
$project = Join-Path $scriptDir "STS2_MCP.csproj"
$outDir = Join-Path (Join-Path $scriptDir "out") "STS2_MCP"

Write-Host "=== Building STS2_MCP ($Configuration) ===" -ForegroundColor Cyan
Write-Host "Game directory : $GameDir"
Write-Host "Output         : $outDir"
Write-Host "dotnet         : $dotnetExe"
Write-Host ""

& $dotnetExe build $project -c $Configuration -o $outDir -p:STS2GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Build succeeded ===" -ForegroundColor Green
Write-Host "To install, copy these files to <game_install>/mods/:"
Write-Host "  $outDir\STS2_MCP.dll"
Write-Host "  $scriptDir\mod_manifest.json  ->  mods\STS2_MCP.json"
