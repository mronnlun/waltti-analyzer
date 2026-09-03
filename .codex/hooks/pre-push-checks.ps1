$ErrorActionPreference = 'Stop'

$inputJson = [Console]::In.ReadToEnd()
try {
    $payload = $inputJson | ConvertFrom-Json
} catch {
    exit 0
}

$command = [string]$payload.tool_input.command
if ($command -notmatch '(^|[;&|]\s*)git\s+push(?:\s|$)') {
    exit 0
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    Write-Error 'Could not resolve the repository root for pre-push checks.'
}

Push-Location -LiteralPath $repoRoot
try {
    Write-Error 'Running pre-push checks...'

    Write-Error '--- dotnet build ---'
    & dotnet build src/WalttiAnalyzer.Web/WalttiAnalyzer.Web.csproj --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Error '--- dotnet test ---'
    & dotnet test tests/WalttiAnalyzer.Tests/WalttiAnalyzer.Tests.csproj --nologo --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Error 'All checks passed. Proceeding with push.'
} finally {
    Pop-Location
}
