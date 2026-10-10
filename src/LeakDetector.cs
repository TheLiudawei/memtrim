// ============================================================================
//  MemTrim · LeakDetector.cs —— 双通道内存泄漏检测
//
//  通道 A「系统压力」：整机已用/可用内存的时间趋势（线性回归斜率 + R²）
//  通道 B「进程趋势」：对每个进程的工作集做滑动窗口采样，用最小二乘线性回归
//                     求增长斜率(MB/分钟) 与拟合优度 R²，斜率持续为正且线性度高
//                     者判定为「疑似泄漏进程」。
//
//  性能约定（每 5 秒执行一次的热路径）：
//      · 样本用环形缓冲保存，淘汰旧样本不产生任何数组搬移；
//      · 环形缓冲按需增长：绝大多数进程只会积累几十个样本，
//        没必要一上来就为它分配整个窗口的容量（见 SampleRing<T>）；
//      · 回归统计量在遍历样本的过程中一次算完，不构造 x[]/y[] 临时数组，
//        且系统通道与进程通道共用同一个 LinearFit（此前系统通道复制了一份
//        手写回归，两份代码已经出现行为差异，见 AnalyzeSystemChannel 注释）；
//      · 进程名随采样一起记录，Analyze 阶段不再按 PID 反查进程。
//
//  正确性约定：
//      · 回归全部用「以均值为中心」的累加式，避免 Σx² 与 (Σx)² 相减的灾难性抵消；
//      · 用进程创建时间识别 PID 复用（内核在 SYSTEM_PROCESS_INFORMATION 里直接给出，
//        不需要打开任何进程句柄），防止「旧样本 + 新进程」被拼成虚假泄漏。
//
//  本文件不引用任何 Win32 API，只处理数据：采集在 Interop.cs，展示在 Ui.cs。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace MemTrim
{
    // ======================================================================
    //  样本
    // ======================================================================
    /// <summary>单个进程的一次采样点。</summary>
    internal struct SamplePoint
    {
        /// <summary>采样时刻（Environment.TickCount，毫秒）。</summary>
        public int Tick;
        /// <summary>工作集（MB）。</summary>
        public double WorkingSetMB;
        /// <summary>提交大小（MB）。</summary>
        public double PrivateMB;
    }

    /// <summary>
    /// 整机内存的一次采样点。
    /// 与 <see cref="SamplePoint"/> 分开定义（而不是复用它的两个字段），
    /// 是为了让「已用/可用」两个数字在代码里就叫这个名字 ——
    /// 此前系统通道借 SamplePoint.WorkingSetMB 存「已用」、PrivateMB 存「可用」，
    /// 读代码的人必须在脑子里做一次翻译。
    /// </summary>
    internal struct SystemSample
    {
        /// <summary>采样时刻（Environment.TickCount，毫秒）。</summary>
        public int Tick;
        /// <summary>系统已用物理内存（MB）。</summary>
        public double UsedMB;
        /// <summary>系统可用物理内存（MB）。</summary>
        public double AvailMB;
    }

    // ======================================================================
    //  环形缓冲
    // ======================================================================
    /// <summary>
    /// 定长环形缓冲：保存最近 N 个采样点，写满后自动覆盖最旧的。
    /// 用环形结构而非 List，是为了避免「淘汰队首」导致的整段数组搬移与反复分配。
    ///
    /// 容量按需增长：初值只有 <see cref="InitialCapacity"/> 个槽位，样本条数追上来时才翻倍。
    /// 这样做的收益是可观的 —— 默认参数（窗口 10 分钟 / 间隔 5 秒）下满容量是 154 个槽位、
    /// 每个跟踪进程约 3.7 KB；而现实中大多数进程只会在窗口里积累几十个样本
    /// （工作集掉到门槛之下或进程退出就会停止采样），按需增长能省掉其中大部分内存。
    /// </summary>
    /// <typeparam name="T">样本类型（SamplePoint 或 SystemSample）。</typeparam>
    internal sealed class SampleRing<T>
    {
        /// <summary>初始槽位数。够放几轮样本，又不至于为短命进程浪费内存。</summary>
        private const int InitialCapacity = 16;

        private T[] _buf;
        private int _head;    // 下一个要覆盖的位置（写满之后才有意义）
        private int _count;
        /// <summary>槽位数上限（= 构造时请求的容量），留存的样本不会超过这个数。</summary>
        private readonly int _capacity;
        /// <summary>
        /// 取样本时间戳的委托，由调用方在构造时给出。
        ///
        /// 为什么不让环形缓冲自己去猜：泛型 T 可能是 SamplePoint 也可能是 SystemSample，
        /// 靠 <c>typeof(T)</c> + 强制转换来分派，一旦类型对不上就会静默返回错误的时间戳。
        /// 交给调用方一个取值函数，既没有转换，也没有「看起来能用」的隐患。
        /// </summary>
        private readonly Func<T, int> _tickOf;
        /// <summary>最新样本的时间戳（由 Add 维护，避免读取时再算一遍下标）。</summary>
        private int _newestTick;

        /// <summary>
        /// 新建一个环形缓冲。
        /// </summary>
        /// <param name="capacity">最多保留多少个样本。</param>
        /// <param name="tickOf">
        /// 如何取出样本时间戳。用于维护 NewestTick 与按时间淘汰旧样本。
        /// 传 null 时这两项功能不可用（样本本身仍然正常保存），仅供不关心时间的测试使用。
        /// </param>
        public SampleRing(int capacity, Func<T, int> tickOf)
        {
            _capacity = capacity < 2 ? 2 : capacity;
            _tickOf = tickOf;
            _buf = new T[_capacity < InitialCapacity ? _capacity : InitialCapacity];
        }

        /// <summary>当前实际槽位数（初始很小，随样本增多增长到构造时请求的容量为止）。</summary>
        public int Capacity { get { return _buf.Length; } }

        /// <summary>构造时请求的容量上限。</summary>
        public int CapacityLimit { get { return _capacity; } }

        /// <summary>当前样本条数。</summary>
        public int Count { get { return _count; } }

        /// <summary>最新样本的时间戳；空缓冲返回 0。</summary>
        public int NewestTick { get { return _count == 0 ? 0 : _newestTick; } }

        /// <summary>按时间顺序（旧 → 新）读取第 i 个样本。i 越界返回 default(T)。</summary>
        public T this[int i]
        {
            get
            {
                if (i < 0 || i >= _count) return default(T);
                int index = _head - _count + i;
                index %= _buf.Length;
                if (index < 0) index += _buf.Length;
                return _buf[index];
            }
        }

        /// <summary>
        /// 写入一个样本。两种状态，各自只有一种写法：
        ///   · 还没写满 → 追加到 [count]；
        ///   · 已经写满 → 环形覆盖最旧的 [_head]。
        /// 未写满时 [0, count) 总是按时间顺序排列的，因此 head 要么等于 count，要么指向最旧的一条。
        /// </summary>
        public void Add(T point)
        {
            if (_count < _buf.Length)
            {
                _buf[_count] = point;
                _count++;
                _head = _count == _buf.Length ? 0 : _count;
            }
            else if (_buf.Length < _capacity)
            {
                // 目标容量在这里算清楚，再交给 Grow 搬运：
                // 让「搬到多大」只依赖调用点的状态，Grow 内部不再做任何判断。
                int target = _buf.Length * 2;
                if (target < _buf.Length || target > _capacity) target = _capacity;
                Grow(target);
                _buf[_count] = point;
                _count++;
                _head = _count == _buf.Length ? 0 : _count;
            }
            else
            {
                // 已到请求容量：覆盖最旧的一条
                _buf[_head] = point;
                _head++;
                if (_head == _buf.Length) _head = 0;
            }

            if (_tickOf != null) _newestTick = _tickOf(point);
        }

        /// <summary>丢弃所有早于 beforeTick 的样本（样本按时间递增，可提前退出）。</summary>
        public void DropOlderThan(int beforeTick)
        {
            while (_count > 0 && _tickOf != null && _tickOf(this[0]) < beforeTick)
            {
                _count--;
            }
        }

        public void Clear()
        {
            _count = 0;
            _head = 0;
            _newestTick = 0;
        }

        /// <summary>
        /// 把槽位数扩到 target（不超过构造时请求的容量），并把现有样本按时间顺序搬到新数组。
        ///
        /// 两条硬性约定：
        ///   · **_count 不在这里改** —— 搬运只是换了一块更大的数组，已采到的样本一条都没少；
        ///     曾经把 count 归零过，结果是每次扩容都静默丢掉整个滑动窗口
        ///     （窗口看上去依然「有序」，极难发现）。
        ///   · **先翻倍再夹到容量** —— 只允许往上走。反过来先夹再判上限时，
        ///     一旦容量小于现有长度就会把目标夹得比现在还小，于是最后一档永远长不上去，
        ///     缓冲停在 2 的幂（如 128）而不是请求容量（如 154）。
        /// </summary>
        private void Grow(int target)
        {
            if (target <= _buf.Length) return;

            int kept = _count;
            T[] grown = new T[target];
            for (int i = 0; i < kept; i++) grown[i] = this[i];
            _buf = grown;
            _head = 0;      // 搬运后样本落在 [0, count)，head 指向最旧的一条
        }
    }

    /// <summary>
    /// 线性回归的取值接口：只暴露「第 i 个样本的 X（时间，分钟）与 Y（内存，MB）」。
    /// 有了它，<see cref="LinearFit.Compute"/> 可以同时服务于
    /// 进程通道（SamplePoint）与系统通道（SystemSample），不必复制两份回归代码。
    /// </summary>
    internal interface IFitSource
    {
        int Count { get; }
        double TimeMinutesAt(int index);
        double ValueAt(int index);
    }

    /// <summary>把进程样本包装成回归数据源（时间轴 = 相对 nowMs 的分钟数）。</summary>
    internal sealed class ProcessFitSource : IFitSource
    {
        private readonly SampleRing<SamplePoint> _ring;
        private readonly int _nowMs;

        public ProcessFitSource(SampleRing<SamplePoint> ring, int nowMs)
        {
            _ring = ring;
            _nowMs = nowMs;
        }

        public int Count { get { return _ring.Count; } }
        public double TimeMinutesAt(int index) { return (_ring[index].Tick - _nowMs) / 60000.0; }
        public double ValueAt(int index) { return _ring[index].WorkingSetMB; }
    }

    /// <summary>把整机样本包装成回归数据源（Y = 可用内存）。</summary>
    internal sealed class SystemFitSource : IFitSource
    {
        private readonly SampleRing<SystemSample> _ring;
        private readonly int _nowMs;

        public SystemFitSource(SampleRing<SystemSample> ring, int nowMs)
        {
            _ring = ring;
            _nowMs = nowMs;
        }

        public int Count { get { return _ring.Count; } }
        public double TimeMinutesAt(int index) { return (_ring[index].Tick - _nowMs) / 60000.0; }
        public double ValueAt(int index) { return _ring[index].AvailMB; }
    }

    /// <summary>
    /// 跟踪中的进程状态。Samples 是字段（不是属性）：
    /// tools\SelfTest.cs 通过反射按字段名读取它来断言样本条数。
    /// </summary>
    internal sealed class TrackedProcess
    {
        public int Pid;
        public string Name;
        /// <summary>进程创建时间（FILETIME，100ns；0 = 内核未给出）。用于识别 PID 复用。</summary>
        public long StartTime;
        /// <summary>上一轮是否真的取到了样本（用于淘汰长期缺席的进程）。</summary>
        public bool Alive;
        public readonly SampleRing<SamplePoint> Samples;

        public TrackedProcess(int capacity)
        {
            Samples = new SampleRing<SamplePoint>(capacity, TickOf);
        }

        /// <summary>取样本时间戳（供环形缓冲维护 NewestTick 与淘汰旧样本）。</summary>
        private static int TickOf(SamplePoint p)
        {
            return p.Tick;
        }
    }

    /// <summary>单个进程的泄漏分析结果（一次 Analyze 生成，之后不再修改）。</summary>
    internal sealed class LeakItem
    {
        public int Pid;
        public string Name;
        /// <summary>当前工作集（MB）。</summary>
        public double WorkingSetMB;
        /// <summary>当前提交大小（MB）。</summary>
        public double PrivateMB;
        /// <summary>工作集增长斜率（MB/分钟）。</summary>
        public double SlopeMBPerMin;
        /// <summary>线性拟合优度 0~1。</summary>
        public double R2;
        /// <summary>窗口内总增长（MB）。</summary>
        public double GrowthMB;
        /// <summary>综合泄漏评分（越大越可疑）。</summary>
        public double Score;
        /// <summary>窗口数据是否已足够参与趋势判定。</summary>
        public bool HasTrend;
        /// <summary>是否被判定为疑似泄漏。</summary>
        public bool IsLeaking;

        public string DisplayName
        {
            get { return Name + " (" + Pid + ")"; }
        }
    }

    /// <summary>一轮分析的完整结果。由 LeakDetector.Analyze 生成，之后不再修改。</summary>
    internal sealed class LeakReport
    {
        public MemSnapshot Memory;
        /// <summary>可用内存斜率（负值 = 系统可用内存持续减少）。</summary>
        public double SystemAvailSlopeMBPerMin;
        /// <summary>系统趋势的线性拟合优度（0~1）。</summary>
        public double SystemTrendR2;
        /// <summary>窗口内系统已用内存增量（MB）。</summary>
        public double SystemUsedGrowthMB;
        /// <summary>当前压力百分比（提交内存口径）。</summary>
        public int SystemPressurePercent;
        /// <summary>压力值取自哪个口径，用于界面文案。</summary>
        public string PressureSource = "提交内存";
        /// <summary>是否达到压力阈值。</summary>
        public bool UnderPressure;
        /// <summary>系统层面是否呈现持续减少趋势。</summary>
        public bool SystemTrending;
        /// <summary>是否有进程被判为泄漏。</summary>
        public bool LeakDetected;
        /// <summary>判定为泄漏的进程（按评分降序，最多 topCount 个）。</summary>
        public List<LeakItem> TopLeaks = new List<LeakItem>();
        /// <summary>工作集最高的进程。</summary>
        public List<LeakItem> TopConsumers = new List<LeakItem>();
        /// <summary>趋势样本不足时的即时占用排行，保证界面早期也能显示内容。</summary>
        public List<LeakItem> RecentConsumers = new List<LeakItem>();
    }

    /// <summary>
    /// 最小二乘线性回归结果。用「以均值为中心」的累加式一次算出斜率与 R²：
    ///   斜率 = Σ(dx·dy) / Σ(dx²)，R² = 1 − SSres/SStot，SStot = Σ(dy²)
    /// 相比 Σx² − (Σx)²/n 的写法，这里的量级都在数据自身波动范围内，不会相互抵消。
    /// </summary>
    internal struct LinearFit
    {
        public double Slope;
        public double R2;

        /// <summary>样本少于 3 个时无法给出可信趋势。</summary>
        public const int MinPoints = 3;

        /// <summary>
        /// 对数据源做回归。x 轴只影响截距不影响斜率与 R²，
        /// 因此调用方随便选一个时间原点（通常是 now）即可。
        /// </summary>
        public static LinearFit Compute(IFitSource source)
        {
            LinearFit fit = new LinearFit();
            int n = source == null ? 0 : source.Count;
            if (n < MinPoints) return fit;

            if (source.TimeMinutesAt(n - 1) <= source.TimeMinutesAt(0)) return fit;   // 时间轴无跨度

            double sumX = 0, sumY = 0;
            for (int i = 0; i < n; i++)
            {
                sumX += source.TimeMinutesAt(i);
                sumY += source.ValueAt(i);
            }
            double meanX = sumX / n;
            double meanY = sumY / n;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = source.TimeMinutesAt(i) - meanX;
                double dy = source.ValueAt(i) - meanY;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
            }

            if (sxx <= 1e-12) return fit;           // 时间轴无跨度
            fit.Slope = sxy / sxx;
            if (syy <= 1e-9) return fit;            // 数值全程纹丝不动：R² 无意义，按 0 处理
            fit.R2 = 1.0 - (syy - fit.Slope * sxy) / syy;
            if (fit.R2 < 0) fit.R2 = 0;
            if (fit.R2 > 1) fit.R2 = 1;
            return fit;
        }
    }

    // ======================================================================
    //  检测器
    // ======================================================================
    internal sealed class LeakDetector
    {
        // ---- 阈值配置（由界面写入）----
        /// <summary>滑动窗口长度（分钟）。</summary>
        public double WindowMinutes = 10.0;
        /// <summary>判定为泄漏的最小增长斜率（MB/分钟）。</summary>
        public double LeakSlopeThreshold = 2.0;
        /// <summary>判定为泄漏所需的最小线性拟合优度。</summary>
        public double LeakR2Threshold = 0.55;
        /// <summary>判定泄漏所需的最少采样点数。</summary>
        public int MinSamples = 5;
        /// <summary>判定泄漏所需的最短时间跨度（分钟）。</summary>
        public double MinSpanMinutes = 2.0;
        /// <summary>低于该工作集的进程不参与统计（MB）。</summary>
        public double MinWorkingSetMB = 50.0;
        /// <summary>触发自动清理的压力阈值（%）。</summary>
        public int PressureThreshold = 85;
        /// <summary>系统可用内存持续下降的判定斜率（MB/分钟）。</summary>
        public double SystemSlopeThreshold = -8.0;
        /// <summary>系统趋势判定所需的最小拟合优度。</summary>
        public double SystemR2Threshold = 0.5;

        private readonly Dictionary<int, TrackedProcess> _tracked = new Dictionary<int, TrackedProcess>();
        private readonly List<int> _dead = new List<int>();
        private readonly SampleRing<SystemSample> _sysRing =
            new SampleRing<SystemSample>(256, TickOf);

        /// <summary>取整机样本时间戳。</summary>
        private static int TickOf(SystemSample s)
        {
            return s.Tick;
        }
        private readonly ProcessSampler _sampler = new ProcessSampler();

        private int _ringCapacity = 256;
        private int _intervalMs;

        /// <summary>最近一次 Sample() 取到的整机快照，供 Analyze 复用，省掉一次重复调用。</summary>
        private MemSnapshot _lastSnapshot;

        /// <summary>已采集的系统样本轮数。</summary>
        public int SystemSampleCount { get { return _sysRing.Count; } }

        /// <summary>当前在跟踪的进程数（含本轮未取到样本者）。</summary>
        public int TrackedCount { get { return _tracked.Count; } }

        /// <summary>最近一轮系统报告的进程总数（门槛过滤前）。</summary>
        public int LastSystemProcessCount { get { return _sampler.LastSystemProcessCount; } }

        /// <summary>
        /// 注入一条人造样本，仅供自检程序（tools\SelfTest.cs）做确定性回归测试。
        /// 正式运行路径不会调用它：真实数据一律经 Sample() 采集。
        /// startTicks 为 0 表示「无法获知进程身份」，此时会退回工作集断崖启发式。
        /// </summary>
        internal void InjectSampleForTest(int pid, string name,
            int tick, double workingSetMB, double privateMB)
        {
            InjectSampleForTest(pid, name, tick, workingSetMB, privateMB, 0);
        }

        /// <summary>同上，并可显式指定进程创建时间（用于验证 PID 复用判定）。</summary>
        internal void InjectSampleForTest(int pid, string name,
            int tick, double workingSetMB, double privateMB, long startTicks)
        {
            // 走与真实采样完全相同的写入路径（含 PID 复用判定），保证测试结论有意义
            AppendSample(pid, name, startTicks, tick, workingSetMB, privateMB, 0);
        }

        /// <summary>当前环形缓冲容量，仅供自检程序验证容量推导。</summary>
        internal int RingCapacityForTest { get { return _ringCapacity; } }

        /// <summary>
        /// 对一组人造数据做回归，仅供自检程序验证 LinearFit 的数值行为
        /// （正式路径用的是已经算好的 SampleRing，不需要 double[]）。
        /// </summary>
        internal LinearFit FitForTest(double[] xs, double[] ys)
        {
            int n = xs == null || ys == null ? 0 : Math.Min(xs.Length, ys.Length);
            SampleRing<SamplePoint> ring = new SampleRing<SamplePoint>(n < 2 ? 2 : n, p => p.Tick);
            for (int i = 0; i < n; i++)
            {
                SamplePoint p = new SamplePoint();
                p.Tick = (int)Math.Round(xs[i] * 60000.0);
                p.WorkingSetMB = ys[i];
                ring.Add(p);
            }
            return LinearFit.Compute(new ProcessFitSource(ring, 0));
        }

        /// <summary>
        /// 采样间隔或窗口长度变化后调用：重算环形缓冲容量。
        /// 容量按「窗口 / 间隔 × 1.25 + 4」推导，够用即可，避免长时间运行内存膨胀。
        /// </summary>
        public void PrepareSamples(int intervalSeconds)
        {
            _intervalMs = Math.Max(1, intervalSeconds) * 1000;

            double spanMs = Math.Max(1.0, WindowMinutes) * 60000.0;
            // 分母转成 double：整数除法会把 3600000/2000 截成 1800，容量凭空少一格
            int need = (int)Math.Ceiling(spanMs / (double)_intervalMs * 1.25) + 4;
            if (need < 8) need = 8;
            if (need > 4096) need = 4096;
            _ringCapacity = need;

            _sampler.MinWorkingSetMB = MinWorkingSetMB;
        }

        /// <summary>清空全部采样数据（改配置或用户手动重置时调用）。</summary>
        public void Reset()
        {
            _tracked.Clear();
            _sysRing.Clear();
            _sampler.Reset();
        }

        /// <summary>
        /// 采集一次样本。本方法只在 UI 线程调用，因此内部不做加锁。
        /// </summary>
        public void Sample(int selfPid)
        {
            int now = Environment.TickCount;
            _lastSnapshot = Native.GetMemorySnapshot();
            _sampler.MinWorkingSetMB = MinWorkingSetMB;
            List<ProcMem> procs = _sampler.Sample(selfPid);

            int cutoff = now - (int)(Math.Max(1.0, WindowMinutes) * 60000.0);

            for (int i = 0; i < procs.Count; i++)
            {
                ProcMem pm = procs[i];
                AppendSample(pm.Pid, pm.Name, pm.StartTime, now,
                    pm.WorkingSet / 1048576.0, pm.PrivateBytes / 1048576.0, cutoff);
            }

            RetireStaleProcesses(cutoff);

            SystemSample sys = new SystemSample();
            sys.Tick = now;
            sys.UsedMB = _lastSnapshot.UsedMB;
            sys.AvailMB = _lastSnapshot.AvailMB;
            _sysRing.Add(sys);
            _sysRing.DropOlderThan(cutoff);
        }

        /// <summary>把一条样本写入跟踪表，并顺带处理 PID 复用与窗口淘汰。</summary>
        private void AppendSample(int pid, string name, long startTicks, int tick,
            double workingSetMB, double privateMB, int cutoff)
        {
            TrackedProcess tracked;
            if (!_tracked.TryGetValue(pid, out tracked))
            {
                tracked = new TrackedProcess(_ringCapacity);
                _tracked[pid] = tracked;
            }
            else if (IsReusedPid(tracked, startTicks, workingSetMB))
            {
                // PID 被复用：窗口里的旧样本属于上一个进程，全部作废
                tracked.Samples.Clear();
            }

            tracked.Pid = pid;
            tracked.Name = name;
            if (startTicks > 0) tracked.StartTime = startTicks;
            tracked.Alive = true;

            SamplePoint point = new SamplePoint();
            point.Tick = tick;
            point.WorkingSetMB = workingSetMB;
            point.PrivateMB = privateMB;
            tracked.Samples.Add(point);
            if (cutoff != 0) tracked.Samples.DropOlderThan(cutoff);
        }

        /// <summary>
        /// 把长期没有样本的进程移出跟踪表。
        ///
        /// 判据是「最新样本已经滑出窗口」而不是「连续两轮没采到」：
        /// 后者会把「工作集临时掉到门槛之下」的进程误当成已退出并丢掉整个窗口，
        /// 而那恰恰是最需要保留历史趋势的情形（进程掉下去又涨回来 = 典型泄漏形态）。
        /// 环形缓冲的空间本来就会随窗口淘汰旧样本，所以放宽淘汰条件不会让内存膨胀。
        /// </summary>
        private void RetireStaleProcesses(int cutoff)
        {
            if (_tracked.Count == 0) return;

            _dead.Clear();
            foreach (KeyValuePair<int, TrackedProcess> kv in _tracked)
            {
                kv.Value.Alive = false;
                if (kv.Value.Samples.NewestTick < cutoff) _dead.Add(kv.Key);
            }
            for (int i = 0; i < _dead.Count; i++) _tracked.Remove(_dead[i]);
        }

        /// <summary>
        /// 判断同一个 PID 是否已经换成了另一个进程。
        ///
        /// 首选判据是进程创建时间：内核在 SYSTEM_PROCESS_INFORMATION 里直接给出该字段，
        /// 与工作集一起在同一个系统调用里返回，因此这个判据是**精确**的，
        /// 且同样不需要打开任何进程句柄（已用 Process.StartTime 交叉验证过逐条相等）。
        ///
        /// 只有在内核没给出创建时间时才退回「工作集断崖」启发式：
        /// 新进程的工作集通常远小于被复用的那个进程留下的峰值。
        /// 该启发式只覆盖「变小」的方向 —— 若新进程一上来就比旧的大，
        /// 旧样本会让回归算出一条陡峭的上升直线并被误判为泄漏；精确判据没有这个盲区。
        /// </summary>
        private static bool IsReusedPid(TrackedProcess tracked, long startTicks, double workingSetMB)
        {
            if (startTicks > 0 && tracked.StartTime > 0)
            {
                return startTicks != tracked.StartTime;
            }
            return LooksLikeWorkingSetCliff(tracked, workingSetMB);
        }

        /// <summary>工作集断崖启发式（仅在拿不到创建时间时使用）。</summary>
        private static bool LooksLikeWorkingSetCliff(TrackedProcess tracked, double workingSetMB)
        {
            int count = tracked.Samples.Count;
            if (count == 0 || workingSetMB <= 0) return false;

            double previous = tracked.Samples[count - 1].WorkingSetMB;
            return workingSetMB < previous * 0.5;
        }

        /// <summary>
        /// 分析当前数据并生成报告。本方法只在 UI 线程调用，内部不做加锁。
        /// </summary>
        /// <param name="topCount">泄漏列表与占用列表各自保留的条目数。</param>
        public LeakReport Analyze(int topCount)
        {
            if (topCount < 1) topCount = 1;

            int now = Environment.TickCount;
            LeakReport report = new LeakReport();
            report.Memory = _lastSnapshot;
            report.SystemPressurePercent = _lastSnapshot.PressurePercent;
            report.PressureSource = _lastSnapshot.PressureSource;
            report.UnderPressure = report.SystemPressurePercent >= PressureThreshold;

            AnalyzeSystemChannel(report, now);

            List<LeakItem> items = new List<LeakItem>(_tracked.Count);
            AnalyzeProcessChannel(items, now);

            // 以「最新工作集」降序排好，供 TopConsumers 与 RecentConsumers 复用同一个数组
            items.Sort(CompareByWorkingSetDesc);

            int consumers = topCount > 16 ? topCount : 16;
            for (int i = 0; i < items.Count; i++)
            {
                LeakItem it = items[i];
                if (it.IsLeaking) report.TopLeaks.Add(it);        // 已按评分降序
                if (report.TopConsumers.Count < topCount) report.TopConsumers.Add(it);
                if (report.RecentConsumers.Count < consumers) report.RecentConsumers.Add(it);
            }

            // 泄漏列表需要按「评分」而不是「工作集」排序
            report.TopLeaks.Sort(CompareByScoreDesc);
            report.LeakDetected = report.TopLeaks.Count > 0;
            if (report.TopLeaks.Count > topCount) report.TopLeaks.RemoveRange(topCount, report.TopLeaks.Count - topCount);

            return report;
        }

        /// <summary>
        /// 系统通道：对「可用内存」序列做回归，斜率为负表示系统可用内存在持续减少。
        /// 判定要求斜率与 R² 同时达标，与进程通道保持同一套口径。
        /// </summary>
        private void AnalyzeSystemChannel(LeakReport report, int now)
        {
            report.SystemAvailSlopeMBPerMin = 0;
            report.SystemTrendR2 = 0;
            report.SystemUsedGrowthMB = 0;
            report.SystemTrending = false;

            int n = _sysRing.Count;
            if (n < LinearFit.MinPoints) return;
            if (_sysRing[n - 1].Tick <= _sysRing[0].Tick) return;      // TickCount 回绕保护

            double span = (_sysRing[n - 1].Tick - _sysRing[0].Tick) / 60000.0;
            if (span < MinSpanMinutes) return;

            LinearFit fit = LinearFit.Compute(new SystemFitSource(_sysRing, now));
            report.SystemAvailSlopeMBPerMin = fit.Slope;
            report.SystemTrendR2 = fit.R2;
            report.SystemUsedGrowthMB = _sysRing[n - 1].UsedMB - _sysRing[0].UsedMB;
            report.SystemTrending = fit.Slope <= SystemSlopeThreshold && fit.R2 >= SystemR2Threshold;
        }

        private void AnalyzeProcessChannel(List<LeakItem> items, int now)
        {
            foreach (KeyValuePair<int, TrackedProcess> kv in _tracked)
            {
                TrackedProcess tp = kv.Value;
                SampleRing<SamplePoint> ring = tp.Samples;
                int n = ring.Count;
                if (n < 2) continue;

                LeakItem item = new LeakItem();
                item.Pid = tp.Pid;
                item.Name = tp.Name;
                item.WorkingSetMB = ring[n - 1].WorkingSetMB;
                item.PrivateMB = ring[n - 1].PrivateMB;
                item.GrowthMB = ring[n - 1].WorkingSetMB - ring[0].WorkingSetMB;

                // 趋势判定需要足够的轮数与时间跨度，否则宁可不给结论
                if (n >= MinSamples && SpanMinutes(ring, n) >= MinSpanMinutes)
                {
                    LinearFit fit = LinearFit.Compute(new ProcessFitSource(ring, now));
                    if (fit.Slope != 0 || fit.R2 != 0)
                    {
                        item.HasTrend = true;
                        item.SlopeMBPerMin = fit.Slope;
                        item.R2 = fit.R2;

                        // 评分 = 斜率 × 线性度，再叠加窗口内绝对增长（长期缓慢泄漏也能浮上来）
                        double score = fit.Slope * fit.R2;
                        if (item.GrowthMB > 0) score += item.GrowthMB * 0.05;
                        item.Score = score;

                        item.IsLeaking = fit.Slope >= LeakSlopeThreshold && fit.R2 >= LeakR2Threshold;
                    }
                }

                items.Add(item);
            }
        }

        /// <summary>采样序列覆盖的时间跨度（分钟）。</summary>
        private static double SpanMinutes(SampleRing<SamplePoint> ring, int count)
        {
            if (count < 2) return 0;
            int span = ring[count - 1].Tick - ring[0].Tick;
            if (span <= 0) return 0;    // TickCount 回绕保护
            return span / 60000.0;
        }

        private static int CompareByWorkingSetDesc(LeakItem a, LeakItem b)
        {
            int c = b.WorkingSetMB.CompareTo(a.WorkingSetMB);
            if (c != 0) return c;
            return a.Pid.CompareTo(b.Pid);        // 工作集相同时按 PID 定序，保证输出稳定
        }

        private static int CompareByScoreDesc(LeakItem a, LeakItem b)
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            return CompareByWorkingSetDesc(a, b);
        }
    }
}
