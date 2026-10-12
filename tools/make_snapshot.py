# -*- coding: utf-8 -*-
"""生成工作快照（只读归档）。

用法：
    python tools/make_snapshot.py                  # 版本号自动从 src/UI/AboutForm.cs 读取
    python tools/make_snapshot.py 1.46.0           # 手动指定版本
    python tools/make_snapshot.py 1.46.0 2026-10-11

输出：snapshots/<日期>_v<版本>/ ，含 src / build / bundle / dist / scripts
      以及 CHECKSUMS.txt（SHA256 清单，sha256sum -c 可校验）。
SNAPSHOT.md 需要另写，本脚本不会覆盖已存在的文件。
"""
import os, sys, shutil, hashlib, datetime, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

EXCLUDE = {"obj", "bin", "__pycache__", ".git", ".vs", "refstub", "ilretarget", "gen"}
COPY_DIRS = ["src", "build", "bundle", "dist"]
ROOT_FILES = ["README.md", "BlockLibraryManager.sln", "pack.py",
              "build.cmd", "build-all.cmd", "build-zwcad.cmd",
              "build-gstarcad.cmd", "build-bricscad.cmd", "update.cmd"]


def read_version():
    """从 src/UI/AboutForm.cs 里读 AboutInfo.Version。"""
    path = os.path.join(ROOT, "src", "UI", "AboutForm.cs")
    try:
        with open(path, "r", encoding="utf-8-sig") as fp:
            m = re.search(r'Version\s*=\s*"([^"]+)"', fp.read())
        if m:
            return m.group(1)
    except Exception:
        pass
    return "unknown"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_tree(src, dst):
    os.makedirs(dst, exist_ok=True)
    for item in sorted(os.listdir(src)):
        s = os.path.join(src, item)
        d = os.path.join(dst, item)
        if os.path.isdir(s):
            if item in EXCLUDE:
                continue
            copy_tree(s, d)
        else:
            shutil.copy2(s, d)


def main():
    version = sys.argv[1] if len(sys.argv) > 1 else read_version()
    date = sys.argv[2] if len(sys.argv) > 2 else datetime.datetime.now().strftime("%Y-%m-%d")

    tag = "%s_v%s" % (date, version)
    dest = os.path.join(ROOT, "snapshots", tag)

    # 注意：不要对整个 dest 做 rmtree —— SNAPSHOT.md 是手写在快照根目录的，
    # 重建会被一起删掉（已踩过两次）。只清掉本脚本负责复制的那几项。
    KEEP_FILES = {"SNAPSHOT.md"}
    if os.path.exists(dest):
        for item in sorted(os.listdir(dest)):
            if item in KEEP_FILES:
                continue
            p = os.path.join(dest, item)
            if os.path.isdir(p):
                shutil.rmtree(p)
            else:
                os.remove(p)
    os.makedirs(dest, exist_ok=True)

    for d in COPY_DIRS:
        p = os.path.join(ROOT, d)
        if os.path.exists(p):
            copy_tree(p, os.path.join(dest, d))

    scripts = os.path.join(dest, "scripts")
    os.makedirs(scripts, exist_ok=True)
    for f in ROOT_FILES:
        p = os.path.join(ROOT, f)
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(scripts, f))

    lines = ["# SHA256 校验清单 — %s\n" % tag,
             "# 生成时间：%s (GMT+8)\n" % datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
             "# 校验方式：在本目录执行  sha256sum -c CHECKSUMS.txt\n",
             "# 说明：本清单不包含 CHECKSUMS.txt 自身\n\n"]
    count = 0
    for base, dirs, files in os.walk(dest):
        dirs[:] = sorted([d for d in dirs if d not in EXCLUDE])
        for f in sorted(files):
            full = os.path.join(base, f)
            rel = "./" + os.path.relpath(full, dest).replace("\\", "/")
            if rel.endswith("CHECKSUMS.txt"):
                continue
            lines.append("%s *%s\n" % (sha256(full), rel))
            count += 1

    with open(os.path.join(dest, "CHECKSUMS.txt"), "w", encoding="utf-8", newline="\n") as fp:
        fp.writelines(lines)

    print("snapshot :", dest)
    print("files    :", count)
    for name in ["CAD2019-2024", "ZWCAD", "GstarCAD", "BricsCAD"]:
        p = os.path.join(dest, "dist", name, "BlockLibraryManager.dll")
        if os.path.exists(p):
            print("%-12s %7d B  %s" % (name, os.path.getsize(p), sha256(p)))


if __name__ == "__main__":
    main()
