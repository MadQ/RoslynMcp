<#
.SYNOPSIS
    Runs the TestHarness on Linux, under WSL, against the current working tree.

.DESCRIPTION
    The harness normally runs on Windows only, so behaviour that differs on Linux — file
    permissions, symlinks, path separators, case-sensitive file names — is never exercised, and
    tests that skip themselves on Windows check nothing. This script runs the same harness on
    Linux without leaving the machine.

    It copies the working tree, uncommitted changes included, into a directory inside the WSL
    filesystem (~/.roslynmcp-wsl/tree) and runs the harness there. The copy is deliberate:
    running from /mnt/<drive> would share bin/ and obj/ with the Windows build, and builds over
    that mount are slow. bin/, obj/, .git/ and .vs/ are not copied, so the Linux build keeps its
    own incremental state between runs.

    The .NET SDK must be the version global.json pins. The script uses a private copy in
    ~/.roslynmcp-wsl/dotnet if it has that version, else the dotnet on the WSL PATH. When
    neither has it, the script stops and says so; -InstallSdk installs the pinned version into
    the private directory (nothing is added to PATH, and the system SDK is left alone).

    Use it in addition to the Windows run when a change is platform-sensitive. See AGENTS.md,
    "Testing".

.PARAMETER Quiet
    Passes --quiet to the harness: failures and one summary line only.

.PARAMETER InstallSdk
    Install the SDK version pinned by global.json into ~/.roslynmcp-wsl/dotnet when WSL does
    not have it.

.PARAMETER Distribution
    The WSL distribution to use. Default: the default distribution.

.PARAMETER HarnessArgs
    Further arguments passed to the harness unchanged. Name the parameter and quote the value,
    since PowerShell does not bind a bare token that starts with a dash:
    -HarnessArgs '--only-build-diag'.

.EXAMPLE
    .\scripts\Test-HarnessWsl.ps1 -Quiet

.EXAMPLE
    .\scripts\Test-HarnessWsl.ps1 -InstallSdk -Quiet
#>
[CmdletBinding()]
param(
    [switch]   $Quiet,
    [switch]   $InstallSdk,
    [string]   $Distribution,
    [string[]] $HarnessArgs
)

$ErrorActionPreference = 'Stop'

if(-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {

    Write-Error 'wsl.exe was not found. This script needs WSL with a Linux distribution installed.'
    exit 2
}

$repoRoot   = Split-Path $PSScriptRoot -Parent
$sdkVersion = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version

$wsl = @()

if($Distribution) {
    $wsl += @('-d', $Distribution)
}

# wslpath turns J:\Projects\RoslynMcp into /mnt/j/Projects/RoslynMcp.
$source = (& wsl.exe @wsl -e wslpath -a $repoRoot).Trim()

if($LASTEXITCODE -ne 0 -or -not $source) {

    Write-Error "Could not translate '$repoRoot' to a WSL path. Is the WSL distribution set up?"
    exit 2
}

$forwarded = @()

if($Quiet) {
    $forwarded += '--quiet'
}

if($HarnessArgs) {
    $forwarded += $HarnessArgs
}

# Runs inside WSL. Arguments: source path, SDK version, install flag, then the harness arguments.
$script = @'
set -euo pipefail

src="$1"; sdk="$2"; install="$3"; shift 3

home_dir="$HOME/.roslynmcp-wsl"
tree="$home_dir/tree"
private_dotnet="$home_dir/dotnet/dotnet"

has_sdk() { [ -x "$1" ] && "$1" --list-sdks 2>/dev/null | grep -q "^$sdk "; }

dotnet_bin=""

if has_sdk "$private_dotnet"; then
    dotnet_bin="$private_dotnet"
elif command -v dotnet >/dev/null 2>&1 && has_sdk "$(command -v dotnet)"; then
    dotnet_bin="$(command -v dotnet)"
elif [ "$install" = "1" ]; then
    echo "Installing .NET SDK $sdk into $home_dir/dotnet ..." >&2
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/roslynmcp-dotnet-install.sh
    bash /tmp/roslynmcp-dotnet-install.sh --version "$sdk" --install-dir "$home_dir/dotnet" --no-path >/dev/null
    dotnet_bin="$private_dotnet"
else
    echo "WSL has no .NET SDK $sdk (the version global.json pins). Run again with -InstallSdk to install it into $home_dir/dotnet." >&2
    exit 2
fi

if ! command -v rsync >/dev/null 2>&1; then
    echo "rsync is not installed in this WSL distribution (Ubuntu: sudo apt install rsync)." >&2
    exit 2
fi

mkdir -p "$tree"

# --delete removes files that no longer exist in the working tree. Excluded directories are
# left alone on both sides, so the Linux bin/ and obj/ survive for the next incremental build.
rsync -a --delete \
    --exclude '.git/' --exclude '.vs/' --exclude 'bin/' --exclude 'obj/' --exclude 'publish/' \
    "$src/" "$tree/"

cd "$tree"

# The private SDK is not on PATH; the harness starts dotnet by name, so put it there.
if [ "$dotnet_bin" = "$private_dotnet" ]; then
    export DOTNET_ROOT="$home_dir/dotnet"
    export PATH="$home_dir/dotnet:$PATH"
fi

export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

exec dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0 -- "$@"
'@

# Bash must not see a carriage return anywhere. Two sources: the CRLF line endings this file
# may have, and the line ending PowerShell appends when it pipes a string to a native command.
# The second one would land directly after "$@" on the last line and become part of the last
# harness argument ("--quiet<CR>", which the harness does not recognise) — hence the newline
# added here, which leaves the appended carriage return alone on a line after the exec.
$script = ($script -replace "`r`n", "`n") + "`n"

# The script is stored in WSL and run from that file, in two steps, rather than piped into
# "bash -s": the processes it starts would share the stdin bash is still reading the script
# from, and a stored file can be re-run by hand when a run needs a closer look.
$script | & wsl.exe @wsl -e bash -c 'mkdir -p ~/.roslynmcp-wsl && cat > ~/.roslynmcp-wsl/run-harness.sh'

if($LASTEXITCODE -ne 0) {

    Write-Error 'Could not write the runner script into the WSL home directory.'
    exit 2
}

$installFlag = if($InstallSdk) { '1' } else { '0' }

& wsl.exe @wsl -e bash -c 'exec bash ~/.roslynmcp-wsl/run-harness.sh "$@"' run-harness $source $sdkVersion $installFlag @forwarded

exit $LASTEXITCODE
