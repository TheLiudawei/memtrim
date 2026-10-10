// ============================================================================
//  MemTrim · AppState.cs —— 配置、清理历史、开机自启动
//
//  三块内容都围绕 %APPDATA%\MemTrim\ 这一个目录：
//      config.json   应用配置（本文件用迷你 JSON 读写，不引入第三方库）
//      history.tsv   每次清理一行，制表符分隔，便于用户直接用 Excel 打开
//
//  设计取舍：为了保持「单文件 exe + 零依赖」，这里手写 JSON 解析，
//  只支持本程序配置所需的扁平对象；解析器对畸形输入一律「退回默认值」而不是抛异常。
//
//  读写约定：
//      · 所有写入都是「临时文件 + File.Replace」的原子替换，
//        不会出现「先删除再改名」中间失败导致配置消失的窗口；
//      · 累计统计不在每次清理时重扫整个历史文件，而是启动时读一次、
//        之后按「最新一条记录的增量」累加，历史文件再大也不影响单次清理；
//      · 历史文件超过上限时自动裁剪，长期运行不会无限增长。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MemTrim
{
    /// <summary>应用配置，界面可调。字段名即 JSON 键名，改动需兼容旧配置。</summary>
    internal sealed class AppConfig
    {
        /// <summary>自动清理总开关。</summary>
        public bool AutoClean = true;
        /// <summary>内置冷却（CD）：两次自动清理之间的最小间隔（秒）。</summary>
        public int CooldownSeconds = 300;
        /// <summary>内存压力达到该百分比即视为高压力（按「已提交/提交上限」计）。</summary>
        public int PressureThreshold = 85;
        /// <summary>进程增长斜率超过该值(MB/min)且线性度高，判定为泄漏。</summary>
        public double LeakSlopeThreshold = 2.0;
        /// <summary>泄漏判定所需的最小线性拟合优度。</summary>
        public double LeakR2Threshold = 0.55;
        /// <summary>滑动窗口长度（分钟）。</summary>
        public int WindowMinutes = 10;
        /// <summary>每次清理的泄漏进程数量上限。</summary>
        public int TopLeakCount = 5;
        /// <summary>清理时是否清空系统待机列表。</summary>
        public bool PurgeStandbyList = true;
        /// <summary>是否清理泄漏进程的工作集。</summary>
        public bool TrimLeakProcesses = true;
        /// <summary>自动清理时是否弹托盘通知。</summary>
        public bool NotifyOnAuto = true;
        /// <summary>开机自启动。</summary>
        public bool AutoStart;
        /// <summary>关闭窗口时最小化到托盘。</summary>
        public bool CloseToTray = true;
        /// <summary>采样间隔（秒）。</summary>
        public int SampleSeconds = 5;
        /// <summary>低于该工作集的进程不参与统计（MB）。</summary>
        public double MinWorkingSetMB = 50.0;

        /// <summary>配置文件绝对路径。</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(AppPaths.DataDir, "config.json"); }
        }

        /// <summary>复制一份配置。设置对话框在副本上编辑，点「取消」时原配置不受影响。</summary>
        public AppConfig Clone()
        {
            return (AppConfig)MemberwiseClone();
        }

        /// <summary>把各项参数夹到合法区间，越界的静默修正。</summary>
        public void Sanitize()
        {
            CooldownSeconds = Clamp(CooldownSeconds, 30, 86400);
            PressureThreshold = Clamp(PressureThreshold, 30, 99);
            LeakSlopeThreshold = Clamp(LeakSlopeThreshold, 0.1, 500.0);
            LeakR2Threshold = Clamp(LeakR2Threshold, 0.0, 0.99);
            WindowMinutes = Clamp(WindowMinutes, 2, 60);
            TopLeakCount = Clamp(TopLeakCount, 1, 20);
            SampleSeconds = Clamp(SampleSeconds, 2, 60);
            MinWorkingSetMB = Clamp(MinWorkingSetMB, 1.0, 4096.0);
        }

        /// <summary>从磁盘读取配置；文件不存在或损坏时返回默认配置。</summary>
        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) return cfg;

                MiniJson json = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (json == null || json.Kind != JsonKind.Object) return cfg;

                cfg.AutoClean = json.GetBool("AutoClean", cfg.AutoClean);
                cfg.CooldownSeconds = json.GetInt("CooldownSeconds", cfg.CooldownSeconds);
                cfg.PressureThreshold = json.GetInt("PressureThreshold", cfg.PressureThreshold);
                cfg.LeakSlopeThreshold = json.GetDouble("LeakSlopeThreshold", cfg.LeakSlopeThreshold);
                cfg.LeakR2Threshold = json.GetDouble("LeakR2Threshold", cfg.LeakR2Threshold);
                cfg.WindowMinutes = json.GetInt("WindowMinutes", cfg.WindowMinutes);
                cfg.TopLeakCount = json.GetInt("TopLeakCount", cfg.TopLeakCount);
                cfg.PurgeStandbyList = json.GetBool("PurgeStandbyList", cfg.PurgeStandbyList);
                cfg.TrimLeakProcesses = json.GetBool("TrimLeakProcesses", cfg.TrimLeakProcesses);
                cfg.NotifyOnAuto = json.GetBool("NotifyOnAuto", cfg.NotifyOnAuto);
                cfg.AutoStart = json.GetBool("AutoStart", cfg.AutoStart);
                cfg.CloseToTray = json.GetBool("CloseToTray", cfg.CloseToTray);
                cfg.SampleSeconds = json.GetInt("SampleSeconds", cfg.SampleSeconds);
                cfg.MinWorkingSetMB = json.GetDouble("MinWorkingSetMB", cfg.MinWorkingSetMB);
                cfg.Sanitize();
            }
            catch
            {
                // 配置损坏时用默认值继续运行，不影响监控功能
            }
            return cfg;
        }

        /// <summary>写回磁盘（原子替换）。失败时返回 false，调用方无需处理异常。</summary>
        public bool Save()
        {
            try
            {
                Sanitize();
                AppPaths.EnsureDataDir();

                // 手写缩进 JSON：键名刻意与字段名一致，方便用户直接编辑
                StringBuilder json = new StringBuilder(640);
                json.Append("{\r\n");
                json.Append("  \"_comment\": \"MemTrim 配置；界面「设置」保存后即时生效\",\r\n");
                json.Append("  \"AutoClean\": ").Append(Bool(AutoClean)).Append(",\r\n");
                json.Append("  \"CooldownSeconds\": ").Append(Int(CooldownSeconds)).Append(",\r\n");
                json.Append("  \"PressureThreshold\": ").Append(Int(PressureThreshold)).Append(",\r\n");
                json.Append("  \"LeakSlopeThreshold\": ").Append(Number(LeakSlopeThreshold)).Append(",\r\n");
                json.Append("  \"LeakR2Threshold\": ").Append(Number(LeakR2Threshold)).Append(",\r\n");
                json.Append("  \"WindowMinutes\": ").Append(Int(WindowMinutes)).Append(",\r\n");
                json.Append("  \"TopLeakCount\": ").Append(Int(TopLeakCount)).Append(",\r\n");
                json.Append("  \"PurgeStandbyList\": ").Append(Bool(PurgeStandbyList)).Append(",\r\n");
                json.Append("  \"TrimLeakProcesses\": ").Append(Bool(TrimLeakProcesses)).Append(",\r\n");
                json.Append("  \"NotifyOnAuto\": ").Append(Bool(NotifyOnAuto)).Append(",\r\n");
                json.Append("  \"AutoStart\": ").Append(Bool(AutoStart)).Append(",\r\n");
                json.Append("  \"CloseToTray\": ").Append(Bool(CloseToTray)).Append(",\r\n");
                json.Append("  \"SampleSeconds\": ").Append(Int(SampleSeconds)).Append(",\r\n");
                json.Append("  \"MinWorkingSetMB\": ").Append(Number(MinWorkingSetMB)).Append("\r\n");
                json.Append("}\r\n");

                return AppPaths.WriteAllTextAtomic(ConfigPath, json.ToString());
            }
            catch
            {
                return false;
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    // ======================================================================
    //  路径与文件写入
    // ======================================================================
    /// <summary>统一管理数据目录，避免每个类各自拼一遍 %APPDATA%\MemTrim。</summary>
    internal static class AppPaths
    {
        /// <summary>
        /// 数据目录覆盖值，仅供自检程序（tools\SelfTest.cs）把数据写到临时目录。
        /// 正式运行时为 null，一律使用 %APPDATA%\MemTrim。
        /// （主程序不写这个字段，因此这里显式关掉「从未赋值」的警告 ——
        ///   它只有在把 SelfTest 一起编译进来时才会被赋值。）
        /// </summary>
#pragma warning disable 649
        internal static string DataDirOverride;
#pragma warning restore 649

        /// <summary>
        /// 数据目录环境变量名。设置后配置与历史都写到该目录，
        /// 用于「便携模式」与界面自动化测试（tools\run-uitest.ps1），
        /// 避免污染 %APPDATA% 里的真实数据。
        /// </summary>
        public const string DataDirEnvVar = "MEMTRIM_DATA_DIR";

        /// <summary>
        /// 数据目录，优先级：<see cref="DataDirOverride"/>（自检用）
        /// → <see cref="DataDirEnvVar"/> 环境变量（便携/测试用）
        /// → %APPDATA%\MemTrim。
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (!string.IsNullOrEmpty(DataDirOverride)) return DataDirOverride;

                string fromEnv = Environment.GetEnvironmentVariable(DataDirEnvVar);
                if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemTrim");
            }
        }

        /// <summary>
        /// 从旧版本的数据目录迁移配置与历史。
        ///
        /// 本工具改名前数据目录是 %APPDATA%\MemTrim，改名后是 %APPDATA%\MemTrim。
        /// 老用户升级时必须把这两个文件搬过来，否则界面上的「累计清理次数 / 累计释放」
        /// 会突然归零，看起来像数据丢了。
        ///
        /// 迁移策略（保守优先）：
        ///   · 只在新目录**完全不存在**时迁移 —— 只要新目录已建立，就认为用户已经在新版本上
        ///     跑过，不再动旧目录，避免用陈旧文件覆盖新写入的数据；
        ///   · 只复制，**不删除**旧目录。多留一份旧数据不值几个字节，但删错了无法挽回；
        ///   · 任何失败都静默忽略：迁移不成功只影响历史统计，绝不能因此启动不了。
        /// </summary>
        /// <returns>本次是否真的迁移了文件。</returns>
        public static bool MigrateLegacyDataDir()
        {
            try
            {
                // 用户显式指定了目录（便携模式/测试）时不迁移，避免动到他的数据
                if (!string.IsNullOrEmpty(DataDirOverride)) return false;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DataDirEnvVar))) return false;

                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MemGuard");     // 旧数据目录，故意保留字面量：这里不能跟着改名走
                string current = DataDir;

                if (string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase)) return false;
                if (!Directory.Exists(legacy)) return false;
                if (Directory.Exists(current)) return false;    // 新目录已存在 → 不覆盖

                Directory.CreateDirectory(current);
                bool moved = false;

                string[] names = new string[] { "config.json", "history.tsv" };
                for (int i = 0; i < names.Length; i++)
                {
                    string from = Path.Combine(legacy, names[i]);
                    string to = Path.Combine(current, names[i]);
                    if (!File.Exists(from) || File.Exists(to)) continue;
                    try
                    {
                        File.Copy(from, to, false);
                        moved = true;
                    }
                    catch
                    {
                        // 单个文件失败不影响另一个，也不影响启动
                    }
                }
                return moved;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 确保数据目录存在；返回是否可用。
        /// </summary>
        public static bool EnsureDataDir()
        {
            try
            {
                string dir = DataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 原子写入：先写 <c>目标.tmp</c>，再用 <see cref="File.Replace(string,string,string)"/>
        /// 一步替换目标文件。
        ///
        /// 为什么不用「File.Delete 目标 + File.Move 临时文件」：那是个两步操作，
        /// 一旦在两步之间失败（进程被杀、断电、文件被占用），用户就会丢掉整个配置或历史；
        /// File.Replace 是同一卷内的原子替换，失败时原文件保持完好。
        /// </summary>
        public static bool WriteAllTextAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));   // 不带 BOM，方便用户直接编辑

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return true;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // 少数文件系统（如部分网络盘）不支持 Replace：退回「删除 + 改名」
                    }
                    catch (IOException)
                    {
                        // 目标被占用等：同样退回「删除 + 改名」
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch
                {
                    // 清理临时文件失败不影响返回结果
                }
                return false;
            }
        }

        /// <summary>按行读取文件末尾的若干行（避免为了拿最后几条记录而载入整个文件）。</summary>
        /// <param name="path">文件路径。</param>
        /// <param name="maxLines">最多返回多少行（不足则返回全部）。</param>
        /// <param name="atMostBytes">最多回溯多少字节，防止单行超长时越读越多。</param>
        /// <returns>按文件顺序（旧 → 新）排列的行。</returns>
        public static List<string> ReadTailLines(string path, int maxLines, int atMostBytes)
        {
            List<string> lines = new List<string>();
            if (maxLines <= 0 || string.IsNullOrEmpty(path)) return lines;

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long position = stream.Length;
                    List<byte> chunk = new List<byte>(8192);
                    int newlines = 0;

                    while (position > 0 && newlines <= maxLines && chunk.Count < atMostBytes)
                    {
                        position--;
                        stream.Position = position;
                        int value = stream.ReadByte();
                        if (value < 0) break;
                        chunk.Add((byte)value);

                        if (value == (byte)'\n') newlines++;
                    }

                    chunk.Reverse();      // 倒着读进来的，翻转回正常顺序
                    string text = new UTF8Encoding(false).GetString(chunk.ToArray());
                    string[] all = text.Split('\n');
                    for (int i = 0; i < all.Length; i++)
                    {
                        string line = all[i].TrimEnd('\r');
                        if (line.Length == 0) continue;
                        lines.Add(line);
                    }
                }
            }
            catch
            {
                lines.Clear();
            }

            // 只保留最后 maxLines 行（第一行可能是被截断的半行，交给调用方按格式校验丢弃）
            if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
            return lines;
        }
    }

    // ======================================================================
    //  清理历史
    // ======================================================================
    /// <summary>一次清理的历史记录（history.tsv 的一行）。</summary>
    internal sealed class HistoryEntry
    {
        public DateTime Time;
        public string Trigger = "";
        public double FreedMB;
        public double BeforeAvailMB;
        public double AfterAvailMB;
        public int ProcessCount;
        public bool StandbyPurged;

        /// <summary>TSV 表头，以 # 开头，读取时会被跳过。</summary>
        public const string Header = "# time\ttrigger\tfreedMB\tbeforeMB\tafterMB\tprocs\tstandby";

        public string ToLine()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}\t{1}\t{2:0.##}\t{3:0.#}\t{4:0.#}\t{5}\t{6}",
                Time, Trigger, FreedMB, BeforeAvailMB, AfterAvailMB,
                ProcessCount, StandbyPurged ? 1 : 0);
        }

        /// <summary>解析一行；格式不合法时返回 null。</summary>
        public static HistoryEntry Parse(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] == '#') return null;

            string[] parts = line.Split('\t');
            if (parts.Length < 7) return null;

            DateTime time;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out time)) return null;

            HistoryEntry e = new HistoryEntry();
            e.Time = time;
            e.Trigger = parts[1];
            e.FreedMB = ParseDouble(parts[2]);
            e.BeforeAvailMB = ParseDouble(parts[3]);
            e.AfterAvailMB = ParseDouble(parts[4]);
            int count;
            if (int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                e.ProcessCount = count;
            e.StandbyPurged = parts[6] == "1";
            return e;
        }

        private static double ParseDouble(string text)
        {
            double value;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            return 0;
        }
    }

    /// <summary>history.tsv 的读写与累计统计。</summary>
    internal static class History
    {
        /// <summary>裁剪后保留的历史行数（含表头）。</summary>
        private const int KeepLines = 2000;

        /// <summary>超过这么多行才裁剪。留一段余量，避免文件刚好卡在阈值上时每次清理都重写。</summary>
        private const int TrimThreshold = KeepLines + 200;

        /// <summary>读尾部时最多回溯的字节数：2200 行 × 约 80 字节，留一倍余量。</summary>
        private const int TailScanBytes = 512 * 1024;

        private static readonly object Lock = new object();

        /// <summary>历史文件路径。</summary>
        public static string HistoryPath
        {
            get { return Path.Combine(AppPaths.DataDir, "history.tsv"); }
        }

        /// <summary>累计释放量（MB）。由 LoadTotals / RefreshTotals / NoteAppended 更新。</summary>
        public static double TotalFreedMB { get; private set; }
        /// <summary>累计清理次数。由 LoadTotals / RefreshTotals / NoteAppended 更新。</summary>
        public static int TotalCleanCount { get; private set; }

        /// <summary>追加一条记录。写入失败不影响主流程。</summary>
        public static void Append(HistoryEntry entry)
        {
            try
            {
                lock (Lock)
                {
                    if (!AppPaths.EnsureDataDir()) return;

                    bool needHeader = !File.Exists(HistoryPath);
                    using (StreamWriter sw = new StreamWriter(HistoryPath, true, new UTF8Encoding(false)))
                    {
                        if (needHeader) sw.WriteLine(HistoryEntry.Header);
                        sw.WriteLine(entry.ToLine());
                    }
                }
            }
            catch
            {
                // 历史写入失败不影响清理本身
            }
            NoteAppended(entry);
        }

        /// <summary>
        /// 把刚追加的一条记录计入累计值。
        ///
        /// 这里刻意**不再**重扫整个历史文件：此前每次清理结束都会
        /// <c>File.ReadAllLines</c> 把 history.tsv 全读进内存再逐行解析求和，
        /// 属于典型的 O(历史长度) 操作，用得越久越慢，而且完全没必要 ——
        /// 累计量本来就可以增量累加。
        /// </summary>
        public static void NoteAppended(HistoryEntry entry)
        {
            if (entry == null) return;
            TotalFreedMB += entry.FreedMB;
            TotalCleanCount++;
        }

        /// <summary>读最近 n 条（最新的排在最前）。</summary>
        public static List<HistoryEntry> ReadRecent(int count)
        {
            List<HistoryEntry> result = new List<HistoryEntry>();
            if (count <= 0) return result;

            lock (Lock)
            {
                // 多读几行：尾部可能掺着表头或被截断的半行，按格式校验时会丢弃
                List<string> lines = AppPaths.ReadTailLines(HistoryPath, count + 2, TailScanBytes);
                for (int i = lines.Count - 1; i >= 0 && result.Count < count; i--)
                {
                    HistoryEntry e = HistoryEntry.Parse(lines[i]);
                    if (e != null) result.Add(e);
                }
            }
            return result;
        }

        /// <summary>启动时调用一次：全量读一遍历史，得到累计释放量与清理次数。</summary>
        public static void LoadTotals()
        {
            double total = 0;
            int count = 0;
            try
            {
                lock (Lock)
                {
                    if (File.Exists(HistoryPath))
                    {
                        string[] lines = File.ReadAllLines(HistoryPath, Encoding.UTF8);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            HistoryEntry e = HistoryEntry.Parse(lines[i]);
                            if (e == null) continue;
                            total += e.FreedMB;
                            count++;
                        }
                    }
                }
            }
            catch
            {
                // 统计失败时保留上一次的值
            }
            TotalFreedMB = total;
            TotalCleanCount = count;
        }

        /// <summary>
        /// 重算累计释放量与清理次数。保留此方法是为了兼容自检程序与「清空后重算」的语义，
        /// 正常清理流程用 <see cref="NoteAppended"/> 增量累加，不调用它。
        /// </summary>
        public static void RefreshTotals()
        {
            LoadTotals();
        }

        /// <summary>清空历史与累计统计。</summary>
        public static void Clear()
        {
            try
            {
                lock (Lock)
                {
                    if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
                }
            }
            catch
            {
                // 文件被占用时忽略
            }
            TotalFreedMB = 0;
            TotalCleanCount = 0;
        }

        /// <summary>
        /// 历史文件超过上限时裁剪掉最旧的记录（保留表头）。
        /// 此前没有这一步：history.tsv 会随使用时间无限增长，
        /// 每天十几次自动清理的话，一年就是几千行。
        /// </summary>
        public static void TrimIfNeeded()
        {
            try
            {
                lock (Lock)
                {
                    string path = HistoryPath;
                    if (!File.Exists(path)) return;

                    List<string> tail = AppPaths.ReadTailLines(path, TrimThreshold, TailScanBytes * 4);
                    // 回溯到的行数还没到阈值，说明文件本来就不大
                    if (tail.Count < TrimThreshold) return;

                    // 尾部第一行可能是被截断的半行，按格式校验时会被丢弃；表头补在最前面
                    if (tail.Count > KeepLines) tail.RemoveRange(0, tail.Count - KeepLines);
                    StringBuilder sb = new StringBuilder(tail.Count * 80);
                    sb.Append(HistoryEntry.Header).Append("\r\n");
                    for (int i = 0; i < tail.Count; i++)
                    {
                        if (HistoryEntry.Parse(tail[i]) == null) continue;
                        sb.Append(tail[i]).Append("\r\n");
                    }
                    AppPaths.WriteAllTextAtomic(path, sb.ToString());
                }
            }
            catch
            {
                // 裁剪失败不影响正常记录
            }
        }
    }

    // ======================================================================
    //  开机自启动
    // ======================================================================
    /// <summary>开机自启动（HKCU\...\Run，普通用户权限即可写）。</summary>
    internal static class AutoStartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MemTrim";

        /// <summary>注册表里是否存在自启动项。注册表是唯一事实来源。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    return value != null && value.ToString().Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>写入或删除自启动项；返回是否操作成功。</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;

                    if (!enabled)
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                        return true;
                    }

                    // 每次启动都按配置校正一次：用户手动删掉自启动项后能自动补回来，
                    // 界面显示的勾选状态与实际行为不会长期不一致。
                    // 但只在内容真的不同时才写 —— 注册表写入会触发变更通知，
                    // 每次开机都无意义地重写一遍属于白白的系统扰动。
                    string exe = System.Windows.Forms.Application.ExecutablePath;
                    string wanted = "\"" + exe + "\" -tray";
                    object current = key.GetValue(ValueName);
                    if (current == null || current.ToString() != wanted)
                    {
                        key.SetValue(ValueName, wanted, Microsoft.Win32.RegistryValueKind.String);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ======================================================================
    //  迷你 JSON 解析
    // ======================================================================
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Object
    }

    /// <summary>
    /// 只支持配置所需的扁平对象（字符串键 + 标量值），故意不支持数组与嵌套对象。
    /// 所有解析方法都不抛异常：遇到畸形输入就停下并返回已解析到的部分。
    /// </summary>
    internal sealed class MiniJson
    {
        public JsonKind Kind = JsonKind.Null;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public Dictionary<string, MiniJson> Members;

        /// <summary>入口：解析整段文本；无法解析出对象时返回 null。</summary>
        public static MiniJson Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int index = 0;
            return ParseValue(text, ref index);
        }

        public bool GetBool(string key, bool fallback)
        {
            MiniJson v = Get(key);
            if (v == null || v.Kind != JsonKind.Bool) return fallback;
            return v.BoolValue;
        }

        public int GetInt(string key, int fallback)
        {
            MiniJson v = Get(key);
            if (v == null || v.Kind != JsonKind.Number) return fallback;
            return (int)Math.Round(v.NumberValue);
        }

        public double GetDouble(string key, double fallback)
        {
            MiniJson v = Get(key);
            if (v == null || v.Kind != JsonKind.Number) return fallback;
            return v.NumberValue;
        }

        private MiniJson Get(string key)
        {
            if (Members == null) return null;
            MiniJson value;
            if (Members.TryGetValue(key, out value)) return value;
            return null;
        }

        // ------------------------------------------------------------------
        //  解析实现
        // ------------------------------------------------------------------
        private static MiniJson ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;

            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' || c == 'T' || c == 'f' || c == 'F') return ParseBool(s, ref i);
            if (c == 'n' || c == 'N') return ParseLiteral(s, ref i, "null");
            if (c == '-' || c == '+' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);

            i++;    // 无法识别的字符：跳过，避免调用方死循环
            return null;
        }

        /// <summary>解析扁平对象；遇到结构错误就结束（返回已解析到的成员）。</summary>
        private static MiniJson ParseObject(string s, ref int i)
        {
            MiniJson obj = new MiniJson();
            obj.Kind = JsonKind.Object;
            obj.Members = new Dictionary<string, MiniJson>(StringComparer.OrdinalIgnoreCase);

            i++;    // 跳过 '{'
            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) break;

                char c = s[i];
                if (c == '}')
                {
                    i++;
                    break;
                }
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c != '"') break;    // 键必须是字符串，否则放弃解析

                MiniJson key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ':') i++;

                MiniJson value = ParseValue(s, ref i);

                // 解析失败必须停下：否则同一个字符会被反复当成新键，
                // 在「键后面缺冒号或值」的畸形配置上退化成 O(n²) 的死循环式扫描。
                if (key == null || key.StringValue == null || value == null) break;

                obj.Members[key.StringValue] = value;
            }
            return obj;
        }

        private static MiniJson ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;

            StringBuilder sb = new StringBuilder(32);
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                i++;    // 跳过反斜杠
                if (i >= s.Length) break;

                char escape = s[i];
                switch (escape)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 'b': sb.Append('\b'); i++; break;
                    case 'f': sb.Append('\f'); i++; break;
                    case 'u':
                        // \uXXXX：需要 4 位十六进制，不足则（含 i 本身）最多 5 个字符
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 5;
                                break;
                            }
                        }
                        i++;
                        break;
                    default:
                        sb.Append(escape);
                        i++;
                        break;
                }
            }

            if (i < s.Length) i++;    // 跳过结尾引号

            MiniJson value = new MiniJson();
            value.Kind = JsonKind.String;
            value.StringValue = sb.ToString();
            return value;
        }

        /// <summary>解析 true / false（大小写不敏感：手写配置里 True/False 很常见）。</summary>
        private static MiniJson ParseBool(string s, ref int i)
        {
            MiniJson value = new MiniJson();
            value.Kind = JsonKind.Bool;
            if (Match(s, i, "true"))
            {
                value.BoolValue = true;
                i += 4;
            }
            else
            {
                value.BoolValue = false;
                i += Match(s, i, "false") ? 5 : 1;   // 兜底：至少前进 1 个字符
            }
            return value;
        }

        private static MiniJson ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && IsNumberChar(s[i])) i++;

            MiniJson value = new MiniJson();
            value.Kind = JsonKind.Number;

            // 长度小于 1 的片段（如孤立的 '-'）不是合法数字
            if (i - start >= 1)
            {
                double parsed;
                if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                {
                    value.NumberValue = parsed;
                }
            }
            return value;
        }

        private static MiniJson ParseLiteral(string s, ref int i, string literal)
        {
            i += Match(s, i, literal) ? literal.Length : 1;
            return new MiniJson();   // JSON null：保持 Kind = Null
        }

        /// <summary>按序号比较（大小写不敏感），避免为比较而分配临时子串。</summary>
        private static bool Match(string s, int index, string literal)
        {
            if (index + literal.Length > s.Length) return false;
            for (int k = 0; k < literal.Length; k++)
            {
                char a = s[index + k];
                char b = literal[k];
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (a != b) return false;
            }
            return true;
        }

        private static bool IsNumberChar(char c)
        {
            return (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') return;
                i++;
            }
        }
    }
}
