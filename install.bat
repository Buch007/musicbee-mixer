@echo off
setlocal enabledelayedexpansion
title Mixer for MusicBee - Installer

echo ========================================================
echo Mixer for MusicBee - 1-Click Installer
echo ========================================================
echo.

:: 1. Locate MusicBee Plugins folder
set "TARGET_DIR=%APPDATA%\MusicBee\Plugins"
if not exist "%TARGET_DIR%" (
    echo Creating MusicBee plugins folder: %TARGET_DIR%
    mkdir "%TARGET_DIR%" 2>nul
)

:: 2. Install MB_Mixer.dll
set "SOURCE_DLL="
if exist "bin\Release\net472\MB_Mixer.dll" (
    set "SOURCE_DLL=bin\Release\net472\MB_Mixer.dll"
) else if exist "MB_Mixer.dll" (
    set "SOURCE_DLL=MB_Mixer.dll"
)

if not "%SOURCE_DLL%"=="" (
    echo Installing MB_Mixer.dll to %TARGET_DIR%...
    copy /y "%SOURCE_DLL%" "%TARGET_DIR%\MB_Mixer.dll" >nul
    if %errorlevel% equ 0 (
        echo MB_Mixer.dll installed successfully.
    ) else (
        echo Warning: Could not overwrite MB_Mixer.dll. Please close MusicBee if it is running.
    )
) else (
    echo Note: MB_Mixer.dll was not found in the current folder.
    echo Please make sure MB_Mixer.dll is downloaded from GitHub Releases.
)
echo.

:: 3. Setup persistent Mixer folder
set "MIXER_DIR=%APPDATA%\MusicBee\Mixer"
if not exist "%MIXER_DIR%" mkdir "%MIXER_DIR%" 2>nul

:: 4. Check for standalone scanner or Python
set "SCANNER_FOUND=0"
if exist "dist\mixer_scanner.exe" (
    echo Copying standalone scanner to %MIXER_DIR%...
    copy /y "dist\mixer_scanner.exe" "%MIXER_DIR%\mixer_scanner.exe" >nul
    set "SCANNER_FOUND=1"
) else if exist "mixer_scanner.exe" (
    echo Copying standalone scanner to %MIXER_DIR%...
    copy /y "mixer_scanner.exe" "%MIXER_DIR%\mixer_scanner.exe" >nul
    set "SCANNER_FOUND=1"
) else if exist "%MIXER_DIR%\mixer_scanner.exe" (
    set "SCANNER_FOUND=1"
)

if "%SCANNER_FOUND%"=="1" (
    echo Standalone scanner is ready. Python is not required.
) else (
    echo Checking for Python environment...
    py --version >nul 2>&1
    if %errorlevel% equ 0 (
        set "PY_CMD=py"
    ) else (
        python --version >nul 2>&1
        if %errorlevel% equ 0 (
            set "PY_CMD=python"
        ) else (
            set "PY_CMD="
        )
    )

    if not "!PY_CMD!"=="" (
        echo Python detected: !PY_CMD!
        echo Installing audio dependencies (librosa, soundfile, numpy)...
        !PY_CMD! -m pip install --upgrade librosa soundfile numpy
        if exist "analyze_library.py" (
            copy /y "analyze_library.py" "%MIXER_DIR%\analyze_library.py" >nul
        )
    ) else (
        echo Python was not detected on this system.
        echo Checking for Windows Package Manager (winget)...
        where winget >nul 2>&1
        if %errorlevel% equ 0 (
            echo Installing Python 3.11 silently via winget...
            winget install Python.Python.3.11 --silent --accept-package-agreements --accept-source-agreements
            echo Installing audio analysis dependencies...
            py -m pip install librosa soundfile numpy
            if exist "analyze_library.py" (
                copy /y "analyze_library.py" "%MIXER_DIR%\analyze_library.py" >nul
            )
        ) else (
            echo Note: Neither Python nor standalone scanner is installed yet.
            echo Mixer will automatically prompt to download the standalone scanner inside MusicBee.
        )
    )
)

echo.
echo ========================================================
echo Installation completed.
echo Next steps:
echo 1. Start or restart MusicBee.
echo 2. Enable Mixer under Edit ^> Preferences ^> Plugins.
echo 3. Access Mixer from the top bar or via Tools ^> Mixer.
echo ========================================================
echo.
pause
