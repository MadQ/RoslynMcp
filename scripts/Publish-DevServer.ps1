#requires -Version 7.0
<#
.SYNOPSIS
    Publishes the server from the working tree as a new build for the development host and makes it
    the build every running host swaps to (#426).

.DESCRIPTION
    The development host (src/RoslynMcp.DevHost) is what an MCP client launches in place of the server
    during development. It runs the build a pointer file names and swaps to another when the pointer
    is written. This script is the other half:

      1. publishes the server into a new directory under <home>\builds, so no running build is ever
         overwritten and nothing has to be stopped first;
      2. writes the pointer, <home>\current, in one step (a rename), which every running host takes
         as the request to swap at its next idle moment;
      3. removes old builds, except the newest few, the ones the pointer and last-good name, and any
         a server still runs from.

    <home> is %LOCALAPPDATA%\RoslynMcp\dev, or the directory ROSLYNMCP_DEV_HOME names.

    Nothing here touches the dotnet tool package or an MCP client's configuration. To run a client
    through the host, install the host once with -InstallHost and point the client's "command" at the
    path the script prints, keeping the server's own arguments.

    See docs/development/DEV_HOST.md.

.PARAMETER InstallHost
    Also publishes the host itself into <home>\host. Fails while a session runs the host, since its
    files are then in use: end those sessions first. Needed once, and again only when the host changes.

.PARAMETER Keep
    How many of the newest builds to keep. Default: 5.

.PARAMETER Restart
    Publishes nothing: writes the pointer again as it is, so every running host starts the same build
    afresh. The remedy for workspaces that loaded badly, for all sessions at once.

.EXAMPLE
    .\scripts\Publish-DevServer.ps1

.EXAMPLE
    .\scripts\Publish-DevServer.ps1 -InstallHost
#>
[CmdletBinding()]
param(
    [switch] $InstallHost,
    [switch] $Restart,
    [ValidateRange(1, 100)]
    [int] $Keep = 5
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$devHome  = if($env:ROSLYNMCP_DEV_HOME) { [IO.Path]::GetFullPath($env:ROSLYNMCP_DEV_HOME) }
            else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) (Join-Path 'RoslynMcp' 'dev') }
$builds   = Join-Path $devHome 'builds'
$pointer  = Join-Path $devHome 'current'
$lastGood = Join-Path $devHome 'last-good'

New-Item -ItemType Directory -Force -Path $builds | Out-Null

# One step, so a host never reads half a name: the hosts poll this file.
function Set-Pointer([string] $Value) {
    $temp = "$pointer.$PID.tmp"
    Set-Content -LiteralPath $temp -Value $Value -Encoding utf8NoBOM

    # The hosts read the file sharing deletes, so they never block the rename. Anything else that has
    # it open for a moment (a scanner, an editor) gets a few tries before the publish counts as failed.
    for($attempt = 1; ; $attempt++) {
        try {
            Move-Item -LiteralPath $temp -Destination $pointer -Force
            return
        }
        catch {
            if($attempt -ge 5) {
                Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
                throw "The pointer $pointer was not written: $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 200
        }
    }
}

# Empty for a file that is missing or empty: Get-Content -Raw returns null for an empty file.
function Get-PointerValue([string] $Path) {
    if(-not (Test-Path -LiteralPath $Path)) { return '' }

    $raw = Get-Content -LiteralPath $Path -Raw
    if($raw) { $raw.Trim() } else { '' }
}

if($Restart -and $InstallHost) {
    throw "-Restart publishes nothing and cannot be combined with -InstallHost."
}

if($Restart) {
    $current = Get-PointerValue $pointer
    if(-not $current) { throw "There is no build to restart: $pointer is missing or empty." }

    Set-Pointer $current
    Write-Host "Restart requested: every running host starts build $current again at its next idle moment."
    return
}

if($InstallHost) {
    $hostDir = Join-Path $devHome 'host'

    dotnet publish (Join-Path $repoRoot 'src/RoslynMcp.DevHost/RoslynMcp.DevHost.csproj') -c Release -o $hostDir --nologo -v q
    if($LASTEXITCODE -ne 0) {
        throw "The host was not installed (dotnet publish exited with code $LASTEXITCODE; its output is above). If sessions run the host, its files are in use: end every session that runs it, then try again."
    }

    $hostExe = Join-Path $hostDir ($IsWindows ? 'RoslynMcp.DevHost.exe' : 'RoslynMcp.DevHost')
    Write-Host "Host installed: $hostExe"
    Write-Host "Point the MCP client's ""command"" at it and keep the server's arguments (""--root"", ""."")."
}

# The tool-style publish rewrites the server's lock file without its per-RID entries. That is noise in
# the working tree, so a lock file that was clean before is put back; one with changes is left alone.
$lockFile     = 'src/RoslynMcp/packages.lock.json'
$lockWasClean = -not (git -C $repoRoot status --porcelain -- $lockFile)

$sha    = (git -C $repoRoot rev-parse --short HEAD).Trim()
$dirty  = if(git -C $repoRoot status --porcelain --untracked-files=no) { '-dirty' } else { '' }
$name   = '{0}-{1}{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $sha, $dirty
$target = Join-Path $builds $name

# PackDebug is the configuration the locally installed tool is packed from: loose assemblies, DEBUG defined.
dotnet publish (Join-Path $repoRoot 'src/RoslynMcp/RoslynMcp.csproj') -c PackDebug -f net10.0 -o $target --nologo -v q
$published = $LASTEXITCODE

if($lockWasClean) { git -C $repoRoot checkout -- $lockFile }

if($published -ne 0) {
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    throw "The server was not published (exit code $published). The pointer is unchanged; running sessions keep their build."
}

Set-Pointer $name
Write-Host "Published build $name. Every running host swaps to it at its next idle moment."

# A build a server still runs from is never removed, not even in part: a server loads assemblies as it
# needs them, and one that lost a file it had not loaded yet fails later, far from the cause.
$named   = @((Get-PointerValue $pointer), (Get-PointerValue $lastGood))
# A server is its own executable in the build directory, or dotnet with the build's RoslynMcp.dll as an
# argument (a build without an executable), so both the path and the command line are looked at.
$running = @(Get-Process | ForEach-Object {
    try { $_.Path } catch { $null }
    try { $_.CommandLine } catch { $null }
} | Where-Object { $_ })

Get-ChildItem -LiteralPath $builds -Directory |
    Sort-Object Name -Descending |
    Select-Object -Skip $Keep |
    Where-Object { $_.Name -notin $named } |
    ForEach-Object {
        $prefix = $_.FullName + [IO.Path]::DirectorySeparatorChar
        if($running | Where-Object { $_.Contains($prefix, [StringComparison]::OrdinalIgnoreCase) }) { return }

        try   { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
        catch { Write-Warning "Old build $($_.Name) was not removed: $($_.Exception.Message)" }
    }
