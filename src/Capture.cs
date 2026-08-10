using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ValeraScreenshot
{
    // Двигун захоплення: увесь віртуальний екран (усі монітори) у нативних фізичних пікселях.
    // Назва НЕ "Capture" — інакше всередині Form її перекриває Control.Capture (bool).
    internal static class ScreenCap
    {
        public static Rectangle VirtualScreen()
        {
            return new Rectangle(
                Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
                Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
                Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        }

        // BitBlt із CAPTUREBLT — 1:1 фізичні пікселі, включно з layered-вікнами.
        public static Bitmap Grab(bool includeCursor)
        {
            Rectangle v = VirtualScreen();
            if (v.Width <= 0 || v.Height <= 0) throw new InvalidOperationException(L.S("порожній віртуальний екран", "empty virtual screen"));
            IntPtr scr = Native.GetDC(IntPtr.Zero);
            if (scr == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
            IntPtr mem = IntPtr.Zero, hbm = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = Native.CreateCompatibleDC(scr);
                if (mem == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
                hbm = Native.CreateCompatibleBitmap(scr, v.Width, v.Height);
                // Перевірка була відсутня: на дуже великому віртуальному екрані GDI повертає
                // IntPtr.Zero, і далі SelectObject/BitBlt/Image.FromHbitmap падали з незрозумілим
                // ArgumentException замість чесного «не вистачило памʼяті на кадр».
                if (hbm == IntPtr.Zero)
                    throw new InvalidOperationException(
                        L.S("GDI не виділив кадр ", "GDI could not allocate a frame ") + v.Width + "x" + v.Height + " (CreateCompatibleBitmap)");
                old = Native.SelectObject(mem, hbm);
                // Результат BitBlt ІГНОРУВАВСЯ. При провалі (захищене вікно, DRM-плеєр, secure
                // desktop) виходив ЧОРНИЙ кадр, який тихо зберігався у файл, а трей рапортував
                // «Збережено 1920 × 1080». Порожній знімок, поданий як успішний, гірший за помилку.
                if (!Native.BitBlt(mem, 0, 0, v.Width, v.Height, scr, v.X, v.Y,
                                   Native.SRCCOPY | Native.CAPTUREBLT))
                    throw new InvalidOperationException(
                        L.S("Захоплення екрана не вдалося (BitBlt). Можливо, активне захищене вікно ", "Screen capture failed (BitBlt). A protected window may be in the foreground ") +
                        L.S("або повноекранна гра з ексклюзивним DirectX.", "or a full-screen game holding exclusive DirectX."));
                if (includeCursor) DrawCursor(mem, v);
                Native.SelectObject(mem, old);
                return Image.FromHbitmap(hbm);
            }
            finally
            {
                // Кидок після SelectObject (провал BitBlt) лишав hbm ОБРАНИМ у mem: DeleteObject
                // обраного бітмапа GDI мовчки відхиляє, DeleteDC знімає вибір — і кадр на весь
                // віртуальний екран не видаляв уже ніхто. Кожен невдалий захват тік повним
                // бітмапом до кінця процесу. Тому СПЕРШУ повертаємо старий бітмап, ПОТІМ
                // видаляємо (на успішному шляху old уже повернуто вище — повтор нешкідливий).
                if (old != IntPtr.Zero && mem != IntPtr.Zero) Native.SelectObject(mem, old);
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, scr);
            }
        }

        private static void DrawCursor(IntPtr hdc, Rectangle v)
        {
            try
            {
                var ci = new Native.CURSORINFO();
                ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.CURSORINFO));
                if (!Native.GetCursorInfo(ref ci) || ci.flags != Native.CURSOR_SHOWING) return;
                int hx = 0, hy = 0;
                Native.ICONINFO ii;
                if (Native.GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                }
                Native.DrawIconEx(hdc, ci.pt.X - v.X - hx, ci.pt.Y - v.Y - hy, ci.hCursor,
                                  0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
            }
            catch { }
        }

        // ---- кодування ----
        public static void SavePng(Bitmap b, string path)
        {
            b.Save(path, ImageFormat.Png);
        }

        public static void SaveJpeg(Bitmap b, string path, int quality)
        {
            ImageCodecInfo codec = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { codec = c; break; }
            if (codec == null) { b.Save(path, ImageFormat.Jpeg); return; }
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                b.Save(path, codec, ep);
            }
        }

        public static void Save(Bitmap b, string path, Config cfg)
        {
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                SaveJpeg(b, path, cfg.JpegQuality);
            else
                SavePng(b, path);
        }
    }
}
