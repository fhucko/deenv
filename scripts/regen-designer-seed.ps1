#requires -Version 5
<#
.SYNOPSIS
    Regenerate the operator designer's seed from the committed apps.

.DESCRIPTION
    The designer IDE (DeEnv/instances/1/app.app) carries, in its `initialData` section,
    a "design" per committed app (instance / crm / shop) plus the designer itself --
    each app's types reverse-projected into the MetaType/MetaProp meta-schema, with its
    other sections as verbatim text. That seed is GENERATED, not hand-written.

    Run this after you change any committed app's schema (DeEnv/instances/<id>/app.app)
    so the designer opens on the current shapes. It runs the [Explicit]
    DesignerSeedGenerator.Generate_the_designer_seed_from_the_committed_apps test, which
    rewrites instances/1/app.app's initialData and writes it in the committed encoding
    (UTF-8 BOM + CRLF) directly -- so the diff is clean, no manual re-emit.

.NOTES
    The designer's own design is a bounded self-snapshot (empty initialData -- a thing
    cannot contain itself). If the seed's designer-design id shifts (it is assigned after
    the other apps' types), update DeEnv/kernel.json's instance-1 `designId` to match.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$seed = 'DeEnv/instances/1/app.app'

Push-Location $repo
try {
    Write-Host 'Building DeEnv.Tests...' -ForegroundColor Cyan
    dotnet build DeEnv.Tests/DeEnv.Tests.csproj -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    Write-Host 'Regenerating the designer seed from the committed apps...' -ForegroundColor Cyan
    & './DeEnv.Tests/bin/Debug/net9.0/DeEnv.Tests.exe' `
        --treenode-filter '/*/*/DesignerSeedGenerator/Generate_the_designer_seed_from_the_committed_apps'
    if ($LASTEXITCODE -ne 0) {
        throw 'Generator failed (the regenerated seed did not round-trip; nothing was written).'
    }

    $changed = (git --no-pager diff --name-only -- $seed)
    if ([string]::IsNullOrWhiteSpace($changed)) {
        Write-Host "`nDesigner seed is already up to date (no changes)." -ForegroundColor Green
    }
    else {
        Write-Host "`nDesigner seed updated. Diff:" -ForegroundColor Green
        git --no-pager diff --stat -- $seed
        Write-Host "`nReview, then commit $seed (check kernel.json's designer designId if ids shifted)." -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
