// ============================================================================
//  MemTrim · IconFactory.cs —— 运行期绘制应用图标
//
//  不依赖任何外部素材：托盘图标与标题栏 Logo 都在这里按 256×256 设计稿
//  等比缩放画出来，因此 exe 只需要内嵌一份 app.ico（清单/资源管理器用）。
//
//  资源约定：
//      · CreateIcon 返回独立副本，调用方负责 Dispose（GetHicon 得到的句柄已即时销毁）；
//      · 只用于 Image 属性时优先用 ToStream()：它把 PNG 存进内存流，
//        不产生「必须显式释放、否则泄漏到进程结束」的 GDI+ 位图句柄。
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MemTrim
{
    internal static class IconFactory
    {
        /// <summary>生成指定边长的应用图标（独立副本，调用方负责释放）。</summary>
        public static Icon CreateIcon(int size)
        {
            using (Bitmap bitmap = CreateBitmap(size))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        /// <summary>
        /// 按 256×256 设计稿等比缩放绘制应用图标。
        /// 返回的 Bitmap 由调用方释放；若只用于 Image 属性，
        /// 可考虑用 <see cref="ToStream"/> 彻底避开 GDI+ 句柄（见标题栏 Logo）。
        /// </summary>
        public static Bitmap CreateBitmap(int size)
        {
            Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                double scale = size / 256.0;

                RectangleF panel = new RectangleF(
                    (float)(8 * scale), (float)(8 * scale),
                    (float)(240 * scale), (float)(240 * scale));
                using (GraphicsPath path = UiKit.RoundedRect(
                    new Rectangle((int)panel.X, (int)panel.Y, (int)panel.Width, (int)panel.Height),
                    (int)(56 * scale)))
                using (LinearGradientBrush gradient = new LinearGradientBrush(panel,
                    Color.FromArgb(28, 48, 86), Color.FromArgb(12, 22, 44), LinearGradientMode.Vertical))
                {
                    g.FillPath(gradient, path);
                }

                Color[] colors = new Color[]
                {
                    Color.FromArgb(90, 190, 255),
                    Color.FromArgb(120, 220, 180),
                    Color.FromArgb(255, 170, 70)
                };
                int[] heights = new int[] { 64, 104, 148 };

                float barWidth = (float)(40 * scale);
                float gap = (float)(22 * scale);
                float left = (float)(46 * scale);
                float baseline = (float)(206 * scale);

                for (int i = 0; i < 3; i++)
                {
                    float height = (float)(heights[i] * scale);
                    RectangleF bar = new RectangleF(
                        left + i * (barWidth + gap), baseline - height, barWidth, height);
                    using (GraphicsPath path = UiKit.RoundedRect(
                        new Rectangle((int)bar.X, (int)bar.Y,
                            Math.Max(1, (int)bar.Width), Math.Max(1, (int)bar.Height)),
                        (int)(14 * scale)))
                    using (SolidBrush brush = new SolidBrush(colors[i]))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }
            return bitmap;
        }

        /// <summary>把图标画到流里，供 Image.FromStream 使用（不产生需要释放的 GDI 位图）。</summary>
        public static System.IO.MemoryStream ToStream(int size)
        {
            System.IO.MemoryStream stream = new System.IO.MemoryStream();
            using (Bitmap bitmap = CreateBitmap(size))
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            stream.Position = 0;
            return stream;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
