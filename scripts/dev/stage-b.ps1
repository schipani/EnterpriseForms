<#
.SYNOPSIS
  Run Stage B integration tests by group (the same groups CI shards on; see docs/testing/FAST_FEEDBACK_TESTING.md).

.EXAMPLE
  scripts/dev/stage-b.ps1 -List                          # group names, titles and test counts
  scripts/dev/stage-b.ps1 -Group search-catalogue        # one group (the stack is started only if it is not already running)
  scripts/dev/stage-b.ps1 -Class DocumentTests           # one test class (must belong to a group; the check is the same)
  scripts/dev/stage-b.ps1 -All                           # every group, one after another, on one stack
  scripts/dev/stage-b.ps1 -Group records-lifecycle -Fresh  # recreate the disposable sectest stack first (down -v)

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
  [switch]$Rebuild     # `up --build` even when the stack is already running
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

$selected = @()
if ($All) { $selected = @(Tool @('list') | ForEach-Object { ($_ -split '\s+')[0] }) }
elseif ($Group) { $selected = @($Group) }
elseif (-not $Class) { Write-Host 'Specify -List, -Group <name>, -Class <name> or -All.'; exit 2 }

if ($Fresh) { Stack @('down', '-v', '--remove-orphans') }
if ($Fresh -or $Rebuild -or -not (StackRunning)) { StackUpAll } else { Write-Host 'Reusing the running sectest stack (use -Rebuild after code changes).' }

$failed = @()
$clock = [Diagnostics.Stopwatch]::StartNew()
if ($Class) {
  $filter = @("EnterpriseForms.StageBTests.Tests.$Class")
  dotnet test --project $project -c Release -- --minimum-expected-tests 1 --filter-class @filter
  if ($LASTEXITCODE -ne 0) { $failed += "class $Class" }
}
foreach ($g in $selected) {
  $filter = @((Tool @('filter', $g)) -split ' ')
  if ($LASTEXITCODE -ne 0 -or $filter.Count -eq 0) { Write-Host "unknown group '$g' (run -List)" -ForegroundColor Red; exit 2 }
  Write-Host "`n=== group $g" -ForegroundColor Cyan
  dotnet test --project $project -c Release -- --minimum-expected-tests 1 --filter-class @filter
  if ($LASTEXITCODE -ne 0) { $failed += $g }
}
Write-Host ("`nelapsed {0:N0}s" -f $clock.Elapsed.TotalSeconds)
if ($failed.Count -gt 0) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host 'PASS' -ForegroundColor Green
