<#
.SYNOPSIS
  Builds the Windows Jarvis panel and prints the executable the host spawns.

.DESCRIPTION
  Mirrors macos/build.sh: one command, one artifact, one printed path. The
  output lives in windows\dist\ so the host plugin can spawn it by a stable
  path the same way it does on macOS.

  Pass -Configuration Release for a release build; Debug is the default so a
  developer build does not silently replace a shipped one.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $windows 'JarvisPanel\JarvisPanel.csproj'
$exe = Join-Path $windows 'dist\jarvis-panel.exe'

if (-not (Test-Path $project)) {
    throw "project not found: $project"
}

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $exe)) {
    throw "build reported success but $exe is missing"
}

Write-Host "built $exe"
