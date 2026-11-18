param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$ZipPath
)

$ErrorActionPreference = 'Stop'

function Stop-WithError([string]$Message) {
    Write-Host ''
    Write-Host $Message -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Get-ReleaseVersion([string]$Directory) {
    $file = Join-Path $Directory 'version.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        return 'неизвестна'
    }

    try {
        return (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).version
    }
    catch {
        return 'неизвестна'
    }
}

try {
    $AppDir = [System.IO.Path]::GetFullPath($AppDir).TrimEnd('\', '/')
    $ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
}
catch {
    Stop-WithError 'Не удалось определить путь к папке приложения или к ZIP.'
}

# Скрипт запускается из временной копии, поэтому рабочую папку можно менять.
Set-Location -LiteralPath $env:TEMP

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    Stop-WithError "Файл не найден: $ZipPath"
}

if (-not (Test-Path -LiteralPath (Join-Path $AppDir 'SweetTaste.exe') -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Join-Path $AppDir 'SweetTaste.Desktop.exe') -PathType Leaf)) {
    Stop-WithError "В папке нет Sweet Taste: $AppDir. Запустите Update.cmd из папки программы."
}

$running = @(Get-Process -Name 'SweetTaste', 'SweetTaste.Desktop' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Stop-WithError 'Sweet Taste сейчас запущен. Закройте все окна приложения и повторите обновление.'
}

$currentVersion = Get-ReleaseVersion $AppDir
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "sweet-taste-update-$stamp"

New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    Write-Host 'Распаковываю новую версию...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $work)

    $exe = Get-ChildItem -Path $work -Recurse -Filter 'SweetTaste.exe' -File |
        Select-Object -First 1

    if ($null -eq $exe) {
        Stop-WithError 'В архиве нет SweetTaste.exe. Выберите ZIP, скачанный для Sweet Taste.'
    }

    $newDir = $exe.Directory.FullName
    $newVersion = Get-ReleaseVersion $newDir

    if ($newVersion -ne 'неизвестна' -and $newVersion -eq $currentVersion) {
        $answer = Read-Host "Установлена та же версия $currentVersion. Всё равно обновить? (y/n)"
        if ($answer -notmatch '^(y|Y|д|Д)') {
            Stop-WithError 'Обновление отменено.'
        }
    }

    $parent = Split-Path -Parent $AppDir
    $leaf = Split-Path -Leaf $AppDir
    $backupName = '{0}-old-{1}-{2}' -f $leaf, $currentVersion, $stamp
    $backupDir = Join-Path $parent $backupName

    Write-Host ''
    Write-Host "Текущая версия: $currentVersion"
    Write-Host "Новая версия:   $newVersion"
    Write-Host "Папка:          $AppDir"
    Write-Host ''
    Write-Host 'Заменяю файлы приложения...'

    Rename-Item -LiteralPath $AppDir -NewName $backupName

    try {
        Move-Item -LiteralPath $newDir -Destination $AppDir
    }
    catch {
        if (Test-Path -LiteralPath $AppDir) {
            Remove-Item -LiteralPath $AppDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        Rename-Item -LiteralPath $backupDir -NewName $leaf
        Stop-WithError ("Не удалось заменить папку приложения. Старая версия возвращена.`n" +
            $_.Exception.Message)
    }

    Write-Host ''
    Write-Host "Готово: Sweet Taste обновлён до версии $newVersion." -ForegroundColor Green
    Write-Host "Старая версия сохранена для отката: $backupDir"
    Write-Host 'Данные в %APPDATA%\SweetTaste не изменялись.'
    Write-Host ''
    Write-Host 'Запускаю новую версию...'

    Start-Process -FilePath (Join-Path $AppDir 'SweetTaste.exe') -WorkingDirectory $AppDir | Out-Null

    Write-Host 'Готово.' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host ('Обновление не завершено: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
