<#
.SYNOPSIS
  C-149: runs ONE Stage B test method on the security-test stack (a focused measurement, not a Stage B run; owner policy C-131).
  -Fresh recreates the stack first (down -v, up --build, wait healthy), as scripts/dev/stage-b.ps1 -Fresh does; -NoBuild keeps the
  current local images (a before/after comparison on the same build). -Class runs a whole class instead (pass -Method x).
  Evidence: artifacts/test-runs/<run id> (TRX, run.json). Environment variables set by the caller (P5_*, IX5_*, IX1_*) pass through.
.EXAMPLE
  scripts/measurement/ix5/run_stageb_method.ps1 -Method SearchIndexFoundationTests.MeasurePairedInterleavedCommitLatencyWithAndWithoutTheIndex -Fresh
#>
param([Parameter(Mandatory = $true)][string]$Method, [switch]$Fresh, [switch]$NoBuild, [string]$Class)
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$docker = Join-Path $root 'infrastructure/docker'
$project = 'tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj'
$compose = @('compose', '-f', 'compose.yaml', '-f', 'compose.security-test.yaml', '--env-file', '.env')
function Stack([string[]]$ComposeArgs) {
  Push-Location $docker
  try { if (-not (Test-Path .env)) { Copy-Item env.example .env }; docker @compose @ComposeArgs } finally { Pop-Location }
}
Set-Location $root
if ($Fresh) {
  Stack @('down', '-v', '--remove-orphans')
  if ($NoBuild) { Stack @('up', '-d') } else { Stack @('up', '-d', '--build') }; if ($LASTEXITCODE -ne 0) { throw 'stack build failed' }
  Push-Location $docker
  try { $all = @(docker @compose config --services) } finally { Pop-Location }
  $services = @($all | Where-Object { $_ -notmatch '^(dev-seed|vault-transit-init|.*-migrate)$' })
  Stack (@('up', '-d', '--wait') + $services); if ($LASTEXITCODE -ne 0) { throw 'stack did not become healthy' }
}
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $root "artifacts/test-runs/$runId"
Write-Host "Test run id: $runId (evidence: $evidence)"
$clock = [Diagnostics.Stopwatch]::StartNew()
$filter = if ($Class) { @('--filter-class', "EnterpriseForms.StageBTests.Tests.$Class") } else { @('--filter-method', "EnterpriseForms.StageBTests.Tests.$Method") }
dotnet test --project $project -c Release -- --minimum-expected-tests 1 @filter --results-directory $evidence --report-xunit-trx
$code = $LASTEXITCODE
New-Item -ItemType Directory -Force $evidence | Out-Null
[ordered]@{ run_id = $runId; method = $Method; fresh = [bool]$Fresh; exit_code = $code; elapsed_seconds = [math]::Round($clock.Elapsed.TotalSeconds) } | ConvertTo-Json | Out-File (Join-Path $evidence 'run.json') -Encoding utf8
if ($code -ne 0) { Write-Host 'FAILED' -ForegroundColor Red; exit 1 }
Write-Host 'PASS' -ForegroundColor Green
