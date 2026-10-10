@echo off
setlocal

REM ============================================================
REM  构建【浩辰 CAD GstarCAD】版本的插件 DLL
REM
REM  前置条件：本机必须装有浩辰 CAD（或 GRX SDK），目录里需要有
REM            GcCoreMgd.dll / GcDbMgd.dll / GcMgd.dll。
REM
REM  用法：
REM    build-gstarcad.cmd "C:\Program Files\Gstarsoft\GstarCAD 2024"
REM    build-gstarcad.cmd "C:\grxsdk\arx\inc"
REM
REM  产物：dist\GstarCAD\BlockLibraryManager.dll
REM  加载：NETLOAD 该 DLL，然后输入 BLOCKLIB。
REM ============================================================

set "GCDIR=%~1"

if "%GCDIR%"=="" (
    for %%D in (
        "C:\Program Files\Gstarsoft\GstarCAD 2027"
        "C:\Program Files\Gstarsoft\GstarCAD 2026"
        "C:\Program Files\Gstarsoft\GstarCAD 2025"
        "C:\Program Files\Gstarsoft\GstarCAD 2024"
        "C:\Program Files\Gstarsoft\GstarCAD 2023"
        "C:\Program Files\Gstarsoft\GstarCAD 2022"
        "C:\Program Files\Gstarsoft\GstarCAD 2021"
    ) do (
        if exist "%%~D\GcDbMgd.dll" set "GCDIR=%%~D"
    )
)

if "%GCDIR%"=="" (
    echo.
    echo [错误] 没有自动找到浩辰 CAD。本脚本不会在缺少官方程序集的情况下
    echo        编译 —— 那样产出的 DLL 拿到浩辰上会报
    echo        "未能加载文件或程序集 GcMgd ..."（无效产物）。
    echo.
    echo 请带路径运行：build-gstarcad.cmd "浩辰CAD的安装目录"
    echo 该目录里需要有 GcCoreMgd.dll / GcDbMgd.dll / GcMgd.dll。
    echo （GRX SDK 的话这 3 个文件在 arx\inc 下）
    exit /b 1
)

echo.
echo 目标平台 : 浩辰 CAD ^(Gssoft.Gscad.*^)
echo 引用目录 : %GCDIR%
echo.

dotnet build "%~dp0build\GstarCAD\BlockLibraryManager.csproj" ^
    -c Release ^
    -p:CadRefDir="%GCDIR%"

if errorlevel 1 (
    echo.
    echo [失败] 请检查上面的错误信息。常见原因：
    echo  1. 未安装 .NET SDK 6.0 或更高版本
    echo  2. 目录里没有 GcCoreMgd.dll / GcDbMgd.dll / GcMgd.dll
    echo  3. 浩辰 2024 的官方要求是 .NET Framework 4.8
    exit /b 1
)

echo.
echo [完成] 输出：%~dp0dist\GstarCAD\BlockLibraryManager.dll
echo        在浩辰 CAD 里 NETLOAD 这个 DLL，然后输入 BLOCKLIB。
echo.
endlocal
