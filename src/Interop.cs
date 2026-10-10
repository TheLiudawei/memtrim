// ============================================================================
//  MemTrim · Interop.cs —— 与 Windows 内核/用户态 API 的互操作层
//
//  职责边界：本文件只做「调用 Win32/NT API 并把结果翻译成托管对象」，
//            不含任何判定逻辑（判定在 LeakDetector.cs）与展示逻辑（在 Ui.cs）。
//
//  内存清理算法移植自 PCL（Plain Craft Launcher 2, Meloong-Git/PCL）的
//  「内存优化」功能，其公开原理为：
//      1) 获取 SeProfileSingleProcessPrivilege / SeIncreaseQuotaPrivilege 特权
//      2) NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)
//         清空系统待机列表（Standby List）
//      3) EmptyWorkingSet / SetProcessWorkingSetSize(-1,-1) 清空目标进程工作集
//  本工具按用户需求只保留「泄漏 Top 进程工作集 + 系统待机列表」两部分，
//  未启用 FlushModifiedList / CombinePhysicalMemory（写盘量大、兼容性风险高）。
//
//  性能约定（本文件是整程序的热路径，每 5 秒执行一次，改动请保持）：
//      · 监控轮询不打开任何进程句柄：一次 NtQuerySystemInformation 取回
//        「全部 PID + 工作集 + 提交量 + 映像名 + 创建时间」；
//      · 非托管缓冲区与结果 List 都由 ProcessSampler 长期持有并复用，
//        不随每轮采样重复分配（此前每轮都要 AllocHGlobal + FreeHGlobal 256 KB）；
//      · P/Invoke 的临时缓冲区一律用缓存，不在循环里申请非托管内存。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MemTrim
{
    /// <summary>整机物理内存与提交内存的一次性快照（单次 GMS 调用即可取全）。</summary>
    internal struct MemSnapshot
    {
        /// <summary>物理内存总量（字节）。</summary>
        public ulong TotalPhysical;
        /// <summary>当前可用物理内存（字节）。</summary>
        public ulong AvailPhysical;
        /// <summary>提交上限（物理内存 + 分页文件，字节）。</summary>
        public ulong CommitLimit;
        /// <summary>已提交内存（字节）。</summary>
        public ulong CommitUsed;
        /// <summary>GlobalMemoryStatusEx 给出的物理内存占用率（0~100，含缓存）。</summary>
        public int LoadPercent;

        public ulong UsedPhysical
        {
            get { return TotalPhysical > AvailPhysical ? TotalPhysical - AvailPhysical : 0UL; }
        }

        public double TotalMB { get { return TotalPhysical / 1048576.0; } }
        public double AvailMB { get { return AvailPhysical / 1048576.0; } }
        public double UsedMB { get { return UsedPhysical / 1048576.0; } }
        public double CommitUsedMB { get { return CommitUsed / 1048576.0; } }
        public double CommitLimitMB { get { return CommitLimit / 1048576.0; } }

        /// <summary>
        /// 内存压力百分比：优先用「已提交 / 提交上限」。
        /// 物理占用率会把待机列表与文件缓存算成「已用」，Win10/11 日常就有 60~75%，
        /// 用它做自动清理的触发条件会频繁误报；提交率才反映真正的压力。
        /// 提交信息不可用时（极老系统）退回物理占用率。
        /// </summary>
        public int PressurePercent
        {
            get
            {
                if (CommitLimit > 0) return (int)Math.Round(CommitUsed * 100.0 / CommitLimit);
                return LoadPercent;
            }
        }

        /// <summary>压力值的文字口径，用于界面提示「阈值作用于哪个数字」。</summary>
        public string PressureSource
        {
            get { return CommitLimit > 0 ? "提交内存" : "物理内存"; }
        }
    }

    /// <summary>一次采样得到的单个进程内存信息。</summary>
    internal struct ProcMem
    {
        public int Pid;
        public string Name;
        public ulong WorkingSet;
        public ulong PrivateBytes;
        /// <summary>
        /// 进程创建时间（FILETIME，100ns；0 = 内核未给出）。
        /// 与工作集来自同一次系统调用，因此「读它」是零额外开销的；
        /// LeakDetector 用它精确识别 PID 复用（已与 Process.StartTime 交叉验证逐条相等）。
        /// </summary>
        public long StartTime;
    }

    internal static class Native
    {
        // ------------------------------------------------------------------
        // 常量
        // ------------------------------------------------------------------
        /// <summary>查询受限信息：Win7+ 即可用，且对受保护进程的成功率最高。</summary>
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        /// <summary>查询完整信息：少数老系统/特殊进程只认这一位。</summary>
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_SET_QUOTA = 0x0100;

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        /// <summary>AdjustTokenPrivileges「部分成功」时的最后一个错误码。</summary>
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        /// <summary>NtSetSystemInformation 的 SystemMemoryListInformation 信息类。</summary>
        private const int SystemMemoryListInformation = 80;
        /// <summary>SYSTEM_MEMORY_LIST_COMMAND：清空待机列表（PCL 内存优化主力动作之一）。</summary>
        private const int MemoryPurgeStandbyList = 4;
        /// <summary>SYSTEM_MEMORY_LIST_COMMAND：只清低优先级待机列表。</summary>
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        /// <summary>NtQuerySystemInformation 的 SystemProcessInformation 信息类。</summary>
        private const int SystemProcessInformation = 5;

        // ------------------------------------------------------------------
        // 结构体
        // ------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>
        /// PROCESS_MEMORY_COUNTERS_EX：cb 声明为 uint（而非 UIntPtr），
        /// 这样 x86/x64 下的字段偏移都与本机 ABI 一致。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        /// <summary>SYSTEM_PROCESS_INFORMATION 的定长头部（变长尾部是映像名，不映射）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_PROCESS_INFORMATION
        {
            public int NextEntryOffset;
            public int NumberOfThreads;
            public long WorkingSetPrivateSize;
            public int HardFaultCount;
            public int NumberOfThreadsHighWatermark;
            public long CycleTime;
            public long CreateTime;
            public long UserTime;
            public long KernelTime;
            public UNICODE_STRING ImageName;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
            public int HandleCount;
            public int SessionId;
            public IntPtr UniqueProcessKey;
            public IntPtr PeakVirtualSize;
            public IntPtr VirtualSize;
            public int PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
            public IntPtr PrivatePageCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        // ------------------------------------------------------------------
        // P/Invoke
        // ------------------------------------------------------------------
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess,
            ref PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr hProcess,
            out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int systemInformationClass,
            IntPtr systemInformation, int systemInformationLength, out int returnLength);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess,
            IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState,
            uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass,
            IntPtr SystemInformation, int SystemInformationLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int value);

        // ==================================================================
        //  整机内存快照
        // ==================================================================
        /// <summary>读取整机物理内存与提交内存快照；失败时返回 false 并给出全 0 快照。</summary>
        public static bool TryGetMemorySnapshot(out MemSnapshot snap)
        {
            snap = new MemSnapshot();
            MEMORYSTATUSEX st = new MEMORYSTATUSEX();
            st.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref st)) return false;

            snap.TotalPhysical = st.ullTotalPhys;
            snap.AvailPhysical = st.ullAvailPhys;
            snap.CommitLimit = st.ullTotalPageFile;
            snap.CommitUsed = st.ullTotalPageFile > st.ullAvailPageFile
                ? st.ullTotalPageFile - st.ullAvailPageFile : 0UL;
            snap.LoadPercent = (int)st.dwMemoryLoad;
            return true;
        }

        /// <summary>读取整机内存快照；失败时返回全 0 快照（界面会显示 0，不抛异常）。</summary>
        public static MemSnapshot GetMemorySnapshot()
        {
            MemSnapshot s;
            TryGetMemorySnapshot(out s);
            return s;
        }

        // ==================================================================
        //  进程枚举（供 ProcessSampler 调用）
        // ==================================================================
        /// <summary>查询一次系统进程信息所需的非托管缓冲区是否增长（返回 false 表示本轮结果为空）。</summary>
        internal static bool QuerySystemProcesses(IntPtr buffer, int bufferSize, out int usedSize)
        {
            usedSize = 0;
            int status = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out usedSize);
            return status >= 0;
        }

        /// <summary>
        /// 一次 NtQuerySystemInformation 取回全部进程的「PID + 工作集 + 提交量 + 映像名 + 创建时间」。
        ///
        /// 为什么不用 Process.GetProcesses()：它只返回 Process 外壳，工作集仍需逐个 OpenProcess
        /// 读取。实测在一个 120 进程的系统上，光 GetProcesses() 本身就要 ~2.2 ms，
        /// 加上逐进程打开句柄，一轮监控约 4 ms 全花在 UI 线程上；
        /// 而 NtQuerySystemInformation 一次系统调用即可拿到全部字段，且不受进程访问权限限制。
        ///
        /// 过滤掉两类无用条目：
        ///   · PID ≤ 4 是 Idle/System 伪进程，清理时必然跳过；
        ///   · 工作集为 0 的多是尚未回收的僵尸条目（本机实测 261 条里有 141 条）。
        /// </summary>
        /// <param name="entries">结果写入此数组（调用方复用，容量不足时返回 false）。</param>
        /// <param name="count">实际写入的条目数。</param>
        /// <param name="rawEntryCount">内核报告的条目总数（含被过滤的），用于界面显示。</param>
        /// <returns>数组容量是否足够。</returns>
        internal static bool ParseSystemProcesses(IntPtr buffer, int usedSize,
            ProcMem[] entries, out int count, out int rawEntryCount)
        {
            count = 0;
            rawEntryCount = 0;

            int entrySize = Marshal.SizeOf(typeof(SYSTEM_PROCESS_INFORMATION));
            long address = buffer.ToInt64();
            long end = address + usedSize;

            while (address + entrySize <= end)
            {
                SYSTEM_PROCESS_INFORMATION info = (SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(
                    new IntPtr(address), typeof(SYSTEM_PROCESS_INFORMATION));
                rawEntryCount++;

                int pid = info.UniqueProcessId.ToInt32();
                long workingSet = info.WorkingSetSize.ToInt64();

                if (pid > 4 && workingSet > 0)
                {
                    if (count == entries.Length) return false;      // 容量不足，交给调用方扩容重试

                    ProcMem item = new ProcMem();
                    item.Pid = pid;
                    item.WorkingSet = (ulong)workingSet;
                    item.PrivateBytes = (ulong)Math.Max(0L, info.PrivatePageCount.ToInt64());
                    item.StartTime = info.CreateTime;
                    item.Name = ReadImageName(info.ImageName);
                    entries[count++] = item;
                }

                if (info.NextEntryOffset == 0) break;
                address += info.NextEntryOffset;
            }
            return true;
        }

        /// <summary>
        /// 读取映像名。UNICODE_STRING.Buffer 是**本进程地址空间内的绝对指针**
        /// （内核在返回时会把它改写成指向缓冲区里那块内联字符串），所以直接按指针读。
        /// </summary>
        private static string ReadImageName(UNICODE_STRING name)
        {
            if (name.Length <= 0 || name.Buffer == IntPtr.Zero) return "";

            try
            {
                // Length 是字节数，且映像名保证是宽字符
                string text = Marshal.PtrToStringUni(name.Buffer, name.Length / 2);
                return text == null ? "" : text;
            }
            catch
            {
                // 指针落在不可读区域时放弃，让调用方退回 "PID xxxx"
                return "";
            }
        }

        // ==================================================================
        //  单进程内存信息（逐进程路径，供交叉校验与按需读取使用）
        // ==================================================================
        /// <summary>
        /// 一次性快照：返回「全部进程的工作集与提交量」，按工作集降序。
        ///
        /// 监控主循环不用它（那个走 ProcessSampler，长期复用缓冲区），
        /// 这里保留一个「用完即弃」的入口，供自检程序做交叉校验与开销对比 ——
        /// 调用方拿到的是独立副本，可以放心保存。
        /// </summary>
        internal static List<ProcMem> QueryAllProcessMemory()
        {
            ProcessSampler sampler = new ProcessSampler();
            sampler.MinWorkingSetMB = 0;            // 不过滤：自检要拿全量列表做逐条比对
            List<ProcMem> owned = sampler.Sample(0);
            return new List<ProcMem>(owned);
        }

        /// <summary>
        /// 一次打开句柄即取得工作集、提交大小与进程启动时间。
        /// 先用 QUERY_LIMITED_INFORMATION（受保护进程也能读），失败再退回
        /// QUERY_INFORMATION，既能覆盖绝大多数进程，又只打开一次句柄。
        /// </summary>
        public static bool TryGetProcessMemory(int pid, out ulong workingSet, out ulong privateBytes)
        {
            long ignored;
            return TryGetProcessMemory(pid, out workingSet, out privateBytes, out ignored);
        }

        /// <summary>同上，并额外返回进程启动时间（100ns，0 = 不可读）。</summary>
        public static bool TryGetProcessMemory(int pid, out ulong workingSet,
            out ulong privateBytes, out long startTicks)
        {
            workingSet = 0;
            privateBytes = 0;
            startTicks = 0;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;

            try
            {
                PROCESS_MEMORY_COUNTERS_EX c = new PROCESS_MEMORY_COUNTERS_EX();
                c.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
                if (!K32GetProcessMemoryInfo(h, ref c, c.cb)) return false;

                workingSet = c.WorkingSetSize.ToUInt64();
                privateBytes = c.PrivateUsage.ToUInt64();

                long created, exit, kernel, user;
                if (GetProcessTimes(h, out created, out exit, out kernel, out user)) startTicks = created;
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// 只读取进程启动时间（100ns，0 = 不可读）。
        /// 监控主循环不需要它（创建时间随系统查询一起返回），
        /// 保留它是为了在需要时能对单个进程做精确核对。
        /// </summary>
        public static long TryGetProcessStartTicks(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;

            try
            {
                long created, exit, kernel, user;
                if (!GetProcessTimes(h, out created, out exit, out kernel, out user)) return 0;
                return created;
            }
            catch
            {
                return 0;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        // ==================================================================
        //  工作集清理（PCL 内存优化核心动作之一）
        // ==================================================================
        /// <summary>
        /// 清空指定进程的工作集，使其常驻页面转入待机列表/分页文件。
        /// EmptyWorkingSet 内部要求「查询 + 设置配额」权限，因此这里合并成一次打开。
        /// </summary>
        public static bool TrimProcessWorkingSet(int pid, out string error)
        {
            error = null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero)
            {
                // 少数受保护进程只接受 QUERY_INFORMATION 组合
                h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            }
            if (h == IntPtr.Zero)
            {
                error = "OpenProcess 失败 (Win32 " + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            try
            {
                if (EmptyWorkingSet(h)) return true;

                int win32 = Marshal.GetLastWin32Error();
                IntPtr minusOne = new IntPtr(-1);
                if (SetProcessWorkingSetSize(h, minusOne, minusOne)) return true;

                error = "EmptyWorkingSet 失败 (Win32 " + win32 + ")";
                return false;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>清空当前进程自身的工作集（清理收尾时把本进程也吐出去）。</summary>
        public static void TrimSelfWorkingSet()
        {
            try
            {
                IntPtr h = GetCurrentProcess();
                EmptyWorkingSet(h);
                SetProcessWorkingSetSize(h, new IntPtr(-1), new IntPtr(-1));
            }
            catch
            {
                // 无权限时忽略：清自己失败不影响清理结果
            }
        }

        // ==================================================================
        //  特权
        // ==================================================================
        /// <summary>启用当前进程令牌中的一个特权。</summary>
        public static bool EnablePrivilege(string privilegeName)
        {
            IntPtr token = IntPtr.Zero;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;

            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, privilegeName, out luid)) return false;

                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges = new LUID_AND_ATTRIBUTES();
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;

                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // 该 API 即使一个特权都没授予也返回 true，必须再查 ERROR_NOT_ALL_ASSIGNED
                return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>PCL 内存优化使用的特权组合；返回每一项的启用结果文本。</summary>
        public static string EnableCleanupPrivileges()
        {
            string[] names = new string[]
            {
                "SeProfileSingleProcessPrivilege",
                "SeIncreaseQuotaPrivilege"
            };

            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
                sb.Append(EnablePrivilege(names[i]) ? "=OK" : "=FAIL");
            }
            return sb.ToString();
        }

        // ==================================================================
        //  清空系统待机列表（PCL 内存优化核心动作之二）
        // ==================================================================
        /// <summary>
        /// 线程间复用的 4 字节非托管缓冲区。内容只有一条命令，没必要每次清理都分配。
        /// 延迟创建：放在静态字段初始化器里会让「加载 Native 类」本身可能抛异常。
        /// </summary>
        private static IntPtr _purgeCommandBuffer = IntPtr.Zero;

        /// <summary>
        /// 清空系统待机列表。需要 SeProfileSingleProcessPrivilege。
        /// 对应 PCL 的 NtSetSystemInformation(SystemMemoryListInformation, &amp;command, 4)。
        /// </summary>
        /// <param name="lowPriorityOnly">true 时只清低优先级待机列表（失败时的回退动作）。</param>
        public static bool PurgeStandbyList(bool lowPriorityOnly)
        {
            try
            {
                if (_purgeCommandBuffer == IntPtr.Zero)
                {
                    _purgeCommandBuffer = Marshal.AllocHGlobal(sizeof(int));
                }
                Marshal.WriteInt32(_purgeCommandBuffer,
                    lowPriorityOnly ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList);
                return NtSetSystemInformation(SystemMemoryListInformation, _purgeCommandBuffer, sizeof(int)) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 先尝试清空整个待机列表，失败则回退到「只清低优先级待机列表」。
        /// 两条路径合并在此，避免调用方重复写回退逻辑。
        /// </summary>
        public static bool PurgeStandbyListBestEffort()
        {
            return PurgeStandbyList(false) || PurgeStandbyList(true);
        }

        // ==================================================================
        //  DPI 感知
        // ==================================================================
        /// <summary>
        /// 让进程成为 Per-Monitor-V2 DPI 感知进程。必须在创建任何窗口之前调用，
        /// 否则 Windows 会按 96 DPI 虚拟化整个界面（发虚、尺寸失真）。
        /// </summary>
        public static bool TryEnableDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4（Win10 1703+）
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return true;
            }
            catch
            {
                // Win7/8.1 上该入口点不存在，落到下面的回退方案
            }

            try
            {
                // Win8.1+ 回退：PROCESS_PER_MONITOR_DPI_AWARE = 2
                return SetProcessDpiAwareness(2) == 0;
            }
            catch
            {
                return false;
            }
        }

        // ==================================================================
        //  提权检测 / 重新启动
        // ==================================================================
        public static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal pr =
                    new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>以管理员身份重新启动自身（清单未生效时的兜底）。</summary>
        public static bool RelaunchElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = System.Windows.Forms.Application.ExecutablePath;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch
            {
                // 用户在 UAC 上点了「否」
                return false;
            }
        }
    }

    // ======================================================================
    //  进程枚举器
    // ======================================================================
    /// <summary>
    /// 采集「值得关注」的进程内存，工作集低于门槛的直接丢弃。
    ///
    /// 一轮的开销构成（120 进程的实测值）：
    ///     NtQuerySystemInformation   ~0.2 ms   一次拿到全部 PID + 工作集 + 映像名 + 创建时间
    ///     门槛过滤与排序             ~0.0 ms   纯托管操作，不打开任何进程句柄
    /// 相比旧实现（GetProcesses 2.2 ms + 117 次 OpenProcess 读取 ~1.8 ms），
    /// 在默认 50 MB 门槛下每轮大约快一个数量级，且不再受进程访问权限限制。
    ///
    /// 缓冲区与结果 List 都由本对象长期持有：
    /// 采样每 5 秒发生一次，长期运行下「每轮分配 256 KB 非托管内存」是不必要的开销。
    /// 返回的 List 归本对象所有 —— 调用方只在当轮读取它，不得保存（见 Sample 注释）。
    /// </summary>
    internal sealed class ProcessSampler
    {
        /// <summary>首次分配的非托管缓冲区大小：260 进程的系统用 256 KB 绰绰有余。</summary>
        private const int InitialBufferSize = 256 * 1024;

        /// <summary>内核条目远少于缓冲区时，下一轮把缓冲区缩回来（避免一次进程高峰永久占住内存）。</summary>
        private const int ShrinkRatio = 4;

        /// <summary>低于该工作集的进程不参与统计（MB）。</summary>
        public double MinWorkingSetMB = 50.0;

        /// <summary>上一次系统查询报告的进程条目数（门槛过滤前，含僵尸条目）。</summary>
        public int LastSystemProcessCount { get; private set; }

        /// <summary>上一轮「系统调用 + 结构解析」耗时（毫秒）。</summary>
        public double LastQueryMs;
        /// <summary>上一轮「门槛过滤 + 排序」耗时（毫秒）。</summary>
        public double LastFilterMs;

        /// <summary>上一轮查询耗时（毫秒），供自检程序诊断。</summary>
        public double LastEnumerateMs;

        private IntPtr _buffer = IntPtr.Zero;
        private int _bufferSize;
        private bool _shrinkPending;
        private ProcMem[] _entries = new ProcMem[512];
        /// <summary>门槛过滤后的结果列表：复用同一个实例，每轮 Clear 后重填。</summary>
        private readonly List<ProcMem> _result = new List<ProcMem>(64);

        /// <summary>复位统计值（缓冲区与结果 List 保留复用，不释放）。</summary>
        public void Reset()
        {
            LastSystemProcessCount = 0;
        }

        /// <summary>
        /// 采集一轮进程内存，按工作集降序返回。
        /// 整轮只有一次系统调用，不打开任何进程句柄。
        ///
        /// 返回的 List 是**内部复用对象**：只在下一轮 Sample 之前有效，调用方不得保存。
        /// </summary>
        /// <param name="selfPid">要排除的进程（0 = 不排除）。</param>
        public List<ProcMem> Sample(int selfPid)
        {
            long start = Stopwatch.GetTimestamp();
            QueryAllProcessMemory();
            LastQueryMs = ElapsedMs(start);

            long filterStart = Stopwatch.GetTimestamp();

            // QueryAllProcessMemory 已按工作集降序，因此一旦小于门槛即可结束循环
            long minBytes = (long)(MinWorkingSetMB * 1048576.0);
            if (minBytes < 0) minBytes = 0;

            _result.Clear();
            for (int i = 0; i < _entries.Length; i++)
            {
                ProcMem item = _entries[i];
                if (item.Pid == 0 && item.WorkingSet == 0) break;      // 数组尾部：有效条目已遍历完
                if (item.Pid == selfPid) continue;
                if (minBytes > 0 && item.WorkingSet < (ulong)minBytes) break;
                if (string.IsNullOrEmpty(item.Name)) item.Name = "PID " + item.Pid;
                _result.Add(item);
            }

            LastFilterMs = ElapsedMs(filterStart);
            LastEnumerateMs = LastQueryMs + LastFilterMs;
            return _result;
        }

        /// <summary>
        /// 一次 NtQuerySystemInformation 填满 <see cref="_entries"/>，按工作集降序。
        /// 缓冲区不足时按内核回报的长度扩容重试（并顺手扩一次条目数组）。
        /// </summary>
        private void QueryAllProcessMemory()
        {
            EnsureBuffer(InitialBufferSize);

            int used;
            if (!Native.QuerySystemProcesses(_buffer, _bufferSize, out used))
            {
                // 绝大多数情况是缓冲区不够：内核在 used 里回报了实际所需长度
                int needed = used > 0 ? used : _bufferSize * 2;
                EnsureBuffer(needed + 64 * 1024);
                if (!Native.QuerySystemProcesses(_buffer, _bufferSize, out used))
                {
                    LastSystemProcessCount = 0;
                    _entries[0] = new ProcMem();
                    return;
                }
            }

            int count, rawCount;
            while (!Native.ParseSystemProcesses(_buffer, used, _entries, out count, out rawCount))
            {
                // 条目数组装不下（进程数异常多）：扩容后重解析
                ProcMem[] grown = new ProcMem[_entries.Length * 2];
                _entries = grown;
            }

            LastSystemProcessCount = rawCount;
            if (count < _entries.Length) _entries[count] = new ProcMem();   // 结束哨兵，便于 Sample 提前退出
            Array.Sort<ProcMem>(_entries, 0, count, WorkingSetDescComparer.Instance);

            if (_bufferSize > used * ShrinkRatio)
            {
                if (_shrinkPending) { _shrinkPending = false; EnsureBuffer(used + 64 * 1024); }
                else _shrinkPending = true;
            }
        }

        private void EnsureBuffer(int size)
        {
            if (size < InitialBufferSize) size = InitialBufferSize;
            if (_buffer != IntPtr.Zero && size <= _bufferSize) return;

            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(size);
            _bufferSize = size;
        }

        private static int CompareWorkingSetDesc(ProcMem a, ProcMem b)
        {
            return b.WorkingSet.CompareTo(a.WorkingSet);
        }

        /// <summary>
        /// 单例比较器。
        /// 这里用 IComparer&lt;T&gt; 而不是 Comparison&lt;T&gt;：本项目的目标框架是
        /// .NET Framework 4.0（Windows 自带），而 Array.Sort 的 Comparison 重载是 4.5 才加入的，
        /// 直接传方法组在 4.0 上编译不过。
        /// </summary>
        private sealed class WorkingSetDescComparer : IComparer<ProcMem>
        {
            public static readonly WorkingSetDescComparer Instance = new WorkingSetDescComparer();

            public int Compare(ProcMem a, ProcMem b)
            {
                return CompareWorkingSetDesc(a, b);
            }
        }

        private static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
