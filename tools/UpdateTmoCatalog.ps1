param(
    [Parameter(Mandatory = $true)]
    [string]$AssetsPath,

    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Data\tmo-unit-catalog.json')
)

$ErrorActionPreference = 'Stop'

$resolvedAssets = (Resolve-Path -LiteralPath $AssetsPath).Path
$resolvedCatalog = (Resolve-Path -LiteralPath $CatalogPath).Path
$assets = Get-Content -Raw -LiteralPath $resolvedAssets | ConvertFrom-Json -AsHashtable
$catalog = Get-Content -Raw -LiteralPath $resolvedCatalog | ConvertFrom-Json

$officialById = @{}
foreach ($tier in $assets.datas.Keys) {
    foreach ($unit in $assets.datas[$tier]) {
        if ($officialById.ContainsKey($unit.id)) {
            throw "Duplicate TMO unit id: $($unit.id)"
        }
        $officialById[$unit.id] = $unit
    }
}

if ($officialById.Count -ne $catalog.units.Count) {
    throw "Catalog size mismatch: official=$($officialById.Count), local=$($catalog.units.Count)"
}

$mergedUnits = foreach ($unit in $catalog.units) {
    if (-not $officialById.ContainsKey($unit.rawcode)) {
        throw "Official TMO data is missing rawcode $($unit.rawcode)"
    }
    $official = $officialById[$unit.rawcode]

    $commands = @()
    if ($official.commands) { $commands = @($official.commands) }

    $row = [ordered]@{
        rawcode = $unit.rawcode
        name = $unit.name
        tier = $unit.tier
        image = $official.image
        recipe = @($unit.recipe)
        abilities = if ($official.abilities) { $official.abilities } else { [ordered]@{} }
        description = if ($official.desc) { [string]$official.desc } else { '' }
    }
    if ($commands.Count -gt 0) { $row.commands = $commands }
    $row
}

$result = [ordered]@{
    source = 'https://raw.githubusercontent.com/tmo-gg/static.tmo.gg/refs/heads/main/ord-helper/assets.json'
    capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
    sourceSha256 = (Get-FileHash -LiteralPath $resolvedAssets -Algorithm SHA256).Hash
    statistics = @($assets.statistics)
    units = @($mergedUnits)
}

$json = $result | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($resolvedCatalog, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Updated $resolvedCatalog with $($mergedUnits.Count) units."
