# AutoCAD 图库管理器（Block Library Manager）

一个 AutoCAD .NET 插件：把磁盘上的 DWG 目录变成**可视化图库**——左侧分类树、右侧缩略图墙、底部插入参数，双击或拖拽即可插入当前图形。同时支持中望 CAD、浩辰 CAD、BricsCAD。

**当前版本：1.51.5。** 覆盖 AutoCAD 2010 ~ 2027（一套源码，6 个目标框架），外加中望、浩辰、BricsCAD。

**许可证：[MIT](LICENSE)。** 作者：悟空。  
AutoCAD、中望、浩辰、BricsCAD 的托管程序集受厂商版权约束，**不包含在本仓库中**。请在装有对应 CAD 的机器上编译，或自行从厂商 NuGet 取得参考程序集后再指定 `CadRefDir`。

## 构建状态（已实测）

| 版本组 | 状态 | 说明 |
| --- | --- | --- |
| **CAD2019-2024** | ✅ **已编译并验证** | 用本机 AutoCAD 2020 的 `acdbmgd/acmgd/accoremgd` 编译，net47，x64，**0 错误 0 警告**。产物 `dist\CAD2019-2024\BlockLibraryManager.dll`，已可用于 AutoCAD 2019–2024 |
| CAD2025-2027 | ⚠️ 代码已通过编译 | 需在装有 AutoCAD 2025+ 的机器上用其自带 DLL 重新编译（本机无 2025，暂用 2020 引用验证过语法） |
| CAD2010-2011 / 2012 / 2013-2014 / 2015-2018 | ⏳ 待编译 | 每个 AutoCAD 大版本的 `acdbmgd.dll` 版本号不同，**必须**在装有对应版本的机器上编译，无法交叉引用 |

> 为什么不能一次编译全部 6 个版本？
> `acdbmgd.dll` 是强命名程序集，AutoCAD 2010 / 2015 / 2020 / 2025 各自的版本号不同。
> 用 2020 的 DLL 编出来的程序集，加载到 AutoCAD 2015 会报"找不到程序集"；
> 反过来，用低版本的 DLL 去编译高版本目标，MSBuild 会因为引用程序集框架版本过低而直接丢弃引用。
> 这正是 `build\` 下要拆 6 个工程的原因——**源码只有一份，引用各取所需**。

---

## 1. 功能

| 功能 | 说明 |
| --- | --- |
| 分类树 | 按磁盘目录自动生成，显示每类的图块数量，支持多图库根目录（本机 + 服务器共享） |
| 缩略图 | 读取 DWG 自带预览图，两级缓存（内存 + `%LOCALAPPDATA%` PNG），空闲时分批生成，上千张图也不卡界面 |
| 三种视图 | 大图标 / 列表 / 详细信息 |
| 搜索 | 按名称过滤，可选是否包含子目录 |
| 最近使用 | 自动记录最近插入的图块 |
| 插入参数 | X/Y/Z 比例（可锁定等比）、旋转角度、插入后是否炸开、是否更新已有块定义 |
| 连续插入 | 一次选择，连续指定多个插入点，Esc 结束 |
| 拖拽入库 | 从资源管理器把 DWG 拖进列表即可复制到当前分类 |
| 拖拽出库 | 把列表里的图块拖到 CAD 图形窗口 / 资源管理器 |
| 展开内部块 | 可选：把每个 DWG 内部的块定义也单独列出来 |
| 同名处理 | 块名自动清洗（空格与非法字符转 `_`）；同名时按"更新已有定义"策略处理 |

---

## 2. 界面布局

```
┌──────────────────────────────────────────────┐
│ 图库管理器                        _  □  X    │  标题栏（自绘，可拖动）
├──────────────────────────────────────────────┤
│ [添加][刷新][上级][视图▾]│[设置]│[搜索][清除]│关于│  工具栏
├────────────┬─────────────────────────────────┤
│ 图库 (128) │  □  □  □  □  □                  │
│  ├ 机械(42)│  □  □  □  □  □                  │  左：分类树
│  ├ 建筑(56)│  □  □  □  □  □                  │  右：缩略图列表
│  └ 电气(30)│  □  □  □  □  □                  │
├────────────┴─────────────────────────────────┤
│ 比例:[1][1][1] ☑等比  旋转°:[0]              │
│ ☐炸开 ☑更新已有定义  [ 插入 ]                 │  插入参数
├──────────────────────────────────────────────┤
│ 共 42 个图块                                  │  状态栏
└──────────────────────────────────────────────┘
```

### 标题栏按钮行为

**窗口模式（v1.1 起）**：不再使用 AutoCAD 的 PaletteSet（它的停靠标题栏是一条无法隐藏的
黑色竖条，官方 API `PaletteSetTitleBarLocation` 只有 Left/Right，没有 None），
改为无边框自绘窗口，我们自己的标题栏就是唯一标题栏：

- 按住标题栏**拖动**窗口位置；窗口**八边可拉伸**缩放
- 关闭 AutoCAD 主窗口时窗口跟随最小化（通过 `ShowModelessDialog` 归属主窗口）
- 位置/尺寸自动记忆到 BlockLibrary.ini，下次打开原位还原；显示器变更时自动拉回屏幕内
- 想改回停靠面板模式，把 `src/UI/LibraryPalette.cs` 换回旧版宿主即可（git 历史）

| 按钮 | 行为 |
| --- | --- |
| `_` 最小化 | 最小化到任务栏（`WindowState.Minimized`） |
| `□` 最大化 / `▣` 还原 | 最大化占满工作区 ↔ 还原（图标随状态切换） |
| `X` 关闭 | 隐藏窗口（`BLOCKLIB` 可再次打开） |

双击标题栏 = 最大化 / 还原。按钮图标全部用 GDI 绘制，不使用特殊字符，避免个别机器上字体缺字。

### 添加图库目录（工具栏第一个按钮）

点「添加」打开系统文件树浏览对话框，选中一个文件夹确定后，它会成为**分类树的一级节点**，
它下面的子文件夹自动成为下级分类。

```
┌──────────────────────────────────────────┐
│ 添加图库目录                       _ □ X │
├──────────────────────────────────────────┤
│ 浏览并选择要加入图库的文件夹：            │
│ ┌──────────────────────────────────────┐ │
│ │ ▾ 桌面                                │ │
│ │ ▾ 计算机                              │ │
│ │     ├ 本地磁盘 (C:)                   │ │
│ │     ├ 数据盘 (D:)                     │ │
│ │     └ 光盘驱动器 (E:)                 │ │
│ │   我的文档                            │ │
│ └──────────────────────────────────────┘ │
│ 位置：[D:\我的图库                ][转到][上级]│
│ [新建文件夹] ☐显示隐藏文件夹   [确定] [取消]  │
└──────────────────────────────────────────┘
```

实现见 `src/UI/FolderPickerForm.cs`。几个刻意的取舍：

- **不用系统 `FolderBrowserDialog`**：net35（AutoCAD 2010/2011）上它是老式控件，不能拉伸、
  没有地址栏、不能粘贴路径；自己写反而全版本一致且能扩展。
- **惰性加载**：只有节点真正展开时才枚举子目录，打开对话框不会卡。
- **图标取真的**：走 `shell32!SHGetFileInfo` 拿 Explorer 的原生图标（驱动器图标各不相同），
  取不到时退回自绘色块，绝不抛异常。整个方法包在 try/catch 里。
- **地址栏可直接粘贴路径**（含 `\\服务器\共享` 这类 UNC），回车即定位。
- 双击-TreeView 节点导航；支持就地**新建文件夹**、**重命名**（节点 LabelEdit）。
- 全程 try/catch：遍历无权访问的目录只是不展开，不会崩。

**选择时的三种冲突处理**（`LibraryControl.Events.cs` 的 `OnAddRootClick`）：

| 情况 | 处理 |
| --- | --- |
| 与已有图库目录相同 | 提示已存在，自动选中该节点 |
| 落在已有图库目录之内 | 提示已在扫描范围内，不必单独添加 |
| 包含了已有图库目录 | 弹窗询问是否移除旧目录（避免重复统计） |

另外选择驱动器根目录（如 `D:\`）会二次确认——那会递归扫描整盘。

### 关于对话框

工具栏最右侧的"关于"按钮弹出版权/版本信息。**所有内容集中在
`src/UI/AboutForm.cs` 顶部的 `AboutInfo` 静态类里**（产品名、版本号、构建日期、
作者、单位、联系方式、功能简介），改常量即可，留空的行不显示。
对话框还会动态显示当前运行插件的 AutoCAD 版本号。

### 旧的 PaletteSet 模式说明（v1.0，已被替换）

`RolledUp` / `AutoRollUp` 并非每个 AutoCAD 版本都有，代码用反射探测，
依次尝试 `RolledUp` → `AutoRollUp` → `AutoHide`，都不支持时退化为隐藏面板，不会抛异常。

---

## 3. 目录结构

```
cad-block-manager/
├── README.md
├── BlockLibraryManager.sln          # VS 解决方案，含 6 个版本工程
├── build.cmd                        # 构建单个版本
├── build-all.cmd                    # 构建全部版本
├── Common.props 位于 src/
├── src/                             # ★ 唯一的一份源码，所有版本共享
│   ├── Common.props                 # 共享的 MSBuild 配置（引用、编译项、输出路径）
│   ├── Plugin.cs                    # 插件入口（ExtensionApplication）
│   ├── Commands.cs                  # 命令 BLOCKLIB / BLOCKLIBH / BLOCKLIBR / BLOCKLIBS / BLOCKLIBIN
│   ├── Model.cs                     # BlockItem / CategoryNode / GalleryView
│   ├── Config.cs                    # 配置读写（DLL 同目录 BlockLibrary.ini）
│   ├── Helpers\MiniJson.cs          # 零依赖 JSON（net35 没有 System.Text.Json）
│   ├── Services\
│   │   ├── LibraryIndex.cs          # 扫描磁盘，构建分类树
│   │   ├── ThumbnailService.cs      # 缩略图队列 + 缓存
│   │   ├── BlockInsertService.cs    # 块定义导入 + Jig 拖拽插入 + 手动炸开
│   │   ├── CadHost.cs               # ★ 国产 CAD 兼容层（反射调用不确定成员，缺失自动降级）
│   │   └── InsertBridge.cs          # 面板 → 命令 的请求桥
│   └── UI\
│       ├── LibraryControl.cs        # 主界面（布局）
│       ├── LibraryControl.Logic.cs  # 主界面（数据）
│       ├── LibraryControl.Events.cs # 主界面（事件）
│       ├── LibraryControl.Draw.cs   # ★ 列表自绘 + 可见区间优先（只为可见项取缩略图）
│       ├── LibraryPalette.cs        # PaletteSet 宿主
│       └── SettingsForm.cs          # 设置对话框
├── build\                           # 版本工程，每个只有十几行
│   ├── CAD2010-2011\  (net35,          AutoCAD 2011)
│   ├── CAD2012\        (net40,          AutoCAD 2012)
│   ├── CAD2013-2014\   (net40, accore,  AutoCAD 2014)
│   ├── CAD2015-2018\   (net45, accore,  AutoCAD 2018)
│   ├── CAD2019-2024\   (net47, accore,  AutoCAD 2024)
│   ├── CAD2025-2027\   (net8.0-windows, AutoCAD 2026)
│   ├── ZWCAD\          (net47,          中望 CAD   —— + Common.CadPlatform.props)
│   ├── GstarCAD\       (net48,          浩辰 CAD   —— + Common.CadPlatform.props)
│   └── BricsCAD\       (net8.0-windows, BricsCAD   —— + Common.CadPlatform.props)
├── tools\
│   └── gen-platform-src.ps1         # ★ 源码命名空间转换（AutoCAD → 中望 / 浩辰 / BricsCAD）
├── bundle\                          # 自动加载插件包（见第 6 节）
└── dist\                            # 构建产物（不入库，编译后生成）
```

---

## 4. 版本对照表

| 目录 | AutoCAD | .NET | 条件编译符号 | accoremgd |
| --- | --- | --- | --- | --- |
| `CAD2010-2011` | 2010 / 2011 | net35 | `ACAD_2012_OR_OLDER` | 否 |
| `CAD2012` | 2012 | net40 | `ACAD_2012_OR_OLDER` | 否 |
| `CAD2013-2014` | 2013 / 2014 | net40 | `ACAD_2013_OR_NEWER` | **是** |
| `CAD2015-2018` | 2015 / 2016 / 2017 / 2018 | net45 | `ACAD_2013_OR_NEWER;ACAD_2015_OR_NEWER` | **是** |
| `CAD2019-2024` | 2019 ~ 2024 | net47 | 同上 | **是** |
| `CAD2025-2027` | 2025 / 2026 / 2027 | net8.0-windows | 同上 + `ACAD2025_OR_NEWER;NETCORE` | **是** |

`accoremgd.dll` 从 AutoCAD 2013 起才存在（`ApplicationServices` / `EditorInput` / `Windows` 都搬进去了），2012 及更早只需要 `acdbmgd.dll` + `acmgd.dll`。

### 4.1 其它 CAD 平台对照表（中望 / 浩辰 / BricsCAD）

| 目录 | 平台 | 命名空间 | 托管程序集 | .NET | 条件符号 |
| --- | --- | --- | --- | --- | --- |
| `ZWCAD` | 中望 CAD 2024~2026 | 单根 `ZwSoft.ZwCAD.*` | `ZwDatabaseMgd.dll` + `ZwManaged.dll` | net47 | `BLM_ZWCAD` |
| `GstarCAD` | 浩辰 CAD 2024/2025 | 单根 `Gssoft.Gscad.*` | `GcCoreMgd.dll` + `GcDbMgd.dll` + `GcMgd.dll` | net48 | `BLM_GSTARCAD` |
| `BricsCAD` | BricsCAD V26 / V27 | **双根**（见下） | `BrxMgd.dll` + `TD_Mgd.dll` | net8.0-windows | `BLM_BRICSCAD` |

原理：这三家的 .NET API 都是 ObjectARX 的整体移植，**类型名与成员签名基本一致，只有命名空间不同**。
所以做法不是复制四份源码，而是：

1. 日常只维护 `src/` 一份源码（写的是 AutoCAD 的命名空间）；
2. 编译时由 `tools\gen-platform-src.ps1` 自动替换命名空间，生成到 `build\<平台>\gen\` 里
   再编译（源码没变就跳过，不拖慢编译）；
3. 少数各家不一定实现的成员（`Application.ShowModelessDialog` / `Document.CommandEnded` /
   `Document.UnknownCommand`），由 `src\Services\CadHost.cs` 用**反射 + IL 适配器**调用：
   有这个成员就走原生路径，没有就自动降级，因此不会在编译期报错。

#### BricsCAD 的双根命名空间（与中望 / 浩辰最大的区别）

BricsCAD 的托管 API 分装在两个程序集里，命名空间也被拆成**两个根**：

| AutoCAD | BricsCAD | 来自 |
| --- | --- | --- |
| `Autodesk.AutoCAD.ApplicationServices` | `Bricscad.ApplicationServices` | `BrxMgd.dll` |
| `Autodesk.AutoCAD.EditorInput` | `Bricscad.EditorInput` | `BrxMgd.dll` |
| `Autodesk.AutoCAD.Windows` | `Bricscad.Windows` | `BrxMgd.dll` |
| `Autodesk.AutoCAD.Runtime` | `Teigha.Runtime` | `TD_Mgd.dll` |
| `Autodesk.AutoCAD.DatabaseServices` | `Teigha.DatabaseServices` | `TD_Mgd.dll` |
| `Autodesk.AutoCAD.Geometry` | `Teigha.Geometry` | `TD_Mgd.dll` |
| `Autodesk.AutoCAD.Colors` | `Teigha.Colors` | `TD_Mgd.dll` |

`Teigha.*` 来自 ODA 的 DWG 内核（`TD_Mgd.dll`），`Bricscad.*` 是 Bricsys 自己在
`BrxMgd.dll` 里做的 CAD 应用层。映射表写在 `tools\gen-platform-src.ps1` 的
`$NsMapBricsCAD` 里，没列到的走命名空间根兜底。

> **版本提醒**：BricsCAD **V26 起宿主切换到 .NET 8**，V25 及更早是 .NET Framework 4.8。
> 本工程默认编 net8.0-windows（对应 V26/V27）；要给 V25 及更早编译，见第 5 节。

| 受影响的功能 | AutoCAD | 其它 CAD（有该成员时） | 其它 CAD（无该成员时） |
| --- | --- | --- | --- |
| 面板跟随 CAD 主窗口最小化 | `ShowModelessDialog` | 自动反射调用，效果一致 | 直接 `Form.Show()`，面板不跟着最小化（功能不受影响） |
| 拖拽插入后自动还原面板 | `CommandEnded` 事件 | 精确时机还原 | 兜底定时器 12 秒还原 |
| 设置里自定义启动命令 | `UnknownCommand` 事件 | 与 AutoCAD 一致 | 该功能不可用，默认命令 `BLOCKLIB` 照常可用 |

> 已用官方程序集逐个核对过 BricsCAD V27：`Application.ShowModelessDialog(Form)` /
> `ShowModalDialog(Form)` / `Idle` / `Document.CommandEnded` / `CommandCancelled` /
> `UnknownCommand` / `UnknownCommandEventArgs.GlobalCommandName` **全部存在**，
> 所以 BricsCAD 上这三项功能都是完整行为，不会降级。

---

## 5. 编译

### 前置条件

- **Visual Studio 2022**（推荐，社区版即可）+ ".NET 桌面开发"工作负载
  - 需要勾选 ".NET Framework 4.7 SDK / 目标包"（用于 net35~net47）
  - 需要 .NET 8 SDK（用于 AutoCAD 2025+）
- 或 **.NET SDK 8.0+** 命令行编译（已通过 `Microsoft.NETFramework.ReferenceAssemblies` 支持老框架，无需装 VS）
- 目标机器装有对应版本的 **AutoCAD**（只为拿 `acdbmgd.dll` 等引用，不装也能编译——把 DLL 拷到一个目录里指过去即可）

### 命令行

```bat
:: 构建一个版本
build.cmd CAD2019-2024 "C:\Program Files\Autodesk\AutoCAD 2024"

:: 构建全部（自动探测常见安装路径，找不到就跳过）
build-all.cmd
```

等价的原始命令：

```bat
dotnet build build\CAD2019-2024\BlockLibraryManager.csproj -c Release ^
    -p:AcadRefDir="C:\Program Files\Autodesk\AutoCAD 2024"
```

`AcadRefDir` 是唯一的必填参数，指向**包含 `acdbmgd.dll` 的目录**。
每个 csproj 里都写了一个默认值（例如 CAD2019-2024 默认 `C:\Program Files\Autodesk\AutoCAD 2024`），
路径一致的话可以省略。

### 构建中望 / 浩辰 / BricsCAD 版本

```bat
:: 中望 CAD（脚本会先自动探测常见安装路径，也可手工指定）
build-zwcad.cmd "C:\Program Files\ZWSOFT\ZWCAD 2021"

:: 浩辰 CAD
build-gstarcad.cmd "C:\Program Files\Gstarsoft\GstarCAD 2024"

:: BricsCAD（官方 API 为 NuGet：BricsCAD.27.NET / BricsCAD.26.NET，本仓库不附带）
::   把包里的 ref\net8.0 解到 tools\bcapi\v27\ref\net8.0 后可直接：
build-bricscad.cmd
build-bricscad.cmd V26
:: V25 及更早是 .NET Framework 4.8，需指向本机安装目录
build-bricscad.cmd V25 "C:\Program Files\Bricsys\BricsCAD V25"

:: 或一次性构建全部 9 个平台（没有装对应 CAD、也没有参考程序集的会自动跳过）
build-all.cmd
```

手工打命令的话，这几个平台对应的属性是 `CadRefDir`（不是 `AcadRefDir`）：

```bat
dotnet build build\ZWCAD\BlockLibraryManager.csproj    -c Release -p:CadRefDir="...中望安装目录"
dotnet build build\GstarCAD\BlockLibraryManager.csproj -c Release -p:CadRefDir="...浩辰安装目录"

:: BricsCAD：把官方 NuGet 的 ref\net8.0 解到 tools\bcapi\v27\ref\net8.0，或直接指向安装目录
dotnet build build\BricsCAD\BlockLibraryManager.csproj -c Release ^
    -p:CadRefDir="tools\bcapi\v27\ref\net8.0"
dotnet build build\BricsCAD\BlockLibraryManager.csproj -c Release ^
    -p:TargetFramework=net48 -p:CadRefDir="C:\Program Files\Bricsys\BricsCAD V25"
```

个别机器上中望把 `acmgd` 的内容也单列了一个 DLL，追加引用：

```bat
dotnet build build\ZWCAD\BlockLibraryManager.csproj -c Release ^
    -p:CadRefDir="..." -p:ExtraCadRefItems=ZwManagedUx
```

> **不要把厂商 DLL 放进仓库。** 中望、浩辰、BricsCAD、Autodesk 的托管程序集都受厂商版权约束，本仓库不附带。
> 找不到这些程序集时，构建会在编译前直接失败，避免生成无法 NETLOAD 的无效 DLL。

### Visual Studio

打开 `BlockLibraryManager.sln`，在"解决方案配置"里选择 `Release | Any CPU`，然后：

- 只生成某一个项目 → 右键该项目 → 生成
- 全部生成 → 生成 → 生成解决方案

如果 VS 报"找不到 acdbmgd.dll"，在 Common.props 顶部或命令行里指定 `AcadRefDir`。

### 产物

```
dist\CAD2019-2024\BlockLibraryManager.dll   ← AutoCAD 2019~2024（本机真编译，实测可加载）
dist\ZWCAD\BlockLibraryManager.dll          ← 中望 CAD（官方 NuGet 程序集真编译）
dist\GstarCAD\BlockLibraryManager.dll       ← 浩辰 CAD（官方 NuGet 程序集真编译）
dist\BricsCAD\BlockLibraryManager.dll       ← BricsCAD V26 / V27（官方 NuGet 程序集真编译）
```

**每个版本只能用自己那份 DLL**，不要交叉复制。

### 本机实测记录

```
> dotnet build build\CAD2019-2024\BlockLibraryManager.csproj -c Release ^
      -p:AcadRefDir="C:\Program Files\Autodesk\AutoCAD 2020"

  BlockLibraryManager -> ...\dist\CAD2019-2024\BlockLibraryManager.dll
  已成功生成。
      0 个警告
      0 个错误

> dotnet build build\BricsCAD\BlockLibraryManager.csproj -c Release

  gen-platform-src: [BricsCAD] 已生成 30 个文件 -> ...\build\BricsCAD\gen
  BlockLibraryManager -> ...\dist\BricsCAD\BlockLibraryManager.dll
  已成功生成。
      0 个警告
      0 个错误

  （换用 BricsCAD V26 的参考程序集再编一次：同样 0 警告 0 错误，
    说明这份 DLL 在 V26 / V27 上通用）
```

编译过程中真实暴露并修复的 6 个问题（都是跨版本容易踩的坑）：

1. `LibraryPalette.cs`：`Application` 在 `Autodesk.AutoCAD.ApplicationServices` 与 `System.Windows.Forms` 之间**二义性**（CS0104）→ 改为写全名
2. `LibraryIndex.cs`：`string filterHit = false;` 类型写错（CS0029）→ 改 `bool`
3. `BlockInsertService.cs`：`ObjectIdPair` 不存在 → 正确类型名是 **`IdPair`**（CS0246）
4~6. 连带的两处 CS0029 / CS0023 均由第 2 条引起

另有 2 个"字段未使用"警告，已删除 `_searchMode` / `_menuRecent` 两个残留字段。

---

## 6. 加载与使用

### 方式一：NETLOAD（临时试用，全版本可用）

1. 启动 AutoCAD，打开任意图形
2. 命令行输入 `NETLOAD`
3. 选择 `bundle\BlockLibraryManager.bundle\Contents\CAD2019-2024\BlockLibraryManager.dll`（自己编译后则用 `dist\<版本组>\` 里对应的 DLL）
4. 输入 `BLOCKLIB` 打开面板

### 方式二：自动加载（AutoCAD 2012+ 推荐）

仓库里的 `bundle\BlockLibraryManager.bundle` 带有 AutoCAD 2019–2024 的 DLL：

```
BlockLibraryManager.bundle\
    ├── PackageContents.xml                     ← 注册 AutoCAD 2019-2024
    ├── PackageContents-full-template.xml       ← 6 版本完整模板（其他版本参考）
    └── Contents\CAD2019-2024\BlockLibraryManager.dll
```

复制到当前用户的自动加载目录：

```
xcopy /E /I bundle\BlockLibraryManager.bundle ^
      "%APPDATA%\Autodesk\ApplicationPlugins\BlockLibraryManager.bundle"
```

复制完成后重启 AutoCAD，输入 `BLOCKLIB` 打开面板。之后更新 DLL 可以运行 `update.cmd`（需先关闭 AutoCAD，且上面的目录已经存在）。

装到所有用户（需管理员）：`C:\Program Files\Autodesk\ApplicationPlugins\`

卸载：删掉 `%APPDATA%\Autodesk\ApplicationPlugins\BlockLibraryManager.bundle` 文件夹。

AutoCAD 2010 / 2011 不支持 ApplicationPlugins，请在 `acaddoc.lsp` 里加：

```lisp
(command "NETLOAD" "D:\\YourPath\\dist\\CAD2010-2011\\BlockLibraryManager.dll")
```

### 命令列表

| 命令 | 作用 |
| --- | --- |
| `BLOCKLIB` | 显示 / 隐藏图库面板（开关） |
| `BLOCKLIBH` | 隐藏面板 |
| `BLOCKLIBR` | 重新扫描图库 |
| `BLOCKLIBS` | 打开设置 |
| `BLOCKLIBIN` | 内部命令：执行插入（面板自动调用，一般不手工输入） |

想改成中文命令名，把 `src\Commands.cs` 里的 `[CommandMethod("BLOCKLIB")]` 改成
`[CommandMethod("图库")]` 即可，AutoCAD 支持 Unicode 命令名。

### 日常操作

- **插入**：双击列表项，或选中后点"插入"，然后在图形窗口点一下位置
- **连续插入**：右键 → "插入多个（连续）"，逐个点位置，Esc 结束
- **入库**：从资源管理器把 DWG 拖到列表区域，会复制到当前选中的分类目录
- **添加图库目录**：工具栏"添加"→ 文件树浏览选文件夹（详见上文）
- **改图库位置**：工具栏"设置" → 增删图库根目录
- **快速入库**：资源管理器里把 DWG 拖到右侧列表；或先把 DWG 放进图库目录再点"刷新"

---

## 7. 兼容性设计说明（为什么这样写）

这部分是给后续维护者看的，也是这套代码能在 2010~2027 全版本跑起来的原因。

### 7.1 一份源码、六个目标框架

`src\**\*.cs` 被 6 个 csproj 通过 `EnableDefaultCompileItems=false` + 通配符共享。
版本差异全部收敛到两处：

- csproj 里的 `DefineConstants`（条件编译符号）
- `src\Common.props` 里的 DLL 引用（是否引用 `accoremgd`）

### 7.2 刻意避开的"版本敏感 API"

| 不用的 API | 原因 | 替代方案 |
| --- | --- | --- |
| `Editor.Command` | 2015 才引入 | 全部用对象数据库 API |
| `Entity.Explode()` | 各版本签名变过 | `ExplodeManually()`：克隆块定义内实体 + `Matrix3d` 变换 |
| `UnitsConverter` | 签名/行为不一致 | 需要时自行按单位换算 |
| `PaletteSet(name, Guid)` | 老版本没有该重载 | `#if ACAD_2013_OR_NEWER` 分流 |
| `System.Text.Json` | net35 没有 | 自研 `MiniJson`（只处理 string/int/bool/数组） |
| Newtonsoft.Json | 需额外部署 DLL | 同上 |
| `XmlSerializer` | 要求类型 public，易踩坑 | 同上 |

### 7.3 缩略图：为什么不用后台线程

AutoCAD 的 `Database` **只能在主线程**操作，丢到 `ThreadPool` 会崩。
所以采用**队列 + `Application.Idle`** 的模式：每次空闲事件最多处理 6 张、
总耗时预算 40ms（到点就收工，剩下的等下一次 Idle），界面始终可响应；
结果先写磁盘 PNG 缓存，第二次打开同一图库基本秒开。

### 7.4 缩略图加载：UI 虚拟化 + 解码降采样

> 说明：本插件是 **WinForms**（不是 WPF），所以用户提的 `BitmapImage.DecodePixelWidth`
> 没有直接对应的 API，这里用的是 GDI+ 的等价做法，效果一致。

**一、UI 虚拟化 —— 只为屏幕上可见的项加载图片**

列表原来走的是 `ListView` + `ImageList`：每加一项就把一整张缩略图塞进 `ImageList`，
打开一个 2000 个图块的分类就要同步做出 2000 张图，这是刷新列表最慢的一步；
而且 `ImageList` 没有"原地替换"的方法，缩略图陆续到位时只能 `RemoveByKey` + `Add`，
一删一加会让后面所有项的索引整体错位。

现在改成 **`OwnerDraw` 自绘**（见 `src\UI\LibraryControl.Draw.cs`）：

| | 原来 | 现在 |
| --- | --- | --- |
| ImageList | 每个 item 一张图（几千张） | 只留 1 张透明占位图，纯粹用来撑出图标尺寸 |
| 取图时机 | 建列表时全部取 | **绘制事件触发时才取**，屏幕外的项永远取不到 |
| 动态块角标 | 每个动态块复制一张 Bitmap 把角标烤进去 | 绘制期直接叠画上去，零拷贝 |
| 缩略图到位 | `RemoveByKey`+`Add`+每条 `BeginInvoke`（索引会错位） | 只打脏标记，一帧统一 `Invalidate` 一次 |

WinForms 的 `ListView` 只为可见项发 `DrawItem` / `DrawSubItem` 事件，
所以"进入一个 2000 项的分类"从"先做 2000 张图"变成"先做屏幕上那几十张"。

**二、数据虚拟化 —— 后台队列跟着视口走**

`ThumbnailService.Prioritize()` 把当前可见区间（用 `LVM_GETTOPINDEX` /
`LVM_GETCOUNTPERPAGE` 算出）的条目提到队列最前面。用户滚到哪儿，哪一段就优先出图。
只有可见区间真的变了才重排队列（用指纹比对），不会每帧重排。

**三、解码时降采样**

- **加载磁盘缓存**：GDI+ 没有解码参数，只能在 `Image.FromStream` 之后**立刻**按
  目标尺寸重采样、然后马上释放大图与流 —— 留在缓存里的是小图而不是原始大图。
  顺带修掉一个隐患：原来 `Image.FromStream` 依赖的 `MemoryStream` 一直挂着不释放，
  每张缩略图都额外占着一份完整文件字节。
- **生成新缩略图**：DWG 内嵌的预览位图动辄 512×512 甚至 1024×768，而后面两步
  （裁右缘黑带、泛洪换底色）都是**逐像素**处理，是整个生成过程最贵的一段。
  现在先等比缩到 **2 倍缩略图尺寸**再处理，像素规模降到十几分之一，出图尺寸不变、
  肉眼无差别。

**四、流畅性**

- 列表打开双缓冲（`DoubleBuffered` 是 `protected`，用反射设），自绘不再闪；
- `DisplayBlocks` 从逐条 `Items.Add` 改成一次性 `Items.AddRange`，
  少发几千次原生通知；
- 每张缩略图到位不再各发一个 `BeginInvoke`，改成脏标记 + 每帧一次 `Invalidate`。

### 7.5 插入：为什么经过一个命令桥

面板是非模态的，在按钮 `Click` 里直接 `Editor.Drag` 经常报 `eNotApplicable`，
或者鼠标输入被面板吃掉。这里用 Autodesk 官方推荐的模式：

```
面板按钮 → InsertBridge.Request(...) → doc.SendStringToExecute("BLOCKLIBIN\n")
        → 命令 BLOCKLIBIN（命令上下文） → Editor.Drag(jig) → 完成插入
```

### 7.6 同名块定义

块名会先清洗（空格与 `< > / \ " : ; ? * | , = \` '` 转 `_`，最长 250 字符）。
插入时若目标图中已有同名定义：

- 勾选"更新已有定义"（默认）→ 先尝试删除旧定义再重新导入，拿到图库最新版本；
  删不掉（说明正被引用）就复用旧定义
- 不勾选 → 直接复用已有定义

### 7.7 已知局限

- "炸开"是手动实现，**含属性定义（Attribute）的块不会转成属性值**
- 缩略图用的是 DWG 自带预览图；如果原图保存时没生成预览图，会显示占位图
- 跨版本扫描网络盘较慢时，建议关闭设置里的"展开 DWG 内部块"
- 单位换算没有自动处理（图块本身的单位与目标图不一致时，比例仍按你填的数值）

---

## 8. 配置与缓存位置

| 内容 | 路径 |
| --- | --- |
| 配置 | DLL 同目录 `BlockLibrary.ini`（DLL 目录不可写时回退到 `%APPDATA%\BlockLibraryManager\`） |
| 缩略图缓存 | `%LOCALAPPDATA%\BlockLibraryManager\Thumbnails\*.png` |
| 默认图库目录 | `我的文档\AutoCAD BlockLibrary`（首次运行自动创建） |

删掉缓存目录即可强制重新生成缩略图。

---

## 9. 故障排查

| 现象 | 排查 |
| --- | --- |
| `NETLOAD` 报"无法加载程序集" | 确认用的是**对应版本**的 DLL；AutoCAD 2025+ 必须用 `CAD2025-2027` 那份 |
| 编译报"找不到 acdbmgd.dll" | 用 `-p:AcadRefDir="..."` 指向正确目录，或确认装了对应 AutoCAD |
| 编译报"未能找到类型或命名空间名 Autodesk" | **`AcadRefDir` 指错了版本**。例如用 AutoCAD 2020 的 DLL 编译 net45 目标，MSBuild 会因引用程序集框架版本过高而静默丢弃引用。必须用与目标版本组匹配的 AutoCAD 目录 |
| `dist\CAD2025-2027` 为空或产物不可用 | 本机没有 AutoCAD 2025+。需在装有 2025+ 的机器上用其自带 DLL 编译 |
| 编译报"缺少 .NET Framework 3.5/4.0 引用程序集" | 首次编译需要联网还原 `Microsoft.NETFramework.ReferenceAssemblies`；离线环境请在能上网的机器上先 `dotnet restore` |
| 面板打开但列表是空的 | 设置 → 检查图库根目录是否存在且有 `.dwg` 文件 |
| 缩略图一直是占位图 | 原 DWG 保存时未生成预览图；或 `%LOCALAPPDATA%` 无写权限 |
| 点击插入没反应 | 确认当前有打开的图形；命令行输入 `BLOCKLIBIN` 看是否有报错 |

---

## 10. 许可证

本项目以 [MIT License](LICENSE) 开源。

AutoCAD、中望 CAD、浩辰 CAD、BricsCAD 及其托管 API 的名称与程序集分别属于 Autodesk、中望软件、浩辰软件、Bricsys，不在本许可证的授权范围内。
