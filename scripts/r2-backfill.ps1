<#
.SYNOPSIS
    XIVLauncher R2 backfill script.
    Backfills Velopack nupkg assets from GitHub Releases to Cloudflare R2.

.DESCRIPTION
    1. Fetch latest N GitHub Releases
    2. For each release, download *.nupkg, compute SHA1/SHA256/Size
    3. Upload nupkgs to R2 (1yr immutable)
    4. Build releases.win.json + RELEASES and upload (5min cache)
    5. Clean up stale nupkgs in R2 not in the backfill set

.ENVIRONMENT
    GH_TOKEN               - GitHub Token (read releases)
    CLOUDFLARE_API_TOKEN   - Cloudflare API Token (R2 read/write)
    CLOUDFLARE_ACCOUNT_ID  - Cloudflare Account ID
    GITHUB_REPOSITORY      - Repository full name
#>

param(
    [string]$BucketName = 'xivlauncher-distribute',
    [string]$PackId     = 'XIVLauncherCN',
    [int]$Limit         = 10
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Msg) {
    Write-Host ">>> $Msg"
}

if (-not (Get-Command wrangler -ErrorAction SilentlyContinue)) {
    npm install -g wrangler
}

$workDir = New-Item -ItemType Directory -Path 'backfill_work' -Force

# ---- 1. Fetch latest N release tags ----
Write-Step "Fetching latest $Limit GitHub Releases..."
$releases     = gh release list --repo $env:GITHUB_REPOSITORY --limit $Limit --json tagName --jq '.[].tagName'
$releaseArray = @($releases)
Write-Host "  Found $($releaseArray.Count) releases"

if ($releaseArray.Count -eq 0) {
    throw 'No releases found.'
}

$allAssets = [System.Collections.Generic.List[object]]::new()

# ---- 2. Download nupkgs per release ----
foreach ($tag in $releaseArray) {
    Write-Step "Processing $tag"

    gh release download $tag --repo $env:GITHUB_REPOSITORY --pattern '*.nupkg' --dir $workDir --clobber 2>&1 | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "  Download of $tag failed (exit=$LASTEXITCODE), skipping"
        continue
    }

    $nupkgFiles = Get-ChildItem -LiteralPath $workDir -Filter '*.nupkg' -File
    if ($nupkgFiles.Count -eq 0) {
        Write-Warning "  $tag has no nupkg assets"
        continue
    }

    foreach ($file in $nupkgFiles) {
        $fileName = $file.Name
        $fileSize = $file.Length
        $sha1     = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1).Hash.ToUpperInvariant()
        $sha256   = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()

        if ($fileName -match '^XIVLauncherCN-(.+)-(full|delta)\.nupkg$') {
            $version = $Matches[1]
            $type    = if ($Matches[2] -eq 'full') { 'Full' } else { 'Delta' }
        }
        else {
            Write-Warning "  Cannot parse version from filename: $fileName, skipping"
            continue
        }

        Write-Host "  $fileName  v=$version  type=$type  sha1=$sha1  size=$fileSize"

        $allAssets.Add([PSCustomObject]@{
            PackageId = $PackId
            Version   = $version
            Type      = $type
            FileName  = $fileName
            SHA1      = $sha1
            SHA256    = $sha256
            Size      = $fileSize
        })

        Write-Host "    Uploading $fileName to R2..."
        npx wrangler r2 object put "$BucketName/$fileName" `
            --remote --file $file.FullName `
            --cache-control 'public, max-age=31536000, immutable' `
            --content-type 'application/octet-stream'

        if ($LASTEXITCODE -ne 0) {
            throw "Upload of $fileName failed"
        }
    }

    Remove-Item "$workDir\*.nupkg" -Force
}

# ---- 3. Sort, build and upload release feeds ----
$sortedAssets = $allAssets | Sort-Object { [Version]($_.Version -replace '^v', '') } -Descending

$releaseJson = @{ Assets = @($sortedAssets) } | ConvertTo-Json -Depth 3
$releaseJsonPath = Join-Path $workDir 'releases.win.json'
$releaseJson | Set-Content -LiteralPath $releaseJsonPath -Encoding utf8NoBOM

Write-Host "`n=== releases.win.json ==="
Write-Host $releaseJson

$releasesContent = ($sortedAssets | ForEach-Object { "$($_.SHA1) $($_.FileName) $($_.Size)" }) -join "`n"
$releasesPath = Join-Path $workDir 'RELEASES'
$releasesContent | Set-Content -LiteralPath $releasesPath -Encoding utf8NoBOM -NoNewline

Write-Host "`n=== RELEASES ==="
Write-Host $releasesContent

Write-Step 'Uploading release feeds...'
npx wrangler r2 object put "$BucketName/releases.win.json" `
    --remote --file $releaseJsonPath `
    --cache-control 'public, max-age=300' `
    --content-type 'application/json; charset=utf-8'

if ($LASTEXITCODE -ne 0) { throw 'Upload of releases.win.json failed' }

npx wrangler r2 object put "$BucketName/RELEASES" `
    --remote --file $releasesPath `
    --cache-control 'public, max-age=300' `
    --content-type 'text/plain; charset=utf-8'

if ($LASTEXITCODE -ne 0) { throw 'Upload of RELEASES failed' }

Write-Host "`nUpload complete: $($sortedAssets.Count) nupkgs from $($releaseArray.Count) releases."

# ---- 4. Clean up stale nupkgs in R2 ----
Write-Step 'Cleaning up stale nupkgs in R2...'
$keepFileNames = @{}
foreach ($a in $sortedAssets) { $keepFileNames[$a.FileName] = $true }

$apiUrl = "https://api.cloudflare.com/client/v4/accounts/$env:CLOUDFLARE_ACCOUNT_ID/r2/buckets/$BucketName/objects?limit=1000"
try {
    $response = Invoke-RestMethod -Uri $apiUrl -Headers @{ Authorization = "Bearer $env:CLOUDFLARE_API_TOKEN" } -ErrorAction Stop
    $allKeys = @($response.result | Where-Object { $_.key -match '\.nupkg$' } | ForEach-Object { $_.key })
    Write-Host "  $($allKeys.Count) nupkgs in R2"

    $deleted = 0
    foreach ($key in $allKeys) {
        if (-not $keepFileNames.ContainsKey($key)) {
            Write-Host "  Deleting stale nupkg: $key"
            npx wrangler r2 object delete "$BucketName/$key" --remote
            if ($LASTEXITCODE -eq 0) { $deleted++ }
        }
    }
    Write-Host "  Deleted $deleted stale nupkgs"
}
catch {
    Write-Warning "  Failed to list R2 objects: $_ , skipping cleanup."
}

Write-Host "`nBackfill and cleanup complete."

# ---- cleanup ----
Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
