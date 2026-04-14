#Requires -Version 7.0
<#
.SYNOPSIS
    Comprehensive integration test verifying RoslynMcp does not re-create files deleted by git,
    and that workspace sync remains correct across branch switches, stash/pop, renames, and commits.

.DESCRIPTION
    Tests the FSW file-recreation bug fix (commit 7ac654f) and related workspace sync behaviour
    across a multi-scenario suite. A single RoslynMcp server process runs for all scenarios,
    exercising repeated workspace reloads without restart.

    Repository layout:
      main:        BaseClass.cs, AnotherClass.cs, Shared.cs
      feat/alpha:  + Alpha.cs, AlphaHelper.cs
      feat/beta:   + Beta.cs, BetaService.cs
      feat/gamma:  + Gamma.cs, GammaHelper.cs

    Scenario A -- Workspace load + basic ghost prevention
    Scenario B -- Multi-branch round-trip (main -> beta -> gamma)
    Scenario C -- roslyn_replace_in_file edit + force branch switch
    Scenario D -- roslyn_replace_in_code + roslyn_insert_lines + force branch switch
    Scenario E -- roslyn_write_file (new untracked file) + workspace verify + git clean
    Scenario F -- git stash + stash pop with workspace content verification
    Scenario G -- roslyn_preview_rename + roslyn_apply_rename + git reset + git clean
    Scenario H -- RM edit + git commit + git push

.PARAMETER FswWaitMs
    Milliseconds to wait after git operations that change the working tree. Default: 1000.
    DebounceMs is 300 internally; 1000ms gives ~3x headroom.

.PARAMETER RmExe
    Path to RoslynMcp.exe. Defaults to publish\net10.0\RoslynMcp.exe in the repo root.

.PARAMETER Pause
    Pause before each scenario waiting for a keypress.
    SPACE advances to the next scenario; any other key disables further pauses and runs to completion.
    Useful for manually inspecting the test repo in VS between scenarios.

.EXAMPLE
    .\scripts\Test-FswFileRecreation.ps1

.EXAMPLE
    .\scripts\Test-FswFileRecreation.ps1 -Pause

.EXAMPLE
    .\scripts\Test-FswFileRecreation.ps1 -FswWaitMs 2000 -Verbose
#>
param(
    [int]    $FswWaitMs = 2000,
    [string] $RmExe     = '',
    [switch] $Pause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- Paths -----------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot  # scripts\ -> repo root

if(!$RmExe) {
    $RmExe = Join-Path $repoRoot 'publish\net10.0\RoslynMcp.exe'
}

if(!(Test-Path $RmExe)) {
    Write-Error "RoslynMcp.exe not found at: $RmExe`nRun .\pub.ps1 first to build a published executable."
    exit 1
}

# -- helpers ---------------------------------------------------------------------

# Safely read a PSCustomObject property by name -- avoids StrictMode -Version Latest
# throwing on non-existent properties (e.g., 'error' is absent on success responses).
function Get-Prop {
    param($obj, [string] $name)
    $p = $obj.PSObject.Properties[$name]
    if($null -ne $p) { return $p.Value }
    return $null
}

# -- MCP helpers -----------------------------------------------------------------

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
    catch { return $text }  # plain string -- return as-is
}

# -- Test state ------------------------------------------------------------------

$ts            = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$tmpDir        = Join-Path ([IO.Path]::GetTempPath()) "RmFswTest_$ts"
$bareDir       = Join-Path ([IO.Path]::GetTempPath()) "RmFswTest_Bare_$ts"
$proc          = $null
$passAll       = $true
$checks        = 0
$failures      = [System.Collections.Generic.List[string]]::new()
$stepDisabled  = $false  # set to $true once the user presses a non-SPACE key in Wait-Step

function Write-Pass {
    param([string] $msg)
    $script:checks++
    Write-Host "  PASS  $msg" -ForegroundColor Green
}

function Write-Fail {
    param([string] $msg)
    $script:passAll = $false
    $script:checks++
    $script:failures.Add($msg)
    Write-Host "  FAIL  $msg" -ForegroundColor Red
}

function Write-ScenarioHeader {
    param([string] $label, [string] $desc)
    Write-Host ""
    Write-Host "-- Scenario $label  $desc" -ForegroundColor Cyan
    Write-Host ""

    Wait-Step "Scenario $label"
}

# Reads a single keypress, ignoring standalone modifier keys (Shift, Ctrl, Alt and their
# left/right variants) so they don't accidentally trigger "run to end".
function Read-ActionKey {

    while($true) {

        $key = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

        # VK codes: Shift=16/160/161  Ctrl=17/162/163  Alt=18/164/165
        if($key.VirtualKeyCode -notin @(16, 17, 18, 160, 161, 162, 163, 164, 165)) {
            return $key
        }
    }
}

# Pauses for a keypress when -Pause is active and the user has not yet pressed a non-SPACE key.
# SPACE advances to the next step; C copies $CopyPath to the clipboard and stays paused;
# any other key disables future pauses and runs to completion.
function Wait-Step {
    param(
        [string] $label    = 'next step',
        [string] $CopyPath = ''
    )

    if(!$Pause -or $script:stepDisabled) { return }

    $copyHint = if($CopyPath) { '   C = copy path to clipboard' } else { '' }
    Write-Host "  [PAUSED: $label]" -ForegroundColor Yellow
    Write-Host "  SPACE = next step${copyHint}   any other key = run to end..." -ForegroundColor DarkYellow

    while($true) {

        $key = Read-ActionKey

        if($CopyPath -and ($key.Character -eq 'c' -or $key.Character -eq 'C')) {

            Set-Clipboard $CopyPath
            Write-Host "  Copied: $CopyPath" -ForegroundColor DarkCyan
            # Stay paused — wait for the next key.
        }
        elseif($key.VirtualKeyCode -eq 32) {
            break  # SPACE — advance to next step
        }
        else {

            # Any other key disables further pauses for the rest of the run.
            $script:stepDisabled = $true
            Write-Host "  Pause disabled — running to end." -ForegroundColor DarkYellow
            break
        }
    }

    Write-Host ""
}

# Runs a state-changing git command. When -Pause is active, pauses for a keypress before
# executing. -Retry: if the command fails, prompts the user to fix the issue and retry
# (intended for non-force checkouts that can fail due to uncommitted changes).
# Does not wrap read-only queries (git status, git rev-parse) -- call those directly.
function Invoke-Git {
    param(
        [Parameter(Position = 0)]
        [string[]] $GitArgs,
        [switch]   $Retry
    )

    $display = "git $($GitArgs -join ' ')"

    while($true) {

        if($Pause -and !$script:stepDisabled) {
            Wait-Step $display
        }

        $out = & git @GitArgs 2>&1
        if($LASTEXITCODE -eq 0) { return }

        if($out) { Write-Host "  $display failed: $out" -ForegroundColor Red }

        if(!$Retry -or !$Pause -or $script:stepDisabled) { return }

        Write-Host "  Stash or revert changes in VS, then:" -ForegroundColor Yellow
        Write-Host "  R = retry   S = skip (continue, failures will be recorded)   any other key = run to end..." -ForegroundColor DarkYellow

        $key = Read-ActionKey
        Write-Host ""

        if($key.Character -eq 'r' -or $key.Character -eq 'R') {
            # loop and retry
        }
        elseif($key.Character -eq 's' -or $key.Character -eq 'S') {
            return
        }
        else {
            $script:stepDisabled = $true
            Write-Host "  Pause disabled — running to end." -ForegroundColor DarkYellow
            return
        }
    }
}

# Non-force git checkout with retry on failure (uncommitted-changes guard).
function Invoke-GitCheckout {
    param([string] $branch)
    Invoke-Git @('checkout', '-q', $branch) -Retry
}

# Checks that the current branch and working tree match expectations at the start of a scenario.
# Records a failure if the preconditions are violated -- does not abort the scenario.
function Test-ScenarioPreconditions {
    param([string] $scenario, [string] $expectedBranch)

    $branch = (& git rev-parse --abbrev-ref HEAD 2>&1).Trim()

    if($branch -ne $expectedBranch) {
        Write-Fail "$scenario PREFLIGHT: expected branch '$expectedBranch', got '$branch'"
    }

    $status = (@(& git status --porcelain) -join "`n").Trim()

    if(-not [string]::IsNullOrEmpty($status)) {
        Write-Fail "$scenario PREFLIGHT: working tree not clean:`n    $status"
    }
}

# Ensures the workspace is loaded for the current branch by calling roslyn_get_project_info.
# Returns the result, or $null on failure (also records a failure in that case).
function Invoke-WorkspaceLoad {
    param(
        [System.Diagnostics.Process] $proc,
        [string]                     $csprojPath,
        [string]                     $scenario
    )

    $pi = Invoke-McpTool $proc 'roslyn_get_project_info' @{ projectPath = $csprojPath } 45000

    if($null -eq $pi -or (Get-Prop $pi 'error')) {
        Write-Fail "${scenario}: roslyn_get_project_info failed -- $(Get-Prop $pi 'error')"
        return $null
    }

    return $pi
}

# -- Project file content written with individual lines to avoid here-string conflicts --------

function New-CsprojContent {
    return @(
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup>',
        '    <OutputType>Library</OutputType>',
        '    <TargetFramework>net10.0</TargetFramework>',
        '    <Nullable>enable</Nullable>',
        '    <ImplicitUsings>enable</ImplicitUsings>',
        '  </PropertyGroup>',
        '</Project>'
    ) -join "`n"
}
function New-SlnContent {
    return @(
        '',
        'Microsoft Visual Studio Solution File, Format Version 12.00',
        '# Visual Studio Version 17',
        'VisualStudioVersion = 17.14.37111.16',
        'MinimumVisualStudioVersion = 10.0.40219.1',
        'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "TestProject", "TestProject.csproj", "{C5C64610-488D-AA8D-59AF-87C40156CD64}"',
        'EndProject',
        'Global',
        "`tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
        "`t`tDebug|Any CPU = Debug|Any CPU",
        "`t`tRelease|Any CPU = Release|Any CPU",
        "`tEndGlobalSection",
        "`tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
        "`t`t{C5C64610-488D-AA8D-59AF-87C40156CD64}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
        "`t`t{C5C64610-488D-AA8D-59AF-87C40156CD64}.Debug|Any CPU.Build.0 = Debug|Any CPU",
        "`t`t{C5C64610-488D-AA8D-59AF-87C40156CD64}.Release|Any CPU.ActiveCfg = Release|Any CPU",
        "`t`t{C5C64610-488D-AA8D-59AF-87C40156CD64}.Release|Any CPU.Build.0 = Release|Any CPU",
        "`tEndGlobalSection",
        "`tGlobalSection(SolutionProperties) = preSolution",
        "`t`tHideSolutionNode = FALSE",
        "`tEndGlobalSection",
        "`tGlobalSection(ExtensibilityGlobals) = postSolution",
        "`t`tSolutionGuid = {8E17AD62-3D0F-468B-A46F-55AE8A56A2D0}",
        "`tEndGlobalSection",
        'EndGlobal'
    ) -join "`n"
}


try {
    Write-Host ""
    Write-Host "======================================================================"
    Write-Host "  RoslynMcp FSW File-Recreation Test  (Extended Suite)"
    Write-Host "======================================================================"
    Write-Host "  RM exe   : $RmExe"
    Write-Host "  FSW wait : ${FswWaitMs}ms  (DebounceMs=300; default=2000)"
    Write-Host "  Pause    : $(if($Pause) { 'on (SPACE=step, C=copy path, any other key=run to end)' } else { 'off' })"
    Write-Host "  Temp dir : $tmpDir"
    Write-Host ""

    # -- Setup: Create temp git repo -----------------------------------------------

    Write-Host "[Setup] Creating test git repo with 4 branches..."

    $null = New-Item -ItemType Directory -Path $tmpDir
    Push-Location $tmpDir

    & git init --initial-branch=main -q 2>&1 | Out-Null
    & git config user.email 'fsw-test@localhost'
    & git config user.name  'FswTest'

    # Minimal SDK project -- no NuGet packages; MSBuildWorkspace loads without extra restore work.
    Set-Content 'TestProject.csproj' (New-CsprojContent) -NoNewline
    Set-Content 'TestProject.sln'   (New-SlnContent)    -NoNewline

    Set-Content 'BaseClass.cs' "namespace TestProject;`npublic class BaseClass { public void DoWork() { } }"
    Set-Content 'AnotherClass.cs' "namespace TestProject;`npublic class AnotherClass { public int Value => 42; }"

    # Shared.cs is on main and inherited by all feature branches.
    Set-Content 'Shared.cs' "namespace TestProject;`npublic class Shared { public string Name => `"shared`"; public void Run() { } }"

    Set-Content '.gitignore' "bin/`nobj/`n.vs/`n*.user"

    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Initial commit' 2>&1 | Out-Null

    # feat/alpha: Alpha.cs + AlphaHelper.cs
    & git checkout -q -b feat/alpha 2>&1 | Out-Null
    Set-Content 'Alpha.cs' "namespace TestProject;`npublic class Alpha { public void DoAlpha() { } }"
    Set-Content 'AlphaHelper.cs' "namespace TestProject;`npublic class AlphaHelper { public int Compute() => 0; }"
    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Add Alpha.cs and AlphaHelper.cs' 2>&1 | Out-Null

    # feat/beta off main: Beta.cs + BetaService.cs
    & git checkout -q main 2>&1 | Out-Null
    & git checkout -q -b feat/beta 2>&1 | Out-Null
    Set-Content 'Beta.cs' "namespace TestProject;`npublic class Beta { public void DoBeta() { } }"
    Set-Content 'BetaService.cs' "namespace TestProject;`npublic class BetaService { public string ServiceName => `"beta`"; }"
    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Add Beta.cs and BetaService.cs' 2>&1 | Out-Null

    # feat/gamma off main: Gamma.cs + GammaHelper.cs
    & git checkout -q main 2>&1 | Out-Null
    & git checkout -q -b feat/gamma 2>&1 | Out-Null
    Set-Content 'Gamma.cs' "namespace TestProject;`npublic class Gamma { public void Run() { } }"
    Set-Content 'GammaHelper.cs' "namespace TestProject;`npublic class GammaHelper { public int GetCount() => 0; }"
    & git add . 2>&1 | Out-Null
    & git commit -q -m 'Add Gamma.cs and GammaHelper.cs' 2>&1 | Out-Null

    # Bare local remote for push testing in Scenario H.
    Write-Host "  Setting up bare remote..."
    $null = & git init --bare -q $bareDir 2>&1
    & git remote add origin $bareDir 2>&1 | Out-Null
    & git push -q origin main feat/alpha feat/beta feat/gamma 2>&1 | Out-Null

    # Start on feat/alpha for Scenario A.
    & git checkout -q feat/alpha 2>&1 | Out-Null

    Write-Host "  Repo ready. Branches: main, feat/alpha, feat/beta, feat/gamma"
    Write-Host "  Local:   $tmpDir"
    Write-Host "  Remote:  $bareDir"
    Write-Host ""

    # -- dotnet restore -----------------------------------------------------------

    Write-Host "[Setup] dotnet restore (MSBuildWorkspace needs SDK props resolved)..."

    & dotnet restore 'TestProject.csproj' -v q 2>&1 | Out-Null

    if($LASTEXITCODE -ne 0) {
        Write-Fail "dotnet restore failed (exit $LASTEXITCODE) -- MSBuildWorkspace may not load"
        Write-Host "  Continuing, but results may be unreliable." -ForegroundColor Yellow
    }
    else {
        Write-Host "  Restore complete."
    }

    Write-Host ""

    # -- Start RM server ----------------------------------------------------------

    Write-Host "[Setup] Starting RoslynMcp server..."

    $psi                        = [System.Diagnostics.ProcessStartInfo]::new($RmExe)
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.WorkingDirectory       = $tmpDir

    # Strip MSBuild env vars set by any parent RoslynMcp process -- inheriting MSBUILD_EXE_PATH
    # causes the child's MSBuildLocator.RegisterDefaults() to fail with "MSBuild not found".
    # Same fix as DotnetRunner.cs strips for child dotnet-build processes.
    foreach($key in @('MSBUILD_EXE_PATH', 'MSBuildExtensionsPath', 'MSBuildSDKsPath', 'MSBuildExtensionsPath32')) {
        if($psi.Environment.ContainsKey($key)) {
            $psi.Environment.Remove($key)
        }
    }

    $proc = [System.Diagnostics.Process]::Start($psi)

    # Drain stderr asynchronously -- prevents pipe deadlock if the server writes to stderr.
    $proc.BeginErrorReadLine()

    Start-Sleep -Milliseconds 600  # Allow server startup before first message.

    # -- MCP handshake ------------------------------------------------------------

    Write-Host "[Setup] MCP handshake..."

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
        Write-Fail 'Server did not respond to initialize -- check server logs'
        Write-Host "  Cannot continue without a live server." -ForegroundColor Red
        return
    }

    Send-Mcp $proc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Write-Host "  MCP session initialized."
    Write-Host ""

    Wait-Step "setup complete — open test repo in VS if needed" -CopyPath $tmpDir

    $csprojPath = Join-Path $tmpDir 'TestProject.csproj'

    # ============================================================================
    # Scenario A -- Workspace load + basic ghost prevention
    #   Start: feat/alpha (Alpha.cs + AlphaHelper.cs present)
    #   End:   main (Alpha.cs + AlphaHelper.cs deleted)
    # ============================================================================

    Write-ScenarioHeader 'A' 'Workspace load + basic ghost prevention'

    Test-ScenarioPreconditions 'A' 'feat/alpha'

    $piA = Invoke-WorkspaceLoad $proc $csprojPath 'A'

    if($null -ne $piA) {
        $isMsb = Get-Prop $piA 'is_msbuild_workspace'
        $mode  = if($isMsb -eq $true) { 'MSBuildWorkspace' } else { 'AdhocWorkspace' }
        Write-Host "  Workspace mode: $mode"
        Write-Pass 'A: Workspace loaded (FSW now watching)'
    }

    $typesA1 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesA1) {
        Write-Fail 'A: roslyn_list_types returned null before branch switch'
    }
    else {
        $namesA1 = @($typesA1.types)
        Write-Host "  Types: $($namesA1 -join ', ')"

        if([bool]($namesA1 -like '*Alpha') -and [bool]($namesA1 -like '*AlphaHelper')) {
            Write-Pass 'A: Alpha and AlphaHelper visible in workspace'
        }
        else {
            Write-Fail 'A: Alpha or AlphaHelper not found in workspace on feat/alpha'
        }
    }

    Write-Host "  git checkout main (deletes Alpha.cs, AlphaHelper.cs)..."
    Invoke-GitCheckout 'main'
    Start-Sleep -Milliseconds $FswWaitMs

    $alphaDiskA  = Test-Path (Join-Path $tmpDir 'Alpha.cs')
    $helperDiskA = Test-Path (Join-Path $tmpDir 'AlphaHelper.cs')

    if(!$alphaDiskA -and !$helperDiskA) {
        Write-Pass 'A: Alpha.cs and AlphaHelper.cs NOT on disk (no ghost files)'
    }
    else {
        Write-Fail "A: BUG -- ghost files re-created by RM!  Alpha.cs=$alphaDiskA  AlphaHelper.cs=$helperDiskA"

        foreach($ghost in @('Alpha.cs', 'AlphaHelper.cs')) {
            $p = Join-Path $tmpDir $ghost
            if(Test-Path $p) { Write-Host "    $ghost  ($((Get-Item $p).Length) bytes)" -ForegroundColor Red }
        }
    }

    $gitStatusA = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusA)) {
        Write-Pass 'A: git status clean after checkout main'
    }
    else {
        Write-Fail "A: BUG -- git status shows unexpected changes:`n    $gitStatusA"
    }

    $typesA2 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesA2) {
        Write-Fail 'A: roslyn_list_types returned null after branch switch'
    }
    else {
        $namesA2 = @($typesA2.types)

        if(-not [bool]($namesA2 -like '*Alpha') -and -not [bool]($namesA2 -like '*AlphaHelper')) {
            Write-Pass 'A: Alpha and AlphaHelper absent from workspace after reload'
        }
        else {
            Write-Fail 'A: Alpha or AlphaHelper still in workspace after checkout main'
        }
    }

    # ============================================================================
    # Scenario B -- Multi-branch round-trip (main -> beta -> gamma)
    #   Start: main
    #   End:   feat/gamma
    # ============================================================================

    Write-ScenarioHeader 'B' 'Multi-branch round-trip (main -> beta -> gamma)'

    Test-ScenarioPreconditions 'B' 'main'

    Write-Host "  git checkout feat/beta..."
    Invoke-GitCheckout 'feat/beta'
    Start-Sleep -Milliseconds $FswWaitMs

    $noAlphaB = -not (Test-Path (Join-Path $tmpDir 'Alpha.cs'))
    $betaDisk = Test-Path (Join-Path $tmpDir 'Beta.cs')

    if($noAlphaB -and $betaDisk) {
        Write-Pass 'B: Alpha.cs absent, Beta.cs present on feat/beta'
    }
    else {
        Write-Fail "B: Unexpected disk state on feat/beta -- Alpha.cs absent=$noAlphaB  Beta.cs=$betaDisk"
    }

    $gitStatusB1 = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusB1)) {
        Write-Pass 'B: git status clean on feat/beta'
    }
    else {
        Write-Fail "B: git status not clean on feat/beta:`n    $gitStatusB1"
    }

    $null = Invoke-WorkspaceLoad $proc $csprojPath 'B'

    $typesB1 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesB1) {
        Write-Fail 'B: roslyn_list_types returned null on feat/beta'
    }
    else {
        $namesB1 = @($typesB1.types)

        if([bool]($namesB1 -like '*Beta') -and [bool]($namesB1 -like '*BetaService') -and -not [bool]($namesB1 -like '*Alpha')) {
            Write-Pass 'B: Beta/BetaService present, Alpha absent in workspace on feat/beta'
        }
        else {
            Write-Fail "B: Unexpected workspace types on feat/beta -- expected Beta+BetaService no Alpha. Got: $($namesB1 -join ', ')"
        }
    }

    Write-Host "  git checkout feat/gamma..."
    Invoke-GitCheckout 'feat/gamma'
    Start-Sleep -Milliseconds $FswWaitMs

    $noBetaG = -not (Test-Path (Join-Path $tmpDir 'Beta.cs'))
    $gammaG  = Test-Path (Join-Path $tmpDir 'Gamma.cs')

    if($noBetaG -and $gammaG) {
        Write-Pass 'B: Beta.cs absent, Gamma.cs present on feat/gamma'
    }
    else {
        Write-Fail "B: Unexpected disk state on feat/gamma -- Beta.cs absent=$noBetaG  Gamma.cs=$gammaG"
    }

    $gitStatusB2 = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusB2)) {
        Write-Pass 'B: git status clean on feat/gamma'
    }
    else {
        Write-Fail "B: git status not clean on feat/gamma:`n    $gitStatusB2"
    }

    $null = Invoke-WorkspaceLoad $proc $csprojPath 'B'

    $typesB2 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -eq $typesB2) {
        Write-Fail 'B: roslyn_list_types returned null on feat/gamma'
    }
    else {
        $namesB2 = @($typesB2.types)

        if([bool]($namesB2 -like '*Gamma') -and -not [bool]($namesB2 -like '*Beta')) {
            Write-Pass 'B: Gamma present, Beta absent in workspace on feat/gamma'
        }
        else {
            Write-Fail "B: Unexpected workspace types on feat/gamma -- expected Gamma no Beta. Got: $($namesB2 -join ', ')"
        }
    }

    # ============================================================================
    # Scenario C -- roslyn_replace_in_file edit + force branch switch
    #   Start: feat/gamma (Gamma.cs + GammaHelper.cs present)
    #   End:   feat/alpha (Gamma.cs deleted)
    # ============================================================================

    Write-ScenarioHeader 'C' 'roslyn_replace_in_file edit + force branch switch'

    Test-ScenarioPreconditions 'C' 'feat/gamma'

    $replaceC = Invoke-McpTool $proc 'roslyn_replace_in_file' @{
        filePath    = 'Gamma.cs'
        projectPath = $csprojPath
        # Rename the Run() method -- uses regex to match exact method name.
        # filePath scopes this edit to Gamma.cs only (Shared.cs also has a Run()).
        pattern     = 'Run\(\)'
        replacement = 'RunV2()'
        useRegex    = $true
    } 30000

    if((Get-Prop $replaceC 'applied') -eq $true) {
        Write-Pass 'C: roslyn_replace_in_file applied to Gamma.cs'
    }
    else {
        Write-Fail "C: roslyn_replace_in_file did not apply -- $(Get-Prop $replaceC 'error')$(Get-Prop $replaceC 'message')"
    }

    $gammaDiskC = Get-Content (Join-Path $tmpDir 'Gamma.cs') -Raw -ErrorAction SilentlyContinue

    if($gammaDiskC -like '*RunV2*') {
        Write-Pass 'C: Gamma.cs disk content contains RunV2 (edit verified)'
    }
    else {
        Write-Fail 'C: Gamma.cs disk content does not show RunV2 edit'
    }

    # Force checkout discards the uncommitted edit and switches branches -- Gamma.cs is deleted.
    Write-Host "  git checkout -f feat/alpha (force-discards edit; deletes Gamma.cs)..."
    Invoke-Git 'checkout', '-f', '-q', 'feat/alpha'
    Start-Sleep -Milliseconds $FswWaitMs

    $gammaGoneC = -not (Test-Path (Join-Path $tmpDir 'Gamma.cs'))
    $alphaBackC = Test-Path (Join-Path $tmpDir 'Alpha.cs')

    if($gammaGoneC -and $alphaBackC) {
        Write-Pass 'C: Gamma.cs absent, Alpha.cs present after checkout feat/alpha'
    }
    else {
        Write-Fail "C: Unexpected disk state -- Gamma.cs absent=$gammaGoneC  Alpha.cs=$alphaBackC"
    }

    $gitStatusC = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusC)) {
        Write-Pass 'C: git status clean after force checkout'
    }
    else {
        Write-Fail "C: BUG -- ghost or modified file persists after force checkout:`n    $gitStatusC"
    }

    # ============================================================================
    # Scenario D -- roslyn_replace_in_code + roslyn_insert_lines + force branch switch
    #   Start: feat/alpha
    #   End:   feat/beta (Alpha.cs + AlphaHelper.cs deleted)
    # ============================================================================

    Write-ScenarioHeader 'D' 'roslyn_replace_in_code + roslyn_insert_lines + force branch switch'

    Test-ScenarioPreconditions 'D' 'feat/alpha'

    # Ensure workspace has loaded on feat/alpha after Scenario C's branch switch.
    $null = Invoke-WorkspaceLoad $proc $csprojPath 'D'

    $replaceD = Invoke-McpTool $proc 'roslyn_replace_in_code' @{
        filePath    = 'Alpha.cs'
        nodeKind    = 'method'
        textPattern = 'DoAlpha'
        replacement = 'public void DoAlpha() { var x = 42; }'
        projectPath = $csprojPath
    } 30000

    if((Get-Prop $replaceD 'applied') -eq $true) {
        Write-Pass 'D: roslyn_replace_in_code applied to Alpha.cs'
    }
    else {
        Write-Fail "D: roslyn_replace_in_code did not apply -- $(Get-Prop $replaceD 'error')$(Get-Prop $replaceD 'message')"
    }

    $insertD = Invoke-McpTool $proc 'roslyn_insert_lines' @{
        filePath    = 'AlphaHelper.cs'
        projectPath = $csprojPath
        text        = '    public int ComputeExtra() => 1;'
        insertAfter = 'public int Compute() => 0;'
    } 30000

    if((Get-Prop $insertD 'applied') -eq $true) {
        Write-Pass 'D: roslyn_insert_lines applied to AlphaHelper.cs'
    }
    else {
        Write-Fail "D: roslyn_insert_lines did not apply -- $(Get-Prop $insertD 'error')$(Get-Prop $insertD 'message')"
    }

    Write-Host "  git checkout -f feat/beta (force-discards edits; deletes Alpha.cs + AlphaHelper.cs)..."
    Invoke-Git 'checkout', '-f', '-q', 'feat/beta'
    Start-Sleep -Milliseconds $FswWaitMs

    $alphaGoneD  = -not (Test-Path (Join-Path $tmpDir 'Alpha.cs'))
    $helperGoneD = -not (Test-Path (Join-Path $tmpDir 'AlphaHelper.cs'))

    if($alphaGoneD -and $helperGoneD) {
        Write-Pass 'D: Alpha.cs and AlphaHelper.cs NOT on disk after checkout feat/beta (no ghost files)'
    }
    else {
        Write-Fail "D: BUG -- edited files persist after branch switch!  Alpha.cs absent=$alphaGoneD  AlphaHelper.cs absent=$helperGoneD"

        foreach($ghost in @('Alpha.cs', 'AlphaHelper.cs')) {
            $p = Join-Path $tmpDir $ghost
            if(Test-Path $p) { Write-Host "    $ghost  ($((Get-Item $p).Length) bytes)" -ForegroundColor Red }
        }
    }

    $gitStatusD = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusD)) {
        Write-Pass 'D: git status clean after force checkout to feat/beta'
    }
    else {
        Write-Fail "D: BUG -- ghost or modified file persists after force checkout:`n    $gitStatusD"
    }

    # ============================================================================
    # Scenario E -- roslyn_write_file (new untracked file) + git clean
    #   Start: feat/beta
    #   End:   feat/beta (NewFeature.cs deleted by git clean)
    # ============================================================================

    Write-ScenarioHeader 'E' 'roslyn_write_file (new untracked file) + git clean'

    Test-ScenarioPreconditions 'E' 'feat/beta'

    $null = Invoke-WorkspaceLoad $proc $csprojPath 'E'

    $writeE = Invoke-McpTool $proc 'roslyn_write_file' @{
        filePath    = 'NewFeature.cs'
        projectPath = $csprojPath
        content     = "namespace TestProject;`npublic class NewFeature { public void Do() { } }"
        createNew   = $true
    } 30000

    if((Get-Prop $writeE 'written') -eq $true) {
        Write-Pass 'E: roslyn_write_file created NewFeature.cs'
    }
    else {
        Write-Fail "E: roslyn_write_file did not create file -- $(Get-Prop $writeE 'error')$(Get-Prop $writeE 'message')"
    }

    if(Test-Path (Join-Path $tmpDir 'NewFeature.cs')) {
        Write-Pass 'E: NewFeature.cs exists on disk'
    }
    else {
        Write-Fail 'E: NewFeature.cs not found on disk after roslyn_write_file'
    }

    $gitStatusE1 = (@(& git status --porcelain) -join "`n").Trim()

    if($gitStatusE1 -like '*NewFeature.cs*') {
        Write-Pass 'E: git status shows NewFeature.cs as untracked'
    }
    else {
        Write-Fail "E: NewFeature.cs not reflected in git status: $gitStatusE1"
    }

    # Force RM to observe the new file before deleting it -- confirms workspace sees it.
    # Also proves write_file correctly invalidates the workspace for the new path.
    $typesE1 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -ne $typesE1 -and [bool](@($typesE1.types) -like '*NewFeature')) {
        Write-Pass 'E: Workspace sees NewFeature after roslyn_write_file'
    }
    else {
        $got = if($null -eq $typesE1) { 'null result' } else { @($typesE1.types) -join ', ' }
        Write-Fail "E: NewFeature not visible in workspace after write_file -- $got"
    }

    Write-Host "  git clean -f (removes untracked files)..."
    Invoke-Git 'clean', '-f', '-q'
    Start-Sleep -Milliseconds $FswWaitMs

    if(-not (Test-Path (Join-Path $tmpDir 'NewFeature.cs'))) {
        Write-Pass 'E: NewFeature.cs removed by git clean'
    }
    else {
        Write-Fail 'E: NewFeature.cs still on disk after git clean'
    }

    $gitStatusE2 = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusE2)) {
        Write-Pass 'E: git status clean after git clean'
    }
    else {
        Write-Fail "E: BUG -- unexpected changes after git clean:`n    $gitStatusE2"
    }

    # Trigger workspace reload -- NewFeature must no longer be visible.
    $typesE2 = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -ne $typesE2 -and -not [bool](@($typesE2.types) -like '*NewFeature')) {
        Write-Pass 'E: NewFeature absent from workspace after git clean'
    }
    else {
        Write-Fail 'E: BUG -- NewFeature still in workspace after git clean deleted the file'
    }

    # ============================================================================
    # Scenario F -- git stash + stash pop with workspace content verification
    #   Start: feat/beta (clean)
    #   End:   feat/beta (Shared.cs modified -- stash pop restored the edit)
    # ============================================================================

    Write-ScenarioHeader 'F' 'git stash + stash pop (workspace content verification)'

    Test-ScenarioPreconditions 'F' 'feat/beta'

    $replaceF = Invoke-McpTool $proc 'roslyn_replace_in_file' @{
        filePath    = 'Shared.cs'
        projectPath = $csprojPath
        pattern     = '"shared"'
        replacement = '"shared-modified"'
    } 30000

    if((Get-Prop $replaceF 'applied') -eq $true) {
        Write-Pass 'F: roslyn_replace_in_file modified Shared.cs'
    }
    else {
        Write-Fail "F: roslyn_replace_in_file did not apply to Shared.cs -- $(Get-Prop $replaceF 'error')$(Get-Prop $replaceF 'message')"
    }

    $sharedDiskF1 = Get-Content (Join-Path $tmpDir 'Shared.cs') -Raw -ErrorAction SilentlyContinue

    if($sharedDiskF1 -like '*shared-modified*') {
        Write-Pass 'F: Shared.cs disk content shows edit before stash'
    }
    else {
        Write-Fail 'F: Shared.cs disk content does not show edit before stash'
    }

    # Stash only Shared.cs -- avoids capturing any accidental working-tree noise.
    Write-Host "  git stash push -- Shared.cs..."
    Invoke-Git 'stash', 'push', '-q', '--', 'Shared.cs'
    Start-Sleep -Milliseconds $FswWaitMs

    $gitStatusF1 = (@(& git status --porcelain) -join "`n").Trim()

    if([string]::IsNullOrEmpty($gitStatusF1)) {
        Write-Pass 'F: git status clean after stash'
    }
    else {
        Write-Fail "F: git status not clean after stash:`n    $gitStatusF1"
    }

    $sharedDiskF2 = Get-Content (Join-Path $tmpDir 'Shared.cs') -Raw -ErrorAction SilentlyContinue

    if($sharedDiskF2 -notlike '*shared-modified*') {
        Write-Pass 'F: Shared.cs reverted on disk after stash (no ghost of edited content)'
    }
    else {
        Write-Fail 'F: BUG -- Shared.cs still shows "shared-modified" on disk after stash'
    }

    # Verify the workspace also sees the reverted content (FSW should have triggered a reload).
    $searchF1 = Invoke-McpTool $proc 'roslyn_search_files' @{
        pattern     = 'shared-modified'
        projectPath = $csprojPath
    } 30000

    if((Get-Prop $searchF1 'total_matches') -eq 0) {
        Write-Pass 'F: Workspace sees Shared.cs reverted after stash ("shared-modified" absent)'
    }
    else {
        Write-Fail 'F: BUG -- Workspace still shows "shared-modified" in Shared.cs after stash'
    }

    Write-Host "  git stash pop..."
    Invoke-Git 'stash', 'pop', '-q'
    Start-Sleep -Milliseconds $FswWaitMs

    $sharedDiskF3 = Get-Content (Join-Path $tmpDir 'Shared.cs') -Raw -ErrorAction SilentlyContinue

    if($sharedDiskF3 -like '*shared-modified*') {
        Write-Pass 'F: Shared.cs edit restored on disk after stash pop'
    }
    else {
        Write-Fail 'F: Shared.cs does not show restored edit after stash pop'
    }

    $gitStatusF2 = (@(& git status --porcelain) -join "`n").Trim()

    if($gitStatusF2 -like '*Shared.cs*') {
        Write-Pass 'F: git status shows Shared.cs modified after stash pop (expected)'
    }
    else {
        Write-Fail "F: Shared.cs not reflected as modified after stash pop: $gitStatusF2"
    }

    # Verify the workspace also sees the restored content.
    $searchF2 = Invoke-McpTool $proc 'roslyn_search_files' @{
        pattern     = 'shared-modified'
        projectPath = $csprojPath
    } 30000

    if((Get-Prop $searchF2 'total_matches') -gt 0) {
        Write-Pass 'F: Workspace sees "shared-modified" in Shared.cs after stash pop'
    }
    else {
        Write-Fail 'F: BUG -- Workspace does not see restored content in Shared.cs after stash pop'
    }

    # ============================================================================
    # Scenario G -- RM rename + git reset + git clean
    #   Start: feat/beta (Shared.cs modified from Scenario F)
    #   Switches to feat/alpha; ends on feat/alpha (clean)
    # ============================================================================

    Write-ScenarioHeader 'G' 'RM rename + git reset --hard + git clean'

    # Force checkout discards the Shared.cs modification left by Scenario F.
    Write-Host "  git checkout -f feat/alpha (force-discards Shared.cs edit from Scenario F)..."
    Invoke-Git 'checkout', '-f', '-q', 'feat/alpha'
    Start-Sleep -Milliseconds $FswWaitMs

    Test-ScenarioPreconditions 'G' 'feat/alpha'

    $null = Invoke-WorkspaceLoad $proc $csprojPath 'G'

    # Preview: AlphaHelper -> AlphaUtility
    $previewG = Invoke-McpTool $proc 'roslyn_preview_rename' @{
        symbolName  = 'AlphaHelper'
        newName     = 'AlphaUtility'
        projectPath = $csprojPath
    } 45000

    $previewTokenG = Get-Prop $previewG 'token'
    $previewDiffG  = Get-Prop $previewG 'diff'
    $previewErrG   = Get-Prop $previewG 'error'

    if($null -ne $previewTokenG -and $null -ne $previewDiffG -and $null -eq $previewErrG) {
        Write-Pass 'G: roslyn_preview_rename returned token and diff'
        $diffPreview = $previewDiffG.Substring(0, [Math]::Min(120, $previewDiffG.Length))
        Write-Host "  Diff preview: $diffPreview..."
    }
    else {
        $errMsg = if($null -ne $previewErrG) { $previewErrG } else { 'token or diff was null' }
        Write-Fail "G: roslyn_preview_rename failed -- $errMsg"
    }

    if($null -ne $previewTokenG) {

        $applyG        = Invoke-McpTool $proc 'roslyn_apply_rename' @{
            token       = $previewTokenG
            approval    = 'y'
            projectPath = $csprojPath
        } 45000

        $applyErrG     = Get-Prop $applyG 'error'
        $filesWrittenG = Get-Prop $applyG 'filesWritten'
        $filesDeletedG = Get-Prop $applyG 'filesDeleted'
        $filesRenamedG = Get-Prop $applyG 'filesRenamed'

        if($null -eq $applyErrG) {
            Write-Pass "G: roslyn_apply_rename succeeded (written=$filesWrittenG deleted=$filesDeletedG renamed=$filesRenamedG)"
        }
        else {
            Write-Fail "G: roslyn_apply_rename failed -- $applyErrG"
        }

        $helperGoneG  = -not (Test-Path (Join-Path $tmpDir 'AlphaHelper.cs'))
        $utilityDiskG = Test-Path (Join-Path $tmpDir 'AlphaUtility.cs')

        if($helperGoneG -and $utilityDiskG) {
            Write-Pass 'G: AlphaHelper.cs absent, AlphaUtility.cs present after rename'
        }
        else {
            Write-Fail "G: Unexpected disk state after rename -- AlphaHelper.cs absent=$helperGoneG  AlphaUtility.cs=$utilityDiskG"
        }

        # Revert: git reset restores AlphaHelper.cs; git clean removes untracked AlphaUtility.cs.
        # Both commands affect the working tree simultaneously -- use a longer wait.
        Write-Host "  git reset --hard HEAD && git clean -fd (revert rename)..."
        Invoke-Git 'reset', '--hard', '-q', 'HEAD'
        Invoke-Git 'clean', '-f', '-d', '-q'
        Start-Sleep -Milliseconds ([Math]::Max($FswWaitMs, 1500))

        $helperBackG  = Test-Path (Join-Path $tmpDir 'AlphaHelper.cs')
        $utilityGoneG = -not (Test-Path (Join-Path $tmpDir 'AlphaUtility.cs'))

        if($helperBackG -and $utilityGoneG) {
            Write-Pass 'G: AlphaHelper.cs restored, AlphaUtility.cs removed after git revert'
        }
        else {
            Write-Fail "G: Unexpected disk state after git revert -- AlphaHelper.cs back=$helperBackG  AlphaUtility.cs gone=$utilityGoneG"
        }

        $gitStatusG = (@(& git status --porcelain) -join "`n").Trim()

        if([string]::IsNullOrEmpty($gitStatusG)) {
            Write-Pass 'G: git status clean after git reset --hard + git clean'
        }
        else {
            Write-Fail "G: BUG -- git status not clean after revert:`n    $gitStatusG"
        }

        $typesG = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

        if($null -ne $typesG) {
            $namesG = @($typesG.types)

            if([bool]($namesG -like '*AlphaHelper') -and -not [bool]($namesG -like '*AlphaUtility')) {
                Write-Pass 'G: AlphaHelper visible, AlphaUtility absent in workspace after revert'
            }
            else {
                Write-Fail "G: Workspace mismatch after revert -- AlphaHelper=$(([bool]($namesG -like '*AlphaHelper')))  AlphaUtility absent=$(-not [bool]($namesG -like '*AlphaUtility'))"
            }
        }
        else {
            Write-Fail 'G: roslyn_list_types returned null after rename revert'
        }
    }

    # ============================================================================
    # Scenario H -- RM edit + git commit + git push
    #   Start: feat/alpha (clean, after Scenario G revert)
    #   End:   feat/alpha (committed edit, pushed to bare remote)
    # ============================================================================

    Write-ScenarioHeader 'H' 'RM edit + git commit + git push'

    Test-ScenarioPreconditions 'H' 'feat/alpha'

    $null = Invoke-WorkspaceLoad $proc $csprojPath 'H'

    # After Scenario G's git reset --hard, Alpha.cs is back to original: DoAlpha() { }.
    $replaceH = Invoke-McpTool $proc 'roslyn_replace_in_file' @{
        filePath    = 'Alpha.cs'
        projectPath = $csprojPath
        pattern     = 'DoAlpha\(\) \{ \}'
        replacement = 'DoAlpha() { /* tested */ }'
        useRegex    = $true
    } 30000

    if((Get-Prop $replaceH 'applied') -eq $true) {
        Write-Pass 'H: roslyn_replace_in_file added comment to Alpha.cs'
    }
    else {
        Write-Fail "H: roslyn_replace_in_file did not apply -- $(Get-Prop $replaceH 'error')$(Get-Prop $replaceH 'message')"
    }

    Write-Host "  git add Alpha.cs && git commit..."
    Invoke-Git 'add', 'Alpha.cs'
    Invoke-Git 'commit', '-q', '-m', 'test: RM edit committed via FSW test script'

    if($LASTEXITCODE -eq 0) {
        Write-Pass 'H: git commit succeeded'
    }
    else {
        Write-Fail "H: git commit failed (exit $LASTEXITCODE)"
    }

    Write-Host "  git push origin feat/alpha..."
    Invoke-Git 'push', '-q', 'origin', 'feat/alpha'

    if($LASTEXITCODE -eq 0) {
        Write-Pass 'H: git push to bare remote succeeded'
    }
    else {
        Write-Fail "H: git push failed (exit $LASTEXITCODE)"
    }

    # Workspace must remain consistent after commit + push.
    $typesH = Invoke-McpTool $proc 'roslyn_list_types' @{ projectPath = $csprojPath } 45000

    if($null -ne $typesH) {

        if([bool](@($typesH.types) -like '*Alpha') -and [bool](@($typesH.types) -like '*AlphaHelper')) {
            Write-Pass 'H: Workspace consistent after commit + push (Alpha and AlphaHelper visible)'
        }
        else {
            Write-Fail "H: Unexpected workspace types after commit + push: $(@($typesH.types) -join ', ')"
        }
    }
    else {
        Write-Fail 'H: roslyn_list_types returned null after commit + push'
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

    foreach($dir in @($tmpDir, $bareDir)) {
        if(Test-Path $dir) {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# -- Summary ---------------------------------------------------------------------

Write-Host ""
Write-Host "======================================================================"

if($passAll) {
    Write-Host "  RESULT: ALL $checks CHECKS PASSED" -ForegroundColor Green
}
else {
    Write-Host "  RESULT: $($failures.Count) / $checks CHECKS FAILED" -ForegroundColor Red

    foreach($f in $failures) {
        Write-Host "    - $f" -ForegroundColor Red
    }
}

Write-Host "======================================================================"
Write-Host ""

exit ($passAll ? 0 : 1)
