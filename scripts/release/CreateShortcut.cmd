@echo off
@chcp 65001 >nul
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$appDir = [System.IO.Path]::GetFullPath('%~dp0..'); $target = Join-Path $appDir 'SweetTaste.exe'; if (-not (Test-Path -LiteralPath $target)) { Write-Host 'SweetTaste.exe не найден в папке программы.' -ForegroundColor Red; exit 1 }; $shell = New-Object -ComObject WScript.Shell; $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Sweet Taste.lnk')); $link.TargetPath = $target; $link.WorkingDirectory = $appDir; $link.IconLocation = $target + ',0'; $link.Description = 'Sweet Taste — приложение для кондитера'; $link.Save(); Write-Host 'Ярлык «Sweet Taste» создан на рабочем столе.' -ForegroundColor Green"

if errorlevel 1 (
    echo.
    echo Не удалось создать ярлык.
    if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
    exit /b 1
)

echo.
if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
exit /b 0
