<#
.SYNOPSIS
  Run Stage B integration tests by group (the same groups CI shards on; see docs/testing/FAST_FEEDBACK_TESTING.md).

  STAGE B IS RELEASE-ONLY (permanent owner policy C-131, docs/testing/STAGE_B_RELEASE_ONLY_POLICY.md): -Group and -All run
  Stage B and are refused unless -ReleaseAuthorization names the owner's explicit release-testing authorization. -Class runs one
  named test class as a FOCUSED development test of the change at hand; it is not a Stage B run. -List only lists.

.EXAMPLE
  scripts/dev/stage-b.ps1 -List                          # group names, titles and test counts
  scripts/dev/stage-b.ps1 -Class DocumentTests           # FOCUSED: one test class directly affected by the change (development)
  scripts/dev/stage-b.ps1 -Class FieldSearchExecutionTests -ExcludeBenchmarks   # FOCUSED: functional tests only (C-133 R-29)
  scripts/dev/stage-b.ps1 -Group search-catalogue -ReleaseAuthorization C-1xx   # RELEASE ONLY: one group, owner-authorized
  scripts/dev/stage-b.ps1 -All -Fresh -ReleaseAuthorization C-1xx               # RELEASE ONLY: every group on a clean stack

  Every run keeps its evidence (C-133 R-28) under artifacts/test-runs/<run id>/: an xUnit TRX report per run (failing test
  names, messages and stack traces), run.json (run id, selection, attempt, exit code) and, on failure, the authority/api/worker
  logs. The script never retries; a rerun is a new run id (record it as a retry).

  The disposable stack uses the `enterpriseforms-sectest` compose project only. This script never touches the dev stack,
  its volumes or any Vite server; stop the dev stack yourself first (both pin the same subnets).
#>
[CmdletBinding()]
param(
  [switch]$List,
  [string]$Group,
  [string]$Class,
  [switch]$All,
  [switch]$Fresh,      # down -v on the sectest project before starting (clean state; slower)
  [switch]$Rebuild,    # `up --build` even when the stack is already running
  [switch]$ExcludeBenchmarks,  # with -Class: skip tests with [Trait("Category", "Benchmark")] (C-133 R-29; release runs keep them)
  [string]$RetryOf,    # optional: the run id this run repeats (recorded in run.json; R-28)
  [string]$ReleaseAuthorization  # C-131: the owner's release-testing authorization reference; required for -Group and -All
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$docker = Join-Path $root 'infrastructure/docker'
$project = 'tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj'
$tool = Join-Path $root 'scripts/ci/stage_b_groups.py'
$compose = @('compose', '-f', 'compose.yaml', '-f', 'compose.security-test.yaml', '--env-file', '.env')

function Tool([string[]]$ToolArgs) { python $tool @ToolArgs }
function Stack([string[]]$ComposeArgs) {
  Push-Location $docker
  try { if (-not (Test-Path .env)) { Copy-Item env.example .env }; docker @compose @ComposeArgs } finally { Pop-Location }
}
function StackRunning {
  Push-Location $docker
  try { $ps = (docker @compose ps --format json 2>$null) -join "`n" } finally { Pop-Location }
  return (@('"authority"', '"api"', '"worker"', '"clamav"') | Where-Object { $ps -notmatch [regex]::Escape($_) }).Count -eq 0
}
function StackUpAll {
  Stack @('up', '-d', '--build'); if ($LASTEXITCODE -ne 0) { throw 'stack build failed' }
  $oneShot = '^(dev-seed|vault-transit-init|.*-migrate)$'
  Push-Location $docker
  try { $all = @(docker @compose config --services) } finally { Pop-Location }
  $services = @($all | Where-Object { $_ -notmatch $oneShot })
  Stack (@('up', '-d', '--wait') + $services); if ($LASTEXITCODE -ne 0) { throw 'stack did not become healthy' }
}

Set-Location $root
if ($List) {
  dotnet build $project -c Release -v q --nologo | Out-Null
  $listing = Join-Path ([IO.Path]::GetTempPath()) 'stage-b-all-tests.txt'
  dotnet test --project $project --no-build -c Release --list-tests | Out-File $listing -Encoding utf8
  Tool @('plan', '--list', $listing)   # also verifies that every class is assigned to exactly one group
  exit $LASTEXITCODE
}

if (($Group -or $All) -and -not ($ReleaseAuthorization -match '^[A-Za-z0-9][A-Za-z0-9 ._:/#()-]{2,119}$')) {
  Write-Host 'Stage B is release-only (owner policy C-131): -Group and -All require -ReleaseAuthorization <owner authorization reference>.' -ForegroundColor Red
  Write-Host 'For development, run the directly affected class with -Class <Name> (a focused test, not a Stage B run).'
  exit 2
}
if ($Group -or $All) { Write-Host "Stage B release run authorized by: $ReleaseAuthorization" -ForegroundColor Yellow }

$selected = @()
if ($All) { $selected = @(Tool @('list') | ForEach-Object { ($_ -split '\s+')[0] }) }
elseif ($Group) { $selected = @($Group) }
elseif (-not $Class) { Write-Host 'Specify -List, -Group <name>, -Class <name> or -All.'; exit 2 }

if ($Fresh) { Stack @('down', '-v', '--remove-orphans') }
if ($Fresh -or $Rebuild -or -not (StackRunning)) { StackUpAll } else { Write-Host 'Reusing the running sectest stack (use -Rebuild after code changes).' }

if ($ExcludeBenchmarks -and -not $Class) { Write-Host '-ExcludeBenchmarks applies to focused -Class runs only; release runs keep every test.' -ForegroundColor Red; exit 2 }

# C-133 R-28: preserved evidence of every run (test names, exceptions and stack traces in TRX; logs on failure).
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $root "artifacts/test-runs/$runId"
# Not created here: `dotnet test` takes an existing directory after `--` for its own project argument. The test platform creates it.
$report = @('--results-directory', $evidence, '--report-xunit-trx')
Write-Host "Test run id: $runId (evidence: $evidence)"

$failed = @()
$clock = [Diagnostics.Stopwatch]::StartNew()
if ($Class) {
  $filter = @('--filter-class', "EnterpriseForms.StageBTests.Tests.$Class")
  if ($ExcludeBenchmarks) { $filter += @('--filter-not-trait', 'Category=Benchmark') }
  dotnet test --project $project -c Release -- --minimum-expected-tests 1 @filter @report
  if ($LASTEXITCODE -ne 0) { $failed += "class $Class" }
}
foreach ($g in $selected) {
  $filter = @((Tool @('filter', $g)) -split ' ')
  if ($LASTEXITCODE -ne 0 -or $filter.Count -eq 0) { Write-Host "unknown group '$g' (run -List)" -ForegroundColor Red; exit 2 }
  Write-Host "`n=== group $g" -ForegroundColor Cyan
  dotnet test --project $project -c Release -- --minimum-expected-tests 1 --filter-class @filter @report
  if ($LASTEXITCODE -ne 0) { $failed += $g }
}
Write-Host ("`nelapsed {0:N0}s" -f $clock.Elapsed.TotalSeconds)
New-Item -ItemType Directory -Force $evidence | Out-Null
if ($failed.Count -gt 0) {
  foreach ($service in @('authority', 'api', 'worker')) { Stack @('logs', '--no-color', '--tail', '5000', $service) *> (Join-Path $evidence "$service.log") }
}
[ordered]@{ run_id = $runId; retry_of = $RetryOf; class = $Class; groups = $selected; exclude_benchmarks = [bool]$ExcludeBenchmarks; release_authorization = $ReleaseAuthorization
  failed = $failed; elapsed_seconds = [math]::Round($clock.Elapsed.TotalSeconds) } | ConvertTo-Json | Out-File (Join-Path $evidence 'run.json') -Encoding utf8
if ($failed.Count -gt 0) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host 'PASS' -ForegroundColor Green
