@echo off
chcp 65001 >nul
rem ============================================================
rem  图库管理器 —— 一键更新（请先关闭 AutoCAD）
rem
rem  用法：双击运行，或在命令行执行  update.cmd
rem  作用：把最新编译好的 DLL 覆盖到 AutoCAD 自动加载目录
rem ============================================================

set "SRC=%~dp0dist\CAD2019-2024\BlockLibraryManager.dll"
set "DST=%APPDATA%\Autodesk\ApplicationPlugins\BlockLibraryManager.bundle\Contents\CAD2019-2024\BlockLibraryManager.dll"

if not exist "%SRC%" (
    echo [错误] 找不到编译产物：%SRC%
    echo 请先执行 build.cmd CAD2019-2024 编译。
    pause
    exit /b 1
)

tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if %ERRORLEVEL%==0 (
    echo [提示] 检测到 AutoCAD 正在运行，它已占用旧 DLL。
    echo        请先关闭 AutoCAD，然后重新运行本脚本。
    pause
    exit /b 2
)

copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 (
    echo [错误] 复制失败，目标被占用或路径不存在：
    echo        %DST%
    pause
    exit /b 3
)

echo [完成] 已更新：
echo        %DST%
echo        现在启动 AutoCAD，输入 BLOCKLIB 即可看到新界面。
pause
