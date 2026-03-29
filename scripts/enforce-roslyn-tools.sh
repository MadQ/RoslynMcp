#!/bin/bash
# PreToolUse hook: blocks when Read/Grep/Edit targets a .cs file.
# The agent should use roslyn_* MCP tools instead.
# Input: JSON on stdin with tool_name and tool_input.

INPUT=$(cat)

# Extract file_path or path from JSON (no jq, no -P)
FILE=$(echo "$INPUT" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)

if [ -z "$FILE" ]; then
  FILE=$(echo "$INPUT" | sed -n 's/.*"path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
fi

# No file path — allow
if [ -z "$FILE" ]; then
  exit 0
fi

# Extract tool name
TOOL=$(echo "$INPUT" | sed -n 's/.*"tool_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)

# Check if it's a .cs file (case-insensitive)
if echo "$FILE" | grep -qi '\.cs$'; then
  echo "{\"decision\":\"block\",\"reason\":\"DOGFOOD! Use roslyn_* tools for .cs files (roslyn_read_file, roslyn_search_files, roslyn_replace_in_file, roslyn_replace_in_code). Only fall back to $TOOL if the RoslynMcp MCP server is disconnected — and if so, state clearly: RoslynMcp disconnected: falling back to $TOOL.\"}"
fi
