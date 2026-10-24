# Build MEV蛟龙控制台 (MEV Jiaolong Control Center) and package it with Inno Setup.
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-SkipPublish]
# Requirements: .NET 8 SDK, Inno Setup 6 (ISCC.exe)
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root (installer/..)
$project = Join-Path $root 'src\MEVJiaolongControlCenter\MEVJiaolongControlCenter.csproj'
$publish = Join-Path $root 'publish'
$iss = Join-Path $root 'installer\MEVJiaolongControlCenter.iss'
$dist = Join-Path $root 'dist'

function Find-Iscc {
    $candidates = @(
        $env:ISCC,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
    }
    throw 'ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup'
}

if (-not $SkipPublish) {
    Write-Host '==> Cleaning publish output' -ForegroundColor Cyan
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host '==> dotnet publish (Release, win-x64, framework-dependent)' -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} elseif (-not (Test-Path $publish)) {
    throw "Publish folder not found: $publish"
}

if (-not (Test-Path (Join-Path $publish 'MEVJiaolongControlCenter.exe'))) {
    throw "MEVJiaolongControlCenter.exe missing from $publish"
}

# 内置 WMI 桥(.NET Framework): 约 20MB 常驻, 替代约 100MB 的常驻 PowerShell。
# 主程序找不到它时会自动回退到 PowerShell 脚本桥, 所以编译失败只警告。
Write-Host '==> Building WmiBridge.exe (.NET Framework WMI bridge)' -ForegroundColor Cyan
$helperSrc = Join-Path $root 'tools\WmiBridge\Bridge.cs'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path $helperSrc) {
    if (Test-Path $csc) {
        $helperOut = Join-Path $publish 'WmiBridge.exe'
        & $csc '/nologo' '/optimize+' '/target:exe' "/out:$helperOut" $helperSrc '/r:System.Management.dll'
        if ($LASTEXITCODE -ne 0) { throw "WmiBridge.exe compile failed with exit code $LASTEXITCODE" }
        Write-Host "WmiBridge: $helperOut" -ForegroundColor Green
    } else {
        Write-Warning 'csc.exe (.NET Framework) not found - installer will fall back to PowerShell bridge'
    }
}

$iscc = Find-Iscc
Write-Host "==> Compiling installer with $iscc" -ForegroundColor Cyan
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Get-ChildItem $dist -Filter 'MEV-Jiaolong-Control-Center-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw 'Installer was not produced' }

$sizeMb = [math]::Round($setup.Length / 1MB, 1)
Write-Host ''
Write-Host "Installer: $($setup.FullName) ($sizeMb MB)" -ForegroundColor Green

# 附带校验和, 发布到 Releases 时一并上传, 用户可核对下载文件未被篡改/替换
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLower()
$sumFile = Join-Path $dist 'SHA256SUMS.txt'
"$hash  $($setup.Name)" | Set-Content $sumFile -Encoding ASCII
Write-Host "SHA256: $hash" -ForegroundColor Green
Write-Host "Checksums: $sumFile" -ForegroundColor Green
