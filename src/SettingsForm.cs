// ============================================================================
//  MemTrim · SettingsForm.cs —— 设置对话框
//
//  所有编辑都作用在传入配置对象的「副本」上（由 MainForm 负责克隆），
//  因此点「取消」不会留下半套生效的配置。
//
//  表单本身不认识运行时：它只负责「界面 ↔ AppConfig」的搬运，
//  真正的落盘与生效由 MainForm.ApplyConfig 完成，避免两处各写一遍。
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MemTrim
{
    internal sealed class SettingsForm : Form
    {
        private const int LabelX = 20;
        private const int ControlX = 360;
        private const int RowHeight = 30;
        private const int CheckHeight = 28;
        private const int HintHeight = 30;

        private readonly AppConfig _cfg;
        private readonly Func<bool> _autoStartGetter;

        private NumericUpDown _numCooldown;
        private NumericUpDown _numPressure;
        private NumericUpDown _numSlope;
        private NumericUpDown _numR2;
        private NumericUpDown _numWindow;
        private NumericUpDown _numTop;
        private NumericUpDown _numMinWs;
        private CheckBox _chkAuto;
        private CheckBox _chkTrim;
        private CheckBox _chkStandby;
        private CheckBox _chkNotify;
        private CheckBox _chkAutoStart;
        private CheckBox _chkCloseTray;

        /// <summary>最近一次由 Number() 创建的输入框，便于直接赋给字段。</summary>
        private NumericUpDown _lastNumber;

        /// <param name="cfg">配置副本：本对话框会直接改它，取消时由调用方丢弃。</param>
        /// <param name="autoStartGetter">读取注册表里真实的自启动状态（注册表才是事实来源）。</param>
        public SettingsForm(AppConfig cfg, Func<bool> autoStartGetter)
        {
            _cfg = cfg;
            _autoStartGetter = autoStartGetter;

            Text = "MemTrim 设置";
            Font = UiTheme.FontUi;              // 共享字体，不随本窗口释放
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(568, 596);
            BackColor = Color.White;

            BuildControls();
            BuildButtons();
        }

        /// <summary>
        /// 正在编辑的配置副本。调用方在 DialogResult == OK 时用它替换原配置，
        /// 点「取消」时直接丢弃，原配置不会出现「改了一半」的状态。
        /// </summary>
        public AppConfig EditedConfig { get { return _cfg; } }

        // ------------------------------------------------------------------
        //  构建
        // ------------------------------------------------------------------
        private void BuildControls()
        {
            int y = 16;

            Section("自动清理", ref y);

            _chkAuto = Check("启用自动清理（满足下面任一条件时自动触发）", ref y);
            _chkAuto.Checked = _cfg.AutoClean;

            Number("内置冷却 CD（秒）", ref y, 30, 86400, _cfg.CooldownSeconds, 30);
            _numCooldown = _lastNumber;
            Hint("默认 300 秒 = 5 分钟：两次自动清理之间的最小间隔", ref y);

            Number("自动触发：内存压力 ≥ (%)", ref y, 30, 99, _cfg.PressureThreshold, 1);
            _numPressure = _lastNumber;
            Hint("压力 = 已提交内存 ÷ 提交上限（不是任务管理器的物理占用率）", ref y);

            Section("泄漏检测", ref y);

            Number("泄漏判定：增长斜率 ≥ (MB/分钟)", ref y,
                0.1M, 500M, (decimal)_cfg.LeakSlopeThreshold, 0.5M);
            _numSlope = _lastNumber;

            Number("泄漏判定：线性拟合优度 R² ≥", ref y, 0M, 0.99M, (decimal)_cfg.LeakR2Threshold, 0.05M);
            _numR2 = _lastNumber;
            _numR2.DecimalPlaces = 2;

            Number("滑动统计窗口（分钟）", ref y, 2, 60, _cfg.WindowMinutes, 1);
            _numWindow = _lastNumber;

            Number("每次清理的进程数量上限", ref y, 1, 20, _cfg.TopLeakCount, 1);
            _numTop = _lastNumber;

            Number("参与统计的最小工作集 (MB)", ref y, 1M, 4096M, (decimal)_cfg.MinWorkingSetMB, 10M);
            _numMinWs = _lastNumber;
            Hint("低于该工作集的进程不参与统计（也不解析映像名），是降低监控开销的主要开关", ref y);

            Hint("斜率与 R² 需同时达标才判定为泄漏进程，可有效抑制噪声误报", ref y);

            Section("清理范围", ref y);

            _chkTrim = Check("清空泄漏进程的工作集（EmptyWorkingSet）", ref y);
            _chkTrim.Checked = _cfg.TrimLeakProcesses;

            _chkStandby = Check("清空系统待机列表（NtSetSystemInformation / PurgeStandbyList）", ref y);
            _chkStandby.Checked = _cfg.PurgeStandbyList;

            Hint("这两项即 PCL「内存优化」的核心动作；写回修改页与物理内存合并未启用", ref y);

            Section("其它", ref y);

            _chkNotify = Check("自动清理时弹托盘通知", ref y);
            _chkNotify.Checked = _cfg.NotifyOnAuto;

            _chkAutoStart = Check("开机自启动（写入 HKCU\\...\\Run）", ref y);
            _chkAutoStart.Checked = _autoStartGetter();     // 以注册表为准，而不是配置里的值

            _chkCloseTray = Check("关闭窗口时最小化到托盘", ref y);
            _chkCloseTray.Checked = _cfg.CloseToTray;
        }

        private void BuildButtons()
        {
            Button ok = FooterButton("保存", ClientSize.Width - 200, 88);
            ok.DialogResult = DialogResult.OK;
            ok.Click += OnSave;

            Button cancel = FooterButton("取消", ClientSize.Width - 104, 88);
            cancel.DialogResult = DialogResult.Cancel;

            Button openDir = FooterButton("打开配置目录", LabelX, 120);
            openDir.Click += OnOpenConfigDir;

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>底部按钮（统一高度与底边对齐，避免各写一串坐标）。</summary>
        private Button FooterButton(string text, int x, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(width, 30);
            button.Location = new Point(x, ClientSize.Height - 46);
            Controls.Add(button);
            return button;
        }

        // ------------------------------------------------------------------
        //  事件
        // ------------------------------------------------------------------
        /// <summary>点「保存」时才把界面值写回配置对象。</summary>
        private void OnSave(object sender, EventArgs e)
        {
            _cfg.AutoClean = _chkAuto.Checked;
            _cfg.CooldownSeconds = (int)_numCooldown.Value;
            _cfg.PressureThreshold = (int)_numPressure.Value;
            _cfg.LeakSlopeThreshold = (double)_numSlope.Value;
            _cfg.LeakR2Threshold = (double)_numR2.Value;
            _cfg.WindowMinutes = (int)_numWindow.Value;
            _cfg.TopLeakCount = (int)_numTop.Value;
            _cfg.MinWorkingSetMB = (double)_numMinWs.Value;
            _cfg.TrimLeakProcesses = _chkTrim.Checked;
            _cfg.PurgeStandbyList = _chkStandby.Checked;
            _cfg.NotifyOnAuto = _chkNotify.Checked;
            _cfg.AutoStart = _chkAutoStart.Checked;
            _cfg.CloseToTray = _chkCloseTray.Checked;
            _cfg.Sanitize();
            // 落盘交给 MainForm：它要保证「保存成功」与「参数已生效」是同一件事
        }

        private static void OnOpenConfigDir(object sender, EventArgs e)
        {
            try
            {
                AppPaths.EnsureDataDir();
                System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\"");
            }
            catch
            {
                // 打不开资源管理器不影响设置本身
            }
        }

        // ------------------------------------------------------------------
        //  布局小工具：统一 y 轴推进，避免手工算坐标
        // ------------------------------------------------------------------
        private void Section(string text, ref int y)
        {
            Controls.Add(UiKit.Text(text, LabelX, y, UiTheme.FontSection, UiTheme.SectionTitle));
            y += 26;
        }

        private void Number(string label, ref int y,
            decimal min, decimal max, decimal value, decimal step)
        {
            Controls.Add(UiKit.Text(label, LabelX, y + 3, Font, UiTheme.TextNormal));

            NumericUpDown input = new NumericUpDown();
            input.Minimum = min;
            input.Maximum = max;
            input.Value = Math.Min(max, Math.Max(min, value));
            input.Increment = step;
            input.Location = new Point(ControlX, y);
            input.Size = new Size(100, 24);
            Controls.Add(input);

            _lastNumber = input;
            y += RowHeight;
        }

        private CheckBox Check(string text, ref int y)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.AutoSize = true;
            box.Location = new Point(LabelX, y);
            box.ForeColor = UiTheme.TextNormal;
            Controls.Add(box);
            y += CheckHeight;
            return box;
        }

        private void Hint(string text, ref int y)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Size = new Size(ClientSize.Width - 40, HintHeight);
            label.ForeColor = UiTheme.TextFaint;
            label.Font = UiTheme.FontSmall;
            label.Location = new Point(LabelX, y);
            Controls.Add(label);
            y += HintHeight;
        }
    }
}
