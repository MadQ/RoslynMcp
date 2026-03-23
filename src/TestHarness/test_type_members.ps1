#!/usr/bin/env pwsh
# Quick test for TypeMembersTool with new projectPath parameter

$ErrorActionPreference = "Stop"

Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  TypeMembersTool Test — Global Context Pattern" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$repoRoot = $PSScriptRoot
$serverExe = "$repoRoot\src\RoslynMcp\bin\Debug\net10.0\RoslynMcp.exe"
$projectPath = "$repoRoot\src\RoslynMcp"

if(!(Test-Path $serverExe)) {
    Write-Host "ERROR: RoslynMcp.exe not found. Build first with: dotnet build src/RoslynMcp" -ForegroundColor Red
    exit 1
}

Write-Host "Server:  $serverExe" -ForegroundColor Gray
Write-Host "Project: $projectPath" -ForegroundColor Gray
Write-Host ""

# Start server without required args (testing global context)
$proc = Start-Process -FilePath $serverExe -NoNewWindow -PassThru -RedirectStandardInput "input.json" -RedirectStandardOutput "output.json" -RedirectStandardError "stderr.log"

Start-Sleep -Milliseconds 500

# Test 1: Initialize MCP session
Write-Host "Test 1: Initialize MCP session..." -NoNewline
$initRequest = @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2024-11-05"
        capabilities = @{}
        clientInfo = @{
            name = "TypeMembersTest"
            version = "1.0"
        }
    }
} | ConvertTo-Json -Depth 10

$initRequest | Out-File -FilePath "input.json" -Encoding utf8 -NoNewline

Start-Sleep -Milliseconds 200

if(Test-Path "output.json") {
    $response = Get-Content "output.json" -Raw | ConvertFrom-Json
    if($response.result) {
        Write-Host " ✓ PASS" -ForegroundColor Green
    } else {
        Write-Host " ✗ FAIL" -ForegroundColor Red
        Write-Host "Response: $response" -ForegroundColor Yellow
    }
} else {
    Write-Host " ✗ FAIL (no output)" -ForegroundColor Red
}

# Test 2: Call get_type_members with projectPath
Write-Host "Test 2: get_type_members with explicit projectPath..." -NoNewline
$toolRequest = @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/call"
    params = @{
        name = "get_type_members"
        arguments = @{
            typeName = "WorkspaceManager"
            projectPath = $projectPath
        }
    }
} | ConvertTo-Json -Depth 10

$toolRequest | Out-File -FilePath "input.json" -Encoding utf8 -Append
Start-Sleep -Milliseconds 500

if(Test-Path "output.json") {
    $lines = Get-Content "output.json"
    if($lines.Count -ge 2) {
        $response = $lines[1] | ConvertFrom-Json
        if($response.result -and $response.result.content) {
            $content = $response.result.content[0].text | ConvertFrom-Json
            if($content.members -and $content.members.Count -gt 0) {
                Write-Host " ✓ PASS ($($content.members.Count) members found)" -ForegroundColor Green
            } else {
                Write-Host " ✗ FAIL (no members)" -ForegroundColor Red
                Write-Host "Content: $content" -ForegroundColor Yellow
            }
        } else {
            Write-Host " ✗ FAIL (no result)" -ForegroundColor Red
            Write-Host "Response: $response" -ForegroundColor Yellow
        }
    } else {
        Write-Host " ✗ FAIL (insufficient output)" -ForegroundColor Red
    }
} else {
    Write-Host " ✗ FAIL (no output)" -ForegroundColor Red
}

# Test 3: Call get_type_members without projectPath (should use CWD)
Write-Host "Test 3: get_type_members without projectPath (CWD fallback)..." -NoNewline
Set-Location $projectPath
$toolRequest = @{
    jsonrpc = "2.0"
    id = 3
    method = "tools/call"
    params = @{
        name = "get_type_members"
        arguments = @{
            typeName = "WorkspaceManager"
        }
    }
} | ConvertTo-Json -Depth 10

Clear-Content "input.json"
Clear-Content "output.json"

# Need to restart server in the project directory
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$proc = Start-Process -FilePath $serverExe -WorkingDirectory $projectPath -NoNewWindow -PassThru -RedirectStandardInput "input.json" -RedirectStandardOutput "output.json" -RedirectStandardError "stderr.log"
Start-Sleep -Milliseconds 500

$toolRequest | Out-File -FilePath "input.json" -Encoding utf8
Start-Sleep -Milliseconds 500

if(Test-Path "output.json") {
    $response = Get-Content "output.json" -Raw | ConvertFrom-Json
    if($response.result -and $response.result.content) {
        $content = $response.result.content[0].text | ConvertFrom-Json
        if($content.members -and $content.members.Count -gt 0) {
            Write-Host " ✓ PASS ($($content.members.Count) members found)" -ForegroundColor Green
        } else {
            Write-Host " ✗ FAIL (no members)" -ForegroundColor Red
        }
    } else {
        Write-Host " ✗ FAIL (no result)" -ForegroundColor Red
    }
} else {
    Write-Host " ✗ FAIL (no output)" -ForegroundColor Red
}

# Cleanup
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Set-Location $repoRoot
Remove-Item -Path "input.json", "output.json", "stderr.log" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Test Complete" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
