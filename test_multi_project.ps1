# Multi-Project Testing for RoslynMcp v0.3.0
# Tests that tools correctly handle projectPath parameter across multiple projects

$ErrorActionPreference = "Stop"

Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  RoslynMcp Multi-Project Test Suite" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Project paths for testing
$roslynMcpPath = "src\RoslynMcp"
$testHarnessPath = "src\TestHarness"
$analyzersPath = "src\RoslynMcp.Analyzers"

Write-Host "Test Projects:" -ForegroundColor Yellow
Write-Host "  1. RoslynMcp      (main server project)" -ForegroundColor Gray
Write-Host "  2. TestHarness    (test client)" -ForegroundColor Gray
Write-Host "  3. RoslynMcp.Analyzers (Roslyn analyzer)" -ForegroundColor Gray
Write-Host ""

Write-Host "Building server..." -ForegroundColor Cyan
dotnet build src\RoslynMcp\RoslynMcp.csproj -c Release -f net10.0 | Out-Null
Write-Host "✓ Build complete" -ForegroundColor Green
Write-Host ""

# Helper function to send MCP request and parse response
function Invoke-McpTool {
    param(
        [string]$ToolName,
        [hashtable]$Arguments,
        [string]$Description
    )

    Write-Host "  Testing: $Description" -ForegroundColor Yellow

    $requestsJson = @"
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"$ToolName","arguments":$(ConvertTo-Json -Depth 10 -Compress $Arguments)}}
"@

    try {
        $result = $requestsJson | dotnet run --project src\RoslynMcp\RoslynMcp.csproj -f net10.0 --no-build -- "." 2>$null | 
            Where-Object { $_ -match '"id":2' } |
            ConvertFrom-Json

        return $result
    }
    catch {
        Write-Host "    ✗ Exception: $_" -ForegroundColor Red
        return $null
    }
}

# Test counters
$totalTests = 0
$passedTests = 0

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 1: Project Info Tool (verify we can load all projects)
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 1: Project Information Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_project_info" -Arguments @{ projectPath = $roslynMcpPath } -Description "RoslynMcp project info"
if ($result -and $result.result -and $result.result.content[0].text -match '"target_framework"') {
    Write-Host "    ✓ RoslynMcp project loaded successfully" -ForegroundColor Green
    $passedTests++
} else {
    Write-Host "    ✗ Failed to load RoslynMcp project" -ForegroundColor Red
    if ($result.error) {
        Write-Host "      Error: $($result.error.message)" -ForegroundColor Red
    }
}

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_project_info" -Arguments @{ projectPath = $testHarnessPath } -Description "TestHarness project info"
if ($result -and $result.result -and $result.result.content[0].text -match '"target_framework"') {
    Write-Host "    ✓ TestHarness project loaded successfully" -ForegroundColor Green
    $passedTests++
} else {
    Write-Host "    ✗ Failed to load TestHarness project" -ForegroundColor Red
    if ($result.error) {
        Write-Host "      Error: $($result.error.message)" -ForegroundColor Red
    }
}

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_project_info" -Arguments @{ projectPath = $analyzersPath } -Description "RoslynMcp.Analyzers project info"
if ($result -and $result.result -and $result.result.content[0].text -match '"target_framework"') {
    Write-Host "    ✓ RoslynMcp.Analyzers project loaded successfully" -ForegroundColor Green
    $passedTests++
} else {
    Write-Host "    ✗ Failed to load RoslynMcp.Analyzers project" -ForegroundColor Red
    if ($result.error) {
        Write-Host "      Error: $($result.error.message)" -ForegroundColor Red
    }
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 2: Type Discovery Across Projects
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 2: Type Discovery Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_list_types" -Arguments @{ projectPath = $roslynMcpPath; namespaceFilter = "RoslynMcp.Tools" } -Description "List types in RoslynMcp.Tools namespace"
if ($result -and $result.result) {
    $content = $result.result.content[0].text
    if ($content -match "TypeMembersTool" -and $content -match "DiagnosticsTool") {
        Write-Host "    ✓ Found tool types in RoslynMcp project" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to find tool types" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_list_types" -Arguments @{ projectPath = $testHarnessPath } -Description "List types in TestHarness project"
if ($result -and $result.result) {
    $content = $result.result.content[0].text
    if ($content -match "\[" -or $content -match "Program") {
        Write-Host "    ✓ Found types in TestHarness project" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to find types in TestHarness" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 3: Type Members Across Projects
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 3: Type Members Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_type_members" -Arguments @{ typeName = "WorkspaceManager"; projectPath = $roslynMcpPath } -Description "Get WorkspaceManager members"
if ($result -and $result.result) {
    $content = $result.result.content[0].text | ConvertFrom-Json
    if ($content.members.Count -gt 5) {
        Write-Host "    ✓ Found $($content.members.Count) members in WorkspaceManager" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to get WorkspaceManager members" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 4: Find References Across Projects
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 4: Find References Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_find_references" -Arguments @{ symbolName = "WorkspaceManager"; projectPath = $roslynMcpPath; take = 10 } -Description "Find WorkspaceManager references in RoslynMcp"
if ($result -and $result.result) {
    $content = $result.result.content[0].text | ConvertFrom-Json
    if ($content.total_references -gt 0) {
        Write-Host "    ✓ Found $($content.total_references) references to WorkspaceManager" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to find WorkspaceManager references" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 5: Diagnostics Across Projects
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 5: Diagnostics Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_diagnostics" -Arguments @{ projectPath = $roslynMcpPath } -Description "Get diagnostics for RoslynMcp"
if ($result -and $result.result) {
    $content = $result.result.content[0].text
    if ($content -match "\[" -or $content -match "No errors") {
        Write-Host "    ✓ Got diagnostics for RoslynMcp project" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to get diagnostics" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_get_diagnostics" -Arguments @{ projectPath = $testHarnessPath } -Description "Get diagnostics for TestHarness"
if ($result -and $result.result) {
    $content = $result.result.content[0].text
    if ($content -match "\[" -or $content -match "No errors") {
        Write-Host "    ✓ Got diagnostics for TestHarness project" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to get diagnostics" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Test Category 6: File Search Across Projects
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══ Category 6: File Operations Across Projects ═══" -ForegroundColor Cyan
Write-Host ""

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_list_files" -Arguments @{ pattern = "*.cs"; projectPath = $roslynMcpPath; take = 50 } -Description "List C# files in RoslynMcp"
if ($result -and $result.result) {
    $content = $result.result.content[0].text | ConvertFrom-Json
    if ($content.count -gt 10) {
        Write-Host "    ✓ Found $($content.count) C# files in RoslynMcp" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to list files" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

$totalTests++
$result = Invoke-McpTool -ToolName "roslyn_search_files" -Arguments @{ pattern = "WorkspaceManager"; projectPath = $roslynMcpPath; take = 10 } -Description "Search for WorkspaceManager in RoslynMcp"
if ($result -and $result.result) {
    $content = $result.result.content[0].text | ConvertFrom-Json
    if ($content.matches.Count -gt 0) {
        Write-Host "    ✓ Found $($content.matches.Count) matches for WorkspaceManager" -ForegroundColor Green
        $passedTests++
    } else {
        Write-Host "    ✗ Failed to search files" -ForegroundColor Red
    }
} else {
    Write-Host "    ✗ Request failed" -ForegroundColor Red
}

Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Results Summary
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Test Results: $passedTests / $totalTests passed" -ForegroundColor $(if ($passedTests -eq $totalTests) { "Green" } else { "Yellow" })
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if ($passedTests -eq $totalTests) {
    Write-Host "✓ All multi-project tests passed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "✗ Some tests failed. Review output above." -ForegroundColor Red
    exit 1
}
