# ===== align.ps1 : align local avatar images to AvatarDataTable.json =====
$basePath = "D:\TrustNTE\Assets\AvatarDataTable"
$jsonPath = Join-Path $basePath "AvatarDataTable.json"
$alignDir = Join-Path $basePath "aligned"
$mapPath  = Join-Path $basePath "aligned_map.txt"
$missPath = Join-Path $basePath "aligned_missing.txt"
$orphPath = Join-Path $basePath "aligned_orphans.txt"

if (-not (Test-Path $alignDir)) { New-Item -ItemType Directory -Path $alignDir | Out-Null }
foreach ($sub in @("256","custom256","other")) {
    $p = Join-Path $alignDir $sub
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p | Out-Null }
}

# --- 1) index local files by folder + name variants ---
$idx = @{}
$allLocal = @()
foreach ($dir in @("100","200","256")) {
    $dirPath = Join-Path $basePath $dir
    if (-not (Test-Path $dirPath)) { continue }
    Get-ChildItem $dirPath -Filter *.png | ForEach-Object {
        $allLocal += $_.FullName
        $raw = $_.BaseName.ToLower()
        $vs = New-Object System.Collections.Generic.List[string]
        $vs.Add($raw)
        $n = $raw
        if ($n -match '_256_touxiang$') { $n = $n -replace '_256_touxiang$',''; $vs.Add($n) }
        if ($n -match '_100$')          { $n = $n -replace '_100$','';          $vs.Add($n) }
        if ($n -match '_256$')          { $n = $n -replace '_256$','';          $vs.Add($n) }
        foreach ($v in $vs) {
            $k = "$dir|$v"
            if (-not $idx.ContainsKey($k)) { $idx[$k] = $_.FullName }
        }
    }
}
Write-Host ("Local files indexed: {0}" -f $allLocal.Count) -ForegroundColor Cyan

# --- 2) load JSON ---
$json = Get-Content $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$root = if ($json -is [array]) { $json[0] } else { $json }
$rowsObj = $root.Rows
if (-not $rowsObj) { $rowsObj = $root.Properties.Rows }
$rows = @($rowsObj.PSObject.Properties)
Write-Host ("JSON rows: {0}" -f $rows.Count) -ForegroundColor Cyan

function Get-Candidates([string]$asset) {
    $list = New-Object System.Collections.Generic.List[string]
    $a = $asset.ToLower()
    $list.Add($a)                                          # exact name
    if ($a -match '^(.+)_256_(\d+)$') {
        $b = $Matches[1]; $i = $Matches[2]
        if ($i -eq '1') { $list.Add($b + "_256") }         # strip trailing .1
        $list.Add($b + "_" + $i + "_256")                  # number position shift
    }
    if ($a -match '^(.+)_256$') { $list.Add($Matches[1]) } # strip size suffix
    return $list
}

# --- 3) resolve each JSON row to a local file, copy with aligned name ---
$used = @{}; $map = @(); $missing = @()
foreach ($row in $rows) {
    $rk = $row.Name
    $cn = ""
    if ($row.Value.Name -and $row.Value.Name.SourceString) { $cn = $row.Value.Name.SourceString }
    $path = $row.Value.BigImage.AssetPathName
    if (-not $path) { $path = $row.Value.icon.AssetPathName }
    if (-not $path) { continue }
    if ($path -notmatch '/([^/]+?)\.([^/\.]+)$') { continue }
    $asset = $Matches[1]

    # which game folder does the JSON path point to
    $zone = "other"; $searchDirs = @("256","200","100")
    if ($path -match '/AvatarImage/256/')                  { $zone = "256";       $searchDirs = @("256") }
    elseif ($path -match '/AvatarImage/100/')              { $zone = "100";       $searchDirs = @("100","256") }
    elseif ($path -match '/AvatarImage/200/')              { $zone = "200";       $searchDirs = @("200","256") }
    elseif ($path -match '/AvatarImage/CustomAvatar/256/') { $zone = "custom256"; $searchDirs = @("256") }
    elseif ($path -match '/AvatarImage/BagelAvatar/')      { $zone = "bagel";     $searchDirs = @() }

    $hit = $null; $how = ""
    foreach ($d in $searchDirs) {
        foreach ($c in (Get-Candidates $asset)) {
            $k = "$d|$c"
            if ($idx.ContainsKey($k)) {
                $hit = $idx[$k]
                if ($c -eq $asset.ToLower()) { $how = "exact" } else { $how = "mapped" }
                break
            }
        }
        if ($hit) { break }
    }

    if ($hit) {
        $used[$hit] = $true
        $sub = $zone
        if ($zone -eq "100" -or $zone -eq "200") { $sub = "other" }
        $target = Join-Path (Join-Path $alignDir $sub) ($asset + ".png")
        Copy-Item $hit $target -Force
        $map += ("{0} | {1} | {2} | {3} | {4} | {5}" -f $rk, $cn, $asset, (Split-Path $hit -Leaf), $how, $sub)
    } else {
        $missing += ("{0} | {1} | {2} | {3}" -f $rk, $cn, $asset, $zone)
    }
}

# --- 4) orphan local files (never referenced by any JSON row) ---
$orphans = @()
foreach ($f in $allLocal) {
    if (-not $used.ContainsKey($f)) { $orphans += $f.Substring($basePath.Length + 1) }
}

# --- 5) write reports ---
$enc = New-Object System.Text.UTF8Encoding($false)
[string[]]$mapArr = $map; [string[]]$missArr = $missing; [string[]]$orphArr = $orphans
[System.IO.File]::WriteAllLines($mapPath,  $mapArr,  $enc)
[System.IO.File]::WriteAllLines($missPath, $missArr, $enc)
[System.IO.File]::WriteAllLines($orphPath, $orphArr, $enc)

Write-Host ("Aligned copies : {0}  -> {1}" -f $map.Count, $alignDir) -ForegroundColor Green
Write-Host ("Missing rows   : {0}  -> {1}" -f $missing.Count, $missPath) -ForegroundColor Yellow
Write-Host ("Orphan files   : {0}  -> {1}" -f $orphans.Count, $orphPath) -ForegroundColor Yellow
