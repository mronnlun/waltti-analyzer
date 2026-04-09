#!/usr/bin/env bash
#
# One-time migration: copy non-sensitive repository secrets into repository
# variables, then delete the originals. Run from your local machine with an
# authenticated `gh` CLI that has admin rights on mronnlun/waltti-analyzer.
#
# GitHub secrets are write-only, so values cannot be read from the API —
# you will be prompted to paste each value. Press Enter to skip any field
# you do not want to migrate.
#
# Usage:
#     ./scripts/migrate-secrets-to-vars.sh
#
# Optional: pre-populate values via environment variables to skip prompts, e.g.
#     AZURE_CLIENT_ID=... AZURE_TENANT_ID=... ./scripts/migrate-secrets-to-vars.sh

set -euo pipefail

REPO="mronnlun/waltti-analyzer"

# Names to migrate from secrets to variables.
NAMES=(
  AZURE_CLIENT_ID
  AZURE_TENANT_ID
  AZURE_SUBSCRIPTION_ID
  NOTIFICATION_EMAIL
  AGENT_PRINCIPAL_ID
)

# --- preflight ---------------------------------------------------------------
if ! command -v gh >/dev/null 2>&1; then
  echo "error: gh CLI is not installed. See https://cli.github.com/" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "error: gh is not authenticated. Run 'gh auth login' first." >&2
  exit 1
fi

echo "Target repository: $REPO"
echo

# --- collect values ----------------------------------------------------------
declare -A VALUES
for name in "${NAMES[@]}"; do
  # Allow pre-populated values from the environment.
  existing="${!name:-}"
  if [[ -n "$existing" ]]; then
    VALUES[$name]="$existing"
    echo "  $name: (from environment)"
    continue
  fi

  printf "  %s: " "$name"
  IFS= read -r value || true
  VALUES[$name]="$value"
done
echo

# --- write variables ---------------------------------------------------------
for name in "${NAMES[@]}"; do
  value="${VALUES[$name]}"
  if [[ -z "$value" ]]; then
    echo "skip  $name (no value provided)"
    continue
  fi
  echo "set   variable $name"
  gh variable set "$name" --repo "$REPO" --body "$value"
done
echo

# --- delete old secrets ------------------------------------------------------
read -r -p "Delete the original repository secrets now? [y/N] " confirm
if [[ "${confirm:-}" =~ ^[Yy]$ ]]; then
  for name in "${NAMES[@]}"; do
    if [[ -z "${VALUES[$name]}" ]]; then
      echo "skip  secret $name (was not migrated)"
      continue
    fi
    if gh secret delete "$name" --repo "$REPO" 2>/dev/null; then
      echo "del   secret $name"
    else
      echo "miss  secret $name (already absent?)"
    fi
  done
else
  echo "Leaving original secrets in place."
fi

echo
echo "Done. Verify with:"
echo "  gh variable list --repo $REPO"
echo "  gh secret list   --repo $REPO"
