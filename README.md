# MemTrim



[![build](https://github.com/TheLiudawei/memtrim/actions/workflows/build.yml/badge.svg)](https://github.com/TheLiudawei/memtrim/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows%207--11-lightgrey.svg)](#)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4.svg)](#)

轻量级 **内存泄漏监控 + 自动清理** 工具，面向 Windows 11（向下兼容 Win7+ / .NET Framework 4.x）。

名字里的 **Trim** 就是它做的核心动作：`EmptyWorkingSet`（修剪工作集）——
把这部分常驻内存还给系统，同时清空待机列表。

内存清理算法移植自 [**PCL（Plain Craft Launcher 2, Meloong-Git/PCL）**](https://github.com/Meloong-Git/PCL) 的「内存优化」功能。

* 单文件 exe，**约 93 KB**，**零第三方依赖**（只用 Windows 自带的 .NET Framework 4.8）
* 托盘常驻，实时监控 + 一键手动清理 + 默认 **5 分钟内置 CD** 的自动清理
* 双通道泄漏检测：系统提交内存压力 + 逐进程增长趋势（线性回归斜率 / 拟合优度 R²）
* **监控过程不打开任何进程句柄**：一次 `NtQuerySystemInformation` 取回全部进程内存、
映像名与创建时间，默认每 5 秒一轮，堆分配约 **67 KB/分钟**
* 一条命令即可从源码重建，**不需要 .NET SDK / NuGet / 任何第三方库**
* 自带 103 项自检 + 18 项界面烟雾测试，CI 每次推送自动跑

> **想直接运行？** 到 [Releases](../../releases) 下载 `MemTrim.exe`（约 93 KB，无依赖）。
> 从源码构建见第 4 节。

> **从旧版本（原名 MemGuard）升级？** 配置与历史会自动从 `%APPDATA%\MemGuard\` 迁到
> `%APPDATA%\MemTrim\`，累计统计不会归零；旧目录只复制、不删除，留作后备。详见第 6 节。

---

## 1. 快速使用

```
双击 dist\MemTrim.exe         →  弹出 UAC，点「是」
```

首次启动会自动在 `%APPDATA%\MemTrim\` 生成：

|文件|说明|
|-|-|
|`config.json`|全部可调参数（也可在界面「设置」里改）|
|`history.tsv`|每次清理的时间、触发方式、释放量|

**界面说明**

|区域|内容|
|-|-|
|顶部进度条|物理内存占用率，颜色随压力变化（绿 → 橙 → 红）|
|状态行|正常 / 内存压力偏高 / 检测到疑似内存泄漏 / 两者同时命中|
|右侧|自动清理倒计时（内置 CD，默认 5 分钟）|
|泄漏检测|系统可用内存趋势 + 达标进程数与名称|
|进程列表|红字加粗 = 判定为泄漏、会被自动清理；绿字 = 当前占用较高但未判定泄漏|
|趋势行|系统可用内存斜率、采样轮数、跟踪进程数|
|底部按钮|立即清理内存（流光描边 + 外发光，窗口最小化/隐藏时自动暂停动画） / 设置 / 清理历史 / 最小化到托盘|

托盘图标右键菜单：打开主窗口、立即清理内存、启用自动清理（勾选）、设置、退出。
双击托盘图标可恢复窗口。关闭窗口默认最小化到托盘（`CloseToTray`）。

**命令行参数**

|参数|作用|
|-|-|
|`-tray` / `--tray` / `/tray` / `-min` / `--minimized`|启动后直接最小化到托盘（开机自启动用）|
|`-nocheck` / `--no-elevation-check`|未提权时不弹提权提示（自动化用）|

---

## 2. 自动触发规则

每次自动清理都要同时满足：

1. **开关开启**（`AutoClean`）
2. **冷却 CD 已结束**（`CooldownSeconds`，默认 **300 秒 = 5 分钟**）
3. **命中以下任一条件**

   * **内存压力**：`已提交内存 ÷ 提交上限` ≥ `PressureThreshold`（默认 **85%**）
   * **内存泄漏**：存在进程同时满足

     * 工作集增长斜率 ≥ `LeakSlopeThreshold`（默认 **2 MB/分钟**）
     * 线性拟合优度 R² ≥ `LeakR2Threshold`（默认 **0.55**）
     * 采样轮数 ≥ 判定所需轮数，且统计窗口跨度 ≥ 窗口时长的一半

> **压力口径为什么不用「物理占用率」**：任务管理器那种
> `(总量 − 可用) / 总量` 把待机列表与文件缓存都算作「已用」，
> Win10/11 日常就在 60~75%，85% 阈值会频繁误报。
> 提交内存（commit charge）才反映真实压力，因此**自动触发只认提交率**；
> 界面同时显示两个数字，`MemorySnapshot.PressureSource` 标明当前用的是哪一个。
> 极老系统若取不到提交信息，会自动退回物理占用率。

> 斜率与 R² **同时**达标才判定泄漏，这是抑制噪声误报的关键：
> 内存上下抖动（锯齿）的进程 R² 很低，不会被误判。
> 统计窗口默认 10 分钟、采样间隔 5 秒 —— 即**启动后约 5 分钟**才会开始输出泄漏判定，
> 在此之前列表显示的是「当前占用最高进程」，不会空白。

手动清理不受 CD 限制，随时可用；若此刻没有判定出泄漏进程，
会退化为清理「当前占用最高的 3 个进程」，保证按钮总有实际效果。

---

## 3. 清理动作（PCL 算法移植）

PCL 的官方仓库中，`PageOtherTest.MemoryOptimize` 系列函数是**空壳**：

```vb
Public Shared Sub MemoryOptimize(ShowHint As Boolean)
    If ShowHint Then Hint("为便于维护，开源内容中不包含百宝箱功能……")
End Sub
```

真实实现位于闭源/子模块中，但其原理已被公开分析（见文末参考）。它通过
`PromoteService` 提权后启用两个特权，再用 `NtSetSystemInformation` 依次执行 7 项操作：

|PCL 的操作|作用|本工具|
|-|-|-|
|`EmptyWorkingSets`|清空**所有进程**工作集|改为只清「泄漏 Top N 进程」（扰动更小，按需求选择）|
|`PurgeStandbyList`|清空系统待机列表|✅ 已实现|
|`PurgeLowPriorityStandbyList`|只清低优先级待机列表|✅ 作为回退方案|
|`FlushModifiedList`|强制脏页写盘|❌ 未启用（SSD 写入量大）|
|`FlushFileCache`|收缩文件缓存|❌ 未启用|
|`RegistryReconciliation`|注册表内存重整|❌ 未启用|
|`CombinePhysicalMemory`|物理页合并|❌ 未启用（存在兼容性/蓝屏风险）|

对应代码（`src/Interop.cs`、`src/MemoryCleaner.cs`）：

```csharp
// 1) 启用 PCL 使用的特权
Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

// 2) 清空系统待机列表：NtSetSystemInformation(SystemMemoryListInformation, \&cmd, 4)
Native.PurgeStandbyList(false);            // MemoryPurgeStandbyList = 4
Native.PurgeStandbyListBestEffort();       // 失败自动回退到只清低优先级待机列表

// 3) 清空目标进程工作集
Native.TrimProcessWorkingSet(pid);         // EmptyWorkingSet → 失败则 SetProcessWorkingSetSize(-1,-1)
```

**效果说明（重要）**：被清出去的页面并没有消失，而是转入待机列表或分页文件。
因此「释放 N MB」里有相当一部分是**把内存挪到了硬盘**，属于救急手段，
不适合高频调用。本工具的做法是：**只在泄漏/高压时触发 + 5 分钟 CD**，
避免频繁拆掉 Windows 辛苦建立的缓存。

---

## 4. 目录结构

```
MemTrim/
├─ build.ps1                构建正式版 → dist\MemTrim.exe
├─ build-test.ps1           构建自检程序 → dist\MemTrimTest.exe
├─ LICENSE                  MIT
├─ .gitignore / .gitattributes
├─ .github/workflows/build.yml   CI：推送后自动编译 + 跑自检
├─ src/
│  ├─ AssemblyInfo.cs       版本信息
│  ├─ Interop.cs            P/Invoke + ProcessSampler：整机内存、进程枚举（零句柄）、工作集、
│  │                        待机列表、特权；缓冲区与结果 List 长期复用
│  ├─ LeakDetector.cs       双通道泄漏检测（按需增长的环形缓冲 + 最小二乘线性回归）
│  ├─ MemoryCleaner.cs      清理流程编排（PCL 动作移植）
│  ├─ AppState.cs           配置 / 历史（尾部读取、自动裁剪、增量累计）/ 自启动 / 迷你 JSON
│  ├─ Log.cs                极简文本日志缓冲（清理结果排版）
│  ├─ UiTheme.cs            配色、共享字体、DPI 换算
│  ├─ UiControls.cs         自绘控件：圆角占用条、流光按钮、卡片、UiKit 构造工具
│  ├─ IconFactory.cs        运行期绘制应用图标（无外部素材）
│  ├─ SettingsForm.cs       设置对话框（在配置副本上编辑）
│  ├─ MainForm.cs           主窗口 + 托盘 + 监控主循环
│  ├─ Program.cs            入口：单实例、提权自检、异常兜底
│  ├─ app.manifest          requireAdministrator + Win7~11 兼容声明
│  └─ app.ico / app.png     图标（由 tools\IconGen.cs 生成）
├─ tools/
│  ├─ IconGen.cs            图标生成器源码
│  ├─ SelfTest.cs           控制台自检程序源码（103 项检查 + 新旧开销实测）
│  ├─ build-uitest.ps1      构建无 UAC 版用于界面检查
│  ├─ run-uitest.ps1        启动并截图界面（数据写到 obj\uitest-data\）
│  ├─ run-selftest.ps1      运行自检并收集日志（可传 quick/clean/smoke）
│  ├─ verify-build.ps1      校验嵌入清单 / 图标 / 依赖
│  └─ check-encoding.ps1    校验 UTF-8 BOM + CRLF（CI 也会跑）
└─ dist/                    产物（不进版本库，由 build.ps1 生成）
```

> 脚本里的路径全部相对自身推导，**克隆到任意目录都能直接跑**，不依赖作者机器上的路径。

> 界面层以前是单个 1800 行的 `Ui.cs`；现在按「视觉常量 → 自绘控件 → 子对话框 → 主窗口」
> 拆成 5 个文件，改一处布局不再需要在两千行里翻找。

**重新构建**（只需 Windows 自带的 .NET Framework 编译器，不需要 .NET SDK）：

```powershell
git clone https://github.com/TheLiudawei/memtrim.git
cd MemTrim
powershell -ExecutionPolicy Bypass -File .\build.ps1      # → dist\MemTrim.exe
powershell -ExecutionPolicy Bypass -File .\tools\verify-build.ps1   # 校验清单/图标/依赖
```

**运行自检**（不弹 UAC）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-test.ps1
.\dist\MemTrimTest.exe quick      # 只读检查 + 新旧开销实测（103 项）
.\dist\MemTrimTest.exe smoke      # 无界面烟雾测试：真实构造一次主窗口并退出（18 项）
.\dist\MemTrimTest.exe clean      # 额外执行一次真实清理（建议管理员运行）
```

也可以用 `tools\run-selftest.ps1` 跑（它会用 UTF-8 收集输出，避免中文乱码）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1          # 默认 quick
powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1 clean    # 真实清理
```

> 自检会把配置与历史写到 `obj\testdata\`，不会碰 `%APPDATA%\MemTrim` 里的真实数据。

`smoke` 模式会真的创建窗口、托盘图标、渲染一次列表，再通过托盘「退出」菜单收尾，
用来验证界面初始化路径没有异常；窗口以 `ShowInTaskbar = false` 显示，
约 1 秒后自动退出，不会打断你的操作。

**便携模式**：设置环境变量 `MEMTRIM_DATA_DIR` 可把配置与历史改到任意目录，
不写 `%APPDATA%`（界面自动化测试 `tools\run-uitest.ps1` 就是用它把数据隔离到 `obj\uitest-data\`）。

**常见问题**

* **编译报 `CS0016: 无法写入输出文件`**：正在运行的 MemTrim 锁住了 `dist\MemTrim.exe`。
先从托盘退出它再重建（`build.ps1` 会明确报出这种情况，不会伪装成成功）。
* **`dist\MemTrimTest.exe` 不见了**：部分杀软（实测 360 安全卫士）会静默删除这个自检程序 ——
它的行为特征太像「内存清道夫」。主程序 `MemTrim.exe` 不受影响；
需要自检时把 `dist` 目录加入杀软白名单，或临时关闭防护后重新 `build-test.ps1`。
* **中文显示为乱码**：本项目源码统一为 UTF-8（含 BOM）+ CRLF，`build.ps1` 也已用
`/codepage:65001` 指定编码。若你改过编码，请保持 UTF-8。

> **改代码前请留意**：Windows PowerShell 5.1 读取**无 BOM** 的 `.ps1` 时会按系统 ANSI
> 代码页（中文系统 = GBK）解码，一旦 BOM 被编辑器抹掉，中文注释就会让解析器报
> 「缺少右 }」这类莫名其妙的语法错误（本项目开发时就踩过一次）。
> 建议在 VS Code 里设置 `"files.encoding": "utf8Bom"`、`"files.eol": "\r\n"`，
> 并在提交前跑一次 `tools\check-encoding.ps1` —— CI 也会跑它，不合规直接失败。

---

## 5. 资源占用（实测数据）

自检程序会在同一台机器、同一时刻用两种策略各跑 10~50 轮，输出真实数字
（两种实现的源码都在 `tools\SelfTest.cs` 里，可逐行核对）。本机
（Win11 / 8 核 / 16 GB / 260 个进程）实测：

|指标|旧实现|当前实现|
|-|-|-|
|每轮枚举方式|`Process.GetProcesses()` + 逐个读内存|一次 `NtQuerySystemInformation`|
|每轮进程句柄操作|**每个进程 1 次** `OpenProcess` + 1 次 `CloseHandle`|**0 次**|
|每轮墙钟时间|与当前实现同量级（瓶颈在内核遍历线程链）|同左|
|堆分配|每轮重建分析列表与 LINQ/数组|**约 5.6 KB/轮 ≈ 67 KB/分钟**（同机对比 15.4 KB/轮）|
|Gen0 回收|随分配量增长|50 轮 0 次|
|非托管缓冲区|每轮 `AllocHGlobal` + `FreeHGlobal` 256 KB|进程采样器长期持有并复用（进程数暴涨时才扩、之后自动缩回）|
|每行列表项的 GDI 对象|每轮为每个泄漏行 `new Font`|复用同一个 Font 实例|

> 说明：`NtQuerySystemInformation(SystemProcessInformation)` 在 260 进程的系统上
> 本身约需 3~4 ms（内核要遍历全部线程链），并没有比逐进程读取快多少；
> 重构的真正收益是**句柄操作归零**与**分配量骤降**——
> 原先每 5 秒要打开/关闭上百个进程句柄，这对系统句柄表和杀软行为监控都是持续扰动。
> 若把采样间隔调到 2 秒，旧实现是每分钟约 3600 次句柄操作，当前实现仍是 0。

**其他资源处理**

* 采样样本用环形缓冲保存，淘汰旧样本不产生任何数组搬移；
缓冲**按需增长**（初值 16 槽）：默认参数下满容量是 154 槽 ≈ 3.7 KB/进程，
而多数进程在窗口内只积累几十条样本，按需增长能省掉其中大部分，
进程退出后整个缓冲随之释放；
* 回归统计量在遍历样本时一次算完，不构造 `x[]`/`y[]` 临时数组；
系统通道与进程通道**共用同一个回归实现**（此前系统通道复制了一份手写回归，
两份代码已经出现行为差异：系统趋势只看斜率、不校验 R²）；
* 进程筛选只对越过门槛的进程排序，阈值以下的进程连名字都不解析；
* 字体、图标、位图、卡片边框笔在构造期各创建一次并在 `Dispose` 中释放；
界面字体改为全局共享实例（原先每个标签 `new Font`，而赋给控件的 Font
**不会**随控件释放 —— 那是实打实的 GDI 句柄泄漏），退出时由 `UiFonts` 统一释放；
* 流光按钮的所有画笔、画刷、路径也只创建一次，每帧只把渐变刷平移一个相位
（不 `new` 任何 GDI 对象），且窗口隐藏/最小化/按钮禁用时自动停表；
* 标签文本先与上次渲染结果比对再赋值，值没变就不碰控件；
* 进程列表**复用行对象**，只改变化的单元格 —— 不再每 5 秒 `Items.Clear()` 整表重建，
因此刷新时不会闪烁，也不会丢掉选中项与滚动位置。

**主按钮的流光光效**

「立即清理内存」的光效借鉴自 [MeteorNOX/DeepSeek-Balance-Whale-Widget](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget)
的**文字光效**（`assets/whale-widget.js` 里的 `.dshwv-trow.dshwv-rgb` 与 `@keyframes dshwvRainbow`），
按它的三条规则原样移植，不再使用色相旋转与亮度呼吸：

|参考仓库（CSS）|本工具（GDI+）|
|-|-|
|固定十段「马卡龙」渐变，首尾同色|`MacaronStops`，逐值照搬|
|`background-size: 200% auto`|渐变刷按底面宽的 **200%** 铺开，`WrapMode.Tile` 无缝平铺|
|`background-position: 0% → 200%`，`2.6s linear infinite`|每帧把渐变刷平移一个相位，**2.6 秒**正好走完一个周期|

参考仓库是把这条渐变裁进文字（`background-clip: text`）；本工具的按钮保留浅色底面与深色文字
（浅色底上彩色文字对比度不够），改用**同一条**流动渐变去画「贴边细描边 + 向外的柔光」——
柔光由 6 层嵌套的圆角块叠加成，越靠内被叠加的次数越多，于是自然形成由内向外递减的衰减，
配色与流动节奏和参考仓库完全一致。

> 周期首尾是否严丝合缝是**可回归验证**的：`MemTrimTest.exe smoke` 会把相位直接摆到
> `p` 与 `p + 一个周期` 各渲染一帧，要求逐像素一致（当前 0 个像素不一致），
> 并顺带确认相位确实在移动、禁用态与相位无关。
> 改渐变色标时若忘了让首尾保持同色，这条检查会立刻失败 ——
> 循环接缝处的跳变肉眼几乎看不出来，只能靠它兜住。

**界面重绘与缩放**

* 窗体和卡片面板都开启了 `ResizeRedraw` 并启用双缓冲：拖动边框改变大小时
整块重画，不会留下「一列列竖向细线」那样的拉伸残影
（`DrawToBitmap` 逐像素比对，拉伸前后同一尺寸的画面完全一致）；
* 缩放不用 `AutoScaleMode.Dpi`（实测它会把 620 px 的客户区撑到 709 px，
导致按绝对坐标摆放的底部按钮错位），改为 `AutoScaleMode.None` +
显式 DPI 换算；底部说明文字所在的面板 `Dock` 到底边，
因此即使窗口高度被屏幕工作区截断也不会互相压住。

---

## 6. 本次修订（bug 修复 / 健壮性 / 重构）

### 6.1 修掉的 bug

|#|位置|问题|处理|
|-|-|-|-|
|1|`LeakDetector` 环形缓冲|**每次扩容都丢掉整个滑动窗口**：搬运到新数组后把 `count` 归零，于是 16→32→64→128 四档扩容各丢一次已采样本（窗口看上去仍然「有序」，极难发现）|搬运只换数组、不改 `count`；新增回归用例「增长过程不打乱样本顺序」|
|2|`LeakDetector` 环形缓冲|**容量长不到请求值**：`Grow` 先夹到容量再判上限，容量（154）小于当前长度时会把目标夹得更小，最后一档永远提前返回，缓冲停在 2 的幂上|先翻倍再夹到容量（只能往上走）；新增用例断言最终容量 = 请求容量|
|3|`LeakDetector` 环形缓冲|`NewestTick` 在 head 刚回绕到 0 时读到空槽（`_head - 1` 未补一圈），导致「最新样本时间」为 0|下标为负时补一圈|
|4|`LeakDetector` PID 复用判定|只用「工作集断崖」启发式，**看不到「小进程退出、大进程接手同一 PID」这个方向**：旧样本会被回归拉成一条陡峭上升直线，从而误判为泄漏并触发清理|改用内核随系统查询一起返回的 **进程创建时间**（与 `Process.StartTime` 交叉验证逐条相等，且零额外句柄开销）；拿不到创建时间时才回退到断崖启发式|
|5|`LeakDetector` 系统通道|复制了一份手写回归，与 `LinearFit` 已经**行为不一致**（只看斜率、不校验 R²，且对平坦序列会给出 R²=1）|两个通道共用同一个 `LinearFit`，系统趋势同样要求斜率与 R² 同时达标，界面上直接显示 R²|
|6|`LeakDetector` 跟踪表淘汰|「连续两轮没采到就移除」会把「工作集临时掉到门槛之下」的进程连同整个窗口一起丢掉 —— 而那恰恰是最需要保留历史的形态|改为「最新样本滑出窗口才淘汰」，环形缓冲本身随窗口淘汰，内存不会因此膨胀|
|7|`AppState` 配置/历史写入|`File.Delete` + `File.Move` 两步替换：中途失败（进程被杀/断电/文件被占用）会**直接丢掉配置或历史**|改用 `File.Replace` 原子替换，失败时才退回两步法|
|8|`AppState` 累计统计|每次清理都 `File.ReadAllLines` 把整个 `history.tsv` 读进内存再逐行求和，**越用越慢**（O(历史长度)），而累计量本来就能增量累加|启动时全量读一次，之后按新增记录增量累加|
|9|`AppState` 历史文件|**无上限增长**：每天十几次自动清理，一年就是几千行，且每次启动都要全量读|超过 2200 行自动裁剪到 2000 行（保留表头与最新记录）；读最近 N 条改为只读尾部|
|10|`AppState` 迷你 JSON|`true/false/null` 只认小写，手写配置里常见的 `True` / `NULL` 会被当成解析错误并**退回默认值**；对象解析失败时不终止，可能在同一字符上反复扫描|大小写不敏感匹配；解析失败立即结束该对象|
|11|`MainForm` 冷却计时|CD 用 `DateTime.Now` 计算，用户改系统时间/时区同步/夏令时会让「还剩 5 分钟」变成「就绪」或「还需 3 小时」|改用单调时钟 `Environment.TickCount`（无符号相减，49 天回绕也成立）|
|12|界面字体|赋给控件的 `Font` **不会**随控件释放：`UiKit` 每造一个标签就 `new Font`，窗体上几十个标签就是几十个不可回收的 GDI 句柄|字体改为全局共享实例，进程退出时统一释放|
|13|`MainForm` 进程列表|每 5 秒 `Items.Clear()` 整表重建：刷新时闪烁、丢掉选中项与滚动位置，还与流光动画抢重绘|复用行对象，只更新变化的单元格与配色|
|14|`Interop` 非托管缓冲区|每轮采样都 `AllocHGlobal(256 KB)` + `FreeHGlobal`；进程数暴涨后缓冲区**不会缩回**|缓冲区与结果 List 由采样器长期复用；连续两轮用量远小于缓冲区时自动缩回|

### 6.2 结构调整

* **项目改名为 MemTrim**（原名 MemGuard）。除了对外标识，还顺带解决了两个实际问题：
  原名与 [awnumar/memguard](https://github.com/awnumar/memguard)（Go 生态里做**内存中密钥保护**的
  知名库）、[libeclipse/memguard](https://pkg.go.dev/github.com/libeclipse/memguard) 等重名，
  且 `MEMGUARD` 是 Concord Idea Corp. 的[注册商标](https://furm.com/trademarks/memguard-78052271)；
  更关键的是语义相反 —— 那些项目「**保护**内存里的数据」，本工具「**释放**内存」，
  同名会让搜索与理解都出问题。`Trim` 则直接对应核心动作 `EmptyWorkingSet`；
* **数据目录随名字迁移**：`%APPDATA%\MemGuard\` → `%APPDATA%\MemTrim\`。
  迁移策略刻意保守：只在新目录**完全不存在**时复制 `config.json` 与 `history.tsv`，
  且**只复制、不删除**旧目录；新目录一旦建立就再也不碰旧数据，
  避免用陈旧文件覆盖新版本写入的记录。任何失败都静默忽略，绝不影响启动；
* `Ui.cs`（1806 行）拆成 `UiTheme` / `UiControls` / `IconFactory` / `SettingsForm` / `MainForm` 五个文件，
  按依赖方向排列；
* 采样器从 `Native` 里独立出来（`ProcessSampler` 持有缓冲区与结果 List），
  `Native` 只保留无状态的 P/Invoke；
* 系统通道不再借用 `SamplePoint.WorkingSetMB` 存「已用」、
  `PrivateMB` 存「可用」（读代码时要在脑子里做一次翻译），改用语义正确的 `SystemSample`；
  两个通道的回归数据源统一抽象为 `IFitSource`；
* 界面文本渲染改为「与上次结果比对后再赋值」，值没变就不碰控件；
* 历史 TSV 的解析/写入集中在 `HistoryEntry`，读尾、裁剪、累计各自单一职责。

### 6.3 测试

自检从 85 项增加到 **103 项**，界面烟雾测试从 6 项增加到 **18 项**，新增的用例专门覆盖上面这些缺陷：

* 环形缓冲的按需增长、增长后连续覆盖多圈是否仍然有序、最终容量是否等于请求容量；
* PID 复用判定的三个方向：工作集断崖、创建时间变化（含**变大**方向）、创建时间缺失时的回退；
* 历史文件的尾部读取与全量读取逐条一致、超限裁剪后仍可解析且保留最新记录；
* 追加记录时累计值增量更新，且与全量重算结果一致；
* 手写配置的 `True` / `FALSE` / `null` 宽容度。

---

## 7. 权限

清单已申请 `requireAdministrator`，启动时必然弹一次 UAC —— 这是刻意的：

* `PurgeStandbyList` 需要 `SeProfileSingleProcessPrivilege`
* 清空**其他进程**的工作集需要 `SET_QUOTA` 权限

如果因故未提权运行，程序会提示并询问是否重新以管理员身份启动；
此时清理仍可执行，但只能清掉自己有权访问的进程、且待机列表清理会失败。

> 注意：**监控**过程（枚举进程、读取工作集）在重构后完全不需要提权，
> 因为 `NtQuerySystemInformation` 不受进程访问权限限制；
> 需要管理员权限的只有**清理**动作。

---

## 8. 已知限制与注意事项

1. **杀软误报**：程序会枚举进程并对其他进程调用 `EmptyWorkingSet`，
这类行为与「内存清道夫」类工具一致。在装有 **360 安全卫士** 的机器上实测，
控制台版自检程序 `MemTrimTest.exe` 会被**静默删除**（主程序 `MemTrim.exe` 不受影响）。
若主程序被拦截，请在杀软中把该 exe 加入信任/白名单。
本项目的 `build-test.ps1` 在交付产物中**未附带** `MemTrimTest.exe`，就是为了避免这一误伤。
2. **高 DPI**：程序在创建窗口前调用 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`
（失败则退回 `SetProcessDpiAwareness(2)`），并配合 `AutoScaleMode.None` + 显式 DPI 换算，
125%/150% 缩放下界面不会发虚或裁剪。
3. **未实测管理员清理路径**：提权后的完整效果（待机列表清理 + 对其他进程清工作集）
需要你手动点一次 UAC 才能验证。可在管理员命令行下用
`build-test.ps1` 生成后运行 `MemTrimTest.exe clean` 来复现验证。
4. **清理时的瞬时卡顿**：清空工作集后，相关程序下次访问这些页面需从分页文件读回，
可能出现 1~2 秒的卡顿。因此默认只清「泄漏 Top N」而不是全系统。
5. **`CombinePhysicalMemory` 未启用**，避免个别驱动/虚拟化环境下的兼容性风险。
6. **配置改动即时生效**：设置对话框点「保存」后立即生效，无需重启；
点「取消」不会留下任何半生效的改动（编辑的是配置副本）。
7. **`MinWorkingSetMB`（默认 50 MB）** 决定哪些进程参与统计，是降低监控与清理开销的主要开关。
调小会纳入更多进程（判定更灵敏，开销更高），调大则反过来。

---

## 9. 参考

* [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 内存优化入口函数（开源部分为空壳）
* [PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE) —— 社区版，`PromoteService` 提权实现
* [PCL2 内存优化原理](https://lily0325-github-io.pages.dev/other/minecraft/cache-optim/what) —— 7 项 NT 操作的逐条作用分析
* [MeteorNOX/DeepSeek-Balance-Whale-Widget](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget) —— 主按钮流光光效（固定十段渐变 + 200% 宽度 + 2.6 秒匀速平移）的来源
* [为什么不开源内存优化函数 · Discussion #2500](https://github.com/Meloong-Git/PCL/discussions/2500)
* [PCL 是怎么执行内存优化的 · Discussion #3624](https://github.com/Meloong-Git/PCL/discussions/3624)

## 10. 许可

本项目以 **MIT 许可证**发布，详见 [LICENSE](LICENSE)。

清理算法**思路**来自 PCL 的公开讨论与原理分析，代码为独立实现（PCL 的相关开源部分是空壳函数，
见第 3 节）；主按钮的流光配色与动画参数移植自 DeepSeek-Balance-Whale-Widget（同样为 MIT）。
若你要再分发 PCL 自身的代码或资源，请注意原仓库的许可条款。

## 11. 贡献

欢迎提 Issue / PR。改代码前请先跑一遍：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-test.ps1
.\dist\MemTrimTest.exe quick    # 必须 103 通过 / 0 失败
.\dist\MemTrimTest.exe smoke    # 必须 18 通过 / 0 失败
```

`tools\SelfTest.cs` 里有若干检查是**故意卡死回归**的，改相关实现前请先读它的注释：

* 流光渐变的**首尾必须同色**（否则循环接缝会跳变，肉眼几乎看不出来）；
* 环形缓冲扩容后**不得打乱样本顺序**、最终容量必须等于请求容量；
* PID 复用判定必须覆盖「变小」与「变大」两个方向；
* 尾部读取的结果必须与全量读取**逐条一致**。

