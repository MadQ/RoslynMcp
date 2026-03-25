#!/usr/bin/env pwsh
# Manual MCP server test - sends a single tool invocation and reads response

$ErrorActionPreference = "Stop"

Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Manual MCP Server Test - Single Tool Invocation" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Build first
Write-Host "Building server..." -ForegroundColor Yellow
dotnet build src/RoslynMcp/RoslynMcp.csproj -f net10.0 --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "✓ Build successful" -ForegroundColor Green
Write-Host ""

# Start the MCP server process
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --project src/RoslynMcp/RoslynMcp.csproj -f net10.0 --no-build -- ."
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)

# Capture stderr in background
$stderrJob = Start-Job -ScriptBlock {
    param($process)
    while ($line = $process.StandardError.ReadLine()) {
        Write-Output $line
    }
} -ArgumentList $proc

Write-Host "Starting MCP server (PID: $($proc.Id))..." -ForegroundColor Yellow
Start-Sleep -Milliseconds 500

# Send initialize request
$initRequest = @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2024-11-05"
        capabilities = @{}
        clientInfo = @{
            name = "manual-test"
            version = "1.0"
        }
    }
} | ConvertTo-Json -Compress

Write-Host "Sending initialize request..." -ForegroundColor Yellow
$proc.StandardInput.WriteLine($initRequest)
$proc.StandardInput.Flush()

# Read initialize response
$initResponse = $proc.StandardOutput.ReadLine()
Write-Host "Initialize response:" -ForegroundColor Green
Write-Host $initResponse -ForegroundColor Gray
Write-Host ""

# Send initialized notification
$initializedNotif = @{
    jsonrpc = "2.0"
    method = "notifications/initialized"
} | ConvertTo-Json -Compress

$proc.StandardInput.WriteLine($initializedNotif)
$proc.StandardInput.Flush()
Start-Sleep -Milliseconds 200

# Send a simple tool call - get_project_info (no parameters required)
$toolRequest = @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/call"
    params = @{
        name = "roslyn_get_project_info"
        arguments = @{}
    }
} | ConvertTo-Json -Compress

Write-Host "Sending roslyn_get_project_info request..." -ForegroundColor Yellow
$proc.StandardInput.WriteLine($toolRequest)
$proc.StandardInput.Flush()

# Read tool response with timeout
$timeout = 5000 # 5 seconds
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$toolResponse = $null

while ($sw.ElapsedMilliseconds -lt $timeout) {
    if ($proc.StandardOutput.Peek() -ge 0) {
        $toolResponse = $proc.StandardOutput.ReadLine()
        break
    }
    Start-Sleep -Milliseconds 50
}

if ($toolResponse) {
    Write-Host "Tool response:" -ForegroundColor Green
    # Pretty print the JSON
    $json = $toolResponse | ConvertFrom-Json | ConvertTo-Json -Depth 10
    Write-Host $json -ForegroundColor Gray
    Write-Host ""
    Write-Host "✓ MCP server is working correctly!" -ForegroundColor Green
} else {
    Write-Host "✗ No response received within timeout" -ForegroundColor Red
}

# Cleanup
Write-Host ""
Write-Host "Shutting down server..." -ForegroundColor Yellow
$proc.Kill()
$proc.WaitForExit(1000)
Stop-Job -Job $stderrJob -ErrorAction SilentlyContinue
Remove-Job -Job $stderrJob -Force -ErrorAction SilentlyContinue

# Show any stderr output
$stderrOutput = Receive-Job -Job $stderrJob -ErrorAction SilentlyContinue
if ($stderrOutput) {
    Write-Host ""
    Write-Host "Stderr output:" -ForegroundColor Yellow
    $stderrOutput | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host "Test complete!" -ForegroundColor Cyan
