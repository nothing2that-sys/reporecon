#Requires -Version 5.1
<#
  RepoRecon release build: produces both the installer and the portable zip
  from a single dotnet publish. See docs/RepoRecon-packaging.md for the spec.
#>
$ErrorActionPreference = "Stop"

$buildDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $buildDir
$csprojPath = Join-Path $repoRoot "src\RepoRecon.App\RepoRecon.App.csproj"
$distDir = Join-Path $repoRoot "dist"
$publishDir = Join-Path $repoRoot "src\RepoRecon.App\bin\Release\net8.0-windows\win-x64\publish"
$issPath = Join-Path $buildDir "RepoRecon.iss"

# --- 1. Clean dist/ -----------------------------------------------------
Write-Host "=== 1. Cleaning dist/ ==="
if (Test-Path -LiteralPath $distDir) {
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# --- 2. Single source of truth for version ------------------------------
Write-Host "=== 2. Reading version from csproj ==="
[xml]$csprojXml = Get-Content -LiteralPath $csprojPath
$version = $null
foreach ($pg in $csprojXml.Project.PropertyGroup) {
    if ($pg.Version) { $version = $pg.Version; break }
}
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read <Version> from $csprojPath. Add it under <PropertyGroup> before building."
}
Write-Host "  Version = $version"

# --- 3. dotnet publish (self-contained single file) ---------------------
Write-Host "=== 3. dotnet publish (self-contained, single file, win-x64) ==="
& dotnet publish $csprojPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publishDir "RepoRecon.App.exe"))) {
    throw "Publish output not found at expected path: $publishDir"
}

# --- 4. Portable zip ------------------------------------------------------
Write-Host "=== 4. Building portable zip ==="
$portableStageDir = Join-Path $env:TEMP ("RepoRecon_PortableStage_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $portableStageDir -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $portableStageDir -Recurse -Force

    # Marker file: presence next to the exe switches the app into portable
    # mode (DatabasePathResolver / DpapiTokenStore read this at startup).
    New-Item -ItemType File -Path (Join-Path $portableStageDir "reporecon.portable") -Force | Out-Null

    $portableReadme = @"
RepoRecon 포터블 버전
======================

사용 방법
- 압축을 풀고 RepoRecon.App.exe를 실행하면 됩니다.
- 데이터는 exe 옆의 index.db, logs/, credentials/ 폴더에 생성됩니다.
- 저장된 토큰은 Windows 사용자 계정으로 암호화되므로, 다른 PC로 옮기면
  재입력이 필요합니다.
- reporecon.portable 파일을 지우면 일반(설치) 모드로 동작하며, 데이터 위치가
  %LOCALAPPDATA%\RepoRecon\ 로 바뀝니다.
"@
    Set-Content -LiteralPath (Join-Path $portableStageDir "README-portable.txt") -Value $portableReadme -Encoding UTF8

    $portableZipPath = Join-Path $distDir "RepoRecon-Portable-$version.zip"
    Compress-Archive -Path (Join-Path $portableStageDir "*") -DestinationPath $portableZipPath -CompressionLevel Optimal
    Write-Host "  Portable zip: $portableZipPath"
}
finally {
    Remove-Item -LiteralPath $portableStageDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 5. Locate iscc.exe ----------------------------------------------------
Write-Host "=== 5. Locating Inno Setup compiler (iscc.exe) ==="
$isccPath = $null

$isccCmd = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
if ($isccCmd) { $isccPath = $isccCmd.Source }

if (-not $isccPath) {
    $registryRoots = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*"
    )
    foreach ($root in $registryRoots) {
        $entries = Get-ItemProperty -Path $root -ErrorAction SilentlyContinue
        foreach ($entry in $entries) {
            if ($entry.InstallLocation) {
                $candidate = Join-Path $entry.InstallLocation "ISCC.exe"
                if (Test-Path -LiteralPath $candidate) { $isccPath = $candidate; break }
            }
        }
        if ($isccPath) { break }
    }
}

if (-not $isccPath) {
    $wellKnownPaths = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $wellKnownPaths) {
        if (Test-Path -LiteralPath $p) { $isccPath = $p; break }
    }
}

if (-not $isccPath) {
    throw "Inno Setup compiler (ISCC.exe) was not found via PATH, registry, or well-known install paths. Install Inno Setup and re-run this script."
}
Write-Host "  Using: $isccPath"

# --- 6. Compile installer ---------------------------------------------------
Write-Host "=== 6. Compiling installer with Inno Setup ==="
& $isccPath "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$distDir" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }

# --- 7. Summary --------------------------------------------------------------
Write-Host "`n=== 7. Build Summary ==="
Write-Host "  Version: $version"
Get-ChildItem -LiteralPath $distDir -File | ForEach-Object {
    $sizeMb = [Math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, $sizeMb)
}
