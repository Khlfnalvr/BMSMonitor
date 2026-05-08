# ============================================================
# build_installer.ps1
# Publish SOCTester + buat installer .exe (Inno Setup)
# Jalankan dari folder SOCTester\ (lokasi file ini)
# ============================================================

$ErrorActionPreference = "Stop"
$root   = $PSScriptRoot
$iscc   = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

# ── 1. Install Inno Setup jika belum ada ─────────────────────────────────────
if (-not (Test-Path $iscc)) {
    Write-Host ">>> Inno Setup tidak ditemukan. Menginstal via winget..." -ForegroundColor Cyan
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Inno Setup gagal terinstal. Coba instal manual dari https://jrsoftware.org/isdl.php"
    }
    Write-Host ">>> Inno Setup berhasil diinstal." -ForegroundColor Green
}

# ── 2. Publish app ───────────────────────────────────────────────────────────
Write-Host ""
Write-Host ">>> Publish SOCTester (Release, win-x64, self-contained)..." -ForegroundColor Cyan

$publishOut = Join-Path $root "Publish\AppFiles"

dotnet publish "$root\SOCTester\SOCTester.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:WindowsPackageType=None `
    -o $publishOut

if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish gagal." }
Write-Host ">>> Publish selesai: $publishOut" -ForegroundColor Green

# ── 3. Compile installer ─────────────────────────────────────────────────────
Write-Host ""
Write-Host ">>> Compile installer dengan Inno Setup..." -ForegroundColor Cyan

$issFile = Join-Path $root "installer.iss"
& $iscc $issFile

if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup compile gagal." }

$outExe = Join-Path $root "Publish\SOCTesterSetup.exe"
$sizeMB = [math]::Round((Get-Item $outExe).Length / 1MB, 1)

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " INSTALLER BERHASIL DIBUAT!" -ForegroundColor Green
Write-Host " File : $outExe" -ForegroundColor Green
Write-Host " Size : $sizeMB MB" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Distribusikan file SOCTesterSetup.exe ke komputer tujuan."
Write-Host "Komputer tujuan tidak perlu install apapun terlebih dahulu."
