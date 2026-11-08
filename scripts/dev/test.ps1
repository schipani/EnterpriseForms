<#
.SYNOPSIS
  Three-level test runner (see docs/testing/FAST_FEEDBACK_TESTING.md).

.EXAMPLE
  scripts/dev/test.ps1 -Level 1                       # seconds: unit + frontend checks, no Docker
  scripts/dev/test.ps1 -Level 2 -Browser              # keeps the Docker stack running; targeted real-stack tests
  scripts/dev/test.ps1 -Level 2 -Class DocumentTests  # one integration test class
  scripts/dev/test.ps1 -Level 2 -Spec designer-ux     # one Playwright spec file
  scripts/dev/test.ps1 -Level 3                       # full development verification; Stage B steps skipped (C-131)
  scripts/dev/test.ps1 -Level 3 -StageBReleaseAuthorization C-1xx   # RELEASE ONLY: also the Stage B integration and browser steps

  STAGE B IS RELEASE-ONLY (permanent owner policy C-131, docs/testing/STAGE_B_RELEASE_ONLY_POLICY.md). The whole Stage B
  integration suite (Level 2 without -Class/-Spec, Level 3) and the full Playwright suite (Level 2 -Browser, Level 3) run only with
  -StageBReleaseAuthorization naming the owner's explicit release-testing authorization. Level 2 -Class and -Spec stay focused
  development tests.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet(1, 2, 3)][int]$Level,
  [string]$Class,          # Level 2: xunit class filter for the Stage B integration project (e.g. DocumentTests)
  [switch]$ExcludeBenchmarks,  # Level 2 -Class: skip [Trait("Category", "Benchmark")] tests (C-133 R-29)
  [string]$Spec,           # Level 2: Playwright spec name filter (e.g. designer-ux)
  [switch]$Browser,        # Level 2: also run the full Playwright suite
  [switch]$NoRebuild,      # Level 2: reuse the running stack as-is (UI-only edits; Vite already hot-reloads)
  [switch]$Full,           # Level 1: run the whole vitest suite instead of only changed files
  [string]$StageBReleaseAuthorization  # C-131: owner release-testing authorization reference; required for any Stage B run
)
$ErrorActionPreference = 'Stop'
# Forward slashes work on Windows and Linux PowerShell alike (the script also runs on hosted Linux runners: level3-script.yml).
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$docker = Join-Path $root 'infrastructure/docker'
$web = Join-Path $root 'src/frontend'
$compose = @('compose', '-f', 'compose.yaml', '-f', 'compose.security-test.yaml', '--env-file', '.env')
$timings = [System.Collections.Generic.List[string]]::new()
# C-133 R-28: every .NET test step writes an xUnit TRX report (names, messages, stack traces) under artifacts/test-runs/<run id>/.
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $root "artifacts/test-runs/$runId"
$report = @('--results-directory', $evidence, '--report-xunit-trx')
Write-Host "Test run id: $runId (evidence: $evidence)"
$stageBAuthorized = $StageBReleaseAuthorization -match '^[A-Za-z0-9][A-Za-z0-9 ._:/#()-]{2,119}$'
if ($stageBAuthorized) { Write-Host "Stage B release run authorized by: $StageBReleaseAuthorization" -ForegroundColor Yellow }
if ($Level -eq 2 -and (($Browser) -or -not ($Class -or $Spec)) -and -not $stageBAuthorized) {
  Write-Host 'Stage B is release-only (owner policy C-131): Level 2 without -Class/-Spec, or with -Browser, runs Stage B and requires -StageBReleaseAuthorization.' -ForegroundColor Red
  Write-Host 'For development, name the directly affected integration class (-Class X) or Playwright spec (-Spec y).'
  exit 2
}

function Step([string]$Name, [scriptblock]$Body) {
  $clock = [System.Diagnostics.Stopwatch]::StartNew()
  Write-Host "`n=== $Name" -ForegroundColor Cyan
  & $Body
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $Name (exit $LASTEXITCODE)" }
  $timings.Add(('{0,6:N0}s  {1}' -f $clock.Elapsed.TotalSeconds, $Name))
}
function InDir([string]$Dir, [scriptblock]$Body) { Push-Location $Dir; try { & $Body } finally { Pop-Location } }
function Stack([string[]]$ComposeArgs) { InDir $docker { if (-not (Test-Path .env)) { Copy-Item env.example .env }; docker @compose @ComposeArgs } }
# Whole-stack start. `up --wait` on the full profile reports failure when a one-shot service (dev-seed, *-migrate, vault-transit-init)
# exits 0, although the stack is healthy. Start everything, then wait only on the long-running services; one-shot services
# must have exited with code 0 (a non-zero exit still fails the step).
function StackUpAll {
  Stack @('up', '-d', '--build'); if ($LASTEXITCODE -ne 0) { return }
  $oneShot = '^(dev-seed|vault-transit-init|.*-migrate)$'
  $services = @(InDir $docker { docker @compose config --services } | Where-Object { $_ -notmatch $oneShot })
  Stack (@('up', '-d', '--wait') + $services); if ($LASTEXITCODE -ne 0) { return }
  $failed = @(InDir $docker { docker @compose ps -a --status exited --format '{{.Service}} {{.ExitCode}}' } | Where-Object { $_ -match $oneShot -and $_ -notmatch ' 0$' })
  if ($failed.Count -gt 0) { Write-Host "one-shot service failed: $failed" -ForegroundColor Red; $global:LASTEXITCODE = 1 }
}

try {
  if ($Level -eq 1) {
    Step '.NET build + unit/architecture/contract tests' {
      InDir $root { dotnet test --solution EnterpriseForms.All.slnx -c Release -- --minimum-expected-tests 10 @report }
    }
    Step 'Frontend lint + typecheck' { InDir $web { npm run lint; if ($LASTEXITCODE -eq 0) { npm run typecheck } } }
    Step 'Frontend unit tests' { InDir $web { if ($Full) { npm run test } else { npx vitest run --changed --passWithNoTests } } }
  }

  if ($Level -eq 2) {
    if (-not $NoRebuild) {
      # Incremental: BuildKit reuses unchanged layers and compose recreates only containers whose image changed.
      Step 'Stack up (incremental build, stays running)' { StackUpAll }
    }
    if ($Class -or -not ($Spec -or $Browser)) {
      Step 'Stage B integration tests' {
        InDir $root {
          $filter = if ($Class) { @('--filter-class', "*$Class*") } else { @() }
          if ($Class -and $ExcludeBenchmarks) { $filter += @('--filter-not-trait', 'Category=Benchmark') }
          dotnet test --project tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj -c Release -- --minimum-expected-tests $(if ($Class) { 1 } else { 10 }) @filter @report
        }
      }
    }
    if ($Spec -or $Browser) {
      Step 'Playwright (real stack via Vite proxy)' {
        InDir $web {
          $env:API_TARGET = 'http://127.0.0.1:5180'
          if ($Spec) { npx playwright test $Spec } else { npx playwright test }
        }
      }
    }
  }

  if ($Level -eq 3) {
    # No filters, no skips, clean volumes: the same checks CI runs, in the same order, on the current working tree.
    Step 'Specifications + integrity vectors' {
      InDir $root { python scripts/validate-specifications.py --strict; if ($LASTEXITCODE -eq 0) { python scripts/integrity/verify_vectors.py } }
    }
    Step '.NET full build + tests + solution-wide floor' {
      InDir $root {
        dotnet build EnterpriseForms.All.slnx -c Release
        if ($LASTEXITCODE -eq 0) { git diff --exit-code -- contracts/openapi }
        if ($LASTEXITCODE -eq 0) { dotnet build tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -c Release }
        if ($LASTEXITCODE -eq 0) { dotnet build tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj -c Release }
        if ($LASTEXITCODE -eq 0) { python -m unittest scripts/ci/test_test_results.py scripts/ci/test_stage_b_groups.py }
        if ($LASTEXITCODE -eq 0) { python scripts/ci/run_dotnet_tests.py EnterpriseForms.All.slnx --results-dir artifacts/test-results --configuration Release --no-build --minimum-expected-tests 10 --min-total 130 }
      }
    }
    Step 'Frontend full (client drift, lint, typecheck, tests, build)' {
      InDir $web {
        npm ci --ignore-scripts
        if ($LASTEXITCODE -eq 0) { npm run generate:api-client }
        if ($LASTEXITCODE -eq 0) { git diff --exit-code -- packages/api-client/src/generated }
        if ($LASTEXITCODE -eq 0) { npm run check }
      }
    }
    Step 'Stage A gate: clean stack' { Stack @('down', '-v', '--remove-orphans') }
    Step 'Stage A gate: start' {
      Stack @('up', '-d', '--build', '--wait', 'business-db', 'authority-db', 'spicedb', 'vault', 'vault-transit-init', 'archive-store', 'azurite', 'authority-migrate', 'authority', 'business-migrate', 'api', 'attacker-sql')
    }
    Step 'Stage A gate: SEC-P01..SEC-P12' {
      InDir $root {
        dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -c Release --results-directory artifacts/security/test-results --report-xunit-trx -- --minimum-expected-tests 13
        if ($LASTEXITCODE -eq 0) { python security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json }
      }
    }
    if ($stageBAuthorized) {
      # The Stage B stack must start from empty volumes: a stack reused after the tamper tests can quarantine (ARCHIVE_BODY_MISMATCH).
      Step 'Stage B: clean stack' { Stack @('down', '-v', '--remove-orphans') }
      Step 'Stage B: start full stack' { StackUpAll }
      Step 'Stage B: integration tests' {
        InDir $root { dotnet test --project tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj -c Release --results-directory artifacts/stage-b/test-results --report-xunit-trx -- --minimum-expected-tests 10 }
      }
      Step 'Stage B: Playwright (all projects)' { InDir $web { $env:API_TARGET = 'http://127.0.0.1:5180'; npx playwright test } }
    }
    else {
      Write-Host "`n=== Stage B skipped: release-only (owner policy C-131). It was NOT run and is NOT reported as passed." -ForegroundColor Yellow
      $timings.Add('     -  Stage B (not run: release-only, C-131)')
    }
    Step 'Tear down (clean state)' { Stack @('down', '-v', '--remove-orphans') }
  }
}
catch {
  Write-Host $_.Exception.Message -ForegroundColor Red
  $timings | ForEach-Object { Write-Host $_ }
  exit 1
}
Write-Host "`nPASS (level $Level)" -ForegroundColor Green
$timings | ForEach-Object { Write-Host $_ }
