// ============================================================================
//  MemTrim · UiTheme.cs —— 视觉常量与 DPI 换算
//
//  集中管理配色与字号，避免同一个颜色在十几处各写一遍 ARGB。
//  DPI 换算也放在这里：它是「视觉常量」的一部分，界面各处的像素值都由它推导。
// ============================================================================

using System;
using System.Drawing;

namespace MemTrim
{
    internal static class UiTheme
    {
        // ---- 配色 ----
        public static readonly Color HeaderBack = Color.FromArgb(22, 40, 74);
        public static readonly Color HeaderSub = Color.FromArgb(168, 190, 224);

        public static readonly Color FormBack = Color.FromArgb(244, 247, 251);
        public static readonly Color CardBack = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(224, 230, 238);

        public static readonly Color TextMain = Color.FromArgb(28, 34, 44);
        public static readonly Color TextDim = Color.FromArgb(118, 126, 138);
        public static readonly Color TextNormal = Color.FromArgb(60, 68, 80);
        public static readonly Color TextFaint = Color.FromArgb(120, 128, 140);
        public static readonly Color SectionTitle = Color.FromArgb(24, 60, 120);

        public static readonly Color AccentGood = Color.FromArgb(46, 160, 67);
        public static readonly Color AccentWarn = Color.FromArgb(224, 145, 40);
        public static readonly Color AccentBad = Color.FromArgb(214, 69, 65);

        public static readonly Color TextGood = Color.FromArgb(30, 130, 70);
        public static readonly Color TextWarn = Color.FromArgb(198, 120, 20);
        public static readonly Color TextBad = Color.FromArgb(198, 44, 40);

        public const string FontName = "Segoe UI";

        // ---- 字体（全程序共用这几个实例，避免每处 new Font 造成 GDI 句柄堆积）----
        //
        //  以前 UiKit 每造一个标签就 new 一个 Font，窗体上几十个标签就是几十个
        //  不可回收的 GDI 对象（.NET 的 Font 只是 GDI 字体句柄的包装，
        //  必须显式 Dispose，而控件被释放时并不会连带释放赋给它的 Font）。
        //  字体一旦创建就不再变化，因此改为全局共享 + 由 UiFonts.DisposeAll 统一释放。
        public static readonly Font FontUi = new Font(FontName, 9F);
        public static readonly Font FontBold = new Font(FontName, 9F, FontStyle.Bold);
        public static readonly Font FontSmall = new Font(FontName, 8.25F);
        public static readonly Font FontSection = new Font(FontName, 9.5F, FontStyle.Bold);
        public static readonly Font FontTitle = new Font(FontName, 13F, FontStyle.Bold);
        public static readonly Font FontButton = new Font(FontName, 9.5F, FontStyle.Bold);

        /// <summary>
        /// 把「按 96 DPI 设计」的像素值换算成当前 DPI 的实际像素。
        ///
        /// 为什么不用 AutoScaleMode.Dpi 自动缩放：实测在 125% 缩放下它会把客户区
        /// 从 620 px 撑到 709 px，于是按绝对坐标摆放的底部按钮与列表底部就错位了。
        /// 这里改成 AutoScaleMode.None + 显式换算，布局与缩放都在自己手里，
        /// 任何 DPI 下都能算准。
        /// </summary>
        public static int Dpi(int designPixels)
        {
            return (int)Math.Round(designPixels * DpiScale);
        }

        private static readonly double DpiScale = DetectDpiScale();

        private static double DetectDpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    double scale = g.DpiX / 96.0;
                    if (scale < 0.5 || scale > 4.0) return 1.0;   // 异常值退回 100%
                    return scale;
                }
            }
            catch
            {
                return 1.0;
            }
        }
    }

    /// <summary>进程退出时统一释放共享字体（GDI 句柄不释放会一直占用到进程结束）。</summary>
    internal static class UiFonts
    {
        public static void DisposeAll()
        {
            UiTheme.FontUi.Dispose();
            UiTheme.FontBold.Dispose();
            UiTheme.FontSmall.Dispose();
            UiTheme.FontSection.Dispose();
            UiTheme.FontTitle.Dispose();
            UiTheme.FontButton.Dispose();
        }
    }
}
