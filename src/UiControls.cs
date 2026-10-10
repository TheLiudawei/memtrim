// ============================================================================
//  MemTrim · UiControls.cs —— 自绘控件与控件构造小工具
//
//  内容：
//      ① RoundedCorners   圆角位置（可组合）
//      ② UiKit            圆角路径、卡片、标签等构造小工具
//      ③ UsageBar         自绘圆角占用条
//      ④ RgbGlowButton    自绘流光发光按钮（主按钮）
//
//  资源约定：字体、图标、位图、画笔都在构造期创建一次并在 Dispose 中释放。
//  曾经的写法是「每次刷新为每一行 new Font(...)」，长时间运行会持续消耗 GDI 句柄。
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MemTrim
{
    // ========================================================================
    //  ① 圆角位置
    // ========================================================================
    /// <summary>圆角位置，可组合。</summary>
    [Flags]
    internal enum RoundedCorners
    {
        None = 0,
        TopLeft = 1,
        TopRight = 2,
        BottomLeft = 4,
        BottomRight = 8,
        All = TopLeft | TopRight | BottomLeft | BottomRight
    }

    // ========================================================================
    //  ② 控件构造小工具
    // ========================================================================
    /// <summary>
    /// 卡片面板：白色底 + 1px 边框，并在尺寸变化时整块重绘。
    ///
    /// 为什么要单独派生一个类型：`ResizeRedraw` 与 `SetStyle` 都是
    /// <see cref="Control"/> 的 protected 成员，只有子类才能设置。
    /// 不开启 ResizeRedraw 时，改变窗口大小只会重画「新暴露的那一条区域」，
    /// 卡片内会残留横向拉伸的旧画面 —— 用户看到的就是一列列竖向细线。
    /// </summary>
    internal sealed class CardPanel : Panel
    {
        private readonly Pen _borderPen;

        public CardPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = UiTheme.CardBack;
            // 画笔常驻：Paint 事件每帧都可能触发，没必要每次 new 一支再释放
            _borderPen = new Pen(UiTheme.CardBorder);
            Paint += OnPaintBorder;
        }

        private void OnPaintBorder(object sender, PaintEventArgs e)
        {
            e.Graphics.DrawRectangle(_borderPen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _borderPen.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>把「new 一个控件再设置 8 个属性」的重复代码压缩成一行。</summary>
    internal static class UiKit
    {
        /// <summary>带 1px 边框的白色卡片面板。</summary>
        public static CardPanel Card(int x, int y, int width, int height)
        {
            CardPanel panel = new CardPanel();
            panel.Location = new Point(x, y);
            panel.Size = new Size(width, height);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            return panel;
        }

        /// <summary>卡片内的小标题。</summary>
        public static Label CardTitle(string text, int x, int y)
        {
            return Text(text, x, y, UiTheme.FontSection, UiTheme.SectionTitle);
        }

        /// <summary>单行自动宽度标签（字体一律复用 UiTheme 里的共享实例）。</summary>
        public static Label Text(string text, int x, int y, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = font;
            label.ForeColor = color;
            label.Location = new Point(x, y);
            return label;
        }

        /// <summary>圆角矩形路径。半径过小或矩形过小时退化为直角矩形。</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            return RoundedRect(bounds, radius, RoundedCorners.All);
        }

        /// <summary>按指定圆角位置生成路径（未选中的角保持直角）。</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius, RoundedCorners corners)
        {
            GraphicsPath path = new GraphicsPath();
            bool any = corners != RoundedCorners.None;
            if (radius <= 1 || !any || bounds.Width < 3 || bounds.Height < 3)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = radius * 2;
            if (diameter > bounds.Width) diameter = bounds.Width;
            if (diameter > bounds.Height) diameter = bounds.Height;

            // 顺序固定为「左上 → 右上 → 右下 → 左下」，四段拼接后路径首尾相接、不自交
            AppendCorner(path, bounds, diameter, corners, RoundedCorners.TopLeft, 180,
                bounds.X, bounds.Y);
            AppendCorner(path, bounds, diameter, corners, RoundedCorners.TopRight, 270,
                bounds.Right - diameter, bounds.Y);
            AppendCorner(path, bounds, diameter, corners, RoundedCorners.BottomRight, 0,
                bounds.Right - diameter, bounds.Bottom - diameter);
            AppendCorner(path, bounds, diameter, corners, RoundedCorners.BottomLeft, 90,
                bounds.X, bounds.Bottom - diameter);
            path.CloseFigure();
            return path;
        }

        private static void AppendCorner(GraphicsPath path, Rectangle bounds, int diameter,
            RoundedCorners wanted, RoundedCorners corner, float startAngle, int x, int y)
        {
            if ((wanted & corner) == 0)
            {
                // 直角：补上该角的两条边，路径才不会断开
                switch (corner)
                {
                    case RoundedCorners.TopLeft:
                        path.AddLine(bounds.X, bounds.Y + diameter, bounds.X, bounds.Y);
                        path.AddLine(bounds.X, bounds.Y, bounds.X + diameter, bounds.Y);
                        break;
                    case RoundedCorners.TopRight:
                        path.AddLine(bounds.Right - diameter, bounds.Y, bounds.Right, bounds.Y);
                        path.AddLine(bounds.Right, bounds.Y, bounds.Right, bounds.Y + diameter);
                        break;
                    case RoundedCorners.BottomRight:
                        path.AddLine(bounds.Right, bounds.Bottom - diameter, bounds.Right, bounds.Bottom);
                        path.AddLine(bounds.Right, bounds.Bottom, bounds.Right - diameter, bounds.Bottom);
                        break;
                    default:
                        path.AddLine(bounds.X + diameter, bounds.Bottom, bounds.X, bounds.Bottom);
                        path.AddLine(bounds.X, bounds.Bottom, bounds.X, bounds.Bottom - diameter);
                        break;
                }
                return;
            }

            path.AddArc(x, y, diameter, diameter, startAngle, 90);
        }
    }

    // ========================================================================
    //  ③ 圆角内存占用条
    // ========================================================================
    /// <summary>自绘的水平占用条：左侧按比例填充渐变色，居中显示说明文字。</summary>
    internal sealed class UsageBar : Control
    {
        private double _ratio;          // 0..1
        private string _text = "";
        private Color _accent = UiTheme.AccentGood;

        public UsageBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(238, 242, 247);
            ForeColor = Color.FromArgb(32, 38, 48);
            Font = UiTheme.FontUi;      // 共享字体：由 UiFonts.DisposeAll 统一释放
            Height = UiTheme.Dpi(28);
        }

        /// <summary>更新占用比例与文字；比例会自动夹到 0~1，颜色随压力变化。</summary>
        public void SetValue(double ratio, string text)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            _ratio = ratio;
            _text = text == null ? "" : text;

            if (ratio >= 0.9) _accent = UiTheme.AccentBad;
            else if (ratio >= 0.75) _accent = UiTheme.AccentWarn;
            else _accent = UiTheme.AccentGood;

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Math.Max(4, Height / 2);

            using (GraphicsPath background = UiKit.RoundedRect(bounds, radius))
            using (SolidBrush brush = new SolidBrush(BackColor))
            {
                g.FillPath(brush, background);
            }

            int fillWidth = (int)Math.Round((Width - 1) * _ratio);
            if (fillWidth > 2)
            {
                Rectangle filled = new Rectangle(0, 0, fillWidth, Height - 1);
                using (GraphicsPath path = UiKit.RoundedRect(filled, Math.Min(radius, fillWidth / 2)))
                using (LinearGradientBrush gradient = new LinearGradientBrush(
                    filled, Lighten(_accent, 0.18), _accent, LinearGradientMode.Vertical))
                {
                    g.FillPath(gradient, path);
                }
            }

            TextRenderer.DrawText(g, _text, Font, bounds, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        /// <summary>把颜色向白色靠拢，用于渐变的高光端。</summary>
        private static Color Lighten(Color color, double amount)
        {
            int r = color.R + (int)((255 - color.R) * amount);
            int g = color.G + (int)((255 - color.G) * amount);
            int b = color.B + (int)((255 - color.B) * amount);
            return Color.FromArgb(color.A, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }
    }

    // ========================================================================
    //  ④ 流光发光按钮
    // ========================================================================
    /// <summary>
    /// 自绘按钮：圆角底面 + 深色文字，光效全部画在底面之外（贴边细描边 + 向外的柔光）。
    ///
    /// 光效做法取自 MeteorNOX/DeepSeek-Balance-Whale-Widget 的「文字光效」
    /// （assets/whale-widget.js 的 <c>.dshwv-trow.dshwv-rgb</c> 与 <c>@keyframes dshwvRainbow</c>）：
    ///   · 一条固定的十段渐变色（默认「马卡龙」配色），首尾同色，因此能无缝平铺；
    ///   · 渐变按盒宽的 200% 铺开（对应 <c>background-size:200% auto</c>）；
    ///   · 只让渐变整体匀速平移，2.6 秒正好走完一整个渐变周期
    ///     （对应 <c>background-position:0% → 200%</c>，首尾相接，不跳变）。
    ///
    /// 参考仓库是把这条渐变裁进文字（<c>background-clip:text</c>）；本控件保留浅色底面
    /// 与深色文字，改用同一条流动渐变去画描边与柔光，因此光晕配色与流动节奏
    /// 和参考仓库完全一致，也不再有色相旋转与亮度呼吸。
    ///
    /// 资源约定（这个控件常驻可见，必须长期零分配）：
    ///   · 圆角路径、渐变刷、描边笔在构造/尺寸变化时各建一次；
    ///   · 每帧只把渐变刷平移一个相位，不 new 任何 GDI 对象；
    ///   · 窗口不可见/最小化/被禁用时自动停表，托盘常驻时不产生任何重绘。
    ///
    /// 类名保持 <c>RgbGlowButton</c> 不变：<c>tools\SelfTest.cs</c> 靠类型名定位主按钮，
    /// 改名要同步改那边。字段名 <c>_textBrush</c>、<c>_phase</c>、<c>_phaseStep</c>、
    /// <c>_period</c> 与方法 <c>ApplyPhase</c>、<c>SetButtonBack</c> 同样被自检程序反射使用。
    /// </summary>
    internal sealed class RgbGlowButton : Control
    {
        private const int FrameIntervalMs = 40;     // ≈25 帧/秒
        private const int SweepPeriodMs = 2600;     // 走完一个完整渐变周期的时间（参考仓库的动画时长）
        private const int GlowLayers = 6;           // 柔光由外向内叠几层
        private const int InnerPadding = 6;         // 底面内缩，给柔光留出绘制空间
        private const float BorderWidth = 1.6f;     // 贴边细描边宽度

        /// <summary>
        /// 参考仓库的十段「马卡龙」渐变，逐值照搬，不做增删：
        /// 首尾同为 rgb(255,180,200)，所以按 200% 宽度平铺后平移，接缝处颜色连续。
        /// </summary>
        private static readonly Color[] MacaronStops = new Color[]
        {
            Color.FromArgb(255, 180, 200), Color.FromArgb(255, 205, 170),
            Color.FromArgb(255, 225, 165), Color.FromArgb(245, 240, 180),
            Color.FromArgb(190, 240, 210), Color.FromArgb(180, 230, 245),
            Color.FromArgb(190, 215, 250), Color.FromArgb(220, 200, 245),
            Color.FromArgb(240, 200, 230), Color.FromArgb(255, 180, 200)
        };

        /// <summary>
        /// 柔光由内向外每一层最终要达到的不透明度（长度必须等于 <see cref="GlowLayers"/>）。
        ///
        /// 曲线是刻意压成「贴边很亮、往外掉得很快」的：如果每层用同一个 alpha，
        /// 衰减接近线性，看着就是一圈差不多亮的色带而不是光晕。
        /// </summary>
        private static readonly double[] GlowBandOpacity =
        {
            0.74, 0.52, 0.33, 0.18, 0.08, 0.025
        };

        /// <summary>
        /// 实际写进每一层渐变刷的 alpha，由 <see cref="GlowBandOpacity"/> 反推得到。
        ///
        /// 柔光是嵌套叠加的：第 k 层还会被它内侧的每一层各覆盖一次，因此
        ///   最终不透明度 t[k] = 1 − Π(j = k … N−1) (1 − a[j])
        /// 反解出 a[k] = 1 − (1 − t[k]) / (1 − t[k+1])，其中 t[N] 取 0（最外层之外什么都没有）。
        /// </summary>
        private static readonly int[] GlowAlphas = BuildGlowAlphas();

        private static int[] BuildGlowAlphas()
        {
            int[] alphas = new int[GlowLayers];
            double outer = 0;                       // 当前层外侧的累计不透明度
            for (int k = GlowLayers - 1; k >= 0; k--)
            {
                double target = GlowBandOpacity[k];
                double alpha = 1.0 - (1.0 - target) / (1.0 - outer);
                if (alpha < 0) alpha = 0;
                if (alpha > 1) alpha = 1;
                alphas[k] = (int)Math.Round(alpha * 255);
                outer = target;
            }
            return alphas;
        }

        private readonly Timer _timer;
        /// <summary>柔光层：下标越大越靠外，每层的透明度已烘进渐变色标里。</summary>
        private readonly LinearGradientBrush[] _glowBrushes;
        private readonly GraphicsPath[] _glowPaths;
        private readonly SolidBrush _fillBrush;
        /// <summary>文字色。tools\SelfTest.cs 按字段名反射读取，改名前请同步改那边。</summary>
        private readonly SolidBrush _textBrush;
        /// <summary>禁用态的静态配色，构造期建好，避免禁用期间每帧 new GDI 对象。</summary>
        private readonly SolidBrush _disabledBack;
        private readonly Pen _disabledBorder;
        private readonly SolidBrush _disabledText;

        private LinearGradientBrush _borderBrush;
        private Pen _borderPen;
        private GraphicsPath _border;
        private RoundedCorners _corners = RoundedCorners.All;
        /// <summary>按钮底色：默认白底（与卡片一致），可由调用方改。</summary>
        private Color _buttonBack = Color.White;
        /// <summary>底色是否为浅色：决定文字取深色还是浅色。</summary>
        private bool _lightBack = true;
        /// <summary>一个渐变周期的像素宽度（= 底面宽的 200%）。</summary>
        private float _period;
        /// <summary>每帧的平移量（像素）。</summary>
        private float _phaseStep;
        /// <summary>当前相位，取值 0 ~ 一个周期。</summary>
        private float _phase;

        public RgbGlowButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // 注意：WinForms 的 Control 不支持 Color.Transparent 作为 BackColor
            // （会抛 ArgumentException）。这里用窗体底色并在 OnPaint 里自己铺底，
            // 让圆角外的区域与窗体无缝衔接。
            BackColor = UiTheme.FormBack;
            Cursor = Cursors.Hand;
            Font = UiTheme.FontButton;      // 共享字体：由 UiFonts.DisposeAll 统一释放

            _glowPaths = new GraphicsPath[GlowLayers];
            _glowBrushes = new LinearGradientBrush[GlowLayers];
            _fillBrush = new SolidBrush(_buttonBack);
            _textBrush = new SolidBrush(Color.FromArgb(38, 46, 60));
            _disabledBack = new SolidBrush(Color.FromArgb(226, 230, 237));
            _disabledBorder = new Pen(Color.FromArgb(198, 205, 216));
            _disabledText = new SolidBrush(Color.FromArgb(150, 157, 168));

            _timer = new Timer();
            _timer.Interval = FrameIntervalMs;
            _timer.Tick += OnFrame;

            // 必须在所有绘图数组就绪之后再设定尺寸：设置 Size 会立刻触发
            // OnResize → RebuildGeometry，而后者要往 _glowPaths/_glowBrushes 里填内容。
            Size = new Size(144, 38);
            ApplyPhase();
        }

        /// <summary>改用其他底色（默认白色）。会自动重算文字配色。</summary>
        public void SetButtonBack(Color color)
        {
            _buttonBack = color;
            _lightBack = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 >= 0.6;
            // 深色底 → 白字；浅色底 → 深灰字（浅色底上用浅色文字几乎看不见）
            _textBrush.Color = _lightBack ? Color.FromArgb(38, 46, 60) : Color.FromArgb(244, 248, 252);
            _fillBrush.Color = color;
            Invalidate();
        }

        /// <summary>要显示的文字（自绘控件不会自动处理 Text 变化）。</summary>
        public override string Text
        {
            get { return base.Text; }
            set
            {
                base.Text = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 圆角策略：同一个按钮在四个角上的圆角半径不同，
        /// 这样它既能和左侧的「设置/清理历史」并排时视觉统一，
        /// 也能在单独居中时显得柔和。
        /// </summary>
        public void SetCorners(RoundedCorners corners)
        {
            if (_corners == corners) return;
            _corners = corners;
            RebuildGeometry();
            ApplyPhase();
            Invalidate();
        }

        // ---- 生命周期：不可见就停表 ----
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncTimer();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            SyncTimer();
            Invalidate();
        }

        private void SyncTimer()
        {
            if (IsDisposed) return;

            bool wanted = Visible && Enabled && IsHandleCreated;
            if (wanted == _timer.Enabled) return;

            if (wanted) _timer.Start();
            else _timer.Stop();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SyncTimer();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _timer.Stop();
            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// 动画每帧：只把同一条渐变整体平移一个相位。
        ///
        /// 这里刻意不做参考仓库以外的任何加工 —— 没有色相旋转、没有亮度呼吸，
        /// 因为「匀速平移 + 首尾同色的固定渐变」本身就是那条光效的全部观感来源。
        /// </summary>
        private void OnFrame(object sender, EventArgs e)
        {
            _phase += _phaseStep;
            if (_period > 0 && _phase >= _period) _phase -= _period;

            ApplyPhase();
            Invalidate();
        }

        /// <summary>把当前相位写进所有渐变刷，这是每帧唯一的绘制状态变化。</summary>
        private void ApplyPhase()
        {
            for (int i = 0; i < _glowBrushes.Length; i++) SyncBrush(_glowBrushes[i]);
            SyncBrush(_borderBrush);
        }

        private void SyncBrush(LinearGradientBrush brush)
        {
            if (brush == null) return;
            brush.ResetTransform();
            brush.TranslateTransform(-_phase, 0f);
        }

        // ---- 布局 ----
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildGeometry();
            ApplyPhase();
        }

        /// <summary>
        /// 尺寸变化时重建全部几何与画笔：底面圆角路径、逐层外扩的柔光路径，
        /// 以及按「底面宽的 200%」定周期的流动渐变刷。
        /// </summary>
        private void RebuildGeometry()
        {
            DisposeGeometry();
            if (Width < 8 || Height < 8) return;

            Rectangle r = new Rectangle(InnerPadding, InnerPadding,
                Width - 1 - InnerPadding * 2, Height - 1 - InnerPadding * 2);
            int radius = Math.Max(3, Math.Min(8, (Height - InnerPadding * 2) / 3));
            _border = UiKit.RoundedRect(r, radius, _corners);

            // 柔光：从贴边那一层起逐层向外扩，最外一层正好铺满控件
            // （控件之外的绘制会被窗口裁掉，所以留 InnerPadding 就够了）
            for (int i = 0; i < GlowLayers; i++)
            {
                int grow = (i + 1) * InnerPadding / GlowLayers;
                _glowPaths[i] = UiKit.RoundedRect(Rectangle.Inflate(r, grow, grow),
                    radius + grow, _corners);
                _glowBrushes[i] = CreateSweepBrush(r.Width * 2, GlowAlphas[i]);
            }

            if (_borderPen != null) _borderPen.Dispose();
            _borderBrush = CreateSweepBrush(r.Width * 2, 255);
            _borderPen = new Pen(_borderBrush, BorderWidth);
            _borderPen.LineJoin = LineJoin.Round;

            // 一个周期 = 底面宽的 200%，2.6 秒走完，换算成每帧平移多少像素
            _period = Math.Max(8, r.Width * 2);
            _phaseStep = _period * FrameIntervalMs / SweepPeriodMs;
            if (_phase >= _period) _phase = 0f;
        }

        /// <summary>
        /// 生成一条横向的「流动渐变」笔刷：把参考仓库的十段马卡龙色均匀铺满一个周期
        /// （底面宽的 200%，对应 CSS 的 <c>background-size:200% auto</c>），
        /// 再配合 <see cref="WrapMode.Tile"/> 让它无缝重复 —— 之后每帧只要平移相位
        /// 就能得到匀速流光，不需要重建任何画笔。
        ///
        /// 所有柔光层与描边用的是同一个周期，只是 alpha 不同，因此它们的横向颜色
        /// 在任何时刻都严丝合缝地对齐。
        /// </summary>
        private LinearGradientBrush CreateSweepBrush(int period, int alpha)
        {
            Rectangle tile = new Rectangle(0, 0, Math.Max(8, period), Math.Max(2, Height));
            LinearGradientBrush brush = new LinearGradientBrush(tile, Color.White, Color.White, 0f);
            brush.WrapMode = WrapMode.Tile;

            // 位置均匀分布（等价于 CSS 里不写色标位置的 linear-gradient）
            ColorBlend blend = new ColorBlend();
            blend.Colors = new Color[MacaronStops.Length];
            blend.Positions = new float[MacaronStops.Length];
            for (int i = 0; i < MacaronStops.Length; i++)
            {
                Color stop = MacaronStops[i];
                blend.Colors[i] = Color.FromArgb(alpha, stop.R, stop.G, stop.B);
                blend.Positions[i] = i / (float)(MacaronStops.Length - 1);
            }
            brush.InterpolationColors = blend;
            return brush;
        }

        // ---- 绘制 ----
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle full = new Rectangle(0, 0, Width, Height);

            if (!Enabled)
            {
                // 禁用态：静态灰底，不参与动画（宽度不变，避免布局跳动）
                g.FillRectangle(_disabledBack, full);
                g.DrawRectangle(_disabledBorder, 0, 0, Width - 1, Height - 1);
                TextRenderer.DrawText(g, Text, Font, full, _disabledText.Color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                return;
            }

            if (_border == null) RebuildGeometry();
            if (_border == null) return;

            // ① 柔光：由外向内叠同一整套流动渐变。每一层都是完整的圆角实心块，
            //    越靠内被叠加的次数越多，于是自然形成「贴边最亮、向外渐隐」的光晕。
            //    用实心块叠而不是描粗边，是为了让衰减完全由层数决定，不依赖线宽取整。
            for (int i = _glowPaths.Length - 1; i >= 0; i--)
            {
                if (_glowPaths[i] != null) g.FillPath(_glowBrushes[i], _glowPaths[i]);
            }

            // ② 底面
            g.FillPath(_fillBrush, _border);

            // ③ 贴边细描边：同一条渐变，取不透明色
            g.DrawPath(_borderPen, _border);

            // ④ 文字：浅色底保持深色（浅色底上彩色/浅色文字对比度不够）。
            //    主色那遍用 GDI（TextRenderer）：GDI+ 的 DrawString 在带透明通道的位图上
            //    画浅色字会因混合方式而发暗，实测白字被画成灰色。
            TextRenderer.DrawText(g, Text, Font, full, _textBrush.Color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);
        }

        /// <summary>释放随尺寸重建的那批 GDI 对象（柔光刷/描边笔与路径）。</summary>
        private void DisposeGeometry()
        {
            if (_border != null) { _border.Dispose(); _border = null; }
            if (_borderPen != null) { _borderPen.Dispose(); _borderPen = null; }
            if (_borderBrush != null) { _borderBrush.Dispose(); _borderBrush = null; }

            for (int i = 0; i < GlowLayers; i++)
            {
                if (_glowPaths[i] != null) { _glowPaths[i].Dispose(); _glowPaths[i] = null; }
                if (_glowBrushes[i] != null) { _glowBrushes[i].Dispose(); _glowBrushes[i] = null; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();

                DisposeGeometry();
                _fillBrush.Dispose();
                _textBrush.Dispose();
                _disabledBack.Dispose();
                _disabledBorder.Dispose();
                _disabledText.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
