@echo off
@chcp 65001 >nul
setlocal
if "%~1"=="" goto usage
if not exist "%~f1" goto nofile

copy /y "%~f0" "%TEMP%\sweet-taste-update.cmd" >nul
if exist "%TEMP%\sweet-taste-update.ps1" del /q "%TEMP%\sweet-taste-update.ps1" >nul 2>&1
copy /y "%~dp0Update.ps1" "%TEMP%\sweet-taste-update.ps1" >nul
if not exist "%TEMP%\sweet-taste-update.ps1" (
    echo Не найден Update.ps1 рядом с этим файлом.
    if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\sweet-taste-update.ps1" -AppDir "%~dp0.." -ZipPath "%~f1"
if errorlevel 1 (
    echo.
    echo Обновление не завершено. Старая версия на месте.
    if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
    exit /b 1
)

echo.
if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
exit /b 0

:usage
echo Перетащите на этот файл ZIP с новой версией Sweet Taste
echo или запустите: tools\Update.cmd "C:\путь\SweetTaste-0.1.0-alpha.2-win-x64.zip"
if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
exit /b 1

:nofile
echo Файл не найден: %~1
if not "%SWEET_TASTE_NO_PAUSE%"=="1" pause
exit /b 1
