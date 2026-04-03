# Publish a debug build of RoslynMcp and refresh RoslynMcpA.exe.
# RoslynMcpA is a copy of the output exe — keeps the original unlocked while the
# copy is what Copilot / MCP clients actually run, so publishes don't require
# killing the client process.

$ErrorActionPreference = 'Stop'

$outDir  = "$PSScriptRoot\publish\net10.0"
$srcExe  = "$outDir\RoslynMcp.exe"
$copyExe = "$outDir\RoslynMcpA.exe"

# ── 1. Kill any running RoslynMcpA instances ──────────────────────────────────
Write-Host "Stopping RoslynMcpA..." -ForegroundColor Yellow
Stop-Process -Name RoslynMcpA -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# ── 2. Publish ────────────────────────────────────────────────────────────────
Write-Host "Publishing (Debug)..." -ForegroundColor Cyan
dotnet publish src\RoslynMcp\RoslynMcp.csproj `
    -c Debug `
    -f net10.0 `
    -r win-x64 `
    --self-contained `
    -o $outDir

if($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# ── 3. Refresh the copy ───────────────────────────────────────────────────────
Write-Host "Copying $([System.IO.Path]::GetFileName($srcExe)) → $([System.IO.Path]::GetFileName($copyExe))..." -ForegroundColor Cyan
Copy-Item -Path $srcExe -Destination $copyExe -Force

Write-Host "Done. $copyExe is ready." -ForegroundColor Green
