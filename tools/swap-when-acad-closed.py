# -*- coding: utf-8 -*-
r"""
AutoCAD 正在运行时，插件 DLL 被进程占用无法直接覆盖。
本脚本后台轮询 acad.exe，等它退出后自动把暂存的 .new 文件换上去。

用法（一般由 update.cmd / 手工触发）：
  python tools\swap-when-acad-closed.py <目标目录> [最长等待分钟数]

行为：
  1. 每 5 秒检查一次 acad.exe 是否存在；
  2. 进程消失后，先把旧 DLL 改名备份成 *.old，再把 *.new 改名为正式文件名；
  3. 换完立即退出（最长等待默认 120 分钟，超时也退出，不做任何改动）。
"""
import os
import sys
import time
import subprocess

TARGET = sys.argv[1] if len(sys.argv) > 1 else None
MAX_MIN = float(sys.argv[2]) if len(sys.argv) > 2 else 120.0

if not TARGET or not os.path.isdir(TARGET):
    print("[swap] 目标目录不存在：%s" % TARGET)
    sys.exit(1)


def acad_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq acad.exe"],
            capture_output=True, timeout=20)
        # 中文 Windows 上 tasklist 输出的是 GBK，用 utf-8 解码会直接抛异常，
        # 进而被下面的 except 吞掉、误判成"CAD 已关闭"——必须容错解码。
        text = out.stdout.decode("utf-8", errors="replace")
        return "acad.exe" in text.lower()
    except Exception:
        # 查不到就当作没在跑，避免脚本卡死
        return False


def swap():
    done = []
    for name in os.listdir(TARGET):
        if not name.endswith(".new"):
            continue
        real = name[:-4]
        staged = os.path.join(TARGET, name)
        target = os.path.join(TARGET, real)
        backup = target + ".old"
        try:
            if os.path.exists(target):
                if os.path.exists(backup):
                    os.remove(backup)
                os.rename(target, backup)
            os.rename(staged, target)
            done.append(real)
        except Exception as exc:
            print("[swap] 替换失败 %s：%s" % (real, exc))
    return done


print("[swap] 监视 acad.exe，目标目录：%s" % TARGET)
deadline = time.time() + MAX_MIN * 60
while time.time() < deadline:
    if not acad_running():
        # 连续两次确认，避免刚关闭时句柄还没释放
        time.sleep(2)
        if not acad_running():
            done = swap()
            if done:
                print("[swap] 已替换：%s" % ", ".join(done))
            else:
                print("[swap] 没有需要替换的 .new 文件")
            sys.exit(0)
    time.sleep(5)

print("[swap] 超时（%.0f 分钟），未做任何改动。" % MAX_MIN)
sys.exit(2)
