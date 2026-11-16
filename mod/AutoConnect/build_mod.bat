@echo off
rem ============================================================
rem  Сборка клиентского мода автоподключения (Mods\ZZZ_WolfAutoConnect).
rem  Нужны сборки игры — компилируем против них, поэтому путь к ним
rem  берётся от папки проекта: ..\..\7DaysToDieServer_Data\Managed
rem  (клиентская установка игры тоже подходит, если указать её путь в GAME).
rem ============================================================
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "GAME=%~dp0..\..\7DaysToDieServer_Data\Managed"

if not exist "%GAME%\Assembly-CSharp.dll" (
    echo [!] не найдены сборки игры: %GAME%
    echo     укажите путь в переменной GAME
    exit /b 1
)

"%CSC%" /nologo /target:library /out:"%~dp0WolfAutoConnect.dll" ^
    /reference:"%GAME%\Assembly-CSharp.dll" ^
    /reference:"%GAME%\UnityEngine.CoreModule.dll" ^
    /reference:"%GAME%\netstandard.dll" ^
    /reference:System.Xml.dll ^
    "%~dp0WolfAutoConnect.cs"

if errorlevel 1 (
    echo [!] сборка мода FAILED
    exit /b 1
)

echo [OK] %~dp0WolfAutoConnect.dll
echo     скопируйте ModInfo.xml и DLL в Mods\ZZZ_WolfAutoConnect
exit /b 0
