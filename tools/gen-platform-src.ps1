# ---------------------------------------------------------------------------
# gen-platform-src.ps1
#   把 src/ 下的公共源码转换成品台专用源码（中望 CAD / 浩辰 CAD）。
#
#   转换内容：
#     1. 命名空间映射
#          中望 CAD / 浩辰 CAD：单根替换
#              Autodesk.AutoCAD.*  ->  ZwSoft.ZwCAD.*   (中望)
#                                  ->  Gssoft.Gscad.*   (浩辰)
#          BricsCAD：**双根**替换 —— 它的 API 分散在 Bricscad.* 与 Teigha.* 两个根下
#              Autodesk.AutoCAD.ApplicationServices -> Bricscad.ApplicationServices
#              Autodesk.AutoCAD.EditorInput         -> Bricscad.EditorInput
#              Autodesk.AutoCAD.Windows             -> Bricscad.Windows
#              Autodesk.AutoCAD.Runtime             -> Teigha.Runtime
#              Autodesk.AutoCAD.DatabaseServices    -> Teigha.DatabaseServices
#              Autodesk.AutoCAD.Geometry            -> Teigha.Geometry
#              Autodesk.AutoCAD.Colors              -> Teigha.Colors
#              其余未列出的走 -NamespaceRoot 兜底
#     2. 平台专属小修补（PATCH 数组，按平台启用）
#
#   用法：
#     powershell -NoProfile -ExecutionPolicy Bypass -File gen-platform-src.ps1 `
#         -SrcDir "..\src" -OutDir "..\build\ZWCAD\gen" `
#         -NamespaceRoot "ZwSoft.ZwCAD" -Platform "ZWCAD"
#
#   幂等：源没变化时会跳过（用 .genstamp 记录），不会每次编译都重抄一遍。
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$SrcDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$NamespaceRoot,
    [Parameter(Mandatory = $true)][string]$Platform,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Fail($msg) { Write-Host "gen-platform-src: $msg" -ForegroundColor Red; exit 1 }

if (!(Test-Path $SrcDir)) { Fail "源码目录不存在：$SrcDir" }

# ---- 命名空间映射表 ---------------------------------------------------------
# 每条规则 = @{ From = 旧前缀; To = 新前缀 }，只在对应平台生效，
# 在通用的 "Autodesk.AutoCAD -> $NamespaceRoot" 替换**之前**逐条执行。
#
# 注意：不能写成 @( @("a","b") ) —— PowerShell 会把嵌套数组展平成一维，
#       循环里再取 $p[0] / $p[1] 就变成取字符串的单个字符，
#       结果是按字符做替换，整份源码会被改坏（已踩过一次）。
#
# 中望 / 浩辰是 ObjectARX 单根移植，不需要逐条映射，留空即可。
# BricsCAD 不一样：它的托管 API 来自 ODA 的 Teigha（DWG 内核侧，BrxMgd.dll
# 之外单独一个 TD_Mgd.dll），命名空间被拆成了两个根：
#   Bricscad.ApplicationServices / EditorInput / Windows / PlottingServices
#   Teigha.DatabaseServices / Geometry / Colors / Runtime / GraphicsInterface
$NsMapBricsCAD = @(
    @{ From = "Autodesk.AutoCAD.ApplicationServices"; To = "Bricscad.ApplicationServices" }
    @{ From = "Autodesk.AutoCAD.EditorInput";         To = "Bricscad.EditorInput" }
    @{ From = "Autodesk.AutoCAD.Windows";             To = "Bricscad.Windows" }
    @{ From = "Autodesk.AutoCAD.PlottingServices";    To = "Bricscad.PlottingServices" }
    @{ From = "Autodesk.AutoCAD.GraphicsSystem";      To = "Bricscad.GraphicsSystem" }
    @{ From = "Autodesk.AutoCAD.Runtime";             To = "Teigha.Runtime" }
    @{ From = "Autodesk.AutoCAD.DatabaseServices";    To = "Teigha.DatabaseServices" }
    @{ From = "Autodesk.AutoCAD.Geometry";            To = "Teigha.Geometry" }
    @{ From = "Autodesk.AutoCAD.Colors";              To = "Teigha.Colors" }
    @{ From = "Autodesk.AutoCAD.GraphicsInterface";   To = "Teigha.GraphicsInterface" }
    @{ From = "Autodesk.AutoCAD.LayerManager";        To = "Teigha.LayerManager" }
)
$NsMapZWCAD    = @()
$NsMapGstarCAD = @()

# ---- 平台专属微调 -----------------------------------------------------------
# 说明：浩辰官方迁移指南要求 ResultBuffer.UnmanagedObject 换成 ResbufObject、
#       ResultBuffer(IntPtr,bool) 换成 ResultBuffer.Create(...)。本项目目前没有用到
#       ResultBuffer，保留规则是为了将来新增代码时能自动修正。
$PatchGstarCAD = @(
    @{ From = "ResultBuffer.UnmanagedObject"; To = "ResultBuffer.ResbufObject" }
)
$PatchZWCAD = @()
$PatchBricsCAD = @()

$nsMap   = @()
$patches = @()
switch ($Platform) {
    "ZWCAD"    { $nsMap = $NsMapZWCAD;    $patches = $PatchZWCAD }
    "GstarCAD" { $nsMap = $NsMapGstarCAD; $patches = $PatchGstarCAD }
    "BricsCAD" { $nsMap = $NsMapBricsCAD; $patches = $PatchBricsCAD }
    default    { Fail "未知平台：$Platform（只支持 ZWCAD / GstarCAD / BricsCAD）" }
}

# ---- 增量判断 ---------------------------------------------------------------
$stampFile = Join-Path $OutDir ".genstamp"
$srcFiles  = Get-ChildItem -Path $SrcDir -Filter *.cs -Recurse -File |
             Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$stampContent = "ns=$NamespaceRoot`nplatform=$Platform`n"
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring((Resolve-Path $SrcDir).Path.Length).TrimStart('\')
    $stampContent += "$rel|$($f.Length)|$($f.LastWriteTimeUtc.Ticks)`n"
}

if (!$Force -and (Test-Path $stampFile)) {
    $old = Get-Content -Path $stampFile -Raw -ErrorAction SilentlyContinue
    if ($old -eq $stampContent) {
        Write-Host "gen-platform-src: [$Platform] 源码无变化，跳过生成"
        exit 0
    }
}

# ---- 生成 -------------------------------------------------------------------
if (Test-Path $OutDir) {
    # 只清理 .cs / .genstamp，避免误删其他东西
    Get-ChildItem -Path $OutDir -Filter *.cs -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force }
}
else {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$count = 0
$hit   = 0
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring((Resolve-Path $SrcDir).Path.Length).TrimStart('\')
    $dst = Join-Path $OutDir $rel
    $dstDir = Split-Path $dst -Parent
    if (!(Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }

    # 统一按 UTF-8 读取（源码里有大量中文注释 / 字符串）
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)

    if ($text -match 'Autodesk\.AutoCAD') { $hit++ }

    # 1) 逐条命名空间映射（BricsCAD 的双根就靠这一步）
    foreach ($m in $nsMap) {
        $text = $text -replace [regex]::Escape($m.From), $m.To
    }

    # 2) 兜底：剩下的 Autodesk.AutoCAD.* 整体换成平台命名空间根
    $text = $text -replace 'Autodesk\.AutoCAD', $NamespaceRoot

    foreach ($p in $patches) {
        $old = $p.From
        $new = $p.To
        if ($text -like "*$old*") {
            $text = $text -replace [regex]::Escape($old), $new
        }
    }

    # 写回：带 BOM 的 UTF-8（Roslyn 已通过 csproj 的 CodePage=65001 处理，
    # 但带 BOM 更保险，尤其在没有 .editorconfig 的环境下）
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($dst, $text, $utf8Bom)

    $count++
}

[System.IO.File]::WriteAllText($stampFile, $stampContent, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "gen-platform-src: [$Platform] 已生成 $count 个文件 -> $OutDir （其中 $hit 个含 CAD 命名空间，已替换为 $NamespaceRoot.*）"
exit 0
