#!/bin/bash
# Runs lint and tests before any git push.
# Invoked as a PreToolUse hook for Bash commands containing "git push".

set -euo pipefail

# Only run for git push commands
input=$(cat)
command=$(echo "$input" | jq -r '.tool_input.command // ""')
if [[ "$command" != *"git push"* ]]; then
  exit 0
fi

echo "Running pre-push checks..." >&2

cd "$(git rev-parse --show-toplevel)"

# Resolve python — prefer venv, fall back to system with dist-packages
if [[ -f ".venv/bin/python" ]]; then
  PYTHON=".venv/bin/python"
  PYTEST=".venv/bin/pytest"
  RUFF=".venv/bin/ruff"
else
  PYTHON="python"
  PYTEST="python -m pytest"
  RUFF="ruff"
  # In this environment packages live in dist-packages
  export PYTHONPATH="${PYTHONPATH:-/usr/local/lib/python3.11/dist-packages}"
fi

echo "--- ruff check ---" >&2
$RUFF check . >&2

echo "--- ruff format --check ---" >&2
$RUFF format --check . >&2

echo "--- pytest ---" >&2
DIGITRANSIT_API_KEY=test-key $PYTEST tests/ -q >&2

echo "All checks passed. Proceeding with push." >&2
exit 0
