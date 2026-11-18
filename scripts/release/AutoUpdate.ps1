param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256,
    [string]$DatabasePath = ''
)

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:LOCALAPPDATA 'SweetTaste'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir 'update.log'

function Write-Log([string]$Message) {
    try {
        $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    }
    catch {
        # Лог не должен ломать само обновление.
    }
}

function Start-App([string]$Directory) {
    $exe = Join-Path $Directory 'SweetTaste.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        Start-Process -FilePath $exe -WorkingDirectory $Directory | Out-Null
        return $true
    }

    return $false
}

function Read-Version([string]$Path) {
    try {
        $text = Get-Content -LiteralPath $Path -Raw
        if ($text -match '"version"\s*:\s*"([^"]+)"') {
            return $matches[1]
        }
    }
    catch {
    }

    return 'неизвестна'
}

$AppDir = [System.IO.Path]::GetFullPath($AppDir).TrimEnd('\', '/')
$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
$work = $null
$backupDir = $null
$renamed = $false

try {
    Write-Log "Начато обновление до $ExpectedVersion. Папка: $AppDir"

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
        throw "Архив обновления не найден: $ZipPath"
    }

    # Ждём, пока приложение закроет файлы. Не убиваем процесс принудительно.
    $deadline = (Get-Date).AddSeconds(120)
    while (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) {
            throw 'Приложение не закрылось за 2 минуты — обновление отменено.'
        }

        Start-Sleep -Milliseconds 500
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($ZipPath)
    try {
        $actualHash = ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }

    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw 'SHA-256 архива не совпадает с ожидаемым.'
    }

    # Копия базы до замены файлов. Основные данные лежат в %APPDATA% и не меняются.
    if ($DatabasePath -and (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
        $backupRoot = Join-Path (Split-Path -Parent $DatabasePath) 'backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $databaseBackup = Join-Path $backupRoot "pre-update-$stamp.db"
        Copy-Item -LiteralPath $DatabasePath -Destination $databaseBackup -Force
        Write-Log "Бэкап базы: $databaseBackup"
    }

    $work = Join-Path $env:TEMP ("sweet-taste-auto-update-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $work)

    $newExe = Get-ChildItem -Path $work -Recurse -Filter 'SweetTaste.exe' -File |
        Select-Object -First 1

    if ($null -eq $newExe) {
        throw 'В архиве нет SweetTaste.exe.'
    }

    $newDir = $newExe.Directory.FullName
    $versionFile = Join-Path $newDir 'version.json'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw 'В новой версии нет version.json.'
    }

    $newVersion = Read-Version $versionFile
    if ($newVersion -ne $ExpectedVersion) {
        throw "Версия в архиве ($newVersion) не совпадает с ожидаемой ($ExpectedVersion)."
    }

    $currentVersion = 'неизвестна'
    $currentVersionFile = Join-Path $AppDir 'version.json'
    if (Test-Path -LiteralPath $currentVersionFile -PathType Leaf) {
        $currentVersion = Read-Version $currentVersionFile
    }

    $parent = Split-Path -Parent $AppDir
    $leaf = Split-Path -Leaf $AppDir
    $backupName = '{0}-old-{1}-{2}' -f $leaf, $currentVersion, (Get-Date -Format 'yyyyMMdd-HHmmss')
    $backupDir = Join-Path $parent $backupName

    Write-Log "Текущая версия: $currentVersion. Новая версия: $newVersion."

    Rename-Item -LiteralPath $AppDir -NewName $backupName
    $renamed = $true

    try {
        Move-Item -LiteralPath $newDir -Destination $AppDir
    }
    catch {
        if (Test-Path -LiteralPath $AppDir) {
            Remove-Item -LiteralPath $AppDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $backupDir) {
            Rename-Item -LiteralPath $backupDir -NewName $leaf
            $renamed = $false
        }

        throw
    }

    if (-not (Start-App $AppDir)) {
        throw 'Новая версия распакована, но не удалось запустить SweetTaste.exe.'
    }

    Write-Log "Обновление завершено: $newVersion. Старая папка: $backupDir"
    exit 0
}
catch {
    Write-Log ('Ошибка обновления: ' + $_.Exception.Message)

    # Возвращаем старую папку, если замена уже началась.
    if ($renamed -and $backupDir -and (Test-Path -LiteralPath $backupDir)) {
        if (Test-Path -LiteralPath $AppDir) {
            Remove-Item -LiteralPath $AppDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        try {
            Rename-Item -LiteralPath $backupDir -NewName (Split-Path -Leaf $AppDir)
        }
        catch {
            Write-Log ('Не удалось вернуть старую папку: ' + $_.Exception.Message)
        }
    }

    # При ошибке возвращаем пользователя в рабочее приложение.
    if (-not (Start-App $AppDir)) {
        Write-Log 'Старую версию запустить не удалось. Запустите SweetTaste.exe вручную.'
    }

    exit 1
}
finally {
    if ($work -and (Test-Path -LiteralPath $work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Удаляем только временную копию скрипта, а не исходник в папке проекта.
    if ($PSCommandPath -and
        $PSCommandPath.StartsWith($env:TEMP, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
    }
}
