// ============================================================================
//  MemTrim · MemoryCleaner.cs —— 内存清理核心
//
//  移植 PCL「内存优化」的实际动作，只执行其中风险最低的两项：
//      ① 清空目标进程的工作集（EmptyWorkingSet → 失败回退 SetProcessWorkingSetSize）
//      ② 清空系统待机列表（NtSetSystemInformation / MemoryPurgeStandbyList）
//  未启用 FlushModifiedList / FlushFileCache / RegistryReconciliation /
//  CombinePhysicalMemory（写盘量大或存在兼容性风险）。
//
//  「释放了多少」的口径：清理前后各取一次整机可用内存快照做差。
//  被清出去的页面只是转移到待机列表或分页文件，所以这个数字代表
//  「立刻可用的内存变多了」，不代表内存被消灭。
// ============================================================================

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;

namespace MemTrim
{
    /// <summary>清理的触发来源，用于历史记录与文案。</summary>
    internal enum CleanupTrigger
    {
        Manual,
        AutoPressure,
        AutoLeak,
        AutoBoth
    }

    /// <summary>一次清理的完整结果。</summary>
    internal sealed class CleanupResult
    {
        public CleanupTrigger Trigger = CleanupTrigger.Manual;
        public DateTime Time = DateTime.Now;

        /// <summary>清理前可用物理内存（MB）。</summary>
        public double BeforeAvailMB;
        /// <summary>清理后可用物理内存（MB）。</summary>
        public double AfterAvailMB;
        /// <summary>实际可用内存增量（MB，负值已归零；见 FreedMaybeZero）。</summary>
        public double FreedMB;
        /// <summary>原始差值是否为负（内存被其他程序同时吃掉了）。</summary>
        public bool FreedMaybeZero;

        /// <summary>被清理进程清理前的工作集合计（MB）。</summary>
        public double ProcWorkingSetSumMB;
        /// <summary>待机列表是否清空成功。</summary>
        public bool StandbyPurged;
        /// <summary>清理特权是否全部启用。</summary>
        public bool PrivilegeOk;
        /// <summary>特权启用明细。</summary>
        public string PrivilegeDetail = "";
        public int ProcessSuccess;
        public int ProcessFailed;
        /// <summary>完整日志（手动清理时弹窗展示）。</summary>
        public string Detail = "";
        public double ElapsedMs;

        /// <summary>「释放 N MB」的展示文本。</summary>
        public string FreedText
        {
            get
            {
                if (FreedMaybeZero) return "≈0 MB（被其他程序同时占用）";
                if (FreedMB >= 1) return FreedMB.ToString("N0") + " MB";
                return "≈0 MB";
            }
        }
    }

    internal static class MemoryCleaner
    {
        private const int PurgeSettleMs = 350;    // 等待内核完成页面回收的时间

        private static bool _privilegesPrepared;
        private static bool _privilegeOk;
        private static string _privilegeDetail = "(未初始化)";

        /// <summary>是否已尝试过启用特权。</summary>
        public static bool PrivilegesPrepared { get { return _privilegesPrepared; } }
        /// <summary>两个清理特权是否全部启用成功。</summary>
        public static bool PrivilegeOk { get { return _privilegeOk; } }
        /// <summary>特权启用明细，如 "SeProfile…=OK, SeIncreaseQuota…=OK"。</summary>
        public static string PrivilegeDetail { get { return _privilegeDetail; } }

        /// <summary>
        /// 启用 PCL 内存优化所需的两个特权。幂等：重复调用只是重试一次。
        /// 令牌上的特权是进程级状态，启用一次即可长期有效，无需每次清理都调。
        /// </summary>
        public static void PreparePrivileges()
        {
            _privilegeDetail = Native.EnableCleanupPrivileges();
            _privilegeOk = _privilegeDetail.IndexOf("=FAIL", StringComparison.Ordinal) < 0;
            _privilegesPrepared = true;
        }

        /// <summary>
        /// 执行一次内存清理。本方法会阻塞数百毫秒，请勿在 UI 线程直接调用。
        /// </summary>
        /// <param name="leakProcesses">需要清空工作集的进程（可为 null 或空）。</param>
        /// <param name="purgeStandby">是否清空系统待机列表。</param>
        /// <param name="trigger">触发来源，仅用于记录与文案。</param>
        public static CleanupResult Run(List<LeakItem> leakProcesses, bool purgeStandby, CleanupTrigger trigger)
        {
            Stopwatch watch = Stopwatch.StartNew();

            CleanupResult result = new CleanupResult();
            result.Trigger = trigger;

            if (!_privilegesPrepared) PreparePrivileges();
            result.PrivilegeOk = _privilegeOk;
            result.PrivilegeDetail = _privilegeDetail;

            Log log = new Log();
            log.Pair("触发方式", TriggerText(trigger));
            log.Pair("清理特权", _privilegeDetail);

            result.BeforeAvailMB = Native.GetMemorySnapshot().AvailMB;

            TrimProcesses(leakProcesses, result, log);
            if (purgeStandby) PurgeStandby(result, log);

            // 给内核一点时间完成页面回收，同时把自身工作集也吐出去
            System.Threading.Thread.Sleep(PurgeSettleMs);
            Native.TrimSelfWorkingSet();

            result.AfterAvailMB = Native.GetMemorySnapshot().AvailMB;
            double delta = result.AfterAvailMB - result.BeforeAvailMB;
            result.FreedMaybeZero = delta < 0;
            result.FreedMB = delta > 0 ? delta : 0;

            log.Pair("清理前可用", result.BeforeAvailMB, "N0");
            log.Pair("清理后可用", result.AfterAvailMB, "N0");
            log.Pair("本次释放", result.FreedMB, "N0");
            if (result.FreedMaybeZero)
            {
                log.Line("提示: 清理期间有其他程序占用了更多内存，本次增量按 0 记录。");
            }

            watch.Stop();
            result.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            log.Pair("耗时", result.ElapsedMs, "N0");
            result.Detail = log.ToString();
            return result;
        }

        /// <summary>① 清空泄漏进程的工作集。</summary>
        private static void TrimProcesses(List<LeakItem> targets, CleanupResult result, Log log)
        {
            if (targets == null || targets.Count == 0)
            {
                log.Line("工作集清理: 无目标进程");
                return;
            }

            log.Line("清空工作集的进程:");
            for (int i = 0; i < targets.Count; i++)
            {
                LeakItem it = targets[i];
                string error;
                if (Native.TrimProcessWorkingSet(it.Pid, out error))
                {
                    result.ProcessSuccess++;
                    result.ProcWorkingSetSumMB += it.WorkingSetMB;
                    log.Detail(string.Format(CultureInfo.InvariantCulture,
                        "[OK]   {0}  工作集 {1:N1} MB", it.DisplayName, it.WorkingSetMB));
                }
                else
                {
                    result.ProcessFailed++;
                    log.Detail(string.Format(CultureInfo.InvariantCulture,
                        "[FAIL] {0}  {1}", it.DisplayName, error));
                }
            }
        }

        /// <summary>② 清空系统待机列表，失败时回退到「只清低优先级待机列表」。</summary>
        private static void PurgeStandby(CleanupResult result, Log log)
        {
            bool purged = Native.PurgeStandbyListBestEffort();
            result.StandbyPurged = purged;
            log.Pair("清空系统待机列表", purged ? "成功" : "失败（需要管理员权限）");
        }

        /// <summary>触发来源的中文描述，历史文件与界面共用同一套措辞。</summary>
        public static string TriggerText(CleanupTrigger trigger)
        {
            switch (trigger)
            {
                case CleanupTrigger.Manual: return "手动清理";
                case CleanupTrigger.AutoPressure: return "自动：内存压力过高";
                case CleanupTrigger.AutoLeak: return "自动：检测到内存泄漏";
                case CleanupTrigger.AutoBoth: return "自动：压力过高 + 检测到泄漏";
                default: return trigger.ToString();
            }
        }

        /// <summary>把历史文件里的触发描述还原成枚举（供界面显示上次清理方式）。</summary>
        public static CleanupTrigger ParseTrigger(string text)
        {
            if (string.IsNullOrEmpty(text)) return CleanupTrigger.Manual;
            bool pressure = text.IndexOf("压力", StringComparison.Ordinal) >= 0;
            bool leak = text.IndexOf("泄漏", StringComparison.Ordinal) >= 0;
            if (pressure && leak) return CleanupTrigger.AutoBoth;
            if (pressure) return CleanupTrigger.AutoPressure;
            if (leak) return CleanupTrigger.AutoLeak;
            return CleanupTrigger.Manual;
        }

        /// <summary>一行式结果摘要，用于托盘气泡。</summary>
        public static string Summary(CleanupResult result)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "释放 {0}，当前可用 {1:N0} MB（清理 {2} 个进程，待机列表{3}）",
                result.FreedText, result.AfterAvailMB, result.ProcessSuccess,
                result.StandbyPurged ? "已清空" : "未清空");
        }
    }
}
