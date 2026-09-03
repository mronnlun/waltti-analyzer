#!/bin/bash
# Runs the .NET build and test suite before any git push.
# Invoked as a PreToolUse hook for Bash commands containing "git push".

set -euo pipefail

input=$(cat)
command=$(echo "$input" | jq -r '.tool_input.command // ""')
if [[ "$command" != *"git push"* ]]; then
  exit 0
fi

echo "Running pre-push checks..." >&2
cd "$(git rev-parse --show-toplevel)"

echo "--- dotnet build ---" >&2
dotnet build src/WalttiAnalyzer.Web/WalttiAnalyzer.Web.csproj --nologo >&2

echo "--- dotnet test ---" >&2
dotnet test tests/WalttiAnalyzer.Tests/WalttiAnalyzer.Tests.csproj --nologo --no-restore >&2

echo "All checks passed. Proceeding with push." >&2
