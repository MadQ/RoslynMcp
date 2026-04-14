#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies that RoslynMcp does not re-create files deleted by git during a branch switch.

.DESCRIPTION
    Reproduces the pre-fix behaviour in FlushMSBuild (WorkspaceManager.Instance.cs):
    the old code called TryApplyChanges with an empty/cleared document, which caused
    MSBuildWorkspace to write a zero-byte ghost file back to disk — making deleted files
    reappear as untracked changes in git status.

    Fix (commit 7ac654f): when a file is in the FSW Deleted set, increment reloadVersion
    so the workspace reloads from disk on the next tool call rather than writing an empty
    file via TryApplyChanges.

    Test flow:
      1.  Create a temp dir with a minimal .csproj and two base .cs files.
      2.  Init a git repo, commit on 'main'.
      3.  Create branch 'feat/extra-files', add Extra1.cs + Extra2.cs, commit.
      4.  git checkout feat/extra-files so extra files exist on disk.
      5.  dotnet restore (MSBuildWorkspace needs SDK props resolved).
      6.  Start RoslynMcp server.
      7.  MCP initialize handshake.
      8.  Call roslyn_get_project_info  → loads workspace + starts FSW.
      9.  Call roslyn_list_types        → verify ExtraOne/ExtraTwo are known.
     10.  git checkout main             → git deletes Extra1.cs and Extra2.cs.
     11.  Wait FswWaitMs ms             → DebounceMs is 300; default 1000 gives ~3x headroom.
     12.  Disk check: Extra1.cs / Extra2.cs must NOT exist    (primary assertion).
     13.  git status --porcelain must be empty                (primary assertion).
     14.  Call roslyn_list_types        → triggers workspace reload; ExtraOne/ExtraTwo gone.

.PARAMETER FswWaitMs
    Milliseconds to wait after the branch switch before checking. Default: 1000.
    300 ms is the internal DebounceMs constant; 1000 ms gives generous headroom.

.PARAMETER RmExe
    Path to RoslynMcp.exe. Defaults to publish\net10.0\RoslynMcp.exe in the repo root.

.EXAMPLE
    .\scripts\Test-FswFileRecreation.ps1

.EXAMPLE
    .\scripts\Test-FswFileRecreation.ps1 -FswWaitMs 2000 -Verbose
#>
param(
    [int]    $FswWaitMs = 1000,
    [string] $RmExe     = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Paths ───────────────────────────────────────────────────────────────────────

$repoRoot = Split-Path -Parent $PSScriptRoot  # scripts\ → repo root

if(!$RmExe) {
    $RmExe = Join-Path $repoRoot 'publish\net10.0\RoslynMcp.exe'
}

if(!(Test-Path $RmExe)) {
    Write-Error "RoslynMcp.exe not found at: $RmExe`nRun .\pub.ps1 first to build a published executable."
    exit 1
}

# ── helpers ──────────────────────────────────────────────────────────────────────

# Safely read a PSCustomObject property by name — avoids StrictMode -Version Latest
# throwing on non-existent properties (e.g., 'error' is absent on success responses).
function Get-Prop {
    param($obj, [string] $name)
    $p = $obj.PSObject.Properties[$name]
    if($null -ne $p) { return $p.Value }
    return $null
}

# ── MCP helpers ─────────────────────────────────────────────────────────────────

$reqId = 0

function Send-Mcp {
    param(
        [System.Diagnostics.Process] $proc,
        [hashtable]                  $msg
    )

    $json = $msg | ConvertTo-Json -Compress -Depth 10
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Receive-Mcp {
    param(
        [System.Diagnostics.Process] $proc,
        [int]                        $timeoutMs = 15000
    )

    $task = $proc.StandardOutput.ReadLineAsync()

    if(!$task.Wait($timeoutMs)) {
        return $null  # timeout
    }

    $line = $task.Result

    if($null -eq $line) {
        return $null  # EOF / server exited
    }

    return $line | ConvertFrom-Json
}

function Invoke-McpTool {
    param(
        [System.Diagnostics.Process] $proc,
        [string]                     $toolName,
        [hashtable]                  $toolArgs,
        [int]                        $timeoutMs = 30000
    )

    Send-Mcp $proc @{
        jsonrpc = '2.0'
        id      = ++$script:reqId
        method  = 'tools/call'
        params  = @{ name = $toolName; arguments = $toolArgs }
    }

    $response = Receive-Mcp $proc $timeoutMs

    if($null -eq $response) {
        return $null
    }

    $text = $response.result?.content?[0]?.text

    if($null -eq $text) {
        return $null
    }

    try   { return $text | ConvertFrom-Json }
    catch { return $text }  # plain string — return as-is
}

# ── Test state ──────────────────────────────────────────────────────────────────

$tmpDir   = Join-Path ([IO.Path]::GetTempPath()) "RmFswTest_$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
$proc     = $null
$passAll  = $true
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Pass { param([string] $msg) Write-Host "  PASS  $msg" -ForegroundColor Green }
function Write-Fail {
    param([string] $msg)
    $script:passAll = $false
    $script:failures.Add($msg)
    Write-Host "  FAIL  $msg" -ForegroundColor Red
}

# ── Main ────────────────────────────────────────────────────────────────────────

try {
    Write-Host ""
    Write-Host "══════════════════════════════════════════════════════════════"
    Write-Host "  RoslynMcp FSW File-Recreation Test"
    Write-Host "══════════════════════════════════════════════════════════════"
    Write-Host "  RM exe   : $RmExe"
    Write-Host "  FSW wait : ${FswWaitMs}ms  (DebounceMs=300; default=1000)"
    Write-Host "  Temp dir : $tmpDir"
    Write-Host ""

    # ── Step 1: Create temp git repo ────────────────────────────────────────────

    Write-Host "[1] Creating test git repo..."

    $null = New-Item -ItemType Directory -Path $tmpDir
    Push-Location $tmpDir

    & git init --initial-branch=main -q 2>&1 | Out-Null
    & git config user.email 'fsw-test@localhost'
    & git config user.name  'FswTest'

    # Minimal SDK project — no NuGet packages; MSBuildWorkspace loads without extra restore work.
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content 'TestProject.csproj'

    @'
namespace TestProject;
public class BaseClass { public void DoWork() { } }
'@ | Set-Content 'BaseClass.cs'

    @'
namespace TestProject;
public class AnotherClass { public int Value => 42; }
'@ | Set-Content 'AnotherClass.cs'

    @'
bin/
obj/
'@ | Set-Content '.gitignore'

    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Initial commit' 2>&1 | Out-Null

    # Feature branch with extra files.
    & git checkout -q -b feat/extra-files 2>&1 | Out-Null

    @'
namespace TestProject;
public class ExtraOne { public void Method1() { } }
'@ | Set-Content 'Extra1.cs'

    @'
namespace TestProject;
public class ExtraTwo { public void Method2() { } }
'@ | Set-Content 'Extra2.cs'

    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Add Extra1.cs and Extra2.cs' 2>&1 | Out-Null

    Write-Host "  Repo ready. On branch: feat/extra-files"
    Write-Host "  Files: BaseClass.cs, AnotherClass.cs, Extra1.cs, Extra2.cs"
    Write-Host ""

    # ── Step 2: NuGet restore ───────────────────────────────────────────────────

    Write-Host "[2] dotnet restore (MSBuildWorkspace needs SDK props resolved)..."

    & dotnet restore 'TestProject.csproj' -v q 2>&1 | Out-Null

    if($LASTEXITCODE -ne 0) {
        Write-Fail "dotnet restore failed (exit $LASTEXITCODE) — MSBuildWorkspace may not load"
        Write-Host "  Continuing, but results may be unreliable." -ForegroundColor Yellow
    }
    else {
        Write-Host "  Restore complete."
    }

    Write-Host ""

    # ── Step 3: Start RM server ─────────────────────────────────────────────────

    Write-Host "[3] Starting RoslynMcp server..."

    $psi                       = [System.Diagnostics.ProcessStartInfo]::new($RmExe)
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.WorkingDirectory       = $tmpDir

    # Strip MSBuild env vars set by any parent RoslynMcp process — inheriting MSBUILD_EXE_PATH
    # causes the child's MSBuildLocator.RegisterDefaults() to fail with "MSBuild not found".
    # Same fix as DotnetRunner.cs strips these for child dotnet-build processes.
    foreach($key in @('MSBUILD_EXE_PATH', 'MSBuildExtensionsPath', 'MSBuildSDKsPath', 'MSBuildExtensionsPath32')) {
        if($psi.Environment.ContainsKey($key)) {
            $psi.Environment.Remove($key)
        }
    }

    $proc = [System.Diagnostics.Process]::Start($psi)

    # Drain stderr asynchronously — prevents pipe deadlock if the server writes to stderr.
    $proc.BeginErrorReadLine()

    Start-Sleep -Milliseconds 600  # Allow server startup before first message.

    # ── Step 4: MCP handshake ───────────────────────────────────────────────────

    Write-Host "[4] MCP handshake..."

    Send-Mcp $proc @{
        jsonrpc = '2.0'
        id      = ++$script:reqId
        method  = 'initialize'
        params  = @{
            protocolVersion = '2024-11-05'
            capabilities    = @{}
            clientInfo      = @{ name = 'FswTest'; version = '1.0' }
        }
    }

    $initResp = Receive-Mcp $proc 10000

    if($null -eq $initResp) {
        Write-Fail 'Server did not respond to initialize — check server logs'
        Write-Host "  Cannot continue without a live server." -ForegroundColor Red
        return
    }

    Send-Mcp $proc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Write-Host "  MCP session initialized."
    Write-Host ""

    # ── Step 5: Load workspace (starts FSW) ─────────────────────────────────────

    Write-Host "[5] roslyn_get_project_info  →  loads MSBuildWorkspace + starts FSW..."

    $csprojPath = Join-Path $tmpDir 'TestProject.csproj'

    $projInfo = Invoke-McpTool $proc 'roslyn_get_project_info' @{ projectPath = $csprojPath } 45000

    if($null -eq $projInfo) {
        Write-Fail 'roslyn_get_project_info returned null — workspace did not load'
    }
    elseif(Get-Prop $projInfo 'error') {
        Write-Fail "roslyn_get_project_info error: $(Get-Prop $projInfo 'error')"
    }
    else {
        $isMsb = Get-Prop $projInfo 'is_msbuild_workspace'
        $mode  = if($isMsb -eq $true) { 'MSBuildWorkspace' } else { 'AdhocWorkspace' }
        Write-Host "  Workspace mode: $mode"
        Write-Pass "Workspace loaded (FSW now watching $tmpDir)"
    }

    Write-Host ""

    # ── Step 6: Verify extra types are present before branch switch ──────────────

    Write-Host "[6] roslyn_list_types  →  verify ExtraOne/ExtraTwo visible in workspace..."

    $typesBefore = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesBefore) {
        Write-Fail 'roslyn_list_types returned null before branch switch'
    }
    else {
        $names       = @($typesBefore.types)
        $hasExtraOne = [bool]($names -like '*ExtraOne')
        $hasExtraTwo = [bool]($names -like '*ExtraTwo')

        Write-Host "  Types: $($names -join ', ')"

        if($hasExtraOne -and $hasExtraTwo) {
            Write-Pass 'ExtraOne and ExtraTwo visible in workspace (initial state correct)'
        }
        else {
            Write-Fail "Pre-switch: expected ExtraOne=$hasExtraOne ExtraTwo=$hasExtraTwo — workspace may not have loaded correctly"
        }
    }

    Write-Host ""

    # ── Step 7: Branch switch ───────────────────────────────────────────────────

    Write-Host "[7] git checkout main  (deletes Extra1.cs, Extra2.cs)..."

    & git checkout -q main 2>&1 | Out-Null

    if($LASTEXITCODE -ne 0) {
        Write-Fail "git checkout main failed (exit $LASTEXITCODE)"
    }

    $extra1ImmediatelyAfter = Test-Path (Join-Path $tmpDir 'Extra1.cs')

    if($extra1ImmediatelyAfter) {
        Write-Host "  Warning: Extra1.cs still on disk immediately after checkout — git may be slow." -ForegroundColor Yellow
    }
    else {
        Write-Host "  git deleted Extra1.cs and Extra2.cs from disk (expected)."
    }

    Write-Host "  Waiting ${FswWaitMs}ms for FSW debounce to fire and reloadVersion to increment..."

    Start-Sleep -Milliseconds $FswWaitMs

    Write-Host ""

    # ── Step 8: Disk check (primary assertion) ───────────────────────────────────

    Write-Host "[8] Disk check  —  files must NOT be re-created by RM..."

    $extra1OnDisk = Test-Path (Join-Path $tmpDir 'Extra1.cs')
    $extra2OnDisk = Test-Path (Join-Path $tmpDir 'Extra2.cs')

    if(!$extra1OnDisk -and !$extra2OnDisk) {
        Write-Pass 'Extra1.cs and Extra2.cs are NOT on disk'
    }
    else {
        $msg = "BUG REPRODUCED — zero-byte ghost files re-created by RM! Extra1.cs=$extra1OnDisk Extra2.cs=$extra2OnDisk"
        Write-Fail $msg

        foreach($ghost in @('Extra1.cs', 'Extra2.cs')) {
            $ghostPath = Join-Path $tmpDir $ghost
            if(Test-Path $ghostPath) {
                $sz = (Get-Item $ghostPath).Length
                Write-Host "    $ghost  ($sz bytes)" -ForegroundColor Red
            }
        }
    }

    Write-Host ""

    # ── Step 9: git status check (primary assertion) ─────────────────────────────

    Write-Host "[9] git status  —  working tree must be clean..."

    $gitStatus = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatus)) {
        Write-Pass 'git status is clean'
    }
    else {
        Write-Fail "BUG REPRODUCED — git status shows unexpected changes (re-created files):`n    $gitStatus"
    }

    Write-Host ""

    # ── Step 10: Workspace check (triggers reload) ───────────────────────────────

    Write-Host "[10] roslyn_list_types  →  triggers workspace reload; Extra types must be gone..."

    $typesAfter = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesAfter) {
        Write-Fail 'roslyn_list_types returned null after branch switch'
    }
    else {
        $namesAfter  = @($typesAfter.types)
        $goneOne     = -not [bool]($namesAfter -like '*ExtraOne')
        $goneTwo     = -not [bool]($namesAfter -like '*ExtraTwo')

        Write-Host "  Types: $($namesAfter -join ', ')"

        if($goneOne -and $goneTwo) {
            Write-Pass 'ExtraOne and ExtraTwo absent from workspace after reload'
        }
        else {
            Write-Fail "Post-switch: ExtraOne/ExtraTwo still in workspace (gone: one=$goneOne two=$goneTwo)"
        }
    }
}
finally {
    Pop-Location -ErrorAction SilentlyContinue

    if($null -ne $proc) {

        if(!$proc.HasExited) {
            $proc.StandardInput.Close()
            $null = $proc.WaitForExit(3000)

            if(!$proc.HasExited) {
                $proc.Kill($true)
            }
        }

        $proc.Dispose()
    }

    if(Test-Path $tmpDir) {
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── Summary ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════════"

if($passAll) {
    Write-Host "  RESULT: ALL CHECKS PASSED ✓" -ForegroundColor Green
}
else {
    Write-Host "  RESULT: $($failures.Count) CHECK(S) FAILED ✗" -ForegroundColor Red

    foreach($f in $failures) {
        Write-Host "    - $f" -ForegroundColor Red
    }
}

Write-Host "══════════════════════════════════════════════════════════════"
Write-Host ""

exit ($passAll ? 0 : 1)
