<#
.SYNOPSIS
    Builds NDS Station: the native emulator core (MSYS2 UCRT64) and the WinUI 3 app.

.PARAMETER Configuration
    Debug or Release (default Release).

.PARAMETER Publish
    Also produce a self-contained folder in .\dist ready to zip and share.

.PARAMETER Installer
    Also build the setup program (implies -Publish): .\dist\NDSStation-Setup-<version>.exe.
    Needs Inno Setup 6 (winget install JRSoftware.InnoSetup).

.PARAMETER Msys2
    MSYS2 install folder (default C:\msys64).
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Installer,
    [string]$Msys2 = 'C:\msys64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'src\NdsStation'
$nativeOut = Join-Path $app 'native-bin'

if (-not (Test-Path (Join-Path $root 'external\melonDS\CMakeLists.txt'))) {
    Write-Host 'Fetching melonDS sources...'
    git -C $root submodule update --init --recursive
}

# --- Native core -------------------------------------------------------------
$bash = Join-Path $Msys2 'usr\bin\bash.exe'
if (-not (Test-Path $bash)) { throw "MSYS2 not found in $Msys2 (see README)." }

$env:MSYSTEM = 'UCRT64'
$env:CHERE_INVOKING = '1'
$nativeDir = (Join-Path $root 'native') -replace '\\', '/'
$buildType = if ($Configuration -eq 'Debug') { 'Debug' } else { 'Release' }

Write-Host "Building ndscore ($buildType)..."
# stderr is folded into stdout: Windows PowerShell treats any native stderr line as an error.
& $bash -lc "cd '$nativeDir' && cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=$buildType -Wno-author > /dev/null 2>&1 && cmake --build build --target ndscore 2>&1"
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }

New-Item -ItemType Directory -Force $nativeOut | Out-Null
Copy-Item (Join-Path $root 'native\build\ndscore.dll') $nativeOut -Force
# Runtime DLLs ndscore links against dynamically.
foreach ($dll in 'SDL2.dll', 'libwinpthread-1.dll') {
    Copy-Item (Join-Path $Msys2 "ucrt64\bin\$dll") $nativeOut -Force
}

# --- App -------------------------------------------------------------------------
Write-Host "Building NdsStation ($Configuration)..."
dotnet build $app -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }

if ($Publish -or $Installer) {
    # The installer packs its own staging copy, so it never fights with a portable copy
    # in dist\NDSStation that may be running.
    $dist = if ($Publish) { Join-Path $root 'dist\NDSStation' } else { Join-Path $root 'dist\.installer-stage' }
    # Start clean so files removed from the app never linger in the package.
    if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
    dotnet publish $app -c $Configuration -p:Platform=x64 -o $dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # A package without WinUI's localized resources (*.mui) starts, then crashes with
    # 0x80073B01 the first time a control needs one of its strings (e.g. a focused TextBox).
    $required = 'NdsStation.exe', 'ndscore.dll', 'SDL2.dll', 'libwinpthread-1.dll', 'resources.pri',
        'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.Graphics.Canvas.dll', 'Assets\AppIcon.ico',
        'en-US\Microsoft.ui.xaml.dll.mui', 'it-IT\Microsoft.ui.xaml.dll.mui'
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $dist $_)) }
    if ($missing) { throw "Published package is incomplete, missing: $($missing -join ', ')" }
    Write-Host "Published to $dist"
}

if ($Installer) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }

    $version = ([xml](Get-Content (Join-Path $app 'NdsStation.csproj'))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    Write-Host "Building installer $version..."
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$dist" (Join-Path $root 'installer\NDSStation.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    Write-Host "Installer: $(Join-Path $root "dist\NDSStation-Setup-$version.exe")"
}
