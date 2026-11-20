# ===== fork_map.ps1 : build Fork weapon map from local DT_ForkItemData.json =====
$base     = "D:\TrustNTE\Assets\Fork"
$jsonPath = Join-Path $base "DT_ForkItemData.json"
$mapPath  = Join-Path $base "fork_map.txt"
$missPath = Join-Path $base "fork_missing.txt"
$orphPath = Join-Path $base "fork_orphans.txt"
$listPath = Join-Path $base "fork_list.json"

if (-not (Test-Path $jsonPath)) { Write-Host "DT_ForkItemData.json not found!" -ForegroundColor Red; exit 1 }

# --- 1) index local icons (normalize: strip fork_ prefix and size suffix, lowercase) ---
$local = @{}
foreach ($d in @("100","256","1024")) {
    $p = Join-Path $base $d
    if (-not (Test-Path $p)) { continue }
    Get-ChildItem $p -Filter *.png | ForEach-Object {
        $n = $_.BaseName.ToLower() -replace '^fork_','' -replace '_(100|256)$',''
        $key = "$d|$n"
        if (-not $local.ContainsKey($key)) { $local[$key] = $_.FullName }
    }
}
Write-Host ("Local icons indexed: {0}" -f $local.Count) -ForegroundColor Cyan

# --- 2) parse JSON (Rows and Properties are siblings, same as avatar table) ---
$json = Get-Content $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$root = if ($json -is [array]) { $json[0] } else { $json }
$rowsObj = $root.Rows
if (-not $rowsObj) { $rowsObj = $root.Properties.Rows }
$rows = @($rowsObj.PSObject.Properties)
Write-Host ("JSON rows: {0}" -f $rows.Count) -ForegroundColor Cyan

$rarityMap = @{ ORANGE = "S"; PURPLE = "A"; BLUE = "B" }

# --- 3) map every row, check local hits ---
$used = @{}; $map = @(); $missing = @(); $list = @()
foreach ($r in $rows) {
    $k = $r.Name; $v = $r.Value
    $cn = ""; if ($v.ItemName -and $v.ItemName.SourceString)    { $cn = $v.ItemName.SourceString }
    $en = ""; if ($v.ItemName -and $v.ItemName.LocalizedString) { $en = $v.ItemName.LocalizedString }
    $qRaw = ""; if ($v.ItemQuality) { $qRaw = $v.ItemQuality }
    $q   = $qRaw -replace '^.*QUALITY_',''
    $rar = if ($rarityMap.ContainsKey($q)) { $rarityMap[$q] } else { "?" }
    $ex  = ""; if ($v.ElementData -and $v.ElementData.ExclusiveCharacterIDArray) { $ex = (@($v.ElementData.ExclusiveCharacterIDArray) -join ',') }
    $short = $k.ToLower() -replace '^fork_',''

    $h256  = $local.ContainsKey("256|$short");   if ($h256)   { $used["256|$short"]   = $true }
    $h100  = $local.ContainsKey("100|$short");   if ($h100)   { $used["100|$short"]   = $true }
    $h1024 = $local.ContainsKey("1024|$short");  if ($h1024)  { $used["1024|$short"]  = $true }

    $map += ("{0} | {1} | {2} | {3}({4}) | excl={5} | 256:{6} 100:{7} 1024:{8}" -f $k,$cn,$en,$q,$rar,$ex,$h256,$h100,$h1024)
    if (-not $h256) { $missing += ("{0} | {1} | {2}" -f $k,$cn,$qRaw) }
    $list += [ordered]@{ id=$k; name=$cn; name_en=$en; quality=$q; rarity=$rar; exclusive=$ex; icon256=("256/fork_{0}_256.png" -f $short) }
}

# --- 4) local files never referenced by any row ---
$orphans = @()
foreach ($key in $local.Keys) { if (-not $used.ContainsKey($key)) { $orphans += $key } }

# --- 5) write outputs (UTF-8 no BOM) ---
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($mapPath,  [string[]]$map,     $enc)
[System.IO.File]::WriteAllLines($missPath, [string[]]$missing, $enc)
[System.IO.File]::WriteAllLines($orphPath, [string[]]$orphans, $enc)
$listJson = ConvertTo-Json @($list) -Depth 4
[System.IO.File]::WriteAllText($listPath, $listJson, $enc)

Write-Host ("Rows mapped  : {0}" -f $map.Count) -ForegroundColor Green
Write-Host ("Missing 256  : {0}  -> {1}" -f $missing.Count, $missPath) -ForegroundColor Yellow
Write-Host ("Orphan files : {0}  -> {1}" -f $orphans.Count, $orphPath) -ForegroundColor Yellow
Write-Host ("Clean list   : {0}" -f $listPath) -ForegroundColor Green
