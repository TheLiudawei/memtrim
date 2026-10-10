// ============================================================================
//  MemTrim - IconGen.cs
//  独立的图标生成器：输出多尺寸 ICO。构建期一次性运行，不属于主程序。
//  用法: csc /target:exe /out:IconGen.exe IconGen.cs && IconGen.exe <输出.ico>
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGen
{
    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        string pngPath = Path.ChangeExtension(outPath, ".png");

        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> blobs = new List<byte[]>();

        foreach (int s in sizes)
        {
            using (Bitmap bmp = Draw(s))
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                blobs.Add(ms.ToArray());
            }
        }

        using (Bitmap big = Draw(256))
        {
            big.Save(pngPath, ImageFormat.Png);
        }

        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)blobs[i].Length);
                bw.Write((uint)offset);
                offset += blobs[i].Length;
            }
            foreach (byte[] b in blobs) bw.Write(b);
        }

        Console.WriteLine("icon written: " + Path.GetFullPath(outPath));
        Console.WriteLine("png  written: " + Path.GetFullPath(pngPath));
    }

    private static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int pad = Math.Max(1, size / 32);
            Rectangle bgRect = new Rectangle(pad, pad, size - pad * 2 - 1, size - pad * 2 - 1);
            int bgRadius = Math.Max(2, (int)(size * 0.22));

            using (GraphicsPath path = Rounded(bgRect, bgRadius))
            using (LinearGradientBrush lb = new LinearGradientBrush(bgRect,
                Color.FromArgb(255, 30, 52, 92), Color.FromArgb(255, 12, 22, 44),
                LinearGradientMode.Vertical))
            {
                g.FillPath(lb, path);
            }

            Color[] colors = new Color[]
            {
                Color.FromArgb(255, 90, 190, 255),
                Color.FromArgb(255, 120, 220, 180),
                Color.FromArgb(255, 255, 170, 70)
            };
            int[] heights = new int[] { 64, 104, 148 };
            int barW = Math.Max(2, (int)(size * 0.155));
            int gap = Math.Max(1, (int)(size * 0.085));
            int left = Math.Max(1, (int)(size * 0.185));
            int baseY = Math.Max(2, (int)(size * 0.80));

            for (int i = 0; i < 3; i++)
            {
                int h = (int)(heights[i] * (size / 256.0));
                if (h < 3) h = 3;
                int x = left + i * (barW + gap);
                Rectangle bar = new Rectangle(x, baseY - h, barW, h);
                int radius = Math.Max(1, (int)(size * 0.055));
                using (GraphicsPath bp = Rounded(bar, radius))
                using (SolidBrush b = new SolidBrush(colors[i]))
                {
                    g.FillPath(b, bp);
                }
            }
        }
        return bmp;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        if (radius <= 1 || r.Width < 3 || r.Height < 3)
        {
            p.AddRectangle(r);
            return p;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
