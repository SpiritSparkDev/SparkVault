<#
.SYNOPSIS
    Builds the SparkVault installer: publishes a self-contained win-x64 build, then compiles
    SparkVault.iss with Inno Setup.

.DESCRIPTION
    Requires Inno Setup 6 (ISCC.exe) to be installed — https://jrsoftware.org/isdl.php.
    Output: installer\output\SparkVault-Setup.exe
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"
$publishDir = Join-Path $installerDir "publish"
$csproj = Join-Path $root "src\SparkVault.App\SparkVault.App.csproj"

Write-Host "Publishing self-contained win-x64 build..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$isccPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $isccPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $isccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $isccPath) {
    Write-Host ""
    Write-Host "Publish succeeded (output: $publishDir), but Inno Setup's ISCC.exe was not found." -ForegroundColor Yellow
    Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php, then either:" -ForegroundColor Yellow
    Write-Host "  - re-run this script, or" -ForegroundColor Yellow
    Write-Host "  - open installer\SparkVault.iss in the Inno Setup IDE and press Compile." -ForegroundColor Yellow
    exit 1
}

Write-Host "Compiling installer with $isccPath..." -ForegroundColor Cyan
& $isccPath (Join-Path $installerDir "SparkVault.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done: installer\output\SparkVault-Setup.exe" -ForegroundColor Green
