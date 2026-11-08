param([switch]$Import)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if (-not $env:ConnectionStrings__LegacyCommerce -or -not $env:ConnectionStrings__Catalog) {
    throw 'Set ConnectionStrings__LegacyCommerce and ConnectionStrings__Catalog before running.'
}
if ($Import -and $env:CATALOG_WRITE_FREEZE_ACK -ne 'true') {
    throw 'Import requires CATALOG_WRITE_FREEZE_ACK=true after legacy Catalog writes are frozen.'
}
$mode = if ($Import) { '--import' } else { '--verify' }
dotnet run --project 'src/Services/Catalog/Catalog.Migration/Catalog.Migration.csproj' -- $mode
exit $LASTEXITCODE
