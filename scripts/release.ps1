<#
.SYNOPSIS
    Собирает альфа-сборку Sweet Taste для Windows.

.DESCRIPTION
    Запускает тесты, публикует self-contained win-x64, добавляет README,
    скрипты обновления и ярлыка, version.json, затем упаковывает ZIP и SHA-256.

.EXAMPLE
    pwsh -File scripts/release.ps1

.EXAMPLE
    pwsh -File scripts/release.ps1 -Version 0.1.0-alpha.2
#>
param(
    [string]$Version = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$desktopProject = Join-Path $root 'src\SweetTaste.Desktop\SweetTaste.Desktop.csproj'
$testProject = Join-Path $root 'tests\SweetTaste.Core.Tests\SweetTaste.Core.Tests.csproj'
$migrator = Join-Path $root 'src\SweetTaste.Core\Storage\DatabaseMigrator.cs'
$releaseFiles = Join-Path $PSScriptRoot 'release'

if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $desktopProject -Raw
    $Version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}

if (-not $Version) {
    throw 'Не удалось определить версию. Укажите её: -Version 0.1.0-alpha.1'
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Версия должна выглядеть как 0.1.0-alpha.1, а не '$Version'."
}

$schemaMatch = [regex]::Match(
    (Get-Content -LiteralPath $migrator -Raw),
    'CurrentVersion\s*=\s*(\d+)')

if (-not $schemaMatch.Success) {
    throw 'Не удалось определить версию схемы базы в DatabaseMigrator.cs.'
}

$schema = [int]$schemaMatch.Groups[1].Value
$distRoot = Join-Path $root 'dist'
$stageName = "SweetTaste-$Version-win-x64"
$stageDir = Join-Path $distRoot $stageName
$zipPath = "$stageDir.zip"
$hashPath = "$zipPath.sha256"
$publishDir = Join-Path $root "artifacts\publish\win-x64\$Version"

Write-Host ''
Write-Host "Sweet Taste $Version · схема базы $schema" -ForegroundColor Cyan
Write-Host ''

if (-not $SkipTests) {
    Write-Host '1/5 Тесты...' -ForegroundColor Yellow
    & dotnet test $testProject -c Release --nologo -v q

    if ($LASTEXITCODE -ne 0) {
        throw 'Тесты не прошли — сборка остановлена.'
    }
}
else {
    Write-Host '1/5 Тесты пропущены (-SkipTests).' -ForegroundColor DarkYellow
}

Write-Host '2/5 Публикация self-contained win-x64...' -ForegroundColor Yellow

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

& dotnet publish $desktopProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir `
    --nologo `
    -v q

if ($LASTEXITCODE -ne 0) {
    throw 'Публикация не удалась.'
}

Write-Host '3/5 Сборка папки релиза...' -ForegroundColor Yellow

foreach ($path in @($stageDir, $zipPath, $hashPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Get-ChildItem -LiteralPath $publishDir | Copy-Item -Destination $stageDir -Recurse -Force

Get-ChildItem -LiteralPath $stageDir -Filter '*.pdb' -Recurse |
    Remove-Item -Force -ErrorAction SilentlyContinue

$frameworkExe = Join-Path $stageDir 'SweetTaste.Desktop.exe'
$appExe = Join-Path $stageDir 'SweetTaste.exe'

if (Test-Path -LiteralPath $frameworkExe) {
    Move-Item -LiteralPath $frameworkExe -Destination $appExe -Force
}

if (-not (Test-Path -LiteralPath $appExe)) {
    throw 'В папке публикации не нашёлся SweetTaste.exe.'
}

# Служебные файлы уходят в подпапку tools: в корне остаются только
# приложение и его компоненты, чтобы пользователь не путался в списке.
$toolsDir = Join-Path $stageDir 'tools'
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $releaseFiles 'README.txt') -Destination $toolsDir -Force
Copy-Item -LiteralPath (Join-Path $releaseFiles 'Update.cmd') -Destination $toolsDir -Force
Copy-Item -LiteralPath (Join-Path $releaseFiles 'Update.ps1') -Destination $toolsDir -Force
Copy-Item -LiteralPath (Join-Path $releaseFiles 'AutoUpdate.ps1') -Destination $toolsDir -Force
Copy-Item -LiteralPath (Join-Path $releaseFiles 'CreateShortcut.cmd') -Destination $toolsDir -Force

$versionJson = [ordered]@{
    product        = 'Sweet Taste'
    version        = $Version
    runtime        = 'win-x64'
    selfContained  = $true
    databaseSchema = $schema
    built          = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
} | ConvertTo-Json

[System.IO.File]::WriteAllText(
    (Join-Path $stageDir 'version.json'),
    $versionJson,
    (New-Object System.Text.UTF8Encoding($false)))

# README и PowerShell-скрипт — с BOM (старые Notepad и Windows PowerShell 5.1),
# cmd-файлы — без BOM. Всем файлам ставим CRLF: cmd.exe плохо читает LF-переводы строк.
function Set-ReleaseTextFile([string]$Path, [bool]$WithBom) {
    $text = [System.IO.File]::ReadAllText($Path)
    $text = $text -replace "`r`n", "`n"
    $text = $text -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($WithBom)))
}

Set-ReleaseTextFile (Join-Path $toolsDir 'README.txt') $true
Set-ReleaseTextFile (Join-Path $toolsDir 'Update.ps1') $true
Set-ReleaseTextFile (Join-Path $toolsDir 'AutoUpdate.ps1') $true
Set-ReleaseTextFile (Join-Path $toolsDir 'Update.cmd') $false
Set-ReleaseTextFile (Join-Path $toolsDir 'CreateShortcut.cmd') $false

Write-Host '4/5 ZIP...' -ForegroundColor Yellow
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
Compress-Archive -LiteralPath $stageDir -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host '5/5 SHA-256...' -ForegroundColor Yellow
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipName = Split-Path -Leaf $zipPath
[System.IO.File]::WriteAllText(
    $hashPath,
    "$hash  $zipName`r`n",
    (New-Object System.Text.UTF8Encoding($false)))

$zipSize = [Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
$folderSize = [Math]::Round(
    (Get-ChildItem -LiteralPath $stageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Host ''
Write-Host 'Готово.' -ForegroundColor Green
Write-Host "  Папка:  $stageDir ($folderSize МБ)"
Write-Host "  Архив:  $zipPath ($zipSize МБ)"
Write-Host "  SHA256: $hashPath"
Write-Host ''
