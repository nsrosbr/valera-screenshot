using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Генерує app.ico (мультирозмірний, PNG-кадри): темно-синій квадрат,
// білі кути видошукача, помаранчева «кнопка затвора».
internal static class MakeIcon
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\ValeraScreenshot\app.ico";
        int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
        var frames = new List<byte[]>();
        foreach (int n in sizes)
        {
            using (var bmp = Render(n))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                frames.Add(ms.ToArray());
            }
        }

        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int n = sizes[i];
                w.Write((byte)(n >= 256 ? 0 : n));
                w.Write((byte)(n >= 256 ? 0 : n));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            for (int i = 0; i < frames.Count; i++) w.Write(frames[i]);
        }
        Console.WriteLine("ICON OK -> " + outPath);
    }

    static Bitmap Render(int n)
    {
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = n / 256f;

            // тло — заокруглений квадрат із градієнтом
            var rect = new RectangleF(8 * k, 8 * k, 240 * k, 240 * k);
            using (var path = Rounded(rect, 52 * k))
            using (var grad = new LinearGradientBrush(rect,
                Color.FromArgb(0x2B, 0x4C, 0x8C), Color.FromArgb(0x12, 0x1E, 0x3C), 90f))
                g.FillPath(grad, path);

            // кути видошукача
            using (var pen = new Pen(Color.White, Math.Max(1.5f, 20 * k)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float m = 56 * k, a = 52 * k;
                float L = rect.Left + m, T = rect.Top + m, R = rect.Right - m, B = rect.Bottom - m;
                g.DrawLines(pen, new[] { new PointF(L, T + a), new PointF(L, T), new PointF(L + a, T) });
                g.DrawLines(pen, new[] { new PointF(R - a, T), new PointF(R, T), new PointF(R, T + a) });
                g.DrawLines(pen, new[] { new PointF(R, B - a), new PointF(R, B), new PointF(R - a, B) });
                g.DrawLines(pen, new[] { new PointF(L + a, B), new PointF(L, B), new PointF(L, B - a) });
            }

            // «кнопка затвора»
            float d = 64 * k;
            using (var b = new SolidBrush(Color.FromArgb(0xFF, 0x5A, 0x3C)))
                g.FillEllipse(b, n / 2f - d / 2, n / 2f - d / 2, d, d);
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
