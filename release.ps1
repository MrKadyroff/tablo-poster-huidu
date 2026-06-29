<#
.SYNOPSIS
  Build a versioned self-contained bundle and (optionally) publish it as a GitHub Release,
  so cashier PCs can self-update from inside the app (see UI/UpdateService.cs).

.DESCRIPTION
  Version is read from LedImageUpdaterService.csproj <Version>. The release tag MUST be
  v<Version> (e.g. v1.0.1) — the in-app updater compares the tag to the running version.
  The app downloads the .zip asset, swaps the program + content/common, and preserves the
  operator's data/markup (appsettings.json, config/, layout/, content/points/).

.PARAMETER Notes
  Release notes shown in the in-app "Что нового" panel. Defaults to "eCash Tablo v<Version>".

.PARAMETER NoPublish
  Build the zip only; do not create the GitHub release.

.EXAMPLE
  # 1) bump <Version> in the csproj (e.g. 1.0.1), then:
  powershell -ExecutionPolicy Bypass -File .\release.ps1 -Notes "Авто-реконнект Wi-Fi, кнопка обновления"
#>
[CmdletBinding()]
param(
    [string]$Notes = "",
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'LedImageUpdaterService.csproj'
$out  = Join-Path $root 'publish-win'

# ── 1. Read <Version> from the csproj ────────────────────────────────────────
[xml]$csproj = Get-Content $proj
$version = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> found in $proj" }
$tag = "v$version"
$zip = Join-Path $root "eCashTablo-$tag-win-x64.zip"
Write-Host "==> Version $version  (tag $tag)" -ForegroundColor Cyan

# ── 1b. Idempotency: if this version is already released, do nothing ──────────
# Lets the CI workflow run on every push — a new release appears only when
# <Version> in the csproj changes.
if (-not $NoPublish) {
    $ghEarly = Get-Command gh -ErrorAction SilentlyContinue
    if ($ghEarly) {
        gh release view $tag *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Release $tag already exists — nothing to do. Bump <Version> to publish a new one." -ForegroundColor Yellow
            return
        }
    }
}

# ── 2. Clean + publish single-file self-contained win-x64 ────────────────────
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $proj --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    --output $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── 3. Ensure data folders + scripts are in the bundle (parity with publish-win.bat) ──
foreach ($d in 'config', 'layout') {
    $src = Join-Path $root $d
    if (Test-Path $src) { Copy-Item $src (Join-Path $out $d) -Recurse -Force }
}
$common = Join-Path $root 'content\common'
if (Test-Path $common) { Copy-Item $common (Join-Path $out 'content\common') -Recurse -Force }
$points = Join-Path $root 'content\points'
if (Test-Path $points) { Copy-Item $points (Join-Path $out 'content\points') -Recurse -Force }
foreach ($s in 'install-service.ps1', 'start.bat', 'stop.bat') {
    $p = Join-Path $root $s
    if (Test-Path $p) { Copy-Item $p $out -Force }
}
foreach ($d in 'logs', 'relay-output') {
    $p = Join-Path $out $d
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p | Out-Null }
}

# ── 4. Zip the bundle (versioned asset name the updater can find) ────────────
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Write-Host "==> Bundle: $zip" -ForegroundColor Green

# ── 5. Create the GitHub release ─────────────────────────────────────────────
if ($NoPublish) { Write-Host "NoPublish set — skipping GitHub release." -ForegroundColor Yellow; return }

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Warning "GitHub CLI (gh) not found. Create the release manually:"
    Write-Warning "  - tag/title: $tag"
    Write-Warning "  - attach asset: $zip"
    return
}

if (-not $Notes) { $Notes = $env:RELEASE_NOTES }          # CI: commit message
if (-not $Notes) { $Notes = "eCash Tablo $tag" }
gh release create $tag $zip --title $tag --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "==> Released $tag on GitHub." -ForegroundColor Green
