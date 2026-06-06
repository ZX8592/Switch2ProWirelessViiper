param(
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectDir "Switch2ProWirelessViiper.csproj"
$releaseDir = Join-Path $projectDir "release"
$publishDir = Join-Path $releaseDir "app"

if (-not $NoClean -and (Test-Path -LiteralPath $releaseDir)) {
    $projectPath = [IO.Path]::GetFullPath($projectDir)
    $releasePath = [IO.Path]::GetFullPath($releaseDir)
    if (-not $releasePath.StartsWith($projectPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release path: $releasePath"
    }

    Remove-Item -LiteralPath $releasePath -Recurse -Force
}

dotnet publish $projectFile -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$viiperDir = Join-Path $projectDir "tools\viiper"
$viiperExe = Join-Path $viiperDir "viiper.exe"
$viiperLicenses = Join-Path $viiperDir "VIIPER_LICENSES.txt"

if (Test-Path -LiteralPath $viiperExe) {
    Copy-Item -LiteralPath $viiperExe -Destination $publishDir -Force
}

if (Test-Path -LiteralPath $viiperLicenses) {
    Copy-Item -LiteralPath $viiperLicenses -Destination $publishDir -Force
}

$supportedCultures = @("en-us", "zh-CN", "ja-JP")
Get-ChildItem -LiteralPath $publishDir -Directory | ForEach-Object {
    $hasMui = Test-Path -LiteralPath (Join-Path $_.FullName "Microsoft.ui.xaml.dll.mui") -PathType Leaf
    $hasPhoneMui = Test-Path -LiteralPath (Join-Path $_.FullName "Microsoft.UI.Xaml.Phone.dll.mui") -PathType Leaf
    if (($hasMui -or $hasPhoneMui) -and ($supportedCultures -notcontains $_.Name)) {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File | Remove-Item -Force

$launcherSource = Join-Path $projectDir "launcher\Launcher.cs"
$launcherExe = Join-Path $releaseDir "Switch2ProWirelessViiper.exe"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found: $compiler"
}

$icon = Join-Path $projectDir "app.ico"
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/out:$launcherExe" $launcherSource
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Published launcher to $launcherExe"
Write-Host "Published app runtime to $publishDir"
