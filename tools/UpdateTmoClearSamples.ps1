# 티모지지 공개 클리어 기록을 컴팩트 표본 파일로 스냅샷한다.
# 사용: .\tools\UpdateTmoClearSamples.ps1 [-SinceDays 14] [-MaxRecords 40000] [-OutPath .\Data\tmo-clear-samples.json]
param(
    [int]$SinceDays = 14,
    [int]$MaxRecords = 40000,
    [string]$OutPath = (Join-Path $PSScriptRoot "..\Data\tmo-clear-samples.json"),
    [double]$DelaySeconds = 0.15
)

$ErrorActionPreference = "Stop"
$baseUrl = "https://tmo.gg/_api/ranks/ordr2/clears"
$since = (Get-Date).ToUniversalTime().AddDays(-$SinceDays)
$headers = @{ "User-Agent" = "OrandOverlay-ClearSnapshot/1.0 (read-only research)" }

$samples = New-Object System.Collections.Generic.List[object]
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$cursor = $null
$pages = 0

while ($samples.Count -lt $MaxRecords) {
    $url = "$baseUrl`?limit=50"
    if ($cursor) { $url += "&next=" + [uri]::EscapeDataString($cursor) }
    try {
        $page = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 30
    } catch {
        Write-Warning "요청 실패($url): $_ — 3초 후 1회 재시도"
        Start-Sleep -Seconds 3
        $page = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 30
    }
    $pages++
    if (-not $page.clears -or $page.clears.Count -eq 0) { break }

    $reachedSince = $false
    foreach ($clear in $page.clears) {
        $created = [datetime]::Parse($clear.createdAt, $null,
            [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        if ($created -lt $since) { $reachedSince = $true; break }
        if (-not $seen.Add([string]$clear.id)) { continue }
        $units = @()
        foreach ($unit in $clear.units) {
            $grade = [string]$unit.grade
            # 등급은 목표 상위 판정에만 쓰므로 초월/불멸/영원/제한만 남겨 파일을 줄인다.
            if ($grade -match '^(초월|불멸|영원|제한)') {
                $units += [ordered]@{ c = [string]$unit.code; k = [int]$unit.count; g = $grade }
            } else {
                $units += [ordered]@{ c = [string]$unit.code; k = [int]$unit.count }
            }
        }
        $samples.Add([ordered]@{
            i = [string]$clear.id
            t = $clear.createdAt
            d = [string]$clear.difficulty
            n = [int]$clear.unitCount
            u = $units
        }) | Out-Null
    }
    if ($reachedSince) { break }
    if (-not $page.nextCursor -or $page.nextCursor -eq $cursor) { break }
    $cursor = [string]$page.nextCursor
    if ($pages % 40 -eq 0) {
        Write-Host ("페이지 {0} · 표본 {1} · 커서 {2}" -f $pages, $samples.Count, $cursor)
    }
    Start-Sleep -Seconds $DelaySeconds
}

$document = [ordered]@{
    schemaVersion = 1
    source = $baseUrl
    capturedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    sinceDays = $SinceDays
    samples = $samples
}

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$json = $document | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText((Resolve-Path -Path (Split-Path -Parent $OutPath)).Path + "\" + (Split-Path -Leaf $OutPath), $json, [System.Text.UTF8Encoding]::new($false))

$byDifficulty = $samples | Group-Object d | ForEach-Object { "{0}={1}" -f $_.Name, $_.Count }
Write-Host ("완료: {0}개 표본, {1}페이지, 난이도 {2}" -f $samples.Count, $pages, ($byDifficulty -join " "))
Write-Host ("저장: {0}" -f $OutPath)
