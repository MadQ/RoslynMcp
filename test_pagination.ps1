# Quick test for v0.2.2-alpha pagination changes
# Tests get_type_members with skip/take parameters

$ErrorActionPreference = "Stop"

Write-Host "Building server..." -ForegroundColor Cyan
dotnet build src\RoslynMcp\RoslynMcp.csproj -c Release -f net10.0 | Out-Null

Write-Host "Starting MCP test session..." -ForegroundColor Cyan

# Create temp JSON file with test requests
$requests = @"
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_type_members","arguments":{"typeName":"WorkspaceManager"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_type_members","arguments":{"typeName":"WorkspaceManager","skip":5,"take":3}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"find_references","arguments":{"symbolName":"GetCompilation","take":5}}}
"@

$requests | dotnet run --project src\RoslynMcp\RoslynMcp.csproj -f net10.0 --no-build -- "src\RoslynMcp" 2>$null | ForEach-Object {
    if ($_ -match '"id":(2|3|4)') {
        $result = $_ | ConvertFrom-Json

        switch ($result.id) {
            2 {
                Write-Host "`nTest 1: get_type_members (defaults)" -ForegroundColor Yellow
                if ($result.result.total_members) {
                    Write-Host "  ✓ total_members: $($result.result.total_members)" -ForegroundColor Green
                    Write-Host "  ✓ skip: $($result.result.skip), take: $($result.result.take)" -ForegroundColor Green
                    Write-Host "  ✓ returned: $($result.result.members.Count) members" -ForegroundColor Green
                }
            }
            3 {
                Write-Host "`nTest 2: get_type_members (skip=5, take=3)" -ForegroundColor Yellow
                if ($result.result.members.Count -eq 3) {
                    Write-Host "  ✓ Correct page: $($result.result.members.Count) members" -ForegroundColor Green
                    Write-Host "  ✓ skip: $($result.result.skip), take: $($result.result.take)" -ForegroundColor Green
                } else {
                    Write-Host "  ✗ Expected 3, got $($result.result.members.Count)" -ForegroundColor Red
                }
            }
            4 {
                Write-Host "`nTest 3: find_references (take=5)" -ForegroundColor Yellow
                if ($result.result.total_references) {
                    Write-Host "  ✓ total: $($result.result.total_references)" -ForegroundColor Green
                    Write-Host "  ✓ returned: $($result.result.references.Count)" -ForegroundColor Green
                }
            }
        }
    }
}

Write-Host "`n✓ Pagination tests complete!" -ForegroundColor Green
