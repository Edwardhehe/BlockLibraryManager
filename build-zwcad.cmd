@echo off
setlocal

REM ============================================================
REM  构建【中望 CAD ZWCAD】版本的插件 DLL
REM
REM  前置条件：本机必须装有中望 CAD（或至少有 ZRX SDK 的
REM            ZwDatabaseMgd.dll / ZwManaged.dll），目录用参数传入。
REM
REM  用法：
REM    build-zwcad.cmd "C:\Program Files\ZWSOFT\ZWCAD 2021"
REM    build-zwcad.cmd "D:\ZRXSDK\inc"
REM
REM  产物：dist\ZWCAD\BlockLibraryManager.dll
REM  加载：在中望 CAD 命令行输入 NETLOAD，选择该 DLL，
REM        然后输入 BLOCKLIB 打开图库面板。
REM ============================================================

set "ZWDIR=%~1"

if "%ZWDIR%"=="" (
    REM 常见安装位置，逐个探测
    for %%D in (
        "C:\Program Files\ZWSOFT\ZWCAD 2026"
        "C:\Program Files\ZWSOFT\ZWCAD 2025"
        "C:\Program Files\ZWSOFT\ZWCAD 2024"
        "C:\Program Files\ZWSOFT\ZWCAD 2023"
        "C:\Program Files\ZWSOFT\ZWCAD 2022"
        "C:\Program Files\ZWSOFT\ZWCAD 2021"
        "C:\Program Files\ZWSOFT\ZWCAD 2020"
    ) do (
        if exist "%%~D\ZwDatabaseMgd.dll" set "ZWDIR=%%~D"
    )
)

REM 部分版本把 acmgd 对应的内容单列成 ZwManagedUx.dll，存在就自动追加引用
set "EXTRA="
if exist "%ZWDIR%\ZwManagedUx.dll" set "EXTRA=-p:ExtraCadRefItems=ZwManagedUx"

if "%ZWDIR%"=="" (
    echo.
    echo [错误] 没有自动找到中望 CAD。本脚本不会在缺少官方程序集的情况下
    echo        编译 —— 那样产出的 DLL 拿到中望上会报
    echo        "未能加载文件或程序集 ZwManagedUx ..."（就是这种无效产物）。
    echo.
    echo 请带路径运行：build-zwcad.cmd "中望CAD的安装目录"
    echo 该目录里需要有 ZwDatabaseMgd.dll 与 ZwManaged.dll。
    echo 常见位置：C:\Program Files\ZWSOFT\ZWCAD 20xx
    exit /b 1
)

echo.
echo 目标平台 : 中望 CAD ^(ZwSoft.ZwCAD.*^)
echo 引用目录 : %ZWDIR%
if not "%EXTRA%"=="" echo 附加引用 : %EXTRA%
echo.

dotnet build "%~dp0build\ZWCAD\BlockLibraryManager.csproj" ^
    -c Release ^
    -p:CadRefDir="%ZWDIR%" %EXTRA%

if errorlevel 1 (
    echo.
    echo [失败] 请检查上面的错误信息。常见原因：
    echo  1. 未安装 .NET SDK 6.0 或更高版本
    echo  2. 目录里没有 ZwDatabaseMgd.dll / ZwManaged.dll
    echo  3. 中望版本较老时 ApplicationServices 可能放在别的 DLL 里，
    echo     可用 -p:ExtraCadRefItems=文件名 追加引用
    exit /b 1
)

echo.
echo [完成] 输出：%~dp0dist\ZWCAD\BlockLibraryManager.dll
echo        在中望 CAD 里 NETLOAD 这个 DLL，然后输入 BLOCKLIB。
echo.
endlocal
