<#
.SYNOPSIS
  Build TiXL (and optionally launch the editor) using an MSBuild invocation that
  survives a restricted shell.

.DESCRIPTION
  Plain `dotnet build` is not dependable here. MSBuild reuses an out-of-process
  build node by default, and that handshake fails silently under a confined
  sandbox or locked-down user account: the command exits 1 with "0 Warning(s),
  0 Error(s)" and no diagnostic at all, which reads like a project error and
  sends you hunting through the code for nothing.

  Passing `-nodeReuse:false -m:1` makes the build run in-process and fixes it.
  This script exists so nobody has to rediscover that.

  Defaults to Release, because that is the configuration that stages the
  operator packages into the editor's output folder. Use -Configuration Debug
  for a faster iteration loop when you only need to recompile.

  Exit code is dotnet's, so callers and CI-style checks can rely on it.

.PARAMETER Project
  Project or solution to build, relative to the repository root.
  Default "t3.sln".

.PARAMETER Configuration
  Build configuration: Release (default) or Debug.

.PARAMETER NoRestore
  Skip the restore step. Faster when the NuGet assets are already current.

.PARAMETER Run
  Launch the editor after a successful Release build, and print the PID. Loads
  whatever -Project built, so point it at Editor/Editor.csproj.

.EXAMPLE
  .\Scripts\build.ps1

.EXAMPLE
  .\Scripts\build.ps1 -Project Editor/Editor.csproj -Run

.EXAMPLE
  .\Scripts\build.ps1 -Project Operators/Lib/Lib.csproj -Configuration Debug -NoRestore
#>

[CmdletBinding()]
param(
    [string]$Project = 't3.sln',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore,

    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $Project

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

# Arguments forcing a single in-process build node. Without both, MSBuild's node
# handshake can fail silently in a restricted environment. Quoting keeps the
# colon-bearing forms intact when dotnet is launched from a PowerShell host.
$singleNodeArgs = @('-nodeReuse:false', '-m:1')

function Invoke-DotNet([string[]]$arguments) {
    # stderr is deliberately left on the pipeline: MSBuild writes diagnostics
    # there and callers need to see them.
    & dotnet @arguments 2>&1 | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Get-BuiltEditorPath {
    $editorOutput = Join-Path $repoRoot "Editor/bin/$Configuration/net10.0-windows"
    foreach ($name in @('TiXL.exe', 'TiXL.dll')) {
        $candidate = Join-Path $editorOutput $name
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found: $projectPath"
}

if (-not $NoRestore) {
    Write-Step "Restoring $Project"
    $restoreExitCode = Invoke-DotNet (@('restore', $projectPath, '-v:minimal', '-nologo') + $singleNodeArgs)
    if ($restoreExitCode -ne 0) {
        Write-Error "Restore failed (exit $restoreExitCode)."
    }
}

Write-Step "Building $Project [$Configuration]"
$buildExitCode = Invoke-DotNet (@('build', $projectPath, '-c', $Configuration, '-v:minimal', '-nologo') + $singleNodeArgs)
if ($buildExitCode -ne 0) {
    Write-Error "Build failed (exit $buildExitCode)."
}

Write-Step "Build succeeded."

if ($Run) {
    $editorPath = Get-BuiltEditorPath
    if (-not $editorPath) {
        Write-Error "No built editor found. Build Editor/Editor.csproj first, e.g. -Project Editor/Editor.csproj -Run."
    }

    Write-Step "Launching $editorPath"
    $editorDirectory = Split-Path -Parent $editorPath
    $process = Start-Process -FilePath $editorPath -WorkingDirectory $editorDirectory -PassThru
    Write-Host "Started PID $($process.Id). Launching the editor needs permission to write user environment variables (HKCU); without it the editor exits during startup."
}

exit 0
