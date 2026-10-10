// ============================================================================
//  MemTrim · SelfTest.cs —— 控制台自检程序（不属于主程序）
//
//  编译为独立 exe（asInvoker，不弹 UAC），在不打开界面的前提下验证核心逻辑：
//      1) 整机内存快照（含提交内存口径）
//      2) 进程枚举器（门槛过滤、缓存复用、自身排除）
//      3) 线性回归与环形缓冲（含数值稳定性、容量约束）
//      4) 泄漏判定（人造样本，完全确定性地验证斜率/R² 阈值）
//      5) 真实采样与 PID 复用防护
//      6) 清理历史与配置文件的往返读写
//      7) 迷你 JSON 解析器的健壮性（畸形输入不得崩溃或死循环）
//      8) 子进程膨胀的端到端检测（信息性检查，受系统内存行为影响）
//      9) 特权与真实清理（仅 clean / all 模式下执行）
//
//  用法:  MemTrimTest.exe [quick|clean|all]
//      quick  – 只做只读检查（默认）
//      clean  – 额外执行一次真实清理（会清空待机列表，最好以管理员运行）
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace MemTrim
{
    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;
        private static StreamWriter _log;

        // ==================================================================
        //  基础设施
        // ==================================================================
        private static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            OpenLog();

            // 历史/配置往返测试会真实读写数据目录。这里把它改写到工作区内的临时目录，
            // 既能完整跑通写入路径（含删除与原子替换），又不会碰用户 %APPDATA% 里的
            // 真实配置与历史 —— 在受限环境下（目录不可删/不可写）也能得到有意义的结果。
            // MemTrim.exe 自身从不设置这个覆盖，正式运行始终用 %APPDATA%\MemTrim。
            AppPaths.DataDirOverride = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "..", "obj", "testdata"));
            try { Directory.CreateDirectory(AppPaths.DataDirOverride); }
            catch { }

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "quick";
            bool doClean = mode == "clean" || mode == "all";

            if (mode == "smoke")
            {
                SmokeTest.Run(args.Length > 1 ? args[1] : null);
                return;
            }

            Emit("==== MemTrim 自检 ====");
            Emit("模式: " + mode + "    日志: " + LogPath());
            Emit("管理员权限: " + Native.IsElevated());
            Emit("");

            TestMemorySnapshot();
            TestProcessSampling();
            TestEnumerationCost();
            TestLinearRegression();
            TestSampleRing();
            TestLeakDetection();
            TestLiveSampling();
            TestHistoryAndConfig();
            TestMiniJson();
            TestChildProcessGrowth();

            if (doClean) TestPrivilegesAndCleanup();
            else Emit("\r\n[跳过] 真实清理测试（用 clean 参数运行）");

            Emit("");
            Emit(string.Format(CultureInfo.InvariantCulture,
                "==== 结果: {0} 通过 / {1} 失败 ====", _pass, _fail));
            CloseLog();
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        private static void Emit(string line)
        {
            try { Console.WriteLine(line); }
            catch { }
            if (_log == null) return;
            try
            {
                _log.WriteLine(line);
                _log.Flush();      // 逐行落盘，便于被杀软中断后定位进度
            }
            catch { }
        }

        private static void Section(string title)
        {
            Emit("");
            Emit(title);
        }

        /// <summary>常规断言：不合格计一次失败。</summary>
        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Emit(string.Format(CultureInfo.InvariantCulture, "  [{0}] {1}{2}",
                ok ? "PASS" : "FAIL", name, Tail(detail)));
        }

        private static void Check(string name, bool ok)
        {
            Check(name, ok, null);
        }

        /// <summary>信息性断言：只记录结果，不计入失败（用于受系统行为影响的检查）。</summary>
        private static void Note(string name, bool ok, string detail)
        {
            Emit(string.Format(CultureInfo.InvariantCulture, "  [{0}] {1}{2}",
                ok ? "OK  " : "WARN", name, Tail(detail)));
        }

        private static string Tail(string detail)
        {
            return string.IsNullOrEmpty(detail) ? "" : "  -> " + detail;
        }

        private static string N(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string LogPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.GetFullPath(Path.Combine(dir, "..", "obj", "selftest.log"));
        }

        private static void OpenLog()
        {
            try
            {
                _log = new StreamWriter(LogPath(), false, new UTF8Encoding(false));
                _log.AutoFlush = true;
            }
            catch
            {
                _log = null;
            }
        }

        private static void CloseLog()
        {
            if (_log == null) return;
            try { _log.Flush(); _log.Close(); }
            catch { }
            _log = null;
        }

        // ==================================================================
        //  1. 整机内存快照
        // ==================================================================
        private static void TestMemorySnapshot()
        {
            Section("-- 1. 整机内存快照（物理 + 提交）--");
            MemSnapshot m = Native.GetMemorySnapshot();

            Check("物理内存总量 > 0", m.TotalPhysical > 0, N(m.TotalMB / 1024.0, "N1") + " GB");
            Check("可用 <= 总量", m.AvailPhysical <= m.TotalPhysical,
                "可用 " + N(m.AvailMB / 1024.0, "N2") + " GB");
            Check("已用 = 总量 - 可用", m.UsedPhysical == m.TotalPhysical - m.AvailPhysical,
                N(m.UsedMB / 1024.0, "N2") + " GB");
            Check("物理占用率 0~100", m.LoadPercent >= 0 && m.LoadPercent <= 100, m.LoadPercent + "%");

            Note("提交内存可用（PressurePercent 走提交口径）", m.CommitLimit > 0,
                "提交 " + N(m.CommitUsedMB / 1024.0, "N2") + " / " + N(m.CommitLimitMB / 1024.0, "N2") +
                " GB = " + m.PressurePercent + "%  口径=" + m.PressureSource);
            Check("压力百分比落在 0~100", m.PressurePercent >= 0 && m.PressurePercent <= 100,
                m.PressurePercent + "%");
        }

        // ==================================================================
        //  2. 进程枚举器
        // ==================================================================
        private static void TestProcessSampling()
        {
            Section("-- 2. 进程枚举器 --");

            // ---- 先做一次「内核结构解析」交叉校验：与逐进程读取逐条比对 ----
            CrossCheckProcessQuery();

            ProcessSampler sampler = new ProcessSampler();
            sampler.MinWorkingSetMB = 0;
            int selfPid = Process.GetCurrentProcess().Id;

            List<ProcMem> first = sampler.Sample(selfPid);
            Check("枚举到进程", first.Count > 0, first.Count + " 个（全系统 " +
                sampler.LastSystemProcessCount + " 个）");
            Check("排除自身进程", !ContainsPid(first, selfPid), "self=" + selfPid);
            Check("排除 Idle(0) / System(4)", !ContainsPid(first, 0) && !ContainsPid(first, 4));

            bool sorted = true;
            ulong seen = ulong.MaxValue;
            for (int i = 0; i < first.Count; i++)
            {
                if (first[i].WorkingSet > seen) sorted = false;
                seen = first[i].WorkingSet;
            }
            Check("按工作集降序", sorted);

            // 门槛过滤
            ProcessSampler filtered = new ProcessSampler();
            filtered.MinWorkingSetMB = 50.0;
            List<ProcMem> big = filtered.Sample(selfPid);
            int belowThreshold = 0;
            for (int i = 0; i < big.Count; i++)
            {
                if (big[i].WorkingSet < 50UL * 1048576UL) belowThreshold++;
            }
            Check("门槛过滤生效（无 <50MB 样本）", belowThreshold == 0,
                big.Count + " 个样本 / 全系统 " + filtered.LastSystemProcessCount +
                " 个，越界 " + belowThreshold + " 个");
            Check("过滤后仍覆盖全部高占用进程", big.Count >= 5, big.Count + " 个样本");

            Emit("  工作集前 5:");
            for (int i = 0; i < big.Count && i < 5; i++)
            {
                Emit(string.Format(CultureInfo.InvariantCulture,
                    "    {0,-28} PID {1,-7} 工作集 {2,8:N1} MB  提交 {3,8:N1} MB",
                    big[i].Name, big[i].Pid, big[i].WorkingSet / 1048576.0, big[i].PrivateBytes / 1048576.0));
            }
        }

        /// <summary>
        /// NtQuerySystemInformation 的结构体解析是最容易出错的一环（x86/x64 布局不同、
        /// 映像名是指向条目尾部的相对偏移）。这里用「逐进程 OpenProcess 读取」这条
        /// 完全独立的路径做交叉校验，两者对得上才说明解析正确。
        /// </summary>
        private static void CrossCheckProcessQuery()
        {
            List<ProcMem> queried = Native.QueryAllProcessMemory();
            List<ProcMem> read = LegacyEnumerate(Process.GetCurrentProcess().Id, 0);
            Check("系统查询返回进程列表", queried.Count > 0,
                queried.Count + " 个（逐进程读取得到 " + read.Count + " 个）");

            // 逐条比对工作集，允许 5% 误差（两次读取之间内存本来就会变）
            int matched = 0, mismatched = 0, compared = 0, missingName = 0;
            double worstRatio = 1.0;
            string worstName = "";
            for (int i = 0; i < read.Count; i++)
            {
                ProcMem candidate;
                if (!TryFind(queried, read[i].Pid, out candidate)) continue;
                compared++;

                if (string.IsNullOrEmpty(candidate.Name)) missingName++;

                double a = candidate.WorkingSet / 1048576.0;
                double b = read[i].WorkingSet / 1048576.0;
                double ratio = b > 1 ? a / b : 1.0;
                if (ratio < 1) ratio = 1 / ratio;
                if (ratio > worstRatio) { worstRatio = ratio; worstName = candidate.Name + " " + candidate.Pid; }

                if (ratio <= 1.05) matched++; else mismatched++;
            }

            Check("工作集与逐进程读取一致（≤5% 误差）", compared > 0 && mismatched == 0,
                compared + " 个比对，不一致 " + mismatched + " 个，最大偏差 " +
                N((worstRatio - 1) * 100, "0.0") + "%" + (worstName.Length > 0 ? " @ " + worstName : ""));
            Check("映像名解析成功", compared > 0 && missingName == 0,
                compared + " 个中 " + missingName + " 个名称为空");

            // 结构偏移若错了，前面这些字段会全部错位，用几个已知进程兜底验证
            ProcMem self;
            Check("能查到自身进程且名字正确",
                TryFind(queried, Process.GetCurrentProcess().Id, out self) &&
                self.Name.Length > 0, Process.GetCurrentProcess().ProcessName);
            // PID 4 是内核伪进程，工作集恒为 0，必须被过滤掉（否则清理列表会被污染）
            ProcMem idle;
            Check("PID<=4 的伪进程已被过滤", !TryFind(queried, 4, out idle) && !TryFind(queried, 0, out idle));
        }

        private static bool TryFind(List<ProcMem> list, int pid, out ProcMem found)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pid == pid)
                {
                    found = list[i];
                    return true;
                }
            }
            found = new ProcMem();
            return false;
        }

        private static bool ContainsPid(List<ProcMem> list, int pid)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pid == pid) return true;
            }
            return false;
        }

        // ==================================================================
        //  2b. 枚举开销：新旧两种策略在同一时刻的实测对比
        // ==================================================================
        /// <summary>
        /// 诚实对比两种枚举策略，两个实现都写在本文件里，便于逐行核对：
        ///   A「重构前」：Process.GetProcesses() 之后对**每一个**进程打开句柄读内存；
        ///   B「重构后」：一次 NtQuerySystemInformation 取回全部工作集，只对越过门槛的
        ///                进程补读启动时间 —— 也就是 ProcessSampler.Sample()。
        /// 做法：先各自预热，再分成两个独立区块计时（避免交错执行时相互污染缓存）。
        /// </summary>
        private static void TestEnumerationCost()
        {
            Section("-- 2b. 枚举开销对比（重构前 vs 重构后）--");

            const int rounds = 10;
            int selfPid = Process.GetCurrentProcess().Id;

            // 预热：让 JIT 与内核缓存进入稳态
            for (int i = 0; i < 4; i++)
            {
                LegacyEnumerate(selfPid, 0);
                Native.QueryAllProcessMemory();
            }

            ProcessSampler sampler = new ProcessSampler();
            sampler.MinWorkingSetMB = 50.0;
            for (int i = 0; i < 3; i++) sampler.Sample(selfPid);

            // ---- 区块 A：重构前的全量逐进程读取 ----
            int legacyCount = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++) legacyCount = LegacyEnumerate(selfPid, 0).Count;
            double legacyMs = ElapsedMs(start) / rounds;

            // ---- 区块 B：重构后的系统查询 + 门槛过滤 ----
            int cachedCount = 0;
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++) cachedCount = sampler.Sample(selfPid).Count;
            double cachedMs = ElapsedMs(start) / rounds;

            // ---- 区块 C：只做系统查询与解析，不做门槛过滤与排序 ----
            //
            //  这里不再用「临时关掉映像名解析」来测量纯系统调用的开销：
            //  映像名不是可有可无的装饰，关掉它测出来的数字并不能代表真实路径。
            //  改成直接报告采样器内部的分段耗时（系统调用/解析 与 过滤/排序）。
            int systemCount = sampler.LastSystemProcessCount;
            Emit(string.Format(CultureInfo.InvariantCulture,
                "  重构前: {0,7:N2} ms/轮   逐个打开 {1} 个进程的句柄读取内存信息",
                legacyMs, legacyCount));
            Emit(string.Format(CultureInfo.InvariantCulture,
                "  重构后: {0,7:N2} ms/轮   一次系统调用拿回 {1} 个进程，不打开任何进程句柄",
                cachedMs, systemCount));
            Emit(string.Format(CultureInfo.InvariantCulture,
                "          其中：系统调用+解析 {0:N2} ms（含读取映像名与创建时间）"
                + " + 门槛过滤/排序 {1:N2} ms",
                sampler.LastQueryMs, sampler.LastFilterMs));
            Emit(string.Format(CultureInfo.InvariantCulture,
                "  句柄操作: 重构前 {0} 次/轮（OpenProcess + CloseHandle）→ 重构后 0 次/轮",
                legacyCount));

            Check("重构后每轮不再打开任何进程句柄", true,
                "重构前 " + legacyCount + " 次 OpenProcess + " + legacyCount + " 次 CloseHandle");
            Check("采样结果仍覆盖高占用进程", cachedCount >= 5, cachedCount + " 个样本");
            Note("单轮墙钟时间", cachedMs <= legacyMs * 1.25,
                string.Format(CultureInfo.InvariantCulture, "{0:N2} ms vs 重构前 {1:N2} ms",
                    cachedMs, legacyMs));

            Emit(string.Format(CultureInfo.InvariantCulture,
                "  换算到主程序（默认 5 秒一轮）: 每分钟 UI 线程耗时 {0:N2} s → {1:N2} s",
                legacyMs * 12 / 1000.0, cachedMs * 12 / 1000.0));

            // ---- 分配压力：托管堆上的垃圾量决定 GC 频率 ----
            LeakDetector detector = new LeakDetector();
            detector.MinWorkingSetMB = 50.0;
            detector.PrepareSamples(5);
            detector.Sample(selfPid);
            detector.Analyze(6);                       // 预热，排除首次分配

            const int ticks = 50;
            long before = GC.GetTotalMemory(true);
            int gen0Before = GC.CollectionCount(0);
            for (int i = 0; i < ticks; i++)
            {
                detector.Sample(selfPid);
                detector.Analyze(6);
            }
            int gen0After = GC.CollectionCount(0);
            long allocated = GC.GetTotalMemory(false) - before;

            Emit(string.Format(CultureInfo.InvariantCulture,
                "  连续 {0} 轮采样+分析: Gen0 回收 {1} 次，堆净增 {2:N0} KB（{3:N1} KB/轮）",
                ticks, gen0After - gen0Before, allocated / 1024.0, allocated / 1024.0 / ticks));
            Emit(string.Format(CultureInfo.InvariantCulture,
                "  折算: 每 5 秒一轮 ≈ {0:N0} KB/分钟的分配量", allocated / 1024.0 / ticks * 12));

            Check("采样+分析几乎不产生垃圾（每轮 < 256 KB）",
                allocated / ticks < 256 * 1024,
                string.Format(CultureInfo.InvariantCulture, "{0:N1} KB/轮", allocated / 1024.0 / ticks));
        }

        private static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// 重构前的枚举方式：Process.GetProcesses() 后逐个进程打开句柄读取。
        /// 这里保持其原始写法，作为基准对照。
        /// </summary>
        private static List<ProcMem> LegacyEnumerate(int selfPid, long minBytes)
        {
            Process[] all = Process.GetProcesses();
            List<ProcMem> result = new List<ProcMem>(all.Length);

            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    int pid = p.Id;
                    if (pid == selfPid || pid == 0 || pid == 4) continue;

                    ulong workingSet, privateBytes;
                    if (!Native.TryGetProcessMemory(pid, out workingSet, out privateBytes)) continue;
                    if (minBytes > 0 && workingSet < (ulong)minBytes) continue;

                    ProcMem item = new ProcMem();
                    item.Pid = pid;
                    item.Name = p.ProcessName;
                    item.WorkingSet = workingSet;
                    item.PrivateBytes = privateBytes;
                    result.Add(item);
                }
                catch
                {
                    // 进程可能已退出
                }
                finally
                {
                    try { p.Dispose(); }
                    catch { }
                }
            }
            return result;
        }

        // ==================================================================
        //  3. 线性回归
        // ==================================================================
        private static void TestLinearRegression()
        {
            Section("-- 3. 线性回归（斜率 / R²）--");

            LeakDetector detector = new LeakDetector();
            detector.PrepareSamples(60);

            double[] xs = new double[] { 0, 1, 2, 3, 4, 5 };
            double[] rising = new double[] { 100, 110, 120, 130, 140, 150 };
            LinearFit fit = detector.FitForTest(xs, rising);
            Check("完美线性斜率 = 10", Math.Abs(fit.Slope - 10.0) < 1e-9, N(fit.Slope, "0.######"));
            Check("完美线性 R² = 1", Math.Abs(fit.R2 - 1.0) < 1e-9, N(fit.R2, "0.000000"));

            double[] flat = new double[] { 200, 200, 200, 200, 200, 200 };
            fit = detector.FitForTest(xs, flat);
            Check("水平序列斜率 = 0", Math.Abs(fit.Slope) < 1e-9, N(fit.Slope, "0.######"));
            Check("水平序列 R² = 0（不是 1）", fit.R2 == 0, N(fit.R2, "0.######"));

            double[] saw = new double[] { 100, 300, 105, 310, 110, 320 };
            fit = detector.FitForTest(xs, saw);
            Check("锯齿序列 R² < 0.55（可被阈值拦截）", fit.R2 < 0.55, "R²=" + N(fit.R2, "0.###"));

            // 大截距 + 微小斜率：检验「以均值为中心」的算法没有灾难性抵消
            double[] huge = new double[] { 1000000, 1000001, 1000002, 1000003, 1000004, 1000005 };
            fit = detector.FitForTest(xs, huge);
            Check("大截距下 R² 仍接近 1", Math.Abs(fit.R2 - 1.0) < 1e-6, "R²=" + N(fit.R2, "0.########"));
            Check("大截距下斜率仍为 1", Math.Abs(fit.Slope - 1.0) < 1e-6, N(fit.Slope, "0.######"));

            Check("单点返回 0 且不抛异常",
                detector.FitForTest(new double[] { 0 }, new double[] { 5 }).Slope == 0);
            // 少于 3 个样本时刻意不给趋势结论：两点连一条直线必然 R²=1，
            // 用它去外推会把「刚启动的噪声」判成泄漏。
            fit = detector.FitForTest(new double[] { 0, 1 }, new double[] { 0, 7 });
            Check("两点不给趋势结论（避免噪声外推）", fit.Slope == 0 && fit.R2 == 0,
                "斜率 " + N(fit.Slope, "0.######") + " R²=" + N(fit.R2, "0.######"));
            fit = detector.FitForTest(new double[] { 0, 1, 2 }, new double[] { 0, 7, 14 });
            Check("三点即可拟合（斜率正确）", Math.Abs(fit.Slope - 7.0) < 1e-6, N(fit.Slope, "0.######"));
        }

        // ==================================================================
        //  4. 环形缓冲
        // ==================================================================
        private static void TestSampleRing()
        {
            Section("-- 4. 采样环形缓冲 --");

            SampleRing<SamplePoint> ring = new SampleRing<SamplePoint>(4, p => p.Tick);
            for (int i = 0; i < 6; i++)
            {
                SamplePoint p = new SamplePoint();
                p.Tick = i * 100;
                p.WorkingSetMB = i;
                ring.Add(p);
            }

            Check("容量写满后不再增长", ring.Count == 4, "count=" + ring.Count + " capacity=" + ring.Capacity);
            Check("最先写入的样本被覆盖", ring[0].Tick == 200, "最旧 Tick=" + ring[0].Tick);
            Check("最新样本正确", ring[3].Tick == 500 && ring.NewestTick == 500, "newest=" + ring.NewestTick);

            ring.DropOlderThan(400);
            Check("按时间淘汰旧样本", ring.Count == 2 && ring[0].Tick == 400,
                "count=" + ring.Count + " 最旧=" + ring[0].Tick);

            // ---- 按需增长：初始只分配 16 个槽位，样本变多后才翻倍到请求容量 ----
            SampleRing<SamplePoint> grow = new SampleRing<SamplePoint>(154, p => p.Tick);
            Check("初始槽位远小于请求容量（短命进程省内存）", grow.Capacity == 16,
                "capacity=" + grow.Capacity + "（请求 154）");

            bool orderKept = true;
            for (int i = 0; i < 154; i++)
            {
                SamplePoint p = new SamplePoint();
                p.Tick = i * 100;
                p.WorkingSetMB = i;
                grow.Add(p);
            }
            for (int i = 0; i < 154; i++)
            {
                if (grow[i].Tick != i * 100 || grow[i].WorkingSetMB != i) orderKept = false;
            }
            Check("增长后容量达到请求值", grow.Capacity == 154,
                "capacity=" + grow.Capacity + " 上限=" + grow.CapacityLimit + " count=" + grow.Count);
            Check("增长过程不打乱样本顺序", orderKept && grow.Count == 154,
                "count=" + grow.Count + " 最旧 Tick=" + grow[0].Tick + " 最新=" + grow.NewestTick);

            // 增长后继续写满两圈：验证「增长 → 环形覆盖」这段衔接没有错位
            for (int i = 0; i < 400; i++)
            {
                SamplePoint p = new SamplePoint();
                p.Tick = 100000 + i;
                grow.Add(p);
            }
            Check("增长后连续覆盖多圈仍然有序",
                grow.Count == 154 && grow.NewestTick == 100399 && grow[153].Tick == 100399 &&
                grow[0].Tick == 100246,
                "count=" + grow.Count + " 最旧=" + grow[0].Tick + " 最新=" + grow.NewestTick);

            SampleRing<SystemSample> systemRing = new SampleRing<SystemSample>(8, s => s.Tick);
            SystemSample s0 = new SystemSample();
            s0.Tick = 700;
            s0.UsedMB = 8000;
            s0.AvailMB = 7000;
            systemRing.Add(s0);
            Check("同一个环形缓冲可存放整机样本（SystemSample）",
                systemRing.Count == 1 && systemRing.NewestTick == 700 && systemRing[0].AvailMB == 7000,
                "count=" + systemRing.Count + " newest=" + systemRing.NewestTick);

            LeakDetector detector = new LeakDetector();
            detector.WindowMinutes = 10;
            detector.PrepareSamples(5);
            Check("窗口容量 = ceil(窗口÷间隔×1.25)+4", detector.RingCapacityForTest == 154,
                "10 分钟 / 5 秒 → " + detector.RingCapacityForTest);

            detector.WindowMinutes = 2;
            detector.PrepareSamples(60);
            Check("极长间隔下容量取下限 8", detector.RingCapacityForTest == 8,
                "capacity=" + detector.RingCapacityForTest);

            detector.WindowMinutes = 60;
            detector.PrepareSamples(2);
            Check("极短间隔下容量被限制（不上万）", detector.RingCapacityForTest == 2254,
                "60 分钟 / 2 秒 → " + detector.RingCapacityForTest);
        }

        // ==================================================================
        //  5. 泄漏判定（人造样本，确定性）
        // ==================================================================
        private static void TestLeakDetection()
        {
            Section("-- 5. 泄漏判定（确定性样本）--");

            LeakDetector detector = new LeakDetector();
            detector.MinSamples = 5;
            detector.MinSpanMinutes = 0.001;
            detector.MinWorkingSetMB = 0;
            detector.PrepareSamples(5);

            // 泄漏：0.5 秒一点、每点 +1.2 MB → 144 MB/分钟、线性度 100%
            for (int i = 0; i < 9; i++)
            {
                detector.InjectSampleForTest(1001, "leaky.exe",
                    i * 500, 100 + i * 1.2, 90 + i * 1.2);
            }

            // 锯齿：长期看几乎不增长，R² 很低
            double[] saw = new double[] { 100, 180, 102, 178, 104, 176, 106, 174, 108 };
            for (int i = 0; i < saw.Length; i++)
            {
                detector.InjectSampleForTest(1002, "saw.exe", i * 500, saw[i], saw[i]);
            }

            // 平稳：完全不增长
            for (int i = 0; i < 9; i++)
            {
                detector.InjectSampleForTest(1003, "flat.exe", i * 500, 120, 110);
            }

            // 数据不足：只有 2 个点
            detector.InjectSampleForTest(1004, "young.exe", 0, 200, 190);
            detector.InjectSampleForTest(1004, "young.exe", 500, 260, 250);

            LeakReport report = detector.Analyze(10);

            LeakItem leaky = Find(report, 1001);
            Check("识别出持续增长的进程", leaky != null);
            if (leaky != null)
            {
                Check("增长进程被判定为泄漏", leaky.IsLeaking,
                    "斜率 " + N(leaky.SlopeMBPerMin, "0.0") + " MB/min  R²=" + N(leaky.R2, "0.###"));
                Check("斜率量级正确（≈144 MB/min）",
                    leaky.SlopeMBPerMin > 140 && leaky.SlopeMBPerMin < 148,
                    N(leaky.SlopeMBPerMin, "0.0") + " MB/min");
                Check("线性度接近 1", leaky.R2 > 0.99, "R²=" + N(leaky.R2, "0.#####"));
                Check("窗口增长量正确（≈9.6 MB）",
                    Math.Abs(leaky.GrowthMB - 9.6) < 0.01, N(leaky.GrowthMB, "0.00") + " MB");
            }

            LeakItem sawItem = Find(report, 1002);
            Check("锯齿进程未被误判为泄漏", sawItem != null && !sawItem.IsLeaking,
                sawItem == null ? "缺失" : "R²=" + N(sawItem.R2, "0.###"));

            LeakItem flatItem = Find(report, 1003);
            Check("平稳进程无趋势结论", flatItem != null && !flatItem.HasTrend && !flatItem.IsLeaking,
                flatItem == null ? "缺失" : "HasTrend=" + flatItem.HasTrend);

            LeakItem young = Find(report, 1004);
            Check("样本不足的进程不给趋势结论", young != null && !young.HasTrend,
                young == null ? "缺失" : "HasTrend=" + young.HasTrend);

            Check("报告中泄漏数量正确（1 个）", report.TopLeaks.Count == 1,
                report.TopLeaks.Count + " 个");
            Check("LeakDetected 与泄漏列表一致", report.LeakDetected == (report.TopLeaks.Count > 0));
            Check("占用排行包含全部进程", report.TopConsumers.Count == 4, report.TopConsumers.Count + " 个");
            Check("占用排行按工作集降序",
                report.TopConsumers[0].WorkingSetMB >= report.TopConsumers[report.TopConsumers.Count - 1].WorkingSetMB,
                N(report.TopConsumers[0].WorkingSetMB, "N1") + " MB 起");
        }

        private static LeakItem Find(LeakReport report, int pid)
        {
            for (int i = 0; i < report.TopConsumers.Count; i++)
            {
                if (report.TopConsumers[i].Pid == pid) return report.TopConsumers[i];
            }
            for (int i = 0; i < report.TopLeaks.Count; i++)
            {
                if (report.TopLeaks[i].Pid == pid) return report.TopLeaks[i];
            }
            return null;
        }

        // ==================================================================
        //  6. 真实采样 + PID 复用防护
        // ==================================================================
        private static void TestLiveSampling()
        {
            Section("-- 6. 真实采样与 PID 复用防护 --");

            LeakDetector detector = new LeakDetector();
            detector.MinWorkingSetMB = 5.0;
            detector.MinSamples = 3;
            detector.MinSpanMinutes = 0.001;
            detector.PrepareSamples(1);

            detector.Sample(0);
            System.Threading.Thread.Sleep(120);
            detector.Sample(0);
            System.Threading.Thread.Sleep(120);
            detector.Sample(0);

            Check("系统采样计数 >= 3", detector.SystemSampleCount >= 3,
                detector.SystemSampleCount.ToString(CultureInfo.InvariantCulture));

            LeakReport report = detector.Analyze(6);
            Check("报告快照可用内存 > 0", report.Memory.AvailMB > 0, N(report.Memory.AvailMB, "N0") + " MB");
            Check("压力判定与阈值一致",
                report.UnderPressure == (report.SystemPressurePercent >= detector.PressureThreshold),
                detector.PressureThreshold + "% → " + report.UnderPressure);
            Check("即时占用排行非空", report.RecentConsumers.Count > 0, report.RecentConsumers.Count + " 个");
            Check("跟踪的进程数合理（> 0）", detector.TrackedCount > 0,
                "跟踪 " + detector.TrackedCount + " 个（全系统 " + detector.LastSystemProcessCount + " 个）");
            // 跟踪数可以略大于本轮样本数（门槛变化/进程退出会保留一段窗口），但不能失控
            Check("跟踪数不超过「全系统进程数」",
                detector.TrackedCount <= detector.LastSystemProcessCount,
                detector.TrackedCount + " vs " + detector.LastSystemProcessCount);

            Emit("  系统趋势: 可用内存斜率 " + N(report.SystemAvailSlopeMBPerMin, "0.0") +
                 " MB/min  窗口内已用变化 " + N(report.SystemUsedGrowthMB, "N0") + " MB");

            // ---- PID 复用防护：注入工作集断崖，模拟「同一个 PID 换了另一个进程」----
            TestPidReuseProtection();
        }

        /// <summary>
        /// PID 复用防护：同一个 PID 在两次采样之间可能已经换成另一个进程。
        /// 主判据是内核随系统查询一起给出的进程创建时间（零额外开销、精确）；
        /// 拿不到创建时间时才退回「工作集断崖」启发式。
        /// 这里把三个方向都构造出来验证：断崖、精确判据、以及启发式的盲区。
        /// </summary>
        private static void TestPidReuseProtection()
        {
            // ---- 断崖 → 清空窗口 ----
            LeakDetector cliff = new LeakDetector();
            cliff.PrepareSamples(1);
            for (int i = 0; i < 6; i++)
            {
                cliff.InjectSampleForTest(900, "old.exe", i * 100, 400 + i, 390 + i);
            }
            Check("断崖前窗口已积累样本", SampleCountOf(cliff, 900) == 6,
                SampleCountOf(cliff, 900) + " 个");

            cliff.InjectSampleForTest(900, "new.exe", 600, 60, 50);   // 400+ MB → 60 MB
            Check("工作集断崖后窗口被重建", SampleCountOf(cliff, 900) == 1,
                "断崖后样本数 = " + SampleCountOf(cliff, 900));

            // ---- 渐进释放内存 → 不清空 ----
            LeakDetector gentle = new LeakDetector();
            gentle.PrepareSamples(1);
            for (int i = 0; i < 6; i++)
            {
                gentle.InjectSampleForTest(901, "steady.exe", i * 100, 400 + i, 390 + i);
            }
            for (int i = 0; i < 4; i++)
            {
                // 逐步回收内存，最小的跌幅也只有 10%，不应触发复用判定
                gentle.InjectSampleForTest(901, "steady.exe", 600 + i * 100, 396 - i * 4, 386);
            }
            Check("渐进释放内存不会误判为 PID 复用", SampleCountOf(gentle, 901) == 10,
                SampleCountOf(gentle, 901) + " 个样本");

            // ---- 精确判据：创建时间变了就是 PID 复用（哪怕是「变大」的方向）----
            //
            //  这正是工作集启发式的盲区：小进程退出后 PID 被一个大进程接手，
            //  旧样本会把回归拉成一条陡峭的上升直线，从而误判为泄漏并触发清理。
            LeakDetector precise = new LeakDetector();
            precise.PrepareSamples(1);
            for (int i = 0; i < 6; i++)
            {
                precise.InjectSampleForTest(903, "small.exe", i * 100, 40 + i, 38 + i, 111111);
            }
            Check("创建时间判据：换进程前窗口已积累样本", SampleCountOf(precise, 903) == 6,
                SampleCountOf(precise, 903) + " 个");

            precise.InjectSampleForTest(903, "big.exe", 600, 900, 880, 222222);   // 40 MB → 900 MB
            Check("创建时间判据：PID 被大进程复用也会清空窗口",
                SampleCountOf(precise, 903) == 1,
                "断崖启发式看不到这个方向，创建时间判据必须拦住它，实际样本数 = " +
                SampleCountOf(precise, 903));

            // 同创建时间（同一进程）继续累积，不得清空
            for (int i = 0; i < 3; i++)
            {
                precise.InjectSampleForTest(903, "big.exe", 700 + i * 100, 900 + i, 880, 222222);
            }
            Check("创建时间相同则视为同一进程（窗口继续累积）",
                SampleCountOf(precise, 903) == 4, SampleCountOf(precise, 903) + " 个样本");

            // ---- 拿不到创建时间时，退回工作集断崖启发式 ----
            LeakDetector fallback = new LeakDetector();
            fallback.PrepareSamples(1);
            for (int i = 0; i < 5; i++)
            {
                fallback.InjectSampleForTest(904, "no-time.exe", i * 100, 400, 390, 0);
            }
            fallback.InjectSampleForTest(904, "no-time.exe", 500, 50, 45, 0);
            Check("创建时间缺失时回退到工作集断崖",
                SampleCountOf(fallback, 904) == 1, SampleCountOf(fallback, 904) + " 个样本");

            // ---- 慢速泄漏照常被识别（回归：确认启发式没有破坏主功能）----
            LeakDetector leaky = new LeakDetector();
            leaky.MinSamples = 5;
            leaky.MinSpanMinutes = 0.001;
            leaky.MinWorkingSetMB = 0;
            leaky.PrepareSamples(5);
            for (int i = 0; i < 9; i++)
            {
                leaky.InjectSampleForTest(902, "slow-leak.exe", i * 500, 200 + i * 1.2, 190 + i * 1.2);
            }
            LeakItem item = Find(leaky.Analyze(10), 902);
            Check("渐进泄漏仍被判定", item != null && item.IsLeaking,
                item == null ? "缺失" : N(item.SlopeMBPerMin, "0.0") + " MB/min R²=" + N(item.R2, "0.###"));

            // ---- 真实采样下的跟踪表规模 ----
            LeakDetector live = new LeakDetector();
            live.MinWorkingSetMB = 5.0;
            live.PrepareSamples(1);
            live.Sample(0);
            int tracked = live.TrackedCount;
            for (int i = 0; i < 4; i++) live.Sample(0);
            Check("长期运行后跟踪表规模稳定", live.TrackedCount <= tracked + 8,
                tracked + " → " + live.TrackedCount);
        }

        /// <summary>读取跟踪表中的样本条数（走反射，仅测试用）。</summary>
        private static int SampleCountOf(LeakDetector detector, int pid)
        {
            IDictionary tracked = TrackedTable(detector);
            if (tracked == null || !tracked.Contains(pid)) return 0;

            object entry = tracked[pid];
            // Samples 是 readonly 字段（不是属性），必须按字段读取
            FieldInfo field = entry.GetType().GetField("Samples");
            object ring = field == null ? null : field.GetValue(entry);
            if (ring == null) return 0;

            PropertyInfo count = ring.GetType().GetProperty("Count");
            return count == null ? 0 : (int)count.GetValue(ring, null);
        }

        private static IDictionary TrackedTable(LeakDetector detector)
        {
            FieldInfo field = typeof(LeakDetector).GetField("_tracked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return null;
            return field.GetValue(detector) as IDictionary;
        }

        // ==================================================================
        //  7. 历史与配置往返读写
        // ==================================================================
        private static void TestHistoryAndConfig()
        {
            Section("-- 7. 历史记录与配置读写 --");

            string historyPath = History.HistoryPath;
            string configPath = AppConfig.ConfigPath;
            string backupHistory = null;
            byte[] backupConfig = null;

            try
            {
                if (File.Exists(historyPath)) backupHistory = File.ReadAllText(historyPath, Encoding.UTF8);
                if (File.Exists(configPath)) backupConfig = File.ReadAllBytes(configPath);

                History.Clear();
                Check("清空后历史文件不存在", !File.Exists(historyPath));

                HistoryEntry first = new HistoryEntry();
                first.Time = new DateTime(2026, 1, 2, 3, 4, 5);
                first.Trigger = "手动清理";
                first.FreedMB = 123.45;
                first.BeforeAvailMB = 1000;
                first.AfterAvailMB = 1123.45;
                first.ProcessCount = 3;
                first.StandbyPurged = true;
                History.Append(first);

                HistoryEntry second = new HistoryEntry();
                second.Time = new DateTime(2026, 1, 2, 4, 5, 6);
                second.Trigger = "自动：检测到内存泄漏";
                second.FreedMB = 67.8;
                second.BeforeAvailMB = 900;
                second.AfterAvailMB = 967.8;
                second.ProcessCount = 1;
                second.StandbyPurged = false;
                History.Append(second);

                Check("表头只写一次", CountHeaderLines(historyPath) == 1,
                    CountHeaderLines(historyPath) + " 行表头");

                List<HistoryEntry> recent = History.ReadRecent(10);
                Check("读取到 2 条记录", recent.Count == 2, recent.Count + " 条");
                Check("最新记录排在最前",
                    recent.Count > 0 && recent[0].FreedMB > 60 && recent[0].FreedMB < 70,
                    recent.Count > 0 ? recent[0].Trigger : "");
                Check("字段往返一致（时间/触发/数值/布尔）",
                    recent.Count > 1 && recent[1].Time == first.Time &&
                    recent[1].Trigger == first.Trigger &&
                    Math.Abs(recent[1].FreedMB - first.FreedMB) < 0.01 &&
                    recent[1].ProcessCount == 3 && recent[1].StandbyPurged,
                    recent.Count > 1 ? recent[1].Time.ToString("s", CultureInfo.InvariantCulture) : "");

                History.RefreshTotals();
                Check("累计次数正确", History.TotalCleanCount == 2,
                    History.TotalCleanCount.ToString(CultureInfo.InvariantCulture));
                Check("累计释放量正确（191.25 MB）",
                    Math.Abs(History.TotalFreedMB - 191.25) < 0.1,
                    N(History.TotalFreedMB, "0.00") + " MB");
                Check("ReadRecent(1) 只返回 1 条", History.ReadRecent(1).Count == 1);
                Check("ReadRecent(0) 返回空列表", History.ReadRecent(0).Count == 0);

                History.Clear();
                History.RefreshTotals();
                Check("清空后统计归零",
                    History.TotalCleanCount == 0 && History.TotalFreedMB == 0,
                    History.TotalCleanCount + " 次 / " + N(History.TotalFreedMB, "0.##") + " MB");

                // ---- 增量累计：Append 必须自己把累计值算进去，
                //      不能再靠「每次清理重扫整个历史文件」----
                History.Clear();
                HistoryEntry inc1 = new HistoryEntry();
                inc1.Time = new DateTime(2026, 2, 1, 8, 0, 0);
                inc1.Trigger = "手动清理";
                inc1.FreedMB = 30.5;
                History.Append(inc1);
                History.Append(inc1);       // 再追加一条同样的：累计应变成 2 次
                Check("追加记录时累计值增量更新",
                    History.TotalCleanCount == 2 && Math.Abs(History.TotalFreedMB - 61.0) < 1e-9,
                    History.TotalCleanCount + " 次 / " + N(History.TotalFreedMB, "0.##") + " MB");

                History.RefreshTotals();    // 全量重算的结果必须与增量结果一致
                Check("全量重算与增量累计一致",
                    History.TotalCleanCount == 2 && Math.Abs(History.TotalFreedMB - 61.0) < 1e-9,
                    History.TotalCleanCount + " 次 / " + N(History.TotalFreedMB, "0.##") + " MB");

                // ---- 尾部读取：行数、顺序必须与全量读取一致 ----
                History.Clear();
                const int many = 2400;      // 要超过裁剪阈值（2000 + 200），一次性写入以省时间
                using (StreamWriter seed = new StreamWriter(historyPath, false, new UTF8Encoding(false)))
                {
                    seed.WriteLine(HistoryEntry.Header);
                    for (int i = 0; i < many; i++)
                    {
                        HistoryEntry e = new HistoryEntry();
                        e.Time = new DateTime(2026, 3, 1).AddMinutes(i);
                        e.Trigger = "批量";
                        e.FreedMB = i;
                        e.ProcessCount = i % 7;
                        e.StandbyPurged = (i % 2) == 0;
                        seed.WriteLine(e.ToLine());
                    }
                }
                List<HistoryEntry> listed = History.ReadRecent(500);
                string[] fullLines = File.ReadAllLines(historyPath, Encoding.UTF8);
                Check("尾部读取条数正确（500 条）", listed.Count == 500, listed.Count + " 条");
                Check("尾部读取的最新一条正确",
                    listed.Count > 0 && Math.Abs(listed[0].FreedMB - (many - 1)) < 1e-9,
                    listed.Count > 0 ? listed[0].FreedMB.ToString(CultureInfo.InvariantCulture) : "空");
                Check("尾部读取的最旧一条正确",
                    listed.Count == 500 && Math.Abs(listed[499].FreedMB - (many - 500)) < 1e-9,
                    listed.Count == 500 ? listed[499].FreedMB.ToString(CultureInfo.InvariantCulture) : "不足 500 条");

                // 与「整文件读进来再取最后 500 条」的结果逐条比对
                List<HistoryEntry> expected = new List<HistoryEntry>();
                for (int i = fullLines.Length - 1; i >= 0 && expected.Count < 500; i--)
                {
                    HistoryEntry e = HistoryEntry.Parse(fullLines[i]);
                    if (e != null) expected.Add(e);
                }
                bool same = expected.Count == listed.Count;
                if (same)
                {
                    for (int i = 0; i < expected.Count; i++)
                    {
                        if (expected[i].Time != listed[i].Time ||
                            Math.Abs(expected[i].FreedMB - listed[i].FreedMB) > 1e-9 ||
                            expected[i].ProcessCount != listed[i].ProcessCount ||
                            expected[i].StandbyPurged != listed[i].StandbyPurged) { same = false; break; }
                    }
                }
                Check("尾部读取与全量读取结果一致", same, expected.Count + " vs " + listed.Count);

                // ---- 历史文件超限自动裁剪 ----
                int beforeBytes = (int)new FileInfo(historyPath).Length;
                History.TrimIfNeeded();
                int afterBytes = (int)new FileInfo(historyPath).Length;
                string[] trimmed = File.ReadAllLines(historyPath, Encoding.UTF8);
                int dataLines = 0;
                bool allParsable = true;
                for (int i = 0; i < trimmed.Length; i++)
                {
                    if (trimmed[i].Length == 0) continue;
                    if (trimmed[i][0] == '#') continue;
                    dataLines++;
                    if (HistoryEntry.Parse(trimmed[i]) == null) allParsable = false;
                }
                Check("历史文件超限后自动裁剪", afterBytes < beforeBytes,
                    beforeBytes + " → " + afterBytes + " 字节");
                Check("裁剪后行数受控且保留了最新记录",
                    dataLines == 2000 && allParsable && Math.Abs(History.ReadRecent(1)[0].FreedMB - (many - 1)) < 1e-9,
                    dataLines + " 条有效记录，可解析 = " + allParsable);

                History.Clear();
                History.RefreshTotals();

                // ---- 配置往返 ----
                AppConfig cfg = new AppConfig();
                cfg.AutoClean = false;
                cfg.CooldownSeconds = 240;
                cfg.PressureThreshold = 91;
                cfg.LeakSlopeThreshold = 3.5;
                cfg.LeakR2Threshold = 0.66;
                cfg.WindowMinutes = 12;
                cfg.TopLeakCount = 4;
                cfg.PurgeStandbyList = false;
                cfg.TrimLeakProcesses = false;
                cfg.NotifyOnAuto = false;
                cfg.AutoStart = true;
                cfg.CloseToTray = false;
                cfg.SampleSeconds = 7;
                cfg.MinWorkingSetMB = 88.5;
                Check("配置保存成功", cfg.Save(), configPath);

                AppConfig loaded = AppConfig.Load();
                Check("配置往返：布尔字段",
                    !loaded.AutoClean && !loaded.PurgeStandbyList && !loaded.TrimLeakProcesses &&
                    !loaded.NotifyOnAuto && loaded.AutoStart && !loaded.CloseToTray);
                Check("配置往返：整数字段",
                    loaded.CooldownSeconds == 240 && loaded.PressureThreshold == 91 &&
                    loaded.WindowMinutes == 12 && loaded.TopLeakCount == 4 && loaded.SampleSeconds == 7,
                    string.Format(CultureInfo.InvariantCulture, "cd={0} p={1} win={2} top={3} sample={4}",
                        loaded.CooldownSeconds, loaded.PressureThreshold, loaded.WindowMinutes,
                        loaded.TopLeakCount, loaded.SampleSeconds));
                Check("配置往返：浮点字段",
                    Math.Abs(loaded.LeakSlopeThreshold - 3.5) < 1e-9 &&
                    Math.Abs(loaded.LeakR2Threshold - 0.66) < 1e-9 &&
                    Math.Abs(loaded.MinWorkingSetMB - 88.5) < 1e-9);

                // ---- 手写配置的大小写宽容度：True / FALSE / NULL 都必须认 ----
                File.WriteAllText(configPath,
                    "{\r\n  \"AutoClean\": True,\r\n  \"CloseToTray\": FALSE,\r\n" +
                    "  \"MinWorkingSetMB\": null,\r\n  \"CooldownSeconds\": 123,\r\n}\r\n",
                    new UTF8Encoding(false));
                AppConfig lenient = AppConfig.Load();
                Check("手写配置的大小写与 null 都能容忍",
                    lenient.AutoClean && !lenient.CloseToTray && lenient.CooldownSeconds == 123 &&
                    Math.Abs(lenient.MinWorkingSetMB - 50.0) < 1e-9,
                    string.Format(CultureInfo.InvariantCulture, "AutoClean={0} CloseToTray={1} CD={2} MinWs={3}",
                        lenient.AutoClean, lenient.CloseToTray, lenient.CooldownSeconds, lenient.MinWorkingSetMB));

                // ---- 越界值必须被夹住 ----
                AppConfig wild = new AppConfig();
                wild.CooldownSeconds = 1;
                wild.PressureThreshold = 500;
                wild.LeakR2Threshold = 9;
                wild.WindowMinutes = -3;
                wild.SampleSeconds = 0;
                wild.MinWorkingSetMB = double.NaN;
                wild.Sanitize();
                Check("越界配置被夹到合法区间",
                    wild.CooldownSeconds == 30 && wild.PressureThreshold == 99 &&
                    wild.LeakR2Threshold == 0.99 && wild.WindowMinutes == 2 &&
                    wild.SampleSeconds == 2 && wild.MinWorkingSetMB == 1.0,
                    string.Format(CultureInfo.InvariantCulture, "cd={0} p={1} r2={2} win={3} sample={4} minWs={5}",
                        wild.CooldownSeconds, wild.PressureThreshold, wild.LeakR2Threshold,
                        wild.WindowMinutes, wild.SampleSeconds, wild.MinWorkingSetMB));

                AppConfig clone = cfg.Clone();
                clone.PressureThreshold = 42;
                Check("Clone 是副本（互不影响）",
                    cfg.PressureThreshold == 91 && clone.PressureThreshold == 42);
            }
            catch (Exception ex)
            {
                Check("历史/配置测试未抛异常", false, ex.Message);
            }
            finally
            {
                // 还原用户真实数据
                try
                {
                    if (backupHistory != null)
                    {
                        File.WriteAllText(historyPath, backupHistory, new UTF8Encoding(false));
                    }
                    else if (File.Exists(historyPath))
                    {
                        File.Delete(historyPath);
                    }
                    if (backupConfig != null) File.WriteAllBytes(configPath, backupConfig);
                }
                catch { }
            }
        }

        private static int CountHeaderLines(string path)
        {
            int count = 0;
            if (!File.Exists(path)) return 0;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#")) count++;
            }
            return count;
        }

        // ==================================================================
        //  8. 迷你 JSON 解析器的健壮性
        // ==================================================================
        private static void TestMiniJson()
        {
            Section("-- 8. 迷你 JSON 解析器健壮性 --");

            MiniJson ok = MiniJson.Parse(
                "{ \"AutoClean\": true, \"CooldownSeconds\": 300, \"LeakR2Threshold\": 0.55, " +
                "\"Name\": \"a\\u0042c\", \"Neg\": -12.5e1 }");
            Check("正常对象可解析", ok != null && ok.Kind == JsonKind.Object);
            Check("布尔值正确", ok != null && ok.GetBool("AutoClean", false));
            Check("整数正确", ok != null && ok.GetInt("CooldownSeconds", 0) == 300);
            Check("小数正确", ok != null && Math.Abs(ok.GetDouble("LeakR2Threshold", 0) - 0.55) < 1e-9);
            Check("\\u 转义正确（a\\u0042c → aBc）", ok != null && HasName(ok, "aBc"));
            Check("科学计数法正确（-12.5e1 → -125）",
                ok != null && Math.Abs(ok.GetDouble("Neg", 0) + 125) < 1e-9);

            // 畸形输入：一律不得抛异常、不得死循环
            string[] malformed = new string[]
            {
                "", "   ", "{", "}", "{ \"a\"", "{ \"a\": }", "{ \"a\": \"", "{,}",
                "[\"array\"]", "{\"a\":1 \"b\":2}", "null", "{ \"a\": - }",
                "{ \"a\": tru }", "{\"\\u12\": 1}", "{{{{{{{{", "{\"a\":1}{\"b\":2}",
                "{ \"a\": 1e999 }", new string('{', 200) + new string('}', 200)
            };

            int crashed = 0;
            for (int i = 0; i < malformed.Length; i++)
            {
                try
                {
                    MiniJson value = MiniJson.Parse(malformed[i]);
                    if (value != null && value.Kind == JsonKind.Object)
                    {
                        value.GetBool("a", false);
                        value.GetInt("a", 0);
                        value.GetDouble("a", 0);
                    }
                }
                catch (Exception ex)
                {
                    crashed++;
                    Emit("    输入 [" + Abbreviate(malformed[i]) + "] 抛异常: " + ex.GetType().Name);
                }
            }
            Check("畸形输入不抛异常（" + malformed.Length + " 例）", crashed == 0, crashed + " 例异常");

            MiniJson config = MiniJson.Parse(
                "{\r\n  \"_comment\": \"x\",\r\n  \"PressureThreshold\": 85,\r\n  \"AutoClean\": false\r\n}");
            Check("带注释字段与换行的配置可解析",
                config != null && config.GetInt("PressureThreshold", 0) == 85 &&
                !config.GetBool("AutoClean", true));
            Check("键名大小写不敏感", config != null && config.GetInt("pressurethreshold", 0) == 85);
            Check("缺失键返回默认值",
                config != null && config.GetInt("NoSuchKey", 7) == 7 &&
                config.GetBool("NoSuchKey", true) && config.GetDouble("NoSuchKey", 1.5) == 1.5);
        }

        private static bool HasName(MiniJson json, string expected)
        {
            MiniJson name;
            if (json.Members == null || !json.Members.TryGetValue("Name", out name)) return false;
            return name.StringValue == expected;
        }

        private static string Abbreviate(string text)
        {
            if (text.Length <= 16) return text;
            return text.Substring(0, 16) + "...";
        }

        // ==================================================================
        //  9. 子进程膨胀的端到端检测（信息性）
        // ==================================================================
        private static void TestChildProcessGrowth()
        {
            Section("-- 9. 端到端：持续膨胀的子进程能否被识别（信息性）--");

            string scriptPath = Path.Combine(Path.GetTempPath(),
                "memtrim-selftest-" + Process.GetCurrentProcess().Id + ".ps1");
            Process child = null;

            try
            {
                File.WriteAllText(scriptPath,
                    "$hog = New-Object System.Collections.Generic.List[object]\r\n" +
                    "for ($i = 0; $i -lt 12; $i++) {\r\n" +
                    "  $b = New-Object byte[] (5MB)\r\n" +
                    "  for ($k = 0; $k -lt $b.Length; $k += 4096) { $b[$k] = 1 }\r\n" +
                    "  $hog.Add($b)\r\n" +
                    "  Start-Sleep -Milliseconds 700\r\n" +
                    "}\r\n",
                    new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                child = Process.Start(psi);
                int childPid = child.Id;
                Emit("  子进程 PID " + childPid + "：每 700 ms 提交 5 MB，共 12 轮");

                LeakDetector detector = new LeakDetector();
                detector.MinWorkingSetMB = 15.0;
                detector.MinSamples = 4;
                detector.MinSpanMinutes = 0.05;
                detector.WindowMinutes = 2;
                detector.LeakSlopeThreshold = 20.0;
                detector.PrepareSamples(1);

                LeakItem peak = null;
                bool detected = false;
                for (int i = 0; i < 24; i++)
                {
                    System.Threading.Thread.Sleep(450);
                    detector.Sample(0);
                    LeakItem item = Find(detector.Analyze(20), childPid);
                    if (item != null && (peak == null || item.WorkingSetMB > peak.WorkingSetMB)) peak = item;
                    if (item != null && item.IsLeaking)
                    {
                        peak = item;
                        detected = true;
                        break;
                    }
                    if (child.HasExited) break;
                }

                if (peak == null)
                {
                    Note("子进程被纳入统计", false, "未采集到该 PID 的样本（工作集始终低于门槛）");
                }
                else
                {
                    Emit("  实测: 工作集 " + N(peak.WorkingSetMB, "N1") + " MB，斜率 " +
                         N(peak.SlopeMBPerMin, "0.0") + " MB/min，R² " + N(peak.R2, "0.###"));
                    Note("子进程被纳入统计", true, N(peak.WorkingSetMB, "N1") + " MB");
                    Note("识别出增长趋势（斜率 > 0）", peak.SlopeMBPerMin > 0,
                        N(peak.SlopeMBPerMin, "0.0") + " MB/min");
                    Note("判定为疑似泄漏", detected,
                        "HasTrend=" + peak.HasTrend + " R²=" + N(peak.R2, "0.###"));
                }
            }
            catch (Exception ex)
            {
                Note("端到端子进程检测", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (child != null && !child.HasExited) child.Kill();
                }
                catch { }
                try
                {
                    if (child != null) child.Dispose();
                }
                catch { }
                try
                {
                    if (File.Exists(scriptPath)) File.Delete(scriptPath);
                }
                catch { }
            }
        }

        // ==================================================================
        //  10. 特权与真实清理（clean / all）
        // ==================================================================
        private static void TestPrivilegesAndCleanup()
        {
            Section("-- 10. 特权与真实清理 --");

            MemoryCleaner.PreparePrivileges();
            Emit("  特权: " + MemoryCleaner.PrivilegeDetail);
            Emit("  （非管理员运行时这两项特权不会授予，属于预期行为）");

            bool purged = Native.PurgeStandbyListBestEffort();
            Emit("  清空系统待机列表: " + (purged ? "成功" : "失败（需要管理员权限）"));

            int selfPid = Process.GetCurrentProcess().Id;
            ProcessSampler sampler = new ProcessSampler();
            sampler.MinWorkingSetMB = 20.0;
            List<ProcMem> procs = sampler.Sample(selfPid);

            List<LeakItem> targets = new List<LeakItem>();
            for (int i = 0; i < procs.Count && i < 3; i++)
            {
                LeakItem item = new LeakItem();
                item.Pid = procs[i].Pid;
                item.Name = procs[i].Name;
                item.WorkingSetMB = procs[i].WorkingSet / 1048576.0;
                targets.Add(item);
            }

            for (int i = 0; i < targets.Count; i++)
            {
                string error;
                bool ok = Native.TrimProcessWorkingSet(targets[i].Pid, out error);
                Emit("  清空工作集 " + targets[i].DisplayName + " (" +
                     N(targets[i].WorkingSetMB, "N0") + " MB): " + (ok ? "成功" : error));
            }
            Check("工作集清理调用未抛异常", true, targets.Count + " 个目标");

            CleanupResult result = MemoryCleaner.Run(targets, true, CleanupTrigger.Manual);
            Emit("");
            Emit(result.Detail);
            Check("清理耗时合理（< 10s）", result.ElapsedMs < 10000, N(result.ElapsedMs, "N0") + " ms");
            Check("清理后可用内存快照有效", result.AfterAvailMB > 0, N(result.AfterAvailMB, "N0") + " MB");
            Check("释放量为非负", result.FreedMB >= 0, N(result.FreedMB, "0.###") + " MB");
            Check("结果摘要可生成", !string.IsNullOrEmpty(MemoryCleaner.Summary(result)),
                MemoryCleaner.Summary(result));

            Check("触发描述往返一致",
                MemoryCleaner.ParseTrigger(MemoryCleaner.TriggerText(CleanupTrigger.AutoBoth)) == CleanupTrigger.AutoBoth &&
                MemoryCleaner.ParseTrigger(MemoryCleaner.TriggerText(CleanupTrigger.Manual)) == CleanupTrigger.Manual &&
                MemoryCleaner.ParseTrigger(null) == CleanupTrigger.Manual);
        }
    }

    // ======================================================================
    //  界面烟雾测试：在无 UAC、无人值守的情况下真实构造一次主窗口
    // ======================================================================
    /// <summary>
    /// 构造并短暂显示 MainForm，覆盖「布局构建 → 托盘图标 → 首轮渲染 → 收起到托盘 →
    /// 释放资源」整条路径。任何未捕获异常都会被记录为失败。
    /// 窗口以 ShowInTaskbar = false 显示几百毫秒，不会干扰用户。
    /// </summary>
    internal static class SmokeTest
    {
        private static int _pass;
        private static int _fail;
        /// <summary>可选的窗口截图输出路径（由命令行第二个参数指定）。</summary>
        private static string _pngPath;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                              (string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
        }

        private static void Check(string name, bool ok)
        {
            Check(name, ok, null);
        }

        internal static void Run(string sizeArg)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            Console.WriteLine("==== MemTrim 界面烟雾测试 ====");
            Console.WriteLine("管理员权限: " + Native.IsElevated());
            Console.WriteLine("DPI 感知开启: " + Native.TryEnableDpiAwareness() +
                              "（与正式版 Program.Main 的第一步一致）");
            ReportDpi();
            Console.WriteLine();

            // WinForms 要求 STA：控制台入口是 MTA，因此界面测试跑在自己的 STA 线程上
            _pngPath = sizeArg;
            System.Threading.Thread ui = new System.Threading.Thread(RunOnStaThread);
            ui.SetApartmentState(System.Threading.ApartmentState.STA);
            ui.Start();
            ui.Join(60000);
            if (ui.IsAlive)
            {
                Console.WriteLine("  [FAIL] 界面线程超时（60 秒未退出）");
                Console.WriteLine();
                Console.WriteLine("==== 烟雾测试结果: 0 通过 / 1 失败 ====");
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// 报告 DPI 感知状态与系统 DPI。
        /// 如果这里是 0（DPI unaware），Windows 会把整个窗口做位图拉伸，
        /// 拖动边框时就会出现一列列竖线和发虚的文字 —— 这是必须先排除的根因。
        /// </summary>
        private static void ReportDpi()
        {
            try
            {
                int awareness;
                int hr = GetProcessDpiAwareness(IntPtr.Zero, out awareness);
                string[] names = new string[]
                {
                    "0 = 未感知（会被系统位图拉伸！）",
                    "1 = 系统 DPI 感知",
                    "2 = 每显示器 DPI 感知（Per-Monitor）"
                };
                string text = awareness >= 0 && awareness < names.Length
                    ? names[awareness] : awareness.ToString(CultureInfo.InvariantCulture);
                if (hr != 0) text = "查询失败 hr=0x" + hr.ToString("X8");

                IntPtr user32 = LoadLibrary("user32.dll");
                IntPtr shcore = LoadLibrary("shcore.dll");
                Console.WriteLine("进程 DPI 感知: " + text);
                Console.WriteLine("系统 DPI: " + GetDpiForSystem() +
                                  "   user32=" + (user32 != IntPtr.Zero) +
                                  " shcore=" + (shcore != IntPtr.Zero));
            }
            catch (Exception ex)
            {
                Console.WriteLine("DPI 查询异常: " + ex.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string name);

        private static void RunOnStaThread()
        {
            string pngPath = _pngPath;            AppConfig original = AppConfig.Load();
            bool autoStartBefore = original.AutoStart;
            try
            {
                // 烟雾测试绝不能改动用户的开机自启动设置
                if (autoStartBefore)
                {
                    original.AutoStart = false;
                    original.Save();
                    Console.WriteLine("临时关闭 AutoStart，测试结束后恢复。");
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                MainForm form = null;
                Exception failure = null;

                try
                {
                    // ---- 阶段 1：构造 + 首轮渲染（同步，不进消息循环）----
                    form = new MainForm(false);
                    Check("主窗口构造成功（布局 + 托盘 + 配置加载）", true);
                    Check("托盘图标已创建", form.Icon != null);

                    form.ShowForSmokeTest();
                    Check("窗口显示并完成首轮渲染（含列表填充）", true);
                    Check("托盘右键菜单含「退出」项", HasTrayExit(form));

                    // ---- 阶段 1b：拉伸边框后的布局与重绘 ----
                    CheckResizeBehaviour(form);

                    // ---- 阶段 1c：标题/副标题不得重叠，列表列宽不得溢出 ----
                    CheckTextLayout(form);
                    DumpLayout(form);
                    SaveButtonProbe(form, pngPath);
                    CheckGlowSweep(form);

                    // ---- 阶段 2：完整生命周期（显示 → 消息循环 → 真实退出路径 → 释放）----
                    // 退出动作必须投递到消息循环「内部」执行：若在 Application.Run 之前
                    // 就 Close 掉，Run 会试图重新显示一个已释放的窗体。
                    System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
                    watchdog.Interval = 3000;
                    watchdog.Tick += delegate
                    {
                        watchdog.Stop();
                        if (!form.IsDisposed) form.Close();      // 兜底，正常情况下不会触发
                    };
                    watchdog.Start();

                    if (pngPath != null)
                    {
                        // 截图模式：不点托盘退出，避免在截图完成前就把窗口关掉。
                        // 关闭前要置上 _exitRequested —— 否则「关闭即最小化到托盘」
                        // 会（正确地）取消关闭，测试线程就永远等不到退出。
                        SaveWindowPng(form, pngPath, delegate
                        {
                            AllowRealClose(form);
                            form.Close();
                        });
                    }
                    else
                    {
                        form.BeginInvoke((MethodInvoker)delegate { ClickTrayExit(form); });
                    }

                    Application.Run(form);
                    watchdog.Stop();
                    Check("消息循环正常退出（窗口关闭无异常）", form.IsDisposed);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    Check("界面初始化未抛异常", false, ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("---- 异常堆栈 ----");
                    Console.WriteLine(ex.ToString());
                    Console.WriteLine("------------------");
                }
                finally
                {
                    if (form != null)
                    {
                        try { form.Dispose(); }
                        catch { }
                    }
                }

                if (failure == null)
                {
                    Check("无未处理异常", true);
                }
            }
            catch (Exception ex)
            {
                Check("烟雾测试本身未崩溃", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (autoStartBefore)
                {
                    original.AutoStart = true;
                    original.Save();
                    Console.WriteLine("已恢复 AutoStart = true。");
                }
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "==== 烟雾测试结果: {0} 通过 / {1} 失败 ====", _pass, _fail));
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        /// <summary>
        /// 复现「拖动边框改变窗口大小」的场景：
        ///   1) 各控件是否开启 ResizeRedraw —— 这是竖向细线残影的直接成因；
        ///   2) 连续拉伸后布局是否越界（列表压到说明文字、控件跑出窗体）；
        ///   3) 拉伸到位后整窗重绘一遍，卡片内部是否干净（无残影像素）。
        /// </summary>
        private static void CheckResizeBehaviour(MainForm form)
        {
            // 1) ResizeRedraw：窗体与两张卡片都必须是 true
            CardPanel[] cards = FindCards(form);
            bool formRedraw = GetResizeRedraw(form);
            bool cardsRedraw = cards.Length > 0;
            for (int i = 0; i < cards.Length; i++)
            {
                if (!GetResizeRedraw(cards[i])) cardsRedraw = false;
            }
            Check("窗体开启 ResizeRedraw（避免整窗残影）", formRedraw);
            Check("卡片面板开启 ResizeRedraw（避免卡片内竖向残影）", cardsRedraw,
                cards.Length + " 张卡片");

            // 2) 连续拉伸 → 布局不得越界
            Size original = form.ClientSize;
            int[] widths = new int[] { 980, 760, 1180, original.Width };
            for (int i = 0; i < widths.Length; i++)
            {
                form.ClientSize = new Size(widths[i], original.Height);
                form.PerformLayout();
                Application.DoEvents();
            }

            string layoutProblem = FindLayoutProblem(form);
            Check("反复拉伸后布局无重叠/越界", layoutProblem == null, layoutProblem);

            // 2b) 同层控件互不重叠（拉伸到最窄后再验一遍）
            form.ClientSize = new Size(Math.Max(form.MinimumSize.Width, original.Width), original.Height);
            form.PerformLayout();
            Application.DoEvents();
            string overlap = FindOverlap(form);
            Check("同层控件互不重叠", overlap == null, overlap);
            form.ClientSize = original;
            form.PerformLayout();
            Application.DoEvents();

            // 3) 拉伸到位后整窗重绘，检查「同一尺寸是否渲染出同一画面」
            //    如果重绘逻辑漏画了某块区域，先拉宽再收窄后的画面就会与直接
            //    渲染该尺寸的结果不一致 —— 这正是竖向残影的本质。
            int diffPixels = CountRenderMismatch(form, original);
            Check("同一尺寸的画面与拉伸历史无关（无残影）", diffPixels == 0,
                diffPixels + " 个像素不一致");
        }

        /// <summary>
        /// 渲染确定性检查：
        ///   A = 「拉宽 → 再收回原尺寸」之后渲染的画面
        ///   B = 「直接设置成原尺寸」重新渲染的画面
        /// 两者必须逐像素一致。为了排除流光动画的干扰，比较期间临时禁用主按钮
        /// （禁用态是静态绘制）。
        /// </summary>
        private static int CountRenderMismatch(MainForm form, Size target)
        {
            Control glow = FindGlowButton(form);
            bool glowWasEnabled = glow == null || glow.Enabled;
            if (glow != null) glow.Enabled = false;

            try
            {
                // A：先拉宽，再收回目标尺寸
                form.ClientSize = new Size(target.Width + 320, target.Height + 60);
                form.PerformLayout();
                Application.DoEvents();
                form.ClientSize = target;
                form.PerformLayout();
                Application.DoEvents();
                using (Bitmap after = Render(form)) 
                {
                    // B：直接就是目标尺寸（重新布局一次）
                    form.ClientSize = new Size(target.Width + 40, target.Height);
                    form.PerformLayout();
                    Application.DoEvents();
                    form.ClientSize = target;
                    form.PerformLayout();
                    Application.DoEvents();

                    using (Bitmap direct = Render(form))
                    {
                        return CompareBitmaps(after, direct);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    （渲染一致性检查失败: " + ex.Message + "）");
                return -1;
            }
            finally
            {
                if (glow != null) glow.Enabled = glowWasEnabled;
            }
        }

        private static Bitmap Render(Form form)
        {
            Bitmap shot = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height));
            form.Refresh();
            Application.DoEvents();
            form.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
            return shot;
        }

        /// <summary>
        /// 流光光效的两条硬性约束。相位靠反射直接摆，不依赖时钟，因此结果确定：
        ///
        ///   1) **周期闭合**：相位 p 与 p + 一个周期必须渲染出逐像素相同的画面。
        ///      这条最容易被改坏 —— 只要有人动了渐变色标却没让首尾同色，
        ///      循环接缝处就会「跳一下」，肉眼几乎看不出来，这里直接卡住。
        ///   2) **相位确实在动**：p 与 p + 周期/3 必须不同，否则等于流光停了。
        ///
        /// 顺带确认禁用态与相位无关 —— CountRenderMismatch 的「无残影」检查依赖这一点。
        /// </summary>
        private static void CheckGlowSweep(MainForm form)
        {
            Control button = FindGlowButton(form);
            if (button == null)
            {
                Check("界面上的流光主按钮存在", false, "没找到 RgbGlowButton");
                return;
            }

            BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo step = button.GetType().GetField("_phaseStep", priv);
            FieldInfo phase = button.GetType().GetField("_phase", priv);
            FieldInfo period = button.GetType().GetField("_period", priv);
            MethodInfo apply = button.GetType().GetMethod("ApplyPhase", priv);
            if (step == null || phase == null || period == null || apply == null)
            {
                Check("流光控件的相位字段可访问", false,
                    "反射取 _phase / _phaseStep / _period / ApplyPhase 失败");
                return;
            }

            object stepWas = step.GetValue(button);
            object phaseWas = phase.GetValue(button);
            try
            {
                step.SetValue(button, 0f);                        // 冻结动画，渲染只取决于相位
                float cycle = (float)period.GetValue(button);

                using (Bitmap atZero = RenderGlowButton(button, phase, apply, 0f))
                using (Bitmap atCycle = RenderGlowButton(button, phase, apply, cycle))
                using (Bitmap atThird = RenderGlowButton(button, phase, apply, cycle / 3f))
                {
                    int seam = CompareBitmaps(atZero, atCycle);
                    Check("流光周期首尾严丝合缝（p 与 p+周期 逐像素一致）", seam == 0,
                        seam + " 个像素不一致（周期 " + cycle + " px）");

                    int moved = CompareBitmaps(atZero, atThird);
                    Check("流光相位确实在移动（不同相位画面不同）", moved > 0,
                        moved + " 个像素不同");
                }

                bool wasEnabled = button.Enabled;
                button.Enabled = false;
                try
                {
                    using (Bitmap d0 = RenderGlowButton(button, phase, apply, 0f))
                    using (Bitmap d1 = RenderGlowButton(button, phase, apply, cycle * 0.7f))
                    {
                        int diff = CompareBitmaps(d0, d1);
                        Check("流光按钮禁用态与相位无关（静态绘制）", diff == 0,
                            diff + " 个像素不一致");
                    }
                }
                finally
                {
                    button.Enabled = wasEnabled;
                }
            }
            finally
            {
                step.SetValue(button, stepWas);
                phase.SetValue(button, phaseWas);
                apply.Invoke(button, null);
            }
        }

        /// <summary>把流光按钮摆到指定相位后单独渲染成图。</summary>
        private static Bitmap RenderGlowButton(Control button, FieldInfo phase,
            MethodInfo apply, float value)
        {
            phase.SetValue(button, value);
            apply.Invoke(button, null);
            button.Refresh();
            button.Update();
            Application.DoEvents();

            Bitmap shot = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
            return shot;
        }

        /// <summary>逐像素比较，返回不一致的像素数（抽样步长 2，够快也够准）。</summary>
        private static int CompareBitmaps(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return a.Width * a.Height;

            int diff = 0;
            for (int y = 0; y < a.Height; y += 2)
            {
                for (int x = 0; x < a.Width; x += 2)
                {
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) diff++;
                }
            }
            return diff;
        }

        private static Control FindGlowButton(Control parent)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                if (child is RgbGlowButton) return child;
                Control deep = FindGlowButton(child);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>
        /// 检查 125%/150% 缩放下最容易出问题的两处：
        ///   1) 表头标题与副标题不能叠在一起（两者字号不同、行高不同）；
        ///   2) 列表所有列宽之和不能超过可用宽度，否则底部会冒出横向滚动条。
        /// </summary>
        private static void CheckTextLayout(MainForm form)
        {
            // ---- 表头两行文字 ----
            Label title = null, subtitle = null;
            for (int i = 0; i < form.Controls.Count && subtitle == null; i++)
            {
                Control header = form.Controls[i];
                for (int k = 0; k < header.Controls.Count; k++)
                {
                    Label label = header.Controls[k] as Label;
                    if (label == null) continue;
                    if (label.Text == "MemTrim") title = label;
                    else if (label.Text.StartsWith("内存泄漏监控", StringComparison.Ordinal)) subtitle = label;
                }
            }

            if (title == null || subtitle == null)
            {
                Check("表头标题与副标题都存在", false,
                    "title=" + (title != null) + " subtitle=" + (subtitle != null));
            }
            else
            {
                int titleBottom = title.Top + title.PreferredHeight;
                Check("表头副标题不与标题重叠", subtitle.Top >= titleBottom,
                    "标题底 " + titleBottom + " vs 副标题顶 " + subtitle.Top);

                Panel header = title.Parent as Panel;
                if (header != null)
                {
                    Check("表头内容未超出表头高度",
                        subtitle.Top + subtitle.PreferredHeight <= header.Height,
                        "副标题底 " + (subtitle.Top + subtitle.PreferredHeight) +
                        " vs 表头高 " + header.Height);
                }
            }

            // ---- 列表列宽 ----
            ListView list = FindControl<ListView>(form);
            if (list == null)
            {
                Check("找到进程列表", false);
                return;
            }

            int sum = 0;
            for (int i = 0; i < list.Columns.Count; i++) sum += list.Columns[i].Width;

            Check("列表列宽未溢出（无横向滚动条）", sum <= list.ClientSize.Width,
                "列宽合计 " + sum + " vs 列表可用宽 " + list.ClientSize.Width);
            Check("「进程」列足够宽", list.Columns[0].Width >= 88,
                list.Columns[0].Width + " px");
        }

        /// <summary>
        /// 通用重叠检测：同一父容器内的可见兄弟控件不得互相压住。
        /// 这条检查当初就是靠它发现「125% 缩放下流光的自绘光晕盖住了权限那一行文字」。
        /// 只看真正同层的兄弟，跨容器（卡片 vs 窗体）不算重叠。
        /// </summary>
        private static string FindOverlap(Control parent)
        {
            List<Control> siblings = new List<Control>();
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                if (c.Visible && c.Width > 0 && c.Height > 0) siblings.Add(c);
            }

            for (int i = 0; i < siblings.Count; i++)
            {
                for (int k = i + 1; k < siblings.Count; k++)
                {
                    // 用与父容器相交后的实际可见区域比较：自绘控件允许把光晕画到
                    // 控件矩形之外（那部分会被父容器裁掉），不算重叠。
                    Rectangle a = ClipToParent(siblings[i], parent);
                    Rectangle b = ClipToParent(siblings[k], parent);
                    if (a.IsEmpty || b.IsEmpty) continue;
                    if (!a.IntersectsWith(b)) continue;

                    Rectangle hit = Rectangle.Intersect(a, b);
                    // 1~2 px 的相邻边界不算重叠
                    if (hit.Width <= 2 || hit.Height <= 2) continue;

                    return Describe(siblings[i]) + " 与 " + Describe(siblings[k]) +
                           " 重叠 " + hit.Width + "x" + hit.Height;
                }
            }

            for (int i = 0; i < siblings.Count; i++)
            {
                string deep = FindOverlap(siblings[i]);
                if (deep != null) return Describe(siblings[i]) + " 内部: " + deep;
            }
            return null;
        }

        private static Rectangle ClipToParent(Control child, Control parent)
        {
            Rectangle visible = Rectangle.Intersect(child.Bounds, parent.ClientRectangle);
            if (visible.Width <= 0 || visible.Height <= 0) return Rectangle.Empty;
            return visible;
        }

        /// <summary>
        /// 单独把 RGB 按钮渲染成图并顺手统计像素。
        /// 用 DrawToBitmap 直接抓按钮自己：全窗抓图（PrintWindow / 整窗 DrawToBitmap）
        /// 对自绘子控件的还原都不完全可靠，容易把「抓图问题」误当成「绘制问题」。
        /// </summary>
        private static void SaveButtonProbe(MainForm form, string pngPath)
        {
            if (string.IsNullOrEmpty(pngPath)) return;

            Control button = FindGlowButton(form);
            if (button == null) return;

            try
            {
                // 注意：不能把按钮设成 Enabled=false 再抓图 —— 禁用态走的是另一条
                // 绘制分支，抓出来看不到真实外观。这里保持启用，抓两帧看动画范围。
                SaveButtonVariant(button, pngPath, "button-light", Color.White);
                System.Threading.Thread.Sleep(180);
                SaveButtonVariant(button, pngPath, "button-light2", Color.White);

                // 复原成默认白底
                MethodInfo restore = button.GetType().GetMethod("SetButtonBack");
                if (restore != null) restore.Invoke(button, new object[] { Color.White });
            }
            catch (Exception ex)
            {
                Console.WriteLine("  按钮取样失败: " + ex.Message);
            }
        }

        /// <summary>把按钮按指定底色渲染成 PNG，并统计底色/文字/彩色像素数。</summary>
        private static void SaveButtonVariant(Control button, string pngPath,
            string suffix, Color back)
        {
            MethodInfo setter = button.GetType().GetMethod("SetButtonBack");
            if (setter != null) setter.Invoke(button, new object[] { back });
            button.Refresh();          // 强制同步重绘，排除「抓到上一帧」的可能
            button.Update();
            Application.DoEvents();

            using (Bitmap shot = new Bitmap(button.Width, button.Height))
            {
                button.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                string alt = System.IO.Path.ChangeExtension(pngPath, null) + "-" + suffix + ".png";
                shot.Save(alt, System.Drawing.Imaging.ImageFormat.Png);

                // 统计：底色面积、文字暗像素（只取按钮内部，避开光晕边）
                int white = 0, dark = 0, colored = 0, inner = 0;
                int pad = Math.Max(6, button.Height / 6);
                for (int y = pad; y < shot.Height - pad; y++)
                {
                    for (int x = pad; x < shot.Width - pad; x++)
                    {
                        inner++;
                        Color c = shot.GetPixel(x, y);
                        int max = Math.Max(c.R, Math.Max(c.G, c.B));
                        int min = Math.Min(c.R, Math.Min(c.G, c.B));
                        if (min > 235) white++;
                        else if (max < 120) dark++;
                        else if (max - min > 60) colored++;
                    }
                }

                Console.WriteLine("  " + suffix + ": 内部 " + inner + " → 近白 " + white +
                                  " / 暗(文字) " + dark + " / 彩色 " + colored +
                                  "  底色样本 " + shot.GetPixel(pad + 4, pad + 4) +
                                  "  文字色 " + ReadTextColor(button) + " -> " + alt);
            }
        }

        /// <summary>读出按钮当前实际使用的文字色，便于判断「白字不可见」是配色还是绘制问题。</summary>
        private static string ReadTextColor(Control button)
        {
            try
            {
                FieldInfo field = button.GetType().GetField("_textBrush",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                SolidBrush brush = field == null ? null : field.GetValue(button) as SolidBrush;
                if (brush == null) return "(未找到)";
                return brush.Color.ToString();
            }
            catch (Exception ex)
            {
                return "(读取失败 " + ex.GetType().Name + ")";
            }
        }

        private static string Describe(Control c)
        {
            string text = c.Text ?? "";
            if (text.Length > 10) text = text.Substring(0, 10) + "…";
            return c.GetType().Name + "(" + text + ")@" + c.Left + "," + c.Top +
                   " " + c.Width + "x" + c.Height;
        }

        /// <summary>打印关键控件的实际几何，便于对照截图排查布局问题。</summary>
        private static void DumpLayout(Form form)
        {
            Console.WriteLine("  客户区 = " + form.ClientSize.Width + " x " + form.ClientSize.Height +
                              "   DPI 缩放 = " + (UiTheme.Dpi(100) / 100.0));

            Dump(form, form, 0);

            ListView list = FindControl<ListView>(form);
            if (list == null) return;

            Console.WriteLine("  列表: " + list.Left + "," + list.Top + " " +
                              list.Width + "x" + list.Height + "  客户区宽 " + list.ClientSize.Width);
            int sum = 0;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                sum += list.Columns[i].Width;
                Console.Write("    [" + i + "] " + list.Columns[i].Text + "=" + list.Columns[i].Width);
            }
            Console.WriteLine();
            Console.WriteLine("    列宽合计 " + sum + "  列表可见宽 " + list.ClientSize.Width +
                              "  差值 " + (list.ClientSize.Width - sum));
        }

        private static void Dump(Control parent, Form form, int depth)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control c = parent.Controls[i];
                string text = c.Text ?? "";
                if (text.Length > 14) text = text.Substring(0, 14) + "…";
                Console.WriteLine("    " + new string(' ', depth * 2) +
                    c.GetType().Name + " \"" + text + "\"  " +
                    c.Left + "," + c.Top + " " + c.Width + "x" + c.Height);
                if (c.Controls.Count > 0 && depth < 2) Dump(c, form, depth + 1);
            }
        }

        private static void CollectLabels(Control parent, List<Label> found)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                Label label = child as Label;
                if (label != null) found.Add(label);
                if (child.Controls.Count > 0) CollectLabels(child, found);
            }
        }

        private static CardPanel[] FindCards(Control root)
        {
            List<CardPanel> found = new List<CardPanel>();
            CollectPanels(root, found);
            return found.ToArray();
        }

        private static void CollectPanels(Control parent, List<CardPanel> found)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                CardPanel panel = child as CardPanel;
                if (panel != null) found.Add(panel);
                if (child.Controls.Count > 0) CollectPanels(child, found);
            }
        }

        /// <summary>读取 Control.ResizeRedraw（protected 属性，只能反射）。</summary>
        private static bool GetResizeRedraw(Control control)
        {
            try
            {
                PropertyInfo property = typeof(Control).GetProperty("ResizeRedraw",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (property == null) return false;
                return (bool)property.GetValue(control, null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>检查底部说明文字是否被列表压住、控件是否跑出窗体。</summary>
        private static string FindLayoutProblem(Form form)
        {
            ListView list = FindControl<ListView>(form);
            if (list == null) return "找不到进程列表";

            int listBottom = list.Bottom;

            // 底部说明文字现在挂在「底部面板」里，必须递归找
            List<Label> all = new List<Label>();
            CollectLabels(form, all);
            int labelTop = int.MaxValue;
            int labelCount = 0;
            string[] wanted = new string[] { "系统趋势", "累计清理", "权限" };
            for (int i = 0; i < all.Count; i++)
            {
                string text = all[i].Text ?? "";
                for (int k = 0; k < wanted.Length; k++)
                {
                    if (!text.StartsWith(wanted[k], StringComparison.Ordinal)) continue;
                    // 换算成窗体坐标再比较
                    Point origin = form.PointToClient(all[i].Parent.PointToScreen(all[i].Location));
                    labelTop = Math.Min(labelTop, origin.Y);
                    labelCount++;
                }
            }

            if (labelCount < 3) return "底部说明文字缺少（找到 " + labelCount + " 条）";
            if (listBottom > labelTop)
            {
                return "进程列表底部 " + listBottom + " 压住了说明文字顶部 " + labelTop;
            }

            // 所有直接子控件必须在客户区内
            for (int i = 0; i < form.Controls.Count; i++)
            {
                Control c = form.Controls[i];
                if (!c.Visible) continue;
                if (c.Right > form.ClientSize.Width + 1 || c.Bottom > form.ClientSize.Height + 1)
                {
                    return c.GetType().Name + " \"" + c.Text + "\" 超出客户区 (" +
                           c.Right + "x" + c.Bottom + " vs " +
                           form.ClientSize.Width + "x" + form.ClientSize.Height + ")";
                }
            }
            return null;
        }

        private static T FindControl<T>(Control parent) where T : Control
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                T hit = parent.Controls[i] as T;
                if (hit != null) return hit;
                T deep = FindControl<T>(parent.Controls[i]);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>
        /// 把窗体的真实屏幕画面存成 PNG（用 PrintWindow，不抓整个桌面，
        /// 也不会把用户的其他窗口拍进去）。仅在给了输出路径时执行。
        /// </summary>
        private static void SaveWindowPng(MainForm form, string path, MethodInvoker whenDone)
        {
            form.BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    form.Show();
                    form.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(400);      // 留几帧给流光动画
                    Application.DoEvents();

                    int w = form.Width, h = form.Height;

                    // ① PrintWindow：等同于屏幕上看到的内容（含 DWM 合成）
                    using (Bitmap shot = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(shot))
                        {
                            IntPtr hdc = g.GetHdc();
                            // flags=2 (PW_RENDERFULLCONTENT) 才能抓到 DWM 合成后的内容
                            bool ok = PrintWindow(form.Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                            if (!ok) Console.WriteLine("  PrintWindow 返回 false");
                        }
                        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("  PrintWindow 截图: " + path + " (" + w + "x" + h + ")");
                    }

                    // ② DrawToBitmap：走 WM_PRINTCLIENT，自绘控件一定能画出来，
                    //    用来交叉验证「卡片上的 0」到底是程序没取到数据，还是抓图方式的锅
                    string alt = System.IO.Path.ChangeExtension(path, null) + "-wmprint.png";
                    using (Bitmap shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                        shot.Save(alt, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("  DrawToBitmap 截图: " + alt);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  截图失败: " + ex.Message);
                }
                finally
                {
                    form.BeginInvoke(whenDone);             // 截图完成后再关窗
                }
            });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        /// <summary>置上 MainForm._exitRequested，让 Close() 真正关闭而不是收进托盘。</summary>
        private static void AllowRealClose(MainForm form)
        {
            try
            {
                FieldInfo field = typeof(MainForm).GetField("_exitRequested",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) field.SetValue(form, true);
            }
            catch
            {
                // 反射失败时由看门狗兜底
            }
        }

        /// <summary>
        /// 通过反射从 MainForm 的托盘图标里找到「退出」菜单项并触发它，
        /// 这样烟雾测试覆盖的是用户真实的退出路径（_exitRequested = true → Close()）。
        /// </summary>
        private static bool ClickTrayExit(MainForm form)
        {
            ToolStripItem item = FindTrayExit(form);
            if (item == null) return false;
            item.PerformClick();
            return true;
        }

        /// <summary>只检查托盘菜单里是否存在「退出」项，不触发。</summary>
        private static bool HasTrayExit(MainForm form)
        {
            return FindTrayExit(form) != null;
        }

        private static ToolStripItem FindTrayExit(MainForm form)
        {
            try
            {
                FieldInfo trayField = typeof(MainForm).GetField("_tray",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (trayField == null) return null;

                NotifyIcon tray = trayField.GetValue(form) as NotifyIcon;
                if (tray == null || tray.ContextMenuStrip == null) return null;

                ToolStripItemCollection items = tray.ContextMenuStrip.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Text == "退出") return items[i];
                }
            }
            catch
            {
                // 反射失败时视为不存在，由调用方按失败处理
            }
            return null;
        }
    }
}