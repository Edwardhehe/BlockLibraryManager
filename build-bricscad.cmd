@echo off
chcp 65001 >nul
setlocal ENABLEDELAYEDEXPANSION

REM ============================================================
REM  构建【BricsCAD】版本的插件 DLL
REM
REM  与中望 / 浩辰不同，BricsCAD 的官方 .NET API 以 NuGet 包
REM  （BricsCAD.27.NET / BricsCAD.26.NET）公开发布。本仓库不附带这些 DLL。
REM  请自行解压到 tools\bcapi\，或直接把参数指到 BricsCAD 安装目录。
REM
REM  用法：
REM    build-bricscad.cmd                 默认：tools\bcapi\v27（BricsCAD V27, net8.0）
REM    build-bricscad.cmd V26             BricsCAD V26（net8.0）
REM    build-bricscad.cmd V25 "C:\Program Files\Bricsys\BricsCAD V25"
REM                                       指定安装目录；V25 及更早是
REM                                       .NET Framework 4.8 宿主，自动切 net48
REM
REM  产物：dist\BricsCAD\BlockLibraryManager.dll
REM  加载：BricsCAD 命令行输入 NETLOAD，选择该 DLL，然后输入 BLOCKLIB。
REM ============================================================

set "TAG=%~1"
set "REFDIR=%~2"
set "TFM=net8.0-windows"

REM ---- 版本标记 -> 仓库自带的官方参考程序集目录 ----
if /I "%TAG%"=="V27" set "REFDIR=%~dp0tools\bcapi\v27\ref\net8.0"
if /I "%TAG%"=="V26" set "REFDIR=%~dp0tools\bcapi\v26\ref\net8.0"
if /I "%TAG%"=="V25" set "TFM=net48"
if /I "%TAG%"=="V24" set "TFM=net48"
if /I "%TAG%"=="V23" set "TFM=net48"

REM ---- 没给目录就用 V27 的官方参考程序集。
REM      注意：net48（V25 及更早）不能用 net8.0 的参考程序集，
REM      那种情况只能靠本机安装的 BricsCAD，所以这里只对 net8 生效。 ----
if "%REFDIR%"=="" if "%TFM%"=="net8.0-windows" set "REFDIR=%~dp0tools\bcapi\v27\ref\net8.0"

REM ---- 仓库自带的也找不到，就探测本机安装的 BricsCAD ----
if not exist "%REFDIR%\BrxMgd.dll" (
    for %%D in (
        "C:\Program Files\Bricsys\BricsCAD V27 en_US"
        "C:\Program Files\Bricsys\BricsCAD V27"
        "C:\Program Files\Bricsys\BricsCAD V26 en_US"
        "C:\Program Files\Bricsys\BricsCAD V26"
        "C:\Program Files\Bricsys\BricsCAD V25 en_US"
        "C:\Program Files\Bricsys\BricsCAD V25"
    ) do (
        if exist "%%~D\BrxMgd.dll" set "REFDIR=%%~D"
    )
)

if not exist "%REFDIR%\BrxMgd.dll" (
    echo.
    echo [ERROR] BrxMgd.dll not found in: %REFDIR%
    echo         请在装有 BricsCAD 的机器上运行，或指定目录：
    echo         build-bricscad.cmd V27 "BricsCAD 安装目录"
    exit /b 1
)

REM ---- 定位 dotnet ----
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet not found. 请安装 .NET 8 SDK 或把它加进 PATH。
    exit /b 1
)

echo.
echo Platform   : BricsCAD
echo Framework  : %TFM%
echo RefDir     : %REFDIR%
echo Dotnet     : %DOTNET%
echo.

"%DOTNET%" build "%~dp0build\BricsCAD\BlockLibraryManager.csproj" -c Release -p:TargetFramework=%TFM% -p:CadRefDir="%REFDIR%"

if errorlevel 1 (
    echo.
    echo [ERROR] build failed.
    exit /b 1
)

echo.
echo [OK] dist\BricsCAD\BlockLibraryManager.dll
echo      在 BricsCAD 里 NETLOAD 这个 DLL，然后输入 BLOCKLIB。
echo.
endlocal
