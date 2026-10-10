@echo off
setlocal

REM ============================================================
REM  一次性构建全部平台：
REM    AutoCAD 6 个版本组 + 中望 CAD + 浩辰 CAD
REM
REM  脚本按顺序探测常见安装目录：找到就构建，找不到就跳过。
REM  装在别的盘的，直接改下面的 ZWCAD / GSTARCAD 变量，
REM  或者直接运行 build-zwcad.cmd / build-gstarcad.cmd 指定路径。
REM ============================================================

set "PF=C:\Program Files\Autodesk"
set "ACAD2011=%PF%\AutoCAD 2011"
set "ACAD2012=%PF%\AutoCAD 2012"
set "ACAD2014=%PF%\AutoCAD 2014"
set "ACAD2018=%PF%\AutoCAD 2018"
set "ACAD2024=%PF%\AutoCAD 2024"
set "ACAD2026=%PF%\AutoCAD 2026"

set "ZWCAD=C:\Program Files\ZWSOFT\ZWCAD 2021"
set "GSTARCAD=C:\Program Files\Gstarsoft\GstarCAD 2024"

call :BuildAcad CAD2010-2011 "%ACAD2011%"
call :BuildAcad CAD2012      "%ACAD2012%"
call :BuildAcad CAD2013-2014 "%ACAD2014%"
call :BuildAcad CAD2015-2018 "%ACAD2018%"
call :BuildAcad CAD2019-2024 "%ACAD2024%"
call :BuildAcad CAD2025-2027 "%ACAD2026%"
call :BuildZw   ZWCAD        "%ZWCAD%"
call :BuildGc   GstarCAD     "%GSTARCAD%"

echo.
echo 全部构建流程结束。产物在 dist\ 下按平台分目录存放。
echo.
pause
exit /b 0

:BuildAcad
set "V=%~1"
set "D=%~2"
if not exist "%D%\acdbmgd.dll" (
    echo [跳过] %V% —— 未找到 %D%
    exit /b 0
)
echo.
echo ============ 正在构建 %V% ============
dotnet build "%~dp0build\%V%\BlockLibraryManager.csproj" -c Release -p:AcadRefDir="%D%"
if errorlevel 1 (echo [失败] %V%) else (echo [成功] %V% -^> dist\%V%)
exit /b 0

:BuildZw
set "V=%~1"
set "D=%~2"
if not exist "%D%\ZwDatabaseMgd.dll" (
    echo.
    echo [跳过] 中望 CAD —— 未找到 ZwDatabaseMgd.dll（%D%）
    echo         装了中望 CAD 请运行：build-zwcad.cmd "安装目录"
    exit /b 0
)
echo.
echo ============ 正在构建 中望 CAD (ZWCAD) ============
dotnet build "%~dp0build\%V%\BlockLibraryManager.csproj" -c Release -p:CadRefDir="%D%"
if errorlevel 1 (echo [失败] %V%) else (echo [成功] %V% -^> dist\%V%)
exit /b 0

:BuildGc
set "V=%~1"
set "D=%~2"
if not exist "%D%\GcDbMgd.dll" (
    echo.
    echo [跳过] 浩辰 CAD —— 未找到 GcDbMgd.dll（%D%）
    echo         装了浩辰 CAD 请运行：build-gstarcad.cmd "安装目录"
    exit /b 0
)
echo.
echo ============ 正在构建 浩辰 CAD (GstarCAD) ============
dotnet build "%~dp0build\%V%\BlockLibraryManager.csproj" -c Release -p:CadRefDir="%D%"
if errorlevel 1 (echo [失败] %V%) else (echo [成功] %V% -^> dist\%V%)
exit /b 0
