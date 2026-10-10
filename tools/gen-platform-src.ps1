# ---------------------------------------------------------------------------
# gen-platform-src.ps1
#   把 src/ 下的公共源码转换成品台专用源码（中望 CAD / 浩辰 CAD）。
#
#   转换内容：
#     1. 命名空间根替换   Autodesk.AutoCAD.*  ->  ZwSoft.ZwCAD.*   (中望)
#                                            ->  Gssoft.Gscad.*   (浩辰)
#     2. 平台专属小修补（PATCH 数组，按平台启用）
#
#   用法：
#     powershell -NoProfile -ExecutionPolicy Bypass -File gen-platform-src.ps1 `
#         -SrcDir "..\src" -OutDir "..\build\ZWCAD-2024\gen" `
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

# ---- 平台专属微调 -----------------------------------------------------------
# 每条规则 = @{ From = 旧文本; To = 新文本 }，只在对应平台生效。
# 注意：不能写成 @( @("a","b") ) —— PowerShell 会把嵌套数组展平成一维，
#       循环里再取 $p[0] / $p[1] 就变成取字符串的单个字符，
#       结果是按字符做替换，整份源码会被改坏。
# 说明：浩辰官方迁移指南要求 ResultBuffer.UnmanagedObject 换成 ResbufObject、
#       ResultBuffer(IntPtr,bool) 换成 ResultBuffer.Create(...)。本项目目前没有用到
#       ResultBuffer，保留规则是为了将来新增代码时能自动修正。
$PatchGstarCAD = @(
    @{ From = "ResultBuffer.UnmanagedObject"; To = "ResultBuffer.ResbufObject" }
)
$PatchZWCAD = @()

$patches = @()
switch ($Platform) {
    "ZWCAD"    { $patches = $PatchZWCAD }
    "GstarCAD" { $patches = $PatchGstarCAD }
    default    { Fail "未知平台：$Platform（只支持 ZWCAD / GstarCAD）" }
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
