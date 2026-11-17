<#
.SYNOPSIS
  HC-12 harness safety check (FU-M2f3, 2026-11-17; record docs/security/E3_M2F3_FU_VERIFICATION_RECORD.md). Development/Testing only.
.DESCRIPTION
  Extracts Get-ConfigEnablers from the HEAD and the working-tree versions of scripts/dev/e3-m2e2-acceptance-pg-gate.ps1 and
  scripts/dev/e3-m2f3g-evidence-pg-gate.ps1 and runs each against the repository and against a SEPARATE throwaway clone (-Probe) in which
  probe files are written and staged (git add only, never committed): enabling settings in application settings, Bicep parameters,
  deployment YAML, a root .env, documentation outside docs/evidence and tool settings must be reported by both versions; a generated
  evidence report under docs/evidence must be reported only by the uncorrected HEAD version (the false positive HC-12 removes).
.EXAMPLE
  git clone --local --no-hardlinks <repo> <scratch>\hc12probe
  powershell -File scripts\dev\e3-m2f3-hc12-preflight-check.ps1 -Real <repo> -Probe <scratch>\hc12probe
#>
param([Parameter(Mandatory = $true)][string]$Real, [Parameter(Mandatory = $true)][string]$Probe)
$ErrorActionPreference = 'Stop'
function Get-Function([string]$text) {
    $m = [regex]::Match($text, '(?s)function Get-ConfigEnablers \{.*?\r?\n\}')
    if (-not $m.Success) { throw 'function not found' }
    return $m.Value
}
$results = @()
$probes = @(
    @{ Path = 'src/authority/AuthorizationAuthority.Api/appsettings.HC12Probe.json'; Text = '{ "Content": { "EnvelopeV2AcceptanceEnabled": true } }'; Expect = $true },
    @{ Path = 'infra/hc12-probe.bicepparam'; Text = "param envelopeV2AcceptanceEnabled = true"; Expect = $true },
    @{ Path = 'deploy/hc12-probe.yaml'; Text = "Content__EnvelopeV2AcceptanceEnabled: 'true'"; Expect = $true },
    @{ Path = 'hc12-probe.env'; Text = 'Content__EnvelopeV2AcceptanceEnabled=true'; Expect = $true },
    @{ Path = 'docs/hc12-probe.yml'; Text = 'EnvelopeV2AcceptanceEnabled: true'; Expect = $true },
    @{ Path = 'tools/experiments/hc12-probe/appsettings.json'; Text = '{ "EnvelopeV2AcceptanceEnabled": true }'; Expect = $true },
    @{ Path = 'docs/evidence/hc12-probe/report.json'; Text = '{ "Description": "EnvelopeV2AcceptanceEnabled = false" }'; Expect = $false }
)
foreach ($p in $probes) {
    $full = Join-Path $Probe $p.Path
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    [System.IO.File]::WriteAllText($full, $p.Text)
    git -C $Probe add -- $p.Path | Out-Null
}
foreach ($gate in 'e3-m2e2-acceptance-pg-gate.ps1', 'e3-m2f3g-evidence-pg-gate.ps1') {
    $corrected = Get-Function ([System.IO.File]::ReadAllText((Join-Path $Real "scripts/dev/$gate")))
    $original = Get-Function ((git -C $Real show "HEAD:scripts/dev/$gate") -join "`n")
    foreach ($variant in @(@{ Name = 'HEAD'; Body = $original }, @{ Name = 'corrected'; Body = $corrected })) {
        Invoke-Expression $variant.Body
        $repo = $Real
        $onReal = @(Get-ConfigEnablers)
        $repo = $Probe
        $onProbe = @(Get-ConfigEnablers)
        foreach ($p in $probes) {
            $hit = [bool]($onProbe | Where-Object { $_ -like "$($p.Path):*" })
            $expected = if ($p.Expect) { $true } else { $variant.Name -eq 'HEAD' }  # evidence probe: only the uncorrected function matches it
            $results += [pscustomobject]@{ Gate = $gate; Variant = $variant.Name; Probe = $p.Path; Expected = $expected; Hit = $hit; Ok = ($hit -eq $expected) }
        }
        $evidenceHitsProbe = @($onProbe | Where-Object { $_ -like 'docs/evidence/e3-m2f3g/*' }).Count
        $results += [pscustomobject]@{ Gate = $gate; Variant = $variant.Name; Probe = '(repository at HEAD + working tree)'; Expected = ($variant.Name -eq 'HEAD'); Hit = ($onReal.Count -gt 0); Ok = (($onReal.Count -gt 0) -eq ($variant.Name -eq 'HEAD')) }
        $results += [pscustomobject]@{ Gate = $gate; Variant = $variant.Name; Probe = '(committed 3G evidence in the probe clone)'; Expected = ($variant.Name -eq 'HEAD'); Hit = ($evidenceHitsProbe -gt 0); Ok = (($evidenceHitsProbe -gt 0) -eq ($variant.Name -eq 'HEAD')) }
    }
}
$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Output
$failed = @($results | Where-Object { -not $_.Ok })
"checks: $($results.Count); failed: $($failed.Count)"
if ($failed.Count -gt 0) { exit 1 }
