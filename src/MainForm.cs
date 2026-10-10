// ============================================================================
//  MemTrim · MainForm.cs —— 主窗口 + 托盘
//
//  结构（按调用顺序）：
//      构造      加载配置 → 建界面 → 建托盘 → 应用配置 → 读历史
//      OnTick    采样 → 分析 → 渲染 → 判断是否自动清理      （每 SampleSeconds 一轮）
//      StartCleanup  选目标 → 后台线程清理 → 回 UI 线程收尾
//
//  线程约定：
//      · 采样、分析、渲染全部在 UI 线程；清理在线程池线程执行，只读取不可变数据
//        （目标进程的 PID/名称/工作集在 Analyze 时已经定格，不会被后续轮次改写）；
//      · 回 UI 线程一律走 BeginInvoke，并捕获窗口已销毁的异常。
//
//  渲染约定（避免无谓的系统调用与分配）：
//      · 文本类标签用 CacheText 比对后再赋值，值没变就不碰控件；
//      · 进程列表复用行对象（见 HomeList），不每轮 Clear + 重建；
//      · 字体、图标、位图在构造期创建一次，随窗口释放；共享字体由 UiFonts 统一释放。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MemTrim
{
    internal sealed class MainForm : Form
    {
        // ---- 配置与运行状态 ----
        private AppConfig _cfg;
        private readonly LeakDetector _detector = new LeakDetector();
        // 显式写全名：本文件同时 using 了 System.Threading（线程池），Timer 会有歧义
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon _tray = new NotifyIcon();

        /// <summary>上一次自动清理的时刻（单调时钟，毫秒）。0 = 从未清理过。</summary>
        private int _lastAutoCleanTick;
        private CleanupResult _lastResult;
        private LeakReport _lastReport;
        private bool _cleaning;
        private bool _trayHintShown;
        /// <summary>tools\SelfTest.cs 反射读取此字段来模拟「用户点了退出」。</summary>
        private bool _exitRequested;
        private readonly bool _startHidden;

        // ---- 控件 ----
        private UsageBar _bar;
        private Label _lblMemLine;
        private Label _lblStatus;
        private Label _lblNextAuto;
        private Label _lblLeakLine;
        private Label _lblListHint;
        private Label _lblTrend;
        private Label _lblHistory;
        private Label _lblPrivilege;
        private ListView _list;
        private RgbGlowButton _btnClean;
        private ToolStripMenuItem _miAuto;
        private ToolStripMenuItem _miStatus;

        // ---- 一次性创建、随窗口释放的资源 ----
        private readonly Icon _appIcon;
        private Bitmap _headerLogo;

        // ---- 渲染缓存：值没变就不碰控件（省下每轮几十次字符串格式化与重绘）----
        private string _textMemLine, _textStatus, _textNextAuto, _textLeakLine;
        private string _textTrend, _textHistory, _textListHint, _textPrivilege, _textTray;
        private readonly List<ListViewItem> _rows = new List<ListViewItem>(24);

        // ---- 布局基准尺寸（按 96 DPI 设计，运行时用 UiTheme.Dpi 换算）----
        //
        //  为什么底部区域要用 Dock 而不是绝对坐标：
        //  窗口高度可能被屏幕工作区截断（本机 125% 缩放下设计高 620×1.25 = 775 px
        //  正好顶到屏幕上限），此时客户区比设计值矮，按「距顶部 y」摆放的按钮就会
        //  压到文字上。交给 Dock 之后，尾部永远贴着客户区底边，多高都不会重叠。
        private const int DesignWidth = 720;
        private const int DesignHeight = 620;
        private const int EdgeMargin = 18;
        private const int InnerMargin = 14;
        private const int HeaderHeight = 58;
        private const int MemCardTop = 72;
        private const int MemCardHeight = 118;
        private const int LeakCardTop = 198;
        private const int LeakCardHeight = 62;
        private const int ListHintTop = 272;
        private const int ListTop = 298;
        private const int ListPadding = 30;
        private const int FooterLineHeight = 22;
        private const int FooterButtonBand = 46;

        /// <summary>列标题与「按 96 DPI 设计」的固定列宽；第 0 列吃掉剩余宽度。</summary>
        private static readonly string[] ColumnTitles = new string[]
        {
            "进程", "PID", "工作集", "提交大小", "增长速率", "线性度 R²", "窗口增长", "状态"
        };

        private static readonly int[] ColumnDesignWidths = new int[]
        {
            0, 50, 76, 76, 84, 66, 74, 74
        };

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            _cfg = AppConfig.Load();

            Text = "MemTrim · 内存泄漏监控与自动清理";
            Font = UiTheme.FontUi;
            BackColor = UiTheme.FormBack;
            // 缩放自己做（见 UiTheme.Dpi）：AutoScaleMode.Dpi 会把客户区撑高，
            // 让绝对坐标摆放的底部按钮与列表错位。
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(UiTheme.Dpi(DesignWidth), UiTheme.Dpi(DesignHeight));
            MinimumSize = new Size(UiTheme.Dpi(660), UiTheme.Dpi(560));
            StartPosition = FormStartPosition.CenterScreen;

            // 抗拉伸残影：窗体本身也必须「尺寸一变就整块重画」。
            // 默认情况下窗体只重画新暴露的区域，配合锚点布局就会在变宽后
            // 留下一列列横向拉伸的残影（用户看到的竖向细线）。
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;

            _appIcon = IconFactory.CreateIcon(32);
            Icon = _appIcon;

            BuildUi();
            BuildTray();
            ApplyConfig(runtimeChanged: false);
            EnsureConfigFile();

            // 历史统计只在这里全量读一次；之后每次清理用增量累加（见 History.NoteAppended）
            History.LoadTotals();
            History.TrimIfNeeded();
            LoadLastCleanup();

            MemoryCleaner.PreparePrivileges();
            UpdatePrivilegeLabel();

            _timer.Interval = _cfg.SampleSeconds * 1000;
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// 供自检程序无界面烟雾测试使用：只显示一次窗口并立即做一次完整渲染
        /// （覆盖布局、卡片绘制、列表填充、托盘提示），随后不关闭窗口。
        /// 关闭动作交给 Application.Run / 托盘「退出」走真实路径。
        /// </summary>
        internal void ShowForSmokeTest()
        {
            ShowInTaskbar = false;
            Show();
            _detector.Sample(0);                       // 先采一轮，卡片才有真实数字
            Render(_detector.Analyze(_cfg.TopLeakCount));
            FitListColumns();
        }

        /// <summary>开始监控：先采一轮样本，让界面立刻有内容，再启动计时器。</summary>
        public void StartMonitoring()
        {
            OnTick(null, EventArgs.Empty);
            _timer.Start();
        }

        /// <summary>首次运行时落盘一份默认配置，方便用户直接编辑。</summary>
        private void EnsureConfigFile()
        {
            try
            {
                if (!System.IO.File.Exists(AppConfig.ConfigPath)) _cfg.Save();
            }
            catch
            {
                // 写不了配置不影响运行
            }
        }

        // ------------------------------------------------------------------
        //  界面构建
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            BuildMemoryCard();
            BuildLeakCard();
            BuildProcessList();
            // 底部区域最后加，并在建好后重算列表高度：
            // 列表用「上边固定 + 下边锚定」，锚定是按当前尺寸算偏移的，
            // 必须等底部区域占好位置再定高度，否则会压到文字上。
            BuildFooter();
            BuildHeader();
            ResizeListToFit();
        }

        /// <summary>底部按钮行的高度：按 DPI 换算，并保证至少放得下最高的那个按钮。</summary>
        private static int FooterBandHeight
        {
            get { return Math.Max(FooterButtonBand * UiTheme.Dpi(1), UiTheme.Dpi(40)); }
        }

        /// <summary>底部区域总高：三行说明文字 + 一行按钮。</summary>
        private static int FooterHeight
        {
            get { return 3 * FooterLineHeight * UiTheme.Dpi(1) + FooterBandHeight; }
        }

        /// <summary>按「客户区高度 − 底部区域高度」重算列表高度。</summary>
        private void ResizeListToFit()
        {
            if (_list == null) return;

            int dpi = UiTheme.Dpi(1);
            _list.Height = Math.Max(UiTheme.Dpi(80),
                ClientSize.Height - ListTop * dpi - FooterHeight - UiTheme.Dpi(ListPadding));
        }

        private void BuildHeader()
        {
            int dpi = UiTheme.Dpi(1);          // 1 设计像素 = dpi 实际像素，便于换算

            Panel header = new Panel();
            header.Location = new Point(0, 0);
            header.Size = new Size(ClientSize.Width, HeaderHeight * dpi);
            header.BackColor = UiTheme.HeaderBack;
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // 用 Stream 构造 Image：不产生需要 DestroyIcon 的 GDI 句柄
            _headerLogo = new Bitmap(IconFactory.ToStream(32));
            PictureBox logo = new PictureBox();
            logo.Image = _headerLogo;
            logo.SizeMode = PictureBoxSizeMode.StretchImage;
            logo.Size = new Size(30 * dpi, 30 * dpi);
            logo.Location = new Point(18 * dpi, 14 * dpi);
            logo.BackColor = Color.Transparent;
            header.Controls.Add(logo);

            // 表头是两行：标题在上、副标题在下。
            // 标题用 13pt（125% 下约 22px 高），副标题必须放到标题基线以下，
            // 否则两者会叠在一起（曾经副标题放在 y=16，正好压在标题上）。
            Label title = UiKit.Text("MemTrim", 58 * dpi, 4 * dpi, UiTheme.FontTitle, Color.White);
            title.BackColor = Color.Transparent;
            header.Controls.Add(title);

            Label subtitle = UiKit.Text("内存泄漏监控 · 自动清理（清理算法移植自 PCL 内存优化）",
                58 * dpi, 34 * dpi, UiTheme.FontSmall, UiTheme.HeaderSub);
            subtitle.BackColor = Color.Transparent;
            header.Controls.Add(subtitle);

            Controls.Add(header);
        }

        private void BuildMemoryCard()
        {
            int dpi = UiTheme.Dpi(1);

            CardPanel card = UiKit.Card(EdgeMargin * dpi, MemCardTop * dpi,
                ClientSize.Width - 2 * EdgeMargin * dpi, MemCardHeight * dpi);
            Controls.Add(card);
            card.Controls.Add(UiKit.CardTitle("物理内存", InnerMargin * dpi, 10 * dpi));

            _bar = new UsageBar();
            _bar.Location = new Point(InnerMargin * dpi, 36 * dpi);
            _bar.Size = new Size(card.Width - 2 * InnerMargin * dpi, 28 * dpi);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_bar);

            _lblMemLine = UiKit.Text("", InnerMargin * dpi, 72 * dpi, Font, UiTheme.TextMain);
            card.Controls.Add(_lblMemLine);

            _lblStatus = UiKit.Text("", InnerMargin * dpi, 92 * dpi, UiTheme.FontBold, UiTheme.TextGood);
            card.Controls.Add(_lblStatus);

            _lblNextAuto = new Label();
            _lblNextAuto.AutoSize = false;
            _lblNextAuto.TextAlign = ContentAlignment.TopRight;
            _lblNextAuto.Size = new Size(240 * dpi, 20 * dpi);
            _lblNextAuto.Location = new Point(card.Width - 254 * dpi, 92 * dpi);
            _lblNextAuto.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblNextAuto.ForeColor = UiTheme.TextDim;
            _lblNextAuto.Font = Font;
            card.Controls.Add(_lblNextAuto);
        }

        private void BuildLeakCard()
        {
            int dpi = UiTheme.Dpi(1);

            CardPanel card = UiKit.Card(EdgeMargin * dpi, LeakCardTop * dpi,
                ClientSize.Width - 2 * EdgeMargin * dpi, LeakCardHeight * dpi);
            Controls.Add(card);
            card.Controls.Add(UiKit.CardTitle("泄漏检测（滑动窗口趋势分析）", InnerMargin * dpi, 10 * dpi));

            _lblLeakLine = UiKit.Text("", InnerMargin * dpi, 34 * dpi, Font, UiTheme.TextMain);
            card.Controls.Add(_lblLeakLine);
        }

        private void BuildProcessList()
        {
            int dpi = UiTheme.Dpi(1);
            int left = UiTheme.Dpi(20);

            _lblListHint = UiKit.Text("可疑进程（按泄漏评分排序；绿色为暂未判定泄漏的进程）",
                left, ListHintTop * dpi, Font, UiTheme.TextDim);
            Controls.Add(_lblListHint);

            _list = new ListView();
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.MultiSelect = false;
            _list.LabelEdit = false;
            _list.HideSelection = false;
            _list.Location = new Point(left, ListTop * dpi);
            // 高度不写死：上边固定、下边锚定到「底部区域的上沿」，
            // 客户区被屏幕截断时列表自动变矮而不是压住文字
            _list.Size = new Size(ClientSize.Width - left * 2,
                Math.Max(UiTheme.Dpi(80), ClientSize.Height - ListTop * dpi - UiTheme.Dpi(ListPadding)));
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _list.BackColor = Color.White;
            for (int i = 0; i < ColumnTitles.Length; i++)
            {
                _list.Columns.Add(ColumnTitles[i], UiTheme.Dpi(ColumnDesignWidths[i]));
            }
            _list.Resize += delegate { FitListColumns(); };
            Controls.Add(_list);
        }

        /// <summary>
        /// 底部区域：三行说明文字 + 一行按钮，整体 Dock 到底边。
        /// 按钮行高度固定，其余三行均分剩余空间，因此任何客户区高度下都不会互相压住。
        /// </summary>
        private void BuildFooter()
        {
            int dpi = UiTheme.Dpi(1);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = FooterHeight;
            footer.BackColor = UiTheme.FormBack;
            Controls.Add(footer);

            // 顺序：Top 逐行堆叠，因此先加的三行在上，最后加的按钮行占据底部。
            // 注意：三行文字不能也用 Dock —— 它们和按钮行是并列关系，
            // 同层 Dock 会互相穿插。这里三行用绝对定位（高度固定，无需自适应）。
            int line = FooterLineHeight * dpi;
            _lblTrend = FooterLine("", 0, line);
            footer.Controls.Add(_lblTrend);

            _lblHistory = FooterLine("", line, line);
            footer.Controls.Add(_lblHistory);

            _lblPrivilege = FooterLine("", 2 * line, line);
            _lblPrivilege.Font = UiTheme.FontSmall;
            footer.Controls.Add(_lblPrivilege);

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = FooterBandHeight;
            buttons.BackColor = UiTheme.FormBack;
            footer.Controls.Add(buttons);

            int band = FooterBandHeight;
            int glowHeight = UiTheme.Dpi(40);
            int plainHeight = UiTheme.Dpi(34);

            // 主按钮用流光自绘控件；尺寸比普通按钮略大，多出来的高度留给外发光
            _btnClean = new RgbGlowButton();
            _btnClean.Text = "立即清理内存";
            _btnClean.SetCorners(RoundedCorners.All);
            _btnClean.Size = new Size(UiTheme.Dpi(144), glowHeight);
            _btnClean.Location = new Point(UiTheme.Dpi(20), (band - glowHeight) / 2);
            _btnClean.Click += delegate { StartCleanup(CleanupTrigger.Manual); };
            buttons.Controls.Add(_btnClean);

            Button settings = FooterButton("设置", UiTheme.Dpi(176), UiTheme.Dpi(92), plainHeight);
            settings.Click += delegate { OpenSettings(); };
            buttons.Controls.Add(settings);

            Button history = FooterButton("清理历史", UiTheme.Dpi(276), UiTheme.Dpi(92), plainHeight);
            history.Click += delegate { ShowHistory(); };
            buttons.Controls.Add(history);

            Button hide = FooterButton("最小化到托盘", 0, UiTheme.Dpi(120), plainHeight);
            hide.Anchor = AnchorStyles.Right;
            hide.Location = new Point(buttons.Width - hide.Width - UiTheme.Dpi(20),
                (band - plainHeight) / 2);
            hide.Click += delegate { HideToTray(); };
            buttons.Controls.Add(hide);
        }

        /// <summary>底部整行说明文字（撑满宽度，长文本自动省略）。</summary>
        private Label FooterLine(string text, int y, int height)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(0, y);
            label.Size = new Size(ClientSize.Width, height);
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            label.Padding = new Padding(UiTheme.Dpi(20), 0, UiTheme.Dpi(20), 0);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = UiTheme.TextDim;
            label.Font = Font;
            label.AutoEllipsis = true;
            return label;
        }

        private Button FooterButton(string text, int x, int width, int height)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(width, height);
            button.Location = new Point(x, 0);
            button.FlatStyle = FlatStyle.System;
            button.Font = Font;
            return button;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 此时控件已完成最终布局，按真实可用宽度分配列宽
            FitListColumns();
        }

        /// <summary>
        /// 列宽自适应：固定列按 DPI 换算，剩余宽度全部分配给「进程」列。
        /// 固定列在设计尺寸下合计 500，客户区宽 656（100%）/ 820（125%），都放得下，
        /// 因此任何 DPI 下都不会溢出、不会出现横向滚动条。
        /// </summary>
        private void FitListColumns()
        {
            if (_list == null || _list.Columns.Count < ColumnDesignWidths.Length) return;

            int sumFixed = 0;
            for (int i = 1; i < ColumnDesignWidths.Length; i++)
            {
                int width = UiTheme.Dpi(ColumnDesignWidths[i]);
                sumFixed += width;
                _list.Columns[i].Width = width;
            }

            int available = _list.ClientSize.Width - UiTheme.Dpi(4);   // 预留滚动条与边框
            int nameWidth = available - sumFixed;
            int minimum = UiTheme.Dpi(88);
            if (nameWidth < minimum) nameWidth = minimum;
            _list.Columns[0].Width = nameWidth;
        }

        // ------------------------------------------------------------------
        //  托盘
        // ------------------------------------------------------------------
        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = UiTheme.FontUi;

            menu.Items.Add(MenuItem("打开主窗口", delegate { ShowFromTray(); }));
            menu.Items.Add(MenuItem("立即清理内存", delegate { StartCleanup(CleanupTrigger.Manual); }));
            menu.Items.Add(new ToolStripSeparator());

            _miAuto = new ToolStripMenuItem("启用自动清理");
            _miAuto.Click += delegate
            {
                _cfg.AutoClean = !_cfg.AutoClean;
                _cfg.Save();
                SyncMenuChecks();
                UpdateStatusLabel();
            };
            menu.Items.Add(_miAuto);

            menu.Items.Add(MenuItem("设置…", delegate { OpenSettings(); }));
            menu.Items.Add(new ToolStripSeparator());

            _miStatus = new ToolStripMenuItem("");
            _miStatus.Enabled = false;
            menu.Items.Add(_miStatus);

            menu.Items.Add(MenuItem("退出", delegate
            {
                _exitRequested = true;
                Close();
            }));

            _tray.Icon = _appIcon;
            _tray.Text = "MemTrim";
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };
            _tray.BalloonTipClicked += delegate { ShowFromTray(); };
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += onClick;
            return item;
        }

        // ------------------------------------------------------------------
        //  配置应用
        // ------------------------------------------------------------------
        /// <summary>
        /// 把配置同步到运行时。runtimeChanged 为 true 时表示用户在设置里改过参数，
        /// 此时重算采样窗口并清空历史样本（旧样本的参数口径已经失效）。
        /// </summary>
        private void ApplyConfig(bool runtimeChanged)
        {
            _detector.WindowMinutes = _cfg.WindowMinutes;
            _detector.LeakSlopeThreshold = _cfg.LeakSlopeThreshold;
            _detector.LeakR2Threshold = _cfg.LeakR2Threshold;
            _detector.PressureThreshold = _cfg.PressureThreshold;
            _detector.MinWorkingSetMB = _cfg.MinWorkingSetMB;
            // 判定至少要覆盖窗口的一半时长，避免用几秒噪声外推出巨大斜率
            _detector.MinSpanMinutes = Math.Max(1.0, _cfg.WindowMinutes / 2.0);

            double samplesInWindow = _cfg.WindowMinutes * 60.0 / _cfg.SampleSeconds;
            _detector.MinSamples = Clamp((int)Math.Round(samplesInWindow * 0.35), 4, 8);
            _detector.PrepareSamples(_cfg.SampleSeconds);
            if (runtimeChanged) _detector.Reset();

            _timer.Interval = _cfg.SampleSeconds * 1000;
            AutoStartManager.SetEnabled(_cfg.AutoStart);
            SyncMenuChecks();
        }

        private void OpenSettings()
        {
            // 在配置副本上编辑：点「取消」不会留下任何半生效的改动
            using (SettingsForm form = new SettingsForm(_cfg.Clone(), AutoStartManager.IsEnabled))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                _cfg = form.EditedConfig;
                _cfg.Sanitize();
                if (!_cfg.Save())
                {
                    ShowToast("MemTrim", @"配置保存失败（%APPDATA%\MemTrim 可能不可写）", ToolTipIcon.Warning);
                }
                ApplyConfig(runtimeChanged: true);
                UpdateStatusLabel();
            }
        }

        private void SyncMenuChecks()
        {
            if (_miAuto != null) _miAuto.Checked = _cfg.AutoClean;
        }

        // ------------------------------------------------------------------
        //  监控主循环（始终在 UI 线程）
        // ------------------------------------------------------------------
        private void OnTick(object sender, EventArgs e)
        {
            if (_cleaning) return;      // 清理期间跳过本轮，避免与清理动作抢内存数据

            try
            {
                _detector.Sample(0);
                LeakReport report = _detector.Analyze(_cfg.TopLeakCount);
                _lastReport = report;
                Render(report);
                CheckAutoClean(report);
            }
            catch
            {
                // 监控循环不允许抛出，否则托盘会失去响应
            }
        }

        // ------------------------------------------------------------------
        //  渲染
        // ------------------------------------------------------------------
        private void Render(LeakReport report)
        {
            RenderMemoryCard(report);
            RenderLeakLine(report);
            RenderTrendLine(report);
            RenderHistoryLine();
            RenderList(report);
            RenderTray(report);
            UpdateStatusLabel();
        }

        /// <summary>只在新文本与上次不同时才写入控件（省掉每轮无谓的排版与重绘）。</summary>
        private static void CacheText(Label label, ref string cache, string text)
        {
            if (cache == text) return;
            cache = text;
            label.Text = text;
        }

        private void RenderMemoryCard(LeakReport report)
        {
            MemSnapshot m = report.Memory;
            double usedRatio = m.TotalPhysical > 0
                ? (double)m.UsedPhysical / (double)m.TotalPhysical : 0;

            _bar.SetValue(usedRatio, string.Format(CultureInfo.CurrentCulture,
                "{0:N1} GB / {1:N1} GB  ·  物理占用 {2}%",
                m.UsedMB / 1024.0, m.TotalMB / 1024.0, m.LoadPercent));

            string commitText = m.CommitLimit > 0
                ? string.Format(CultureInfo.CurrentCulture,
                    "提交 {0:N1} / {1:N1} GB（{2}%）", m.CommitUsedMB / 1024.0,
                    m.CommitLimitMB / 1024.0, m.PressurePercent)
                : "提交信息不可用";

            CacheText(_lblMemLine, ref _textMemLine, string.Format(CultureInfo.CurrentCulture,
                "可用 {0:N0} MB    物理占用（含缓存）{1}%    自动清理阈值 {2}%（按{3}判定）    {4}",
                m.AvailMB, m.LoadPercent, _cfg.PressureThreshold, report.PressureSource, commitText));
        }

        private void RenderLeakLine(LeakReport report)
        {
            if (report.LeakDetected)
            {
                CacheText(_lblLeakLine, ref _textLeakLine, string.Format(CultureInfo.CurrentCulture,
                    "发现 {0} 个疑似泄漏进程（窗口 {1} 分钟）：{2}",
                    report.TopLeaks.Count, _cfg.WindowMinutes, JoinNames(report.TopLeaks, 4)));
                _lblLeakLine.ForeColor = UiTheme.TextBad;
                return;
            }

            if (report.SystemTrending)
            {
                CacheText(_lblLeakLine, ref _textLeakLine, string.Format(CultureInfo.CurrentCulture,
                    "暂无进程达标；但系统可用内存持续下降 {0:N0} MB/分钟（R² {1:0.00}），请留意",
                    -report.SystemAvailSlopeMBPerMin, report.SystemTrendR2));
                _lblLeakLine.ForeColor = UiTheme.TextWarn;
                return;
            }

            _lblLeakLine.ForeColor = UiTheme.TextMain;
            CacheText(_lblLeakLine, ref _textLeakLine, string.Format(CultureInfo.CurrentCulture,
                "暂无进程达到泄漏判定条件（已采集 {0} 轮样本；需 ≥ {1} 轮且跨度 ≥ {2:0.#} 分钟）",
                _detector.SystemSampleCount, _detector.MinSamples, _detector.MinSpanMinutes));
        }

        private void RenderTrendLine(LeakReport report)
        {
            CacheText(_lblTrend, ref _textTrend, string.Format(CultureInfo.CurrentCulture,
                "系统趋势：可用内存斜率 {0:N1} MB/分钟（R² {1:0.00}）    窗口内已用变化 {2:N0} MB    "
                + "采样 {3} 轮 · 跟踪 {4} 个进程（全系统 {5} 个，未打开任何进程句柄）",
                report.SystemAvailSlopeMBPerMin, report.SystemTrendR2, report.SystemUsedGrowthMB,
                _detector.SystemSampleCount, _detector.TrackedCount, _detector.LastSystemProcessCount));
        }

        /// <summary>上次清理与累计统计。只在清理结束后重算一次（见 _textHistory 缓存）。</summary>
        private void RenderHistoryLine()
        {
            string last = "无";
            if (_lastResult != null)
            {
                last = string.Format(CultureInfo.CurrentCulture, "{0:MM-dd HH:mm:ss} {1} 释放 {2}",
                    _lastResult.Time, MemoryCleaner.TriggerText(_lastResult.Trigger), _lastResult.FreedText);
            }

            CacheText(_lblHistory, ref _textHistory, string.Format(CultureInfo.CurrentCulture,
                "累计清理 {0} 次，累计释放 {1:N0} MB（{2:N1} GB）    上次：{3}",
                History.TotalCleanCount, History.TotalFreedMB, History.TotalFreedMB / 1024.0, last));
        }

        private void UpdateStatusLabel()
        {
            if (_cleaning)
            {
                _lblStatus.ForeColor = UiTheme.TextWarn;
                CacheText(_lblStatus, ref _textStatus, "状态：正在清理内存…");
                UpdateNextAutoLabel();
                return;
            }

            LeakReport report = _lastReport;
            bool pressure = report != null && report.UnderPressure;
            bool leaking = report != null && report.LeakDetected;

            string text;
            if (pressure && leaking)
            {
                text = "状态：内存压力过高 + 检测到内存泄漏";
                _lblStatus.ForeColor = UiTheme.TextBad;
            }
            else if (pressure)
            {
                text = "状态：内存压力过高（" + _cfg.PressureThreshold + "% 阈值已触发）";
                _lblStatus.ForeColor = UiTheme.TextWarn;
            }
            else if (leaking)
            {
                text = "状态：检测到疑似内存泄漏";
                _lblStatus.ForeColor = UiTheme.TextWarn;
            }
            else
            {
                text = "状态：内存正常";
                _lblStatus.ForeColor = UiTheme.TextGood;
            }

            if (!_cfg.AutoClean) text += "（自动清理已关闭）";
            CacheText(_lblStatus, ref _textStatus, text);
            UpdateNextAutoLabel();
        }

        private void UpdateNextAutoLabel()
        {
            if (!_cfg.AutoClean)
            {
                CacheText(_lblNextAuto, ref _textNextAuto, "自动清理：已关闭");
                return;
            }

            double remain = CooldownRemainSeconds();
            if (remain <= 0)
            {
                CacheText(_lblNextAuto, ref _textNextAuto, "自动清理：就绪（CD " + FormatCooldown() + "）");
                return;
            }

            TimeSpan left = TimeSpan.FromSeconds(remain);
            CacheText(_lblNextAuto, ref _textNextAuto, string.Format(CultureInfo.CurrentCulture,
                "自动清理 CD：{0:00}:{1:00} 后可再次触发", (int)left.TotalMinutes, left.Seconds));
        }

        /// <summary>
        /// 冷却剩余秒数；0 表示可以立即触发。
        ///
        /// 用 <see cref="Environment.TickCount"/> 而不是 DateTime.Now：
        /// 单调时钟不会因为用户改系统时间、时区同步或夏令时调整而跳变，
        /// 「CD 还有 5 分钟」不会因为对表而突然变成「就绪」或「还需 3 小时」。
        /// 无符号相减让 49 天一次的回绕也自然成立。
        /// </summary>
        private double CooldownRemainSeconds()
        {
            if (_lastAutoCleanTick == 0) return 0;
            uint elapsedMs = unchecked((uint)(Environment.TickCount - _lastAutoCleanTick));
            return _cfg.CooldownSeconds - elapsedMs / 1000.0;
        }

        private string FormatCooldown()
        {
            if (_cfg.CooldownSeconds % 60 == 0) return (_cfg.CooldownSeconds / 60) + " 分钟";
            return _cfg.CooldownSeconds + " 秒";
        }

        private static string JoinNames(List<LeakItem> items, int max)
        {
            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < items.Count && i < max; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(items[i].Name);
            }
            if (items.Count > max) sb.Append(" 等");
            return sb.ToString();
        }

        private void UpdatePrivilegeLabel()
        {
            CacheText(_lblPrivilege, ref _textPrivilege, MemoryCleaner.PrivilegeOk
                ? "权限：管理员 + 清理特权已启用（SeProfileSingleProcessPrivilege / SeIncreaseQuotaPrivilege）"
                : "权限：管理员，但部分清理特权启用失败 → " + MemoryCleaner.PrivilegeDetail);
        }

        /// <summary>
        /// 刷新进程列表：复用已有的行对象，只改变化的单元格与配色。
        ///
        /// 为什么不再「每轮 Clear + 重建」：ListView 清空重填会丢掉用户的选中项与滚动位置，
        /// 而且在流光动画同时运行时整块重建肉眼可见地闪一下。
        /// 复用行的成本是「比较文本」，比重建 8 列 × 16 行便宜得多，也不会闪烁。
        /// </summary>
        private void RenderList(LeakReport report)
        {
            List<LeakItem> source = report.RecentConsumers;
            if (source.Count == 0) source = report.TopConsumers;

            if (source.Count == 0)
            {
                if (_rows.Count > 0)
                {
                    _list.Items.Clear();
                    _rows.Clear();
                }
                CacheText(_lblListHint, ref _textListHint,
                    "正在采集样本…（首轮完成后即显示占用排行）");
                return;
            }

            _list.BeginUpdate();
            try
            {
                // 行数不够就补（只在进程数变化时发生）
                while (_rows.Count < source.Count)
                {
                    ListViewItem row = new ListViewItem("");
                    for (int c = 1; c < ColumnTitles.Length; c++) row.SubItems.Add("");
                    _rows.Add(row);
                    _list.Items.Add(row);
                }
                // 行数多了就删
                while (_rows.Count > source.Count)
                {
                    ListViewItem last = _rows[_rows.Count - 1];
                    _list.Items.Remove(last);
                    _rows.RemoveAt(_rows.Count - 1);
                }
                for (int i = 0; i < source.Count; i++) SyncRow(_rows[i], source[i]);
            }
            finally
            {
                _list.EndUpdate();
            }

            int leaks = report.TopLeaks.Count;
            CacheText(_lblListHint, ref _textListHint, leaks > 0
                ? string.Format(CultureInfo.CurrentCulture,
                    "可疑进程（红色加粗 = 判定为疑似内存泄漏，将被自动清理；共 {0} 个）", leaks)
                : "当前占用最高进程（趋势样本积累中，达到判定条件后会标红泄漏进程）");
        }

        /// <summary>把一条分析结果写进已有的行（只改真正变化的单元格）。</summary>
        private void SyncRow(ListViewItem row, LeakItem item)
        {
            SetCell(row, 0, item.Name);
            SetCell(row, 1, item.Pid.ToString(CultureInfo.InvariantCulture));
            SetCell(row, 2, item.WorkingSetMB.ToString("N1") + " MB");
            SetCell(row, 3, item.PrivateMB.ToString("N1") + " MB");
            if (item.HasTrend)
            {
                SetCell(row, 4, Signed(item.SlopeMBPerMin, "0.0") + " MB/min");
                SetCell(row, 5, item.R2.ToString("0.00"));
            }
            else
            {
                SetCell(row, 4, "采样中");
                SetCell(row, 5, "—");
            }
            SetCell(row, 6, Signed(item.GrowthMB, "N0") + " MB");
            SetCell(row, 7, item.IsLeaking ? "疑似泄漏" : "正常");

            if (item.IsLeaking)
            {
                if (row.Font != UiTheme.FontBold) row.Font = UiTheme.FontBold;
                if (row.ForeColor != UiTheme.TextBad) row.ForeColor = UiTheme.TextBad;
            }
            else
            {
                if (row.Font != null) row.Font = null;      // null = 用 ListView 自身的字体
                if (row.ForeColor != UiTheme.TextNormal) row.ForeColor = UiTheme.TextNormal;
            }
        }

        private static void SetCell(ListViewItem row, int index, string text)
        {
            if (row.SubItems[index].Text != text) row.SubItems[index].Text = text;
        }

        private static string Signed(double value, string format)
        {
            return (value >= 0 ? "+" : "") + value.ToString(format);
        }

        private void RenderTray(LeakReport report)
        {
            string state;
            if (_cleaning) state = "正在清理…";
            else if (report.UnderPressure && report.LeakDetected) state = "压力高+泄漏";
            else if (report.UnderPressure) state = "内存压力高";
            else if (report.LeakDetected) state = "疑似泄漏";
            else state = "正常";

            string text = string.Format(CultureInfo.CurrentCulture,
                "可用 {0:N1} GB / {1:N1} GB（物理 {2}% · 提交 {3}%）· {4}",
                report.Memory.AvailMB / 1024.0, report.Memory.TotalMB / 1024.0,
                report.Memory.LoadPercent, report.SystemPressurePercent, state);

            if (_miStatus != null) _miStatus.Text = text;

            // NotifyIcon.Text 上限 127 字符，留出余量；内容没变就不重复设置
            string trimmed = text.Length > 120 ? text.Substring(0, 120) : text;
            if (trimmed == _textTray) return;
            _textTray = trimmed;

            try
            {
                _tray.Text = trimmed;
            }
            catch
            {
                try { _tray.Text = "MemTrim"; }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        //  上次清理记录
        // ------------------------------------------------------------------
        private void LoadLastCleanup()
        {
            List<HistoryEntry> entries = History.ReadRecent(1);
            if (entries.Count == 0) return;

            HistoryEntry e = entries[0];
            CleanupResult result = new CleanupResult();
            result.Time = e.Time;
            result.FreedMB = e.FreedMB;
            result.BeforeAvailMB = e.BeforeAvailMB;
            result.AfterAvailMB = e.AfterAvailMB;
            result.ProcessSuccess = e.ProcessCount;
            result.StandbyPurged = e.StandbyPurged;
            result.Trigger = MemoryCleaner.ParseTrigger(e.Trigger);
            _lastResult = result;
        }

        // ------------------------------------------------------------------
        //  自动触发
        // ------------------------------------------------------------------
        private void CheckAutoClean(LeakReport report)
        {
            if (!_cfg.AutoClean) return;
            if (CooldownRemainSeconds() > 0) return;      // CD 未结束

            bool pressure = report.UnderPressure;
            bool leaking = report.LeakDetected;
            if (!pressure && !leaking) return;

            CleanupTrigger trigger;
            if (pressure && leaking) trigger = CleanupTrigger.AutoBoth;
            else if (pressure) trigger = CleanupTrigger.AutoPressure;
            else trigger = CleanupTrigger.AutoLeak;

            if (_cfg.NotifyOnAuto)
            {
                ShowToast("MemTrim 自动清理", MemoryCleaner.TriggerText(trigger) + "，开始清理内存…",
                    ToolTipIcon.Info);
            }
            StartCleanup(trigger);
        }

        // ------------------------------------------------------------------
        //  执行清理
        // ------------------------------------------------------------------
        private void StartCleanup(CleanupTrigger trigger)
        {
            if (_cleaning)
            {
                if (trigger == CleanupTrigger.Manual)
                {
                    MessageBox.Show(this, "上一次清理尚未结束，请稍候。", "MemTrim",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            _cleaning = true;
            _btnClean.Enabled = false;
            UpdateStatusLabel();

            // 目标列表在 Analyze 时就已定格（LeakItem 每轮新建，不会被后续轮次改写），
            // 因此可以安全地交给后台线程使用。
            List<LeakItem> payload = SelectCleanupTargets(trigger);
            bool purgeStandby = _cfg.PurgeStandbyList;      // 先取快照，避免读线程与设置对话框竞争

            // 清理会阻塞数百毫秒，放到线程池执行，完成后回 UI 线程收尾
            ThreadPool.QueueUserWorkItem(delegate
            {
                CleanupResult result;
                try
                {
                    result = MemoryCleaner.Run(payload, purgeStandby, trigger);
                }
                catch (Exception ex)
                {
                    // 清理阶段不允许把异常抛到线程池（会终结进程）
                    result = new CleanupResult();
                    result.Trigger = trigger;
                    result.Detail = "清理异常: " + ex.Message;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnCleanupFinished(result, trigger); });
                }
                catch
                {
                    // 窗口已销毁
                }
            });
        }

        /// <summary>
        /// 选出要被清空工作集的进程：
        /// 默认只清「判定为泄漏」的进程；手动清理且暂无判定结果时，
        /// 退化为清理当前占用最高的几个进程，让手动按钮始终有实际效果。
        /// </summary>
        private List<LeakItem> SelectCleanupTargets(CleanupTrigger trigger)
        {
            List<LeakItem> targets = new List<LeakItem>();
            if (!_cfg.TrimLeakProcesses || _lastReport == null) return targets;

            List<LeakItem> leaks = _lastReport.TopLeaks;
            int limit = Math.Min(_cfg.TopLeakCount, leaks.Count);
            for (int i = 0; i < limit; i++) targets.Add(leaks[i]);

            if (targets.Count == 0 && trigger == CleanupTrigger.Manual
                && _lastReport.TopConsumers.Count > 0)
            {
                int fallback = Math.Min(3, _lastReport.TopConsumers.Count);
                for (int i = 0; i < fallback; i++) targets.Add(_lastReport.TopConsumers[i]);
            }
            return targets;
        }

        private void OnCleanupFinished(CleanupResult result, CleanupTrigger trigger)
        {
            _cleaning = false;
            _btnClean.Enabled = true;
            _lastResult = result;

            if (trigger != CleanupTrigger.Manual) _lastAutoCleanTick = Environment.TickCount;

            History.Append(ToHistoryEntry(result));
            History.TrimIfNeeded();
            _textHistory = null;            // 强制下一轮重算累计行

            RenderHistoryLine();
            UpdateStatusLabel();

            if (trigger == CleanupTrigger.Manual)
            {
                MessageBox.Show(this, result.Detail,
                    "内存清理完成（" + MemoryCleaner.TriggerText(result.Trigger) + "）",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (_cfg.NotifyOnAuto)
            {
                ShowToast("MemTrim 自动清理完成", MemoryCleaner.Summary(result), ToolTipIcon.Info);
            }
        }

        private static HistoryEntry ToHistoryEntry(CleanupResult result)
        {
            HistoryEntry entry = new HistoryEntry();
            entry.Time = result.Time;
            entry.Trigger = MemoryCleaner.TriggerText(result.Trigger);
            entry.FreedMB = result.FreedMB;
            entry.BeforeAvailMB = result.BeforeAvailMB;
            entry.AfterAvailMB = result.AfterAvailMB;
            entry.ProcessCount = result.ProcessSuccess;
            entry.StandbyPurged = result.StandbyPurged;
            return entry;
        }

        // ------------------------------------------------------------------
        //  托盘与窗口生命周期
        // ------------------------------------------------------------------
        private void ShowToast(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            }
            catch
            {
                // 通知失败不影响主流程
            }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;

            if (_trayHintShown) return;
            _trayHintShown = true;
            ShowToast("MemTrim 仍在后台运行",
                "程序已最小化到托盘，将继续监控内存并按设置自动清理。双击托盘图标可打开窗口。",
                ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _cfg.CloseToTray) HideToTray();
            // 宽度变化时重算列宽：否则在 125%/150% 缩放下（客户区比设计值更宽）
            // 固定列会溢出，列表底部冒出一条横向滚动条
            FitListColumns();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool userClosing = e.CloseReason == CloseReason.UserClosing ||
                               e.CloseReason == CloseReason.TaskManagerClosing;
            if (!_exitRequested && _cfg.CloseToTray && userClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();

                _tray.Visible = false;
                _tray.Dispose();

                // 共享字体（UiTheme.FontUi 等）由 Program 在退出时统一释放；
                // 这里只释放本窗口独占的资源。
                if (_headerLogo != null) _headerLogo.Dispose();
                if (_appIcon != null) _appIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        //  清理历史窗口
        // ------------------------------------------------------------------
        private void ShowHistory()
        {
            using (Form window = new Form())
            {
                window.Text = "MemTrim 清理历史";
                window.Font = UiTheme.FontUi;
                window.ClientSize = new Size(760, 460);
                window.StartPosition = FormStartPosition.CenterParent;
                window.ShowInTaskbar = false;
                window.MinimizeBox = false;

                TextBox text = new TextBox();
                text.Multiline = true;
                text.ReadOnly = true;
                text.ScrollBars = ScrollBars.Both;
                text.WordWrap = false;
                text.Font = new Font("Consolas", 9F);       // 等宽字体：只为对齐表格
                text.Dock = DockStyle.Fill;
                text.Text = BuildHistoryText(50);
                window.Controls.Add(text);

                Panel bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = 46;

                Button clear = new Button();
                clear.Text = "清空历史";
                clear.Size = new Size(88, 30);
                clear.Location = new Point(12, 8);
                clear.Click += delegate
                {
                    if (MessageBox.Show(window, "确定清空全部清理历史与累计统计？", "MemTrim",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    History.Clear();
                    text.Text = "已清空。";
                    _textHistory = null;
                    RenderHistoryLine();
                };
                bottom.Controls.Add(clear);

                Button close = new Button();
                close.Text = "关闭";
                close.Size = new Size(88, 30);
                close.Location = new Point(window.ClientSize.Width - 104, 8);
                close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
                close.DialogResult = DialogResult.OK;
                bottom.Controls.Add(close);

                window.Controls.Add(bottom);
                window.CancelButton = close;
                window.ShowDialog(this);
            }
        }

        private static string BuildHistoryText(int count)
        {
            List<HistoryEntry> entries = History.ReadRecent(count);

            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("累计清理 " + History.TotalCleanCount + " 次，累计释放 " +
                          History.TotalFreedMB.ToString("N0") + " MB");
            sb.AppendLine("历史文件：" + History.HistoryPath);
            sb.AppendLine();

            if (entries.Count == 0)
            {
                sb.AppendLine("（暂无记录）");
                return sb.ToString();
            }

            sb.AppendLine("时间                 触发方式              释放        清理前可用     清理后可用   进程数  待机列表");
            for (int i = 0; i < entries.Count; i++)
            {
                HistoryEntry e = entries[i];
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "{0:yyyy-MM-dd HH:mm:ss}  {1,-18}  {2,8:N0} MB  {3,10:N0} MB  {4,10:N0} MB  {5,5}  {6}",
                    e.Time, e.Trigger, e.FreedMB, e.BeforeAvailMB, e.AfterAvailMB,
                    e.ProcessCount, e.StandbyPurged ? "是" : "否"));
            }
            return sb.ToString();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
