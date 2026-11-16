@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
rem ============================================================
rem  WolfSuite build - compiles with the .NET Framework 4.8
rem  C# compiler shipped with Windows. No SDK, no NuGet needed.
rem
rem  Output layout:
rem    dist\WolfServer\   - tool for the server admin
rem    dist\WolfLauncher\ - launcher handed out to players
rem    bin\               - test executables only
rem ============================================================

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [!] csc.exe not found - .NET Framework 4.8 is required.
    if /i not "%~1"=="nopause" pause
    exit /b 1
)

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "TESTS=%ROOT%tests"
set "SERVEROUT=%ROOT%dist\WolfServer"
set "CLIENTOUT=%ROOT%dist\WolfLauncher"
set "TESTOUT=%ROOT%bin"

if not exist "%SERVEROUT%" mkdir "%SERVEROUT%"
if not exist "%CLIENTOUT%" mkdir "%CLIENTOUT%"
if not exist "%TESTOUT%" mkdir "%TESTOUT%"

set "REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Management.dll"
rem The icon must carry frames for every shell size: a single 64x64 frame makes
rem Explorer show a generic blank page instead of the wolf (large icons ask for 256).
set "ICON=%ROOT%assets\wolf_multi.ico"

rem Every path is quoted: the project may live in a folder with spaces
rem ("C:\Server 1\..."), and csc splits unquoted arguments at spaces.
set "COMMON="%SRC%\Common\Json.cs" "%SRC%\Common\GamePaths.cs" "%SRC%\Common\ChunkStore.cs" "%SRC%\Common\ModManifest.cs" "%SRC%\Common\ModFilters.cs" "%SRC%\Common\HashCache.cs" "%SRC%\Common\LauncherUpdate.cs" "%SRC%\Common\Net.cs" "%SRC%\Common\P2P.cs" "%SRC%\Common\Theme.cs" "%SRC%\Common\Proc.cs" "%SRC%\Common\ServerKit.cs" "%SRC%\Common\ServerConfig.cs" "%SRC%\Common\Backup.cs" "%SRC%\Common\Scheduler.cs" "%SRC%\Common\WolfInfo.cs" "%SRC%\Common\ManifestDiff.cs" "%SRC%\Common\PackHistory.cs" "%SRC%\Common\PlayerStats.cs" "%SRC%\Common\Notify.cs" "%SRC%\Common\TrayIcon.cs" "%SRC%\Common\NeonButton.cs" "%SRC%\Common\ImageButton.cs" "%SRC%\Common\ShellNotify.cs""

echo.
echo [*] dist\WolfServer\WolfServer.exe  - server admin tool
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ %REFS% ^
    /win32icon:"%ICON%" ^
    /out:"%SERVEROUT%\WolfServer.exe" ^
    %COMMON% ^
    "%SRC%\Server\*.cs"
if errorlevel 1 (
    echo [!] WolfServer build FAILED
    if /i not "%~1"=="nopause" pause
    exit /b 1
)

echo [*] dist\WolfLauncher\WolfLauncher.exe - client launcher
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ %REFS% ^
    /win32icon:"%ICON%" ^
    /resource:"%ROOT%assets\banner.jpg",WolfLauncher.Banner.jpg ^
    /resource:"%ROOT%assets\blood.png",WolfLauncher.Blood.png ^
    /out:"%CLIENTOUT%\WolfLauncher.exe" ^
    %COMMON% ^
    "%SRC%\Launcher\*.cs"
if errorlevel 1 (
    echo [!] WolfLauncher build FAILED
    if /i not "%~1"=="nopause" pause
    exit /b 1
)

echo [*] tests
"%CSC%" /nologo /target:exe /platform:x64 /out:"%TESTOUT%\SelfTest.exe" %REFS% %COMMON% "%TESTS%\TestPaths.cs" "%TESTS%\SelfTest.cs"
if errorlevel 1 (
    echo [!] SelfTest build FAILED
    if /i not "%~1"=="nopause" pause
    exit /b 1
)
"%CSC%" /nologo /target:exe /platform:x64 /out:"%TESTOUT%\NetTest.exe" %REFS% %COMMON% "%TESTS%\NetTest.cs"
if errorlevel 1 (
    echo [!] NetTest build FAILED
    if /i not "%~1"=="nopause" pause
    exit /b 1
)
"%CSC%" /nologo /target:exe /platform:x64 /out:"%TESTOUT%\BackupTest.exe" %REFS% %COMMON% "%TESTS%\BackupTest.cs"
if errorlevel 1 (
    echo [!] BackupTest build FAILED
    if /i not "%~1"=="nopause" pause
    exit /b 1
)

echo.
echo [OK] dist\WolfServer\WolfServer.exe
echo [OK] dist\WolfLauncher\WolfLauncher.exe
echo [OK] bin\SelfTest.exe, bin\NetTest.exe, bin\BackupTest.exe
if /i "%~1"=="nopause" exit /b 0
pause
