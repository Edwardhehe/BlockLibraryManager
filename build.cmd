@echo off
setlocal

REM ============================================================
REM  构建某一个 AutoCAD 版本对应的插件 DLL
REM
REM  用法：
REM    build.cmd CAD2019-2024 "C:\Program Files\Autodesk\AutoCAD 2024"
REM    build.cmd CAD2025-2027 "C:\Program Files\Autodesk\AutoCAD 2026"
REM
REM  不传参数时默认构建 CAD2019-2024 + AutoCAD 2024。
REM  可选的目录名：CAD2010-2011 / CAD2012 / CAD2013-2014 /
REM                CAD2015-2018 / CAD2019-2024 / CAD2025-2027
REM ============================================================

set "VERSION=%~1"
set "ACADDIR=%~2"

if "%VERSION%"=="" set "VERSION=CAD2019-2024"
if "%ACADDIR%"=="" set "ACADDIR=C:\Program Files\Autodesk\AutoCAD 2024"

echo.
echo 版本组   : %VERSION%
echo 引用目录 : %ACADDIR%
echo.

dotnet build "%~dp0build\%VERSION%\BlockLibraryManager.csproj" ^
    -c Release ^
    -p:AcadRefDir="%ACADDIR%"

if errorlevel 1 (
    echo.
    echo [失败] 请检查上面的错误信息。
    echo  1. 是否安装了 .NET SDK 6.0 或更高版本
    echo  2. AcadRefDir 里是否真的存在 acdbmgd.dll / acmgd.dll
    echo  3. AutoCAD 2025+ 需要 .NET 8 SDK
    exit /b 1
)

echo.
echo [完成] 输出目录：%~dp0dist\%VERSION%\
echo.
endlocal
