using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ValeraScreenshot;

// Drive.exe - THE MISSING LAYER. Runs the REAL ValeraScreenshot.exe and drives it with REAL mouse and
// keyboard input (SendInput), then asserts on what the user would actually observe: a file on
// disk, pixels inside it, the clipboard, the registry, an empty crash.log.
//
// WHY IT EXISTS. The gate had 50 assertions and every one of them was either a pure function or a
// direct file/registry call. Nothing ever started the app. That is how a green 51/51 shipped
// alongside an autostart checkbox that erased itself on first launch: no test could see it,
// because seeing it requires the app to RUN.
//
// SANDBOXED BY CONSTRUCTION: the driven copy lives in a temp folder next to a portable.txt marker,
// so it can never self-install, never write the host's Run key and never touch the owner's config.
// Its screenshots land in the sandbox and are removed with it.
internal static class Drive
{
    // ---- Win32 input ----
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint flags);
    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    // ---- ЧЕКАТИ НА ПОДІЮ, А НЕ НА ГОДИННИК ----
    //
    // Тут стояли фіксовані паузи: Thread.Sleep(1400) «на появу оверлея» після гарячої клавіші.
    // Це працює рівно доти, доки машина не зайнята. Виміряно на цій же машині: чотири прогони
    // поспіль дали 23/0, 22/1, 21/2 і 15/5, і щоразу падали ІНШІ твердження — найчастіше
    // «no file appeared» на ПЕРШИХ сценаріях, тоді як пізніші проходили. Тобто застосунок був
    // справний, а міряч — ні. Гейт, що падає випадково, привчає ігнорувати гейт; це дефект
    // того самого класу, що й пруф, якого ніхто не міряє.
    //
    // Оверлей — повноекранне вікно процесу застосунку. Його поява СПОСТЕРЕЖУВАНА, тож на неї
    // й чекаємо: опитуємо вікна саме нашого PID, доки не побачимо вікно завбільшки з
    // віртуальний екран. Пауза лишається лише як стеля очікування, а не як здогадка.
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    // true, якщо в процесі застосунку є ВИДИМЕ вікно завбільшки з увесь віртуальний екран.
    private static bool OverlayIsUp()
    {
        if (_app == null || _app.HasExited) return false;
        uint want = (uint)_app.Id;
        Rectangle v = ScreenCap.VirtualScreen();
        bool found = false;
        EnumWindows(delegate (IntPtr h, IntPtr p)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != want || !IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.R - r.L, ht = r.B - r.T;
            // Допуск у кілька пікселів: рамки немає, але DWM іноді віддає межі на піксель ширші.
            if (Math.Abs(w - v.Width) <= 4 && Math.Abs(ht - v.Height) <= 4) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Чекати на умову до timeoutMs. Повертає false — і це ЧЕСНА поразка, яку видно у звіті,
    // а не мовчазне «мабуть уже».
    private static bool WaitFor(Func<bool> cond, int timeoutMs, int stepMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += stepMs)
        {
            try { if (cond()) return true; }
            catch { }
            Thread.Sleep(stepMs);
        }
        try { return cond(); }
        catch { return false; }
    }

    private static uint Gdi()
    {
        try { return _app == null || _app.HasExited ? 0 : GetGuiResources(_app.Handle, GR_GDIOBJECTS); }
        catch { return 0; }
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12;

    private static void Send(INPUT[] a) { SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))); }

    // wScan MUST be filled. Without it the events are delivered but RegisterHotKey never fires -
    // the first live run of this driver produced a running app and zero screenshots, and the
    // scan code was the whole difference. Diagnosed by A/B: identical chord with wScan worked.
    private static void Key(ushort vk, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.U.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKey(vk, 0),
            dwFlags = up ? KEYEVENTF_KEYUP : 0
        };
        Send(new[] { i });
        Thread.Sleep(30);
    }
    private static void Tap(ushort vk) { Key(vk, false); Key(vk, true); }
    private static void Chord(ushort mod, ushort vk) { Key(mod, false); Tap(vk); Key(mod, true); }
    private static void Chord2(ushort m1, ushort m2, ushort vk)
    { Key(m1, false); Key(m2, false); Tap(vk); Key(m2, true); Key(m1, true); }
    // Latin letters and digits map to fixed virtual-key codes (A..Z = 0x41..0x5A) on EVERY layout.
    // VkKeyScan does NOT: under a Ukrainian layout it returns -1 for 'p', and (-1 & 0xFF) = 0xFF
    // silently pressed a junk key. The tool never switched, so the drag moved the SELECTION instead
    // of drawing - which is exactly why the first honest run reported 0 pen pixels for every tool.
    // The app itself is fine here: it dispatches on KeyCode (the VK), so its Latin shortcuts work
    // under any layout - and this driver now proves that rather than assuming it.
    private static void TapChar(char c)
    {
        char u = char.ToUpperInvariant(c);
        if ((u >= 'A' && u <= 'Z') || (u >= '0' && u <= '9')) { Tap((ushort)u); return; }
        short s = VkKeyScan(c);
        if (s == -1) return;
        Tap((ushort)(s & 0xFF));
    }

    private static void MoveTo(int x, int y) { SetCursorPos(x, y); Thread.Sleep(25); }
    private static void MouseDown() { var i = new INPUT { type = INPUT_MOUSE }; i.U.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN }; Send(new[] { i }); Thread.Sleep(35); }
    private static void MouseUp() { var i = new INPUT { type = INPUT_MOUSE }; i.U.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP }; Send(new[] { i }); Thread.Sleep(35); }
    private static void Click(int x, int y) { MoveTo(x, y); MouseDown(); MouseUp(); }
    private static void Wheel(int notches)
    {
        for (int k = 0; k < Math.Abs(notches); k++)
        {
            var i = new INPUT { type = INPUT_MOUSE };
            i.U.mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches > 0 ? 120 : -120)), dwFlags = MOUSEEVENTF_WHEEL };
            Send(new[] { i });
            Thread.Sleep(60);
        }
    }
    private static void Drag(int x1, int y1, int x2, int y2)
    {
        MoveTo(x1, y1); MouseDown();
        for (int s = 1; s <= 8; s++) MoveTo(x1 + (x2 - x1) * s / 8, y1 + (y2 - y1) * s / 8);
        // Осісти на кінцевій точці ДО відпускання кнопки: без цієї паузи WM_MOUSEMOVE іноді
        // не встигав дійти, і виділення виходило на десяток пікселів іншим, ніж тяга.
        MoveTo(x2, y2);
        Thread.Sleep(140);
        MouseUp();
        Thread.Sleep(120);
    }

    // ---------------- deterministic backdrop ----------------
    // The scenarios used to capture whatever happened to be on the desktop. That made two
    // assertions meaningless: the sampled area was a SOLID colour, so "pixelate collapses detail"
    // had no detail to collapse (1 distinct colour -> 1), and a translucent marker over a dark
    // background never reached the pen threshold. A test whose verdict depends on which window
    // was open is not a test. So the driver paints its own target: a fine multi-colour
    // checkerboard on white, sized to the selection.
    private sealed class Backdrop : Form
    {
        private readonly Rectangle _r;
        public Backdrop(Rectangle r)
        {
            _r = r;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            BackColor = Color.White;
            DoubleBuffered = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            // 6 px cells, colour varies with both axes -> hundreds of distinct colours
            for (int y = 0; y < _r.Height; y += 6)
                for (int x = 0; x < _r.Width; x += 6)
                {
                    if (((x / 6) + (y / 6)) % 2 == 0) continue;
                    // Deliberately RED-FREE: the pen signature is red dominance, so a backdrop
                    // with reddish cells would forge it. The D07 control caught exactly that -
                    // the first palette scored 2880 "pen" pixels on an untouched capture.
                    int rr = 20 + (x * 2) % 50, gg = 90 + (y * 5) % 160, bb = 110 + ((x + y) * 7) % 140;
                    using (var b = new SolidBrush(Color.FromArgb(rr, gg, bb)))
                        g.FillRectangle(b, x, y, 6, 6);
                }
        }
    }

    private static Backdrop _backdrop;
    private static Thread _backdropThread;

    private static void ShowBackdrop(Rectangle r)
    {
        var ready = new ManualResetEvent(false);
        _backdropThread = new Thread(delegate ()
        {
            _backdrop = new Backdrop(r);
            _backdrop.Shown += delegate { ready.Set(); };
            Application.Run(_backdrop);
        });
        _backdropThread.SetApartmentState(ApartmentState.STA);
        _backdropThread.IsBackground = true;
        _backdropThread.Start();
        ready.WaitOne(5000);
        Thread.Sleep(400);
    }

    private static void BringBackdropFront()
    {
        try
        {
            if (_backdrop == null || !_backdrop.IsHandleCreated) return;
            _backdrop.Invoke((MethodInvoker)delegate
            {
                _backdrop.TopMost = true;
                _backdrop.BringToFront();
                _backdrop.Activate();
                _backdrop.Refresh();
                _backdrop.TopMost = false;   // знову звичайне вікно, щоб не сперечатись з оверлеєм
            });
            Thread.Sleep(250);
        }
        catch { }
    }

    private static void CloseBackdrop()
    {
        try
        {
            if (_backdrop != null && _backdrop.IsHandleCreated)
                _backdrop.Invoke((MethodInvoker)delegate { Application.ExitThread(); });
        }
        catch { }
    }

    // ---- harness ----
    private static int _pass, _fail;
    private static string _sandbox, _shots, _exe;
    private static Process _app;

    private static void Ok(string name, bool cond, string detail)
    {
        if (cond) { _pass++; Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
        else { _fail++; Console.WriteLine("FAIL  " + name + "  [" + detail + "]"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Native.EnsureDpiAware();
        _keep = Array.IndexOf(args, "-keep") >= 0;
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== DRIVE: real app, real input, observable results ===");

        string srcExe = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Application.ExecutablePath)), Ident.Exe);
        if (!File.Exists(srcExe)) { Console.WriteLine("FATAL: " + Ident.Exe + " not found at " + srcExe); return 2; }

        _sandbox = Path.Combine(Path.GetTempPath(), "ValeraScreenshotDrive");
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); } catch { }
        Directory.CreateDirectory(_sandbox);
        _exe = Path.Combine(_sandbox, Ident.Exe);
        File.Copy(srcExe, _exe, true);
        // portable marker FIRST: without it the copy would self-install into the host profile
        File.WriteAllText(Path.Combine(_sandbox, "portable.txt"), "");
        _shots = Path.Combine(_sandbox, "Shots");
        Directory.CreateDirectory(_shots);
        SeedConfig();

        try
        {
            if (!StartApp()) { Cleanup(); return 2; }
            ShowBackdrop(SelAbs());
            ScenarioRegionSave();
            ScenarioFullScreen();
            ScenarioTools();
            ScenarioRoute();
            ScenarioEscapeAndEmpty();
            ScenarioGdiLeak();
            ScenarioNoCrash();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL  driver crashed  [" + ex.Message + "]");
        }
        finally { Cleanup(); }

        Console.WriteLine();
        Console.WriteLine("DRIVE RESULT: " + _pass + " PASS, " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    // Config next to the exe: the sandbox folder is writable, so Config.Dir resolves there.
    private static void SeedConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# drive sandbox");
        sb.AppendLine("SaveDir=" + _shots);
        sb.AppendLine("Template=drv_{w}x{h}");
        sb.AppendLine("Format=png");
        sb.AppendLine("CopyAfterSave=True");
        sb.AppendLine("PlaySound=False");
        sb.AppendLine("ShowBalloon=False");
        sb.AppendLine("StartWithWindows=False");
        sb.AppendLine("UiTheme=light");
        File.WriteAllText(Path.Combine(_sandbox, "settings.ini"), sb.ToString(), new UTF8Encoding(false));
    }

    private static bool StartApp()
    {
        // Стартовий стан теж чекаємо ПО СЛІДУ, а не по секундоміру. Застосунок пише
        // _hotkeys.txt одразу після RegisterHotkeys — а саме готовність гарячих клавіш і є те,
        // без чого весь цей драйвер безсилий. Фіксовані 1500 мс були здогадкою: на зайнятій
        // машині вони інколи спливали ДО реєстрації, і перші сценарії падали як «no file».
        string hk = Path.Combine(_sandbox, "_hotkeys.txt");
        try { if (File.Exists(hk)) File.Delete(hk); } catch { }

        _app = Process.Start(new ProcessStartInfo(_exe) { UseShellExecute = false, WorkingDirectory = _sandbox });
        bool ready = WaitFor(delegate { return File.Exists(hk); }, 15000, 100);
        bool alive = _app != null && !_app.HasExited;
        Ok("D01 the app starts and stays running", alive, alive ? "pid " + _app.Id : "process exited");
        Ok("D01b the app reports its hotkey registration before we drive it", ready,
            ready ? File.ReadAllText(hk).Replace("\r\n", " ").Trim() : "_hotkeys.txt never appeared");
        return alive && ready;
    }

    private static void Cleanup()
    {
        CloseBackdrop();
        try { if (_app != null && !_app.HasExited) { _app.Kill(); _app.WaitForExit(4000); } } catch { }
        // ★ ТІЛЬКИ ТЕ, ЩО ЗАПУЩЕНЕ З НАШОЇ ПІСОЧНИЦІ. Пошук за самим ІМЕНЕМ процесу діставав і
        //   той екземпляр, який власник ВСТАНОВИВ і яким користується: застосунок у треї з
        //   глобальними гарячими клавішами мовчки зникав, і виглядало це як дефект продукту.
        //   Той самий недогляд був у test.ps1 і коштував власнику робочого дня — див. запис там.
        try
        {
            foreach (var p in Process.GetProcessesByName(Ident.AppId))
            {
                string path = null;
                try { path = p.MainModule.FileName; } catch { }   // чужий процес нам не належить
                if (path != null && path.StartsWith(_sandbox, StringComparison.OrdinalIgnoreCase))
                    try { p.Kill(); } catch { }
            }
        }
        catch { }
        Thread.Sleep(400);
        if (_keep) { Console.WriteLine("sandbox kept: " + _sandbox); return; }
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); } catch { }
    }
    private static bool _keep;

    private static string[] Shots() { try { return Directory.GetFiles(_shots, "*.png"); } catch { return new string[0]; } }

    private static string NewestShot(int before, int timeoutMs)
    {
        for (int w = 0; w < timeoutMs; w += 150)
        {
            var f = Shots();
            if (f.Length > before)
            {
                Array.Sort(f, delegate (string a, string b)
                { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                Thread.Sleep(250);   // let the writer finish
                return f[0];
            }
            Thread.Sleep(150);
        }
        return null;
    }

    // Opens the overlay, drags a known rectangle, runs `body`, then saves with Ctrl+S.
    private static string CaptureWith(Action body, Rectangle sel)
    {
        int before = Shots().Length;
        // Підняти фон НАД усім безпосередньо перед заморозкою кадру. Інакше після закриття
        // попереднього оверлея наперед виходило чуже вікно, і «чистий» кадр приносив сторонні
        // кольори — контроль D07 чесно падав на 467 «пенових» пікселях, яких ніхто не малював.
        BringBackdropFront();
        Chord2(VK_CONTROL, VK_SHIFT, (ushort)'4');
        // ЧЕКАЄМО НА ОВЕРЛЕЙ, а не на секундомір. Фіксована пауза тут була коренем
        // недетермінованості: коли машина зайнята, вікно не встигало з'явитись, тяга йшла по
        // робочому столу, Ctrl+S нікуди не потрапляв — і твердження падало як «no file appeared»,
        // хоча застосунок був справний.
        if (!WaitFor(OverlayIsUp, 8000, 100))
        {
            Ok("D00 the overlay appears after the hotkey", false, "no full-screen window from the app after 8 s");
            return null;
        }
        Thread.Sleep(200);                        // дати оверлею домалювати заморожений кадр
        Drag(sel.Left, sel.Top, sel.Right, sel.Bottom);
        Thread.Sleep(250);
        if (body != null) body();
        Chord(VK_CONTROL, (ushort)'S');
        return NewestShot(before, 6000);
    }

    private static Rectangle Sel()
    {
        var v = ScreenCap.VirtualScreen();
        int x = v.Left + 120, y = v.Top + 120;
        return new Rectangle(x, y, 360, 260);     // right/bottom used as absolute below
    }
    private static Rectangle SelAbs()
    {
        var r = Sel();
        return Rectangle.FromLTRB(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
    }

    // ---------------- scenarios ----------------

    private static void ScenarioRegionSave()
    {
        var s = SelAbs();
        string f = CaptureWith(null, s);
        Ok("D02 Ctrl+Shift+4 -> drag -> Ctrl+S writes a file", f != null, f ?? "no file appeared");
        if (f == null) return;
        using (var bmp = new Bitmap(f))
        {
            int wantW = s.Width, wantH = s.Height;
            bool near = Math.Abs(bmp.Width - wantW) <= 6 && Math.Abs(bmp.Height - wantH) <= 6;
            Ok("D03 the saved image matches the dragged rectangle", near,
                bmp.Width + "x" + bmp.Height + " vs " + wantW + "x" + wantH);
        }
        Ok("D04 the file name follows the configured template",
            Path.GetFileName(f).StartsWith("drv_"), Path.GetFileName(f));
    }

    private static void ScenarioFullScreen()
    {
        int before = Shots().Length;
        Chord2(VK_CONTROL, VK_SHIFT, (ushort)'3');
        string f = NewestShot(before, 6000);
        Ok("D05 Ctrl+Shift+3 saves the whole screen without an overlay", f != null, f ?? "no file");
        if (f == null) return;
        using (var bmp = new Bitmap(f))
        {
            var v = ScreenCap.VirtualScreen();
            Ok("D06 full capture is the whole virtual screen in physical pixels",
                bmp.Width == v.Width && bmp.Height == v.Height,
                bmp.Width + "x" + bmp.Height + " vs " + v.Width + "x" + v.Height);
        }
    }

    // Every drawing tool must leave a visible mark.
    //
    // The first version of this scenario compared the shot against a clean baseline and demanded
    // "more than 200 pixels changed". It passed for every tool - and it was WORTHLESS: nine tools
    // reported the identical 10479 changed pixels, because what actually changed between the two
    // captures was the DESKTOP behind the selection, not the annotation. A metric that reports the
    // same number for nine different tools is measuring noise.
    //
    // Now each tool is judged by ITS OWN signature: pen-coloured pixels for the colour tools, near
    // black for Redact, a collapse in distinct colours for Pixelate. The clean baseline is asserted
    // to carry NONE of the pen signature - a positive control, so the metric cannot silently rot
    // into "always true".
    private static void ScenarioTools()
    {
        var s = SelAbs();
        string baseline = CaptureWith(null, s);
        if (baseline == null) { Ok("D07 tool baseline", false, "no baseline shot"); return; }

        int basePen = CountPen(baseline);
        Ok("D07 control: a clean capture carries no pen-coloured pixels", basePen < 40,
            basePen + " pen px in the untouched shot");

        var tools = new[]
        {
            new[]{"p","Pencil"}, new[]{"m","Marker"}, new[]{"l","Line"}, new[]{"a","Arrow"},
            new[]{"r","Rect"}, new[]{"e","Ellipse"}, new[]{"f","Highlight"}, new[]{"n","Step"}
        };
        foreach (var t in tools)
        {
            char key = t[0][0];
            string name = t[1];
            string f = CaptureWith(delegate
            {
                TapChar(key);
                Thread.Sleep(150);
                Drag(s.Left + 60, s.Top + 60, s.Left + 260, s.Top + 190);
                Thread.Sleep(200);
            }, s);
            if (f == null) { Ok("D08." + name + " draws in the pen colour", false, "no file"); continue; }
            int pen = CountPen(f);
            Ok("D08." + name + " draws in the pen colour", pen > 150, pen + " pen px (baseline " + basePen + ")");
        }

        // Redact promises IRREVERSIBLE hiding: a solid black block, not a tint.
        string fb = CaptureWith(delegate
        {
            TapChar('b');
            Thread.Sleep(150);
            Drag(s.Left + 60, s.Top + 60, s.Left + 260, s.Top + 190);
            Thread.Sleep(200);
        }, s);
        if (fb != null)
        {
            int black = CountDark(fb, new Rectangle(70, 70, 180, 110));
            Ok("D08.Redact fills the area with solid black", black > 15000,
                black + " near-black px inside the dragged block");
        }
        else Ok("D08.Redact fills the area with solid black", false, "no file");

        // Pixelate must COLLAPSE detail: far fewer distinct colours than the untouched frame.
        string fi = CaptureWith(delegate
        {
            TapChar('i');
            Thread.Sleep(150);
            MoveTo(s.Left + 160, s.Top + 130);
            Wheel(6);                 // thicker tool = bigger mosaic tiles (documented behaviour)
            Thread.Sleep(150);
            Drag(s.Left + 60, s.Top + 60, s.Left + 260, s.Top + 190);
            Thread.Sleep(200);
        }, s);
        if (fi != null)
        {
            var area = new Rectangle(70, 70, 180, 110);
            int cBase = CountColors(baseline, area), cPix = CountColors(fi, area);
            Ok("D08.Pixelate collapses detail in the dragged area", cPix * 2 < cBase,
                "distinct colours " + cBase + " -> " + cPix);
        }
        else Ok("D08.Pixelate collapses detail in the dragged area", false, "no file");

        // Text: type, commit with Enter, THEN save. Enter is overloaded in the overlay - outside a
        // text box it means "copy and exit", which is why an unfocused click here silently closed
        // the overlay and produced no file at all in the first run.
        string ft = CaptureWith(delegate
        {
            TapChar('t');
            Thread.Sleep(200);
            Click(s.Left + 90, s.Top + 110);
            Thread.Sleep(700);                       // the inline TextBox must exist and be focused
            foreach (char c in "XYZ") { TapChar(c); Thread.Sleep(60); }
            Thread.Sleep(250);
            Tap((ushort)Keys.Enter);                 // commits the text box
            Thread.Sleep(450);
        }, s);
        if (ft != null) { int pen = CountPen(ft); Ok("D08.Text draws typed glyphs in the pen colour", pen > 40, pen + " pen px"); }
        else Ok("D08.Text draws typed glyphs in the pen colour", false, "no file (Enter closed the overlay?)");
    }

    private static void ScenarioRoute()
    {
        var s = SelAbs();
        string f = CaptureWith(delegate
        {
            TapChar('g');
            Thread.Sleep(200);
            Click(s.Left + 60, s.Top + 200);
            Thread.Sleep(120);
            Click(s.Left + 150, s.Top + 90);
            Thread.Sleep(120);
            Click(s.Left + 250, s.Top + 170);
            Thread.Sleep(120);
            Tap((ushort)Keys.Enter);   // Enter finishes the route (double-click also copies+exits)
            Thread.Sleep(400);
        }, s);
        if (f == null) { Ok("D09 route is drawn by clicks and finished with Enter", false, "no file"); return; }
        int pen = CountPen(f);
        Ok("D09 route is drawn by clicks and finished with Enter", pen > 200, pen + " pen px");
    }

    private static void ScenarioEscapeAndEmpty()
    {
        int before = Shots().Length;
        Chord2(VK_CONTROL, VK_SHIFT, (ushort)'4');
        Thread.Sleep(1300);
        Tap((ushort)Keys.Escape);
        Thread.Sleep(700);
        Ok("D10 Esc leaves the overlay without writing a file", Shots().Length == before,
            "files: " + before + " -> " + Shots().Length);

        // a second hotkey press while the overlay is open must not stack a second overlay
        Chord2(VK_CONTROL, VK_SHIFT, (ushort)'4');
        Thread.Sleep(1300);
        Chord2(VK_CONTROL, VK_SHIFT, (ushort)'4');
        Thread.Sleep(600);
        Tap((ushort)Keys.Escape);
        Thread.Sleep(700);
        bool alive = _app != null && !_app.HasExited;
        Ok("D11 a repeated hotkey while the overlay is open is ignored, app survives", alive,
            alive ? "still running" : "app died");
    }

    // Оверлей відкривається десятки разів за сесію, і кожна анотація «Мозаїка» тримала власний
    // Bitmap, який не звільняв ніхто. Тут це міряється НА ЖИВОМУ процесі: скільки GDI-обʼєктів
    // застосунок тримає після серії відкриттів із мозаїкою й текстом.
    private static void ScenarioGdiLeak()
    {
        var s = SelAbs();
        uint before = Gdi();
        for (int i = 0; i < 6; i++)
        {
            CaptureWith(delegate
            {
                TapChar('i');
                Thread.Sleep(120);
                Drag(s.Left + 60, s.Top + 60, s.Left + 200, s.Top + 150);
                Thread.Sleep(120);
            }, s);
        }
        Thread.Sleep(1200);
        uint after = Gdi();
        // Поріг щедрий навмисно: WinForms кешує шрифти й пера, тож нуль недосяжний. Але ВИТІК
        // мозаїки давав би стабільний приріст на кожне відкриття — саме це й ловиться.
        bool ok = before == 0 || after <= before + 60;
        Ok("D13 six overlay sessions with mosaic leak no GDI objects",
            ok, "GDI " + before + " -> " + after);
    }

    private static void ScenarioNoCrash()
    {
        string crash = Path.Combine(_sandbox, "crash.log");
        bool clean = !File.Exists(crash);
        string detail = clean ? "no crash.log" : File.ReadAllText(crash);
        if (detail.Length > 220) detail = detail.Substring(0, 220) + "...";
        Ok("D12 no unhandled exception during the whole run", clean, detail);
    }

    // ---------------- helpers ----------------

    // The default annotation colour is Windows red (0xE81123). Desktop content is very unlikely to
    // be that saturated, so this is a signature of OUR mark rather than of screen churn.
    private static int CountPen(string file)
    {
        try
        {
            using (var b = new Bitmap(file))
            {
                int n = 0;
                for (int j = 0; j < b.Height; j++)
                    for (int i = 0; i < b.Width; i++)
                    {
                        Color c = b.GetPixel(i, j);
                        // RED DOMINANCE, not "saturated red": the marker is deliberately
                        // translucent, so over a light backdrop it lands near (243,136,145) and an
                        // absolute threshold would call a working tool broken.
                        if (c.R - Math.Max(c.G, c.B) > 45) n++;
                    }
                return n;
            }
        }
        catch { return -1; }
    }

    private static int CountDark(string file, Rectangle r)
    {
        try
        {
            using (var b = new Bitmap(file))
            {
                int n = 0;
                for (int j = r.Top; j < Math.Min(r.Bottom, b.Height); j++)
                    for (int i = r.Left; i < Math.Min(r.Right, b.Width); i++)
                    {
                        Color c = b.GetPixel(i, j);
                        if (c.R < 40 && c.G < 40 && c.B < 40) n++;
                    }
                return n;
            }
        }
        catch { return -1; }
    }

    private static int CountColors(string file, Rectangle r)
    {
        try
        {
            using (var b = new Bitmap(file))
            {
                var seen = new Dictionary<int, bool>();
                for (int j = r.Top; j < Math.Min(r.Bottom, b.Height); j++)
                    for (int i = r.Left; i < Math.Min(r.Right, b.Width); i++)
                        seen[b.GetPixel(i, j).ToArgb()] = true;
                return seen.Count;
            }
        }
        catch { return -1; }
    }

    private static int DiffPixels(string a, string b)
    {
        try
        {
            using (var x = new Bitmap(a))
            using (var y = new Bitmap(b))
            {
                if (x.Width != y.Width || x.Height != y.Height) return int.MaxValue;
                int n = 0;
                for (int j = 0; j < x.Height; j += 2)
                    for (int i = 0; i < x.Width; i += 2)
                        if (x.GetPixel(i, j).ToArgb() != y.GetPixel(i, j).ToArgb()) n++;
                return n;
            }
        }
        catch { return -1; }
    }
}
