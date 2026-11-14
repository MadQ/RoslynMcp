#requires -Version 7.0
<#
.SYNOPSIS
    Starts the RoslynMcp server for an MCP client working on this repository: the development host when it
    is installed, the installed tool otherwise (#464).

.DESCRIPTION
    The command of .github/mcp.json. A developer working on RoslynMcp gets the development host
    (src/RoslynMcp.DevHost), so a build published with scripts/Publish-DevServer.ps1 replaces the server
    under running sessions. Anyone else gets the installed tool, madq-roslynmcp, as before.

    The host is used when <home>\host holds it and a build has been published (<home>\current is not
    empty). <home> is %LOCALAPPDATA%\RoslynMcp\dev, or the directory ROSLYNMCP_DEV_HOME names: the same
    rule the host and Publish-DevServer.ps1 follow. Set ROSLYNMCP_USE_DEVHOST=0 to run the installed tool
    even when the host is there, e.g. to check whether a problem also happens without it.

    Every argument is passed on unchanged. The child inherits the standard handles, so the protocol
    never passes through PowerShell; this script writes nothing to stdout, which is the protocol's, and
    its own messages go to stderr. The exit code is the child's.

    See docs/development/DEV_HOST.md.
#>

$ErrorActionPreference = 'Stop'

$devHome = if($env:ROSLYNMCP_DEV_HOME) { [IO.Path]::GetFullPath($env:ROSLYNMCP_DEV_HOME) }
           else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) (Join-Path 'RoslynMcp' 'dev') }
$hostExe = Join-Path $devHome (Join-Path 'host' ($IsWindows ? 'RoslynMcp.DevHost.exe' : 'RoslynMcp.DevHost'))
$pointer = Join-Path $devHome 'current'

# A host without a published build has no server to start, so it counts as not installed.
$useHost = $env:ROSLYNMCP_USE_DEVHOST -ne '0' -and
           (Test-Path -LiteralPath $hostExe -PathType Leaf) -and
           (Test-Path -LiteralPath $pointer -PathType Leaf) -and
           (Get-Item -LiteralPath $pointer).Length -gt 0

# A bare name is looked up on PATH by Process.Start, as a shell would.
$command = $useHost ? $hostExe : 'madq-roslynmcp'

# Process.Start without redirection: the child gets this process's stdin, stdout and stderr as they are.
# Invoking it with & instead would put stdout through the PowerShell pipeline, which re-encodes it.
$start = [Diagnostics.ProcessStartInfo]::new($command)
$start.UseShellExecute = $false
foreach($argument in $args) { $start.ArgumentList.Add($argument) }

try {
    $process = [Diagnostics.Process]::Start($start)
}
catch {
    $hint = $useHost
        ? "Reinstall it with .\scripts\Publish-DevServer.ps1 -InstallHost, or set ROSLYNMCP_USE_DEVHOST=0."
        : "Install the tool (dotnet tool install --global MadQ.RoslynMcp --prerelease), or install the development host with .\scripts\Publish-DevServer.ps1 -InstallHost."
    [Console]::Error.WriteLine("Start-RoslynMcp: $command did not start: $($_.Exception.Message) $hint")
    exit 1
}

$process.WaitForExit()
exit $process.ExitCode
