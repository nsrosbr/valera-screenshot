using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using ValeraScreenshot;

// Тести ядра ValeraScreenshot (без UI): захоплення, кодування, конфіг, анотації, гарячі клавіші.
internal static class TestMain
{
    private static int _pass, _fail;

    static void Check(string name, bool ok, string info)
    {
        if (ok) { _pass++; Console.WriteLine("PASS  " + name + (info != null ? "  [" + info + "]" : "")); }
        else { _fail++; Console.WriteLine("FAIL  " + name + (info != null ? "  [" + info + "]" : "")); }
    }

    static int CountDiff(Bitmap a, Bitmap b)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y += 2)
            for (int x = 0; x < a.Width; x += 2)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) n++;
        return n;
    }

    static Bitmap White(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(b)) g.Clear(Color.White);
        return b;
    }

    static int Main()
    {
        Native.EnsureDpiAware();
        string tmp = Path.Combine(Path.GetTempPath(), "valerascreenshot_tests");
        Directory.CreateDirectory(tmp);

        // T01: метрики віртуального екрана
        var v = ScreenCap.VirtualScreen();
        Check("T01 virtual-screen metrics", v.Width > 0 && v.Height > 0, v.Width + "x" + v.Height);

        // T02: захоплення 1:1 із фізичними метриками екрана (нативна роздільність)
        Bitmap shot = null;
        try { shot = ScreenCap.Grab(false); } catch (Exception ex) { Check("T02 grab", false, ex.Message); }
        if (shot != null)
        {
            Check("T02 grab == native resolution",
                shot.Width == v.Width && shot.Height == v.Height,
                shot.Width + "x" + shot.Height + " vs " + v.Width + "x" + v.Height);

            // T03: PNG roundtrip без втрат розміру
            string png = Path.Combine(tmp, "t03.png");
            ScreenCap.SavePng(shot, png);
            using (var back = new Bitmap(png))
                Check("T03 png roundtrip dims",
                    back.Width == shot.Width && back.Height == shot.Height,
                    new FileInfo(png).Length + " bytes");

            // T04: JPEG із параметром якості
            string jpg = Path.Combine(tmp, "t04.jpg");
            ScreenCap.SaveJpeg(shot, jpg, 92);
            Check("T04 jpeg quality save", new FileInfo(jpg).Length > 1000, new FileInfo(jpg).Length + " bytes");
            shot.Dispose();
        }

        // T05: конфіг — roundtrip з кирилицею
        var c = new Config();
        c.Template = "Знімок_{date}_{time}_{w}x{h}";
        c.Format = "jpg"; c.JpegQuality = 77; c.RegionMods = Native.MOD_CONTROL;
        c.IncludeCursor = true; c.PlaySound = true; c.LastWidth = 7;
        c.Save();
        var c2 = Config.Load();
        Check("T05 config roundtrip",
            c2.Template == c.Template && c2.Format == "jpg" && c2.JpegQuality == 77 &&
            c2.RegionMods == Native.MOD_CONTROL && c2.IncludeCursor && c2.PlaySound && c2.LastWidth == 7, null);
        try { File.Delete(Config.IniPath); } catch { }

        // T06/T07: шаблон імені — токени підставлені, колізії отримують суфікс
        var c3 = new Config();
        c3.SaveDir = Path.Combine(tmp, "shots");
        string p1 = c3.MakeFilePath(800, 600);
        Check("T07 template tokens", !p1.Contains("{") && !p1.Contains("}"), Path.GetFileName(p1));
        File.WriteAllText(p1, "x");
        string p2 = c3.MakeFilePath(800, 600);
        Check("T06 unique filename", p1 != p2, Path.GetFileName(p2));

        // T08: рамка малюється
        using (var a = White(200, 100))
        using (var b = White(200, 100))
        {
            using (var g = Graphics.FromImage(b))
                AnnRender.DrawAll(g, new Ann[] { new RectAnn { Color = Color.Red, Width = 3, A = new Point(10, 10), B = new Point(180, 80) } });
            Check("T08 rect annotation", CountDiff(a, b) > 20, CountDiff(a, b) + " px");
        }

        // T09: заливка — регіон повністю чорний
        using (var b = White(100, 60))
        {
            using (var g = Graphics.FromImage(b))
                AnnRender.DrawAll(g, new Ann[] { new RedactAnn { A = new Point(20, 10), B = new Point(70, 40) } });
            bool allBlack = true;
            for (int y = 12; y <= 38 && allBlack; y++)
                for (int x = 22; x <= 68 && allBlack; x++)
                    if (b.GetPixel(x, y).ToArgb() != Color.FromArgb(255, 0, 0, 0).ToArgb()) allBlack = false;
            Check("T09 redact fully black", allBlack, null);
        }

        // T10: стрілка/олівець/маркер/текст рендеряться
        using (var a = White(300, 150))
        using (var b = White(300, 150))
        {
            var pen = new PenAnn { Color = Color.Blue, Width = 3 };
            pen.Points.AddRange(new[] { new Point(10, 120), new Point(60, 40), new Point(120, 100) });
            var mk = new MarkerAnn { Color = Color.Yellow, Width = 3 };
            mk.Points.AddRange(new[] { new Point(10, 20), new Point(200, 20) });
            using (var g = Graphics.FromImage(b))
                AnnRender.DrawAll(g, new Ann[]
                {
                    new ArrowAnn { Color = Color.Red, Width = 3, A = new Point(20, 130), B = new Point(250, 30) },
                    pen, mk,
                    new TextAnn { Color = Color.Black, Width = 3, Pos = new Point(150, 100), Text = "Тест" },
                    new EllipseAnn { Color = Color.Green, Width = 2, A = new Point(200, 60), B = new Point(280, 130) },
                    new LineAnn { Color = Color.Purple, Width = 2, A = new Point(5, 5), B = new Point(290, 145) },
                });
            Check("T10 arrow/pen/marker/text/ellipse/line", CountDiff(a, b) > 100, CountDiff(a, b) + " px");
        }

        // T13: мозаїка — після Bake регіон відрізняється від оригіналу, але не чорний
        using (var src = new Bitmap(200, 100))
        {
            using (var g = Graphics.FromImage(src))
            {
                for (int i = 0; i < 20; i++)
                    using (var b2 = new SolidBrush(Color.FromArgb(255, i * 12, 255 - i * 12, 40)))
                        g.FillRectangle(b2, i * 10, 0, 10, 100);
            }
            var px = new PixelateAnn { Width = 3, A = new Point(20, 10), B = new Point(180, 90) };
            px.Bake(src);
            bool baked = px.Tile != null && px.Tile.Width >= 1;
            using (var outB = new Bitmap(src))
            {
                using (var g = Graphics.FromImage(outB)) AnnRender.DrawAll(g, new Ann[] { px });
                Check("T13 pixelate bakes and alters", baked && CountDiff(src, outB) > 50,
                    baked ? CountDiff(src, outB) + " px" : "tile missing");
            }
        }

        // T14: нумерований крок рендериться і містить біле кільце
        using (var a = White(120, 120))
        using (var b = White(120, 120))
        {
            using (var g = Graphics.FromImage(b))
                AnnRender.DrawAll(g, new Ann[] { new StepAnn { Color = Color.Red, Width = 3, Pos = new Point(60, 60), Number = 7 } });
            Check("T14 step badge renders", CountDiff(a, b) > 40, CountDiff(a, b) + " px");
        }

        // T15: напівпрозора заливка не перекриває вміст повністю
        using (var b = White(100, 60))
        {
            using (var g = Graphics.FromImage(b))
                AnnRender.DrawAll(g, new Ann[] { new FillRectAnn { Color = Color.Red, Width = 2, A = new Point(10, 10), B = new Point(90, 50) } });
            var mid = b.GetPixel(50, 30);
            Check("T15 fillrect semi-transparent", mid.R > 200 && mid.G > 120 && mid.G < 240,
                "#" + mid.R.ToString("X2") + mid.G.ToString("X2") + mid.B.ToString("X2"));
        }

        // T16: маршрут — полілінія з ≥2 точок малюється; довжина рахується
        using (var bg = White(300, 200))
        {
            var route = new RouteAnn { Color = Color.FromArgb(0xE8, 0x11, 0x23), Width = 3 };
            route.Points.Add(new Point(20, 180));
            route.Points.Add(new Point(90, 60));
            route.Points.Add(new Point(180, 120));
            route.Points.Add(new Point(270, 30));
            using (var chk = White(300, 200))
            {
                using (var g = Graphics.FromImage(chk)) AnnRender.DrawAll(g, new Ann[] { route });
                bool drew = CountDiff(bg, chk) > 150;
                bool len = route.PixelLength() > 300 && !route.IsDegenerate();
                Check("T16 route polyline draws + length", drew && len,
                    ((int)route.PixelLength()) + " px over " + route.Points.Count + " pts");
            }
        }

        // T17: маршрут з 1 точки — вироджений (не комітиться)
        {
            var r1 = new RouteAnn { Color = Color.Red, Width = 3 };
            r1.Points.Add(new Point(10, 10));
            Check("T17 single-point route is degenerate", r1.IsDegenerate(), null);
        }

        // T18: конфіг зберігає/читає запасні клавіші (ПК+ноут)
        var hc = new Config();
        hc.Region2Vk = Native.VK_SNAPSHOT; hc.Region2Mods = 0;
        hc.Full2Vk = Native.VK_SNAPSHOT; hc.Full2Mods = Native.MOD_SHIFT;
        hc.Save();
        var hc2 = Config.Load();
        Check("T18 secondary hotkeys roundtrip",
            hc2.Region2Vk == Native.VK_SNAPSHOT && hc2.Region2Mods == 0 &&
            hc2.Full2Vk == Native.VK_SNAPSHOT && hc2.Full2Mods == Native.MOD_SHIFT, null);
        try { File.Delete(Config.IniPath); } catch { }

        // T11: реєстрація глобальної гарячої клавіші (Ctrl+Alt+F11, потокова)
        bool reg = Native.RegisterHotKey(IntPtr.Zero, 41, Native.MOD_CONTROL | Native.MOD_ALT, 0x7A);
        if (reg) Native.UnregisterHotKey(IntPtr.Zero, 41);
        Check("T11 hotkey register/unregister", reg, "Ctrl+Alt+F11");

        // T12 (інформативний): чи вільний голий PrtScr
        bool prt = Native.RegisterHotKey(IntPtr.Zero, 42, 0, Native.VK_SNAPSHOT);
        if (prt) Native.UnregisterHotKey(IntPtr.Zero, 42);
        Console.WriteLine("INFO  T12 bare PrtScr is " + (prt ? "FREE (hotkey will bind)" : "BUSY (fallback Ctrl+PrtScr)"));

        try { Directory.Delete(tmp, true); } catch { }

        PrivacyMatrix();
        UpdaterMatrix();
        LifecycleMatrix();
        AccessibilityMatrix();
        OverlayMatrix();
        CaptureMatrix();
        LocalisationMatrix();
        HonestyMatrix();
        Corpus();

        Console.WriteLine();
        Console.WriteLine("RESULT: " + _pass + " PASS, " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    // LIFECYCLE MATRIX (STD-LIFE-03/04). Дві обіцянки, обидві — реальні польові регреси в лінійці:
    // портативна копія НЕ сміє чіпати реєстр господаря машини, а оновлення НЕ сміє переписувати
    // чужу команду видалення (інакше кнопка «Видалити» тихо перестає працювати).
    static void LifecycleMatrix()
    {
        _mp = 0; _mt = 0;

        // Маркер шукається ПОРУЧ ІЗ ЦИМ exe, тож для тесту кладемо його біля Test.exe.
        string myDir = AppDomain.CurrentDomain.BaseDirectory;
        string myMarker = Path.Combine(myDir, "portable.txt");
        bool hadMarker = File.Exists(myMarker);

        // L1: маркер відсутній -> НЕ портатив
        try { if (File.Exists(myMarker)) File.Delete(myMarker); } catch { }
        M("L1 without the marker the copy is NOT portable", !Installer.IsPortable(), myDir);

        // L2: маркер з'явився -> портатив (обидва стани, не лише зручний)
        try { File.WriteAllText(myMarker, ""); } catch { }
        M("L2 portable marker is detected", Installer.IsPortable(), "portable.txt next to the exe");

        // L3: у портативі автозапуск НЕ пишеться. Реєстр знімаємо ДО і відновлюємо ПІСЛЯ точно —
        // якщо гард раптом зламаний, тест це побачить і НЕ лишить сліду на машині господаря.
        object runBefore = null;
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Ident.RunKey))
                runBefore = run == null ? null : run.GetValue(Ident.RunValue);
        }
        catch { }

        Installer.SetAutostart(true);   // портатив -> мусить бути no-op

        object runAfter = null;
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Ident.RunKey))
                runAfter = run == null ? null : run.GetValue(Ident.RunValue);
        }
        catch { }
        M("L3 portable copy writes NO autostart entry",
            (runBefore == null && runAfter == null) || (runBefore != null && runBefore.Equals(runAfter)),
            "registry value unchanged");

        // відновити реєстр рівно як було (гарантія, навіть якщо гард зламаний)
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Ident.RunKey, true))
            {
                if (run != null)
                {
                    if (runBefore == null) { if (run.GetValue(Ident.RunValue) != null) run.DeleteValue(Ident.RunValue, false); }
                    else run.SetValue(Ident.RunValue, runBefore);
                }
            }
        }
        catch { }

        M("L3b install dir is the per-user Programs folder",
            Installer.InstallDir.IndexOf("Programs", StringComparison.OrdinalIgnoreCase) >= 0, Installer.InstallDir);

        const string sandbox = @"Software\ValeraScreenshotTest\RunProbe";
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); } catch { }

        // L4-L6: Arp.Refresh оновлює версію, але НЕ чіпає команду видалення (STD-LIFE-04).
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sandbox))
            {
                Arp.Write(k, "C:\\fake\\ValeraScreenshot.exe", "C:\\fake", "\"C:\\fake\\ThirdParty.exe\" /x", "\"C:\\fake\\ThirdParty.exe\" /x /s");
                string beforeCmd = k.GetValue("UninstallString") as string;
                k.SetValue("DisplayVersion", "0.0.1");           // вдаємо стару картку
                Arp.Refresh(k, "C:\\fake\\ValeraScreenshot.exe", "C:\\fake");
                string afterCmd = k.GetValue("UninstallString") as string;
                string afterVer = k.GetValue("DisplayVersion") as string;

                M("L4 Arp.Refresh keeps the registered uninstall command", beforeCmd == afterCmd, afterCmd);
                M("L5 Arp.Refresh updates the version to the running build", afterVer == Ver.Number, "0.0.1 -> " + afterVer);
                M("L6 Arp card carries publisher from the single identity source",
                    (k.GetValue("Publisher") as string) == Ident.Publisher, Ident.Publisher);

                // L6b: дата ВСТАНОВЛЕННЯ переживає оновлення. SelfHeal кличе Refresh після кожної
                // зміни версії, а Write ставить InstallDate = сьогодні — тож без збереження
                // Windows показувала б «Встановлено: сьогодні» після кожного апдейту, і справжня
                // дата зникала б назавжди. Ставимо явно давню дату й вимагаємо, щоб вона вціліла.
                k.SetValue("InstallDate", "20200101");
                k.SetValue("DisplayVersion", "0.0.1");
                Arp.Refresh(k, "C:\\fake\\ValeraScreenshot.exe", "C:\\fake");
                M("L6b an update keeps the ORIGINAL install date, not today's",
                    (k.GetValue("InstallDate") as string) == "20200101",
                    "20200101 -> " + (k.GetValue("InstallDate") as string));
            }
        }
        catch (Exception ex) { M("L4-L6 Arp probe", false, ex.Message); }
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ValeraScreenshotTest", false); } catch { }

        // L7-L8: засів наміру автозапуску в конфіг (польовий регрес 2026-07-29: чекбокс інсталятора
        // писав ключ Run без конфіга, і перший же запуск читав дефолтне False та видаляв ключ —
        // «я точно ставив чекбокс»). Сіється рівно один рядок; чуже в ini не переписується.
        string seedIni = Path.Combine(myDir, "_seed_probe.ini");
        try { if (File.Exists(seedIni)) File.Delete(seedIni); } catch { }
        Installer.SeedAutostartIni(seedIni);
        string seeded1 = File.Exists(seedIni) ? File.ReadAllText(seedIni) : "";
        M("L7 seeding an absent config creates StartWithWindows=True",
            seeded1.Contains("StartWithWindows=True"), seedIni);

        File.WriteAllLines(seedIni, new[] { "SaveDir=D:\\Знімки", "StartWithWindows=False", "UiTheme=dark" });
        Installer.SeedAutostartIni(seedIni);
        string[] seeded2 = File.ReadAllLines(seedIni);
        bool flipped = false, othersKept = false;
        foreach (var ln in seeded2) if (ln == "StartWithWindows=True") flipped = true;
        othersKept = Array.IndexOf(seeded2, "SaveDir=D:\\Знімки") >= 0 && Array.IndexOf(seeded2, "UiTheme=dark") >= 0;
        M("L8 seeding flips only the autostart line and keeps the rest",
            flipped && othersKept && seeded2.Length == 3, string.Join(" | ", seeded2));
        try { File.Delete(seedIni); } catch { }

        // L9-L12: команда самовидалення. НАЙНЕБЕЗПЕЧНІШИЙ код продукту: тут стояло
        // `rmdir /s /q "<тека exe>"` під гардом, який вимагав, щоб тека знімків ІСНУВАЛА — тож на
        // копії, якою ще не знімали, гард мовчав і команда рекурсивно зносила теку, де просто лежав
        // exe. Портатив у «Документах» -> /uninstall стирав «Документи». Перевіряємо РЯДОК команди,
        // а не наслідок: інакше єдиний спосіб протестувати — дати їй щось справді видалити.
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string portableExe = Path.Combine(docs, Ident.Exe);
        string cmd = Installer.BuildSelfDeleteCommand(portableExe, Installer.InstallDir);

        M("L9 self-delete never removes a folder recursively",
            cmd.IndexOf("rmdir /s", StringComparison.OrdinalIgnoreCase) < 0 &&
            cmd.IndexOf("rd /s", StringComparison.OrdinalIgnoreCase) < 0, "no /s in any rmdir");

        M("L10 the folder the exe was run from is never wiped",
            cmd.IndexOf("/q \"" + docs + "\"", StringComparison.OrdinalIgnoreCase) < 0 &&
            cmd.IndexOf("rmdir /s /q \"" + docs, StringComparison.OrdinalIgnoreCase) < 0, docs);

        // кожен `del` мусить бити у файл із нашого списку, а не в теку чи чужий файл
        bool onlyOwned = true; string offender = "";
        foreach (string seg in cmd.Split(new[] { " & " }, StringSplitOptions.None))
        {
            string t = seg.Trim();
            if (!t.StartsWith("del ", StringComparison.OrdinalIgnoreCase)) continue;
            int q = t.IndexOf('"');
            string target = t.Substring(q + 1).TrimEnd('"');
            string leaf = Path.GetFileName(target);
            if (Array.IndexOf(Installer.OwnedFiles, leaf) < 0 && leaf != Ident.Exe)
            { onlyOwned = false; offender = target; break; }
        }
        M("L11 every delete targets a file the installer itself placed", onlyOwned,
            onlyOwned ? Installer.OwnedFiles.Length + " owned names" : "offender: " + offender);

        M("L12 install dir is still cleaned up (non-recursively)",
            cmd.Contains("rmdir \"" + Installer.InstallDir + "\""), Installer.InstallDir);

        // L13: гард портативу на гілці /install. Перевірка ЖИВА — запускається справжній exe у
        // пісочниці з portable.txt; доти /install був єдиним життєвим шляхом без цього гарду і
        // ставив застосунок у систему попри маркер.
        string guardDir = Path.Combine(Path.GetTempPath(), "ValeraScreenshotGuardProbe");
        try
        {
            if (Directory.Exists(guardDir)) Directory.Delete(guardDir, true);
            Directory.CreateDirectory(guardDir);
            string srcExe = Path.Combine(Path.GetDirectoryName(myDir.TrimEnd('\\')), Ident.Exe);
            if (File.Exists(srcExe))
            {
                string probeExe = Path.Combine(guardDir, Ident.Exe);
                File.Copy(srcExe, probeExe, true);
                File.WriteAllText(Path.Combine(guardDir, "portable.txt"), "");
                var psi = new System.Diagnostics.ProcessStartInfo(probeExe, "/install")
                { UseShellExecute = false, CreateNoWindow = true };
                var proc = System.Diagnostics.Process.Start(psi);
                bool done = proc.WaitForExit(15000);
                int code = done ? proc.ExitCode : -1;
                if (!done) { try { proc.Kill(); } catch { } }
                M("L13 /install refuses to run while portable.txt is present", code == 2,
                    "exit code " + code + " (2 = refused)");
            }
            else M("L13 /install portable guard", false, "ValeraScreenshot.exe not found at " + srcExe);
        }
        catch (Exception ex) { M("L13 /install portable guard", false, ex.Message); }
        try { if (Directory.Exists(guardDir)) Directory.Delete(guardDir, true); } catch { }

        // L14-L17: КОРІНЬ польового бага «чекбокс автозапуску зникає». Намір живе в конфігу, а
        // ключ Run — лише його проєкція, яку застосунок вирівнює на кожному старті. Тому засівач
        // мусить писати за ТІЄЮ САМОЮ адресою, за якою читатиме ВСТАНОВЛЕНА копія. Доти адреси
        // рахувались у трьох місцях по-різному, і збігалися рівно в 2 випадках із 7.
        string cfgProbe = Path.Combine(Path.GetTempPath(), "ValeraScreenshotCfgProbe");
        try { if (Directory.Exists(cfgProbe)) Directory.Delete(cfgProbe, true); } catch { }
        Directory.CreateDirectory(cfgProbe);

        M("L14 a writable install folder keeps its config next to the exe",
            Config.DirFor(cfgProbe) == cfgProbe.TrimEnd('\\'), Config.DirFor(cfgProbe));

        string unwritable = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string fake = Path.Combine(unwritable, "ValeraScreenshotNotThere");
        M("L15 a non-writable install folder falls back to %APPDATA%",
            Config.DirFor(fake) == Ident.AppDataDir, Config.DirFor(fake));

        // L15b-L15d — ПОПРАВКА 2026-08-08: адреса конфіга НЕ ЗАЛЕЖИТЬ від прав покликача.
        // Елевований інсталятор проходив пробу запису в Program Files і сіяв намір туди, куди
        // деелевований застосунок не дивиться ніколи; перший старт стирав свіжий ключ Run.
        // Адмінські корені тепер відповідають %APPDATA% для БУДЬ-КОГО, і це доводиться без елевації
        // (L15 покривав лише НЕІСНУЮЧУ теку — вона проходила й до виправлення).
        M("L15b Program Files is admin-only for every caller",
            Seed.IsAdminOnlyDir(Path.Combine(unwritable, "ValeraScreenshot")) && !Seed.IsAdminOnlyDir(cfgProbe),
            "IsAdminOnlyDir(PF)=true, IsAdminOnlyDir(temp)=false");
        string pfExisting = null;
        try { string[] pfDirs = Directory.GetDirectories(unwritable); if (pfDirs.Length > 0) pfExisting = pfDirs[0]; } catch { }
        M("L15c an EXISTING dir under Program Files answers %APPDATA%",
            pfExisting == null || Config.DirFor(pfExisting) == Ident.AppDataDir,
            pfExisting == null ? "(немає тек для проби)" : Config.DirFor(pfExisting));
        M("L15d no prefix false-positive next to the Program Files root",
            !Seed.IsAdminOnlyDir(unwritable + " Extra"), unwritable + " Extra");

        // Той самий засів у теці з КИРИЛИЧНИМ вмістом: читання «як вийде» замість UTF-8 уже
        // одного разу подвоїло кодування і зіпсувало шлях до теки знімків користувача.
        string ini2 = Path.Combine(cfgProbe, "settings.ini");
        File.WriteAllText(ini2, "SaveDir=E:\\Скріншоти\r\nTemplate=Знімок_{date}\r\nStartWithWindows=False\r\n",
            new System.Text.UTF8Encoding(false));
        Installer.SeedAutostartIni(ini2);
        string[] back = File.ReadAllLines(ini2, System.Text.Encoding.UTF8);
        bool cyrKept = Array.IndexOf(back, "SaveDir=E:\\Скріншоти") >= 0 &&
                       Array.IndexOf(back, "Template=Знімок_{date}") >= 0;
        bool flipped2 = Array.IndexOf(back, "StartWithWindows=True") >= 0;
        M("L16 seeding preserves Cyrillic values byte-for-byte", cyrKept, string.Join(" | ", back));
        M("L17 seeding still flips the autostart intent", flipped2, "StartWithWindows=True");
        try { Directory.Delete(cfgProbe, true); } catch { }

        // L18-L19: прапорець тихого видалення. Він був двома різними літералами в двох одиницях
        // компіляції — картка ARP реєструвала «/silent», а деінсталятор розбирав лише «/S», тож
        // кожне автоматизоване видалення впиралося в модальне вікно. Тепер константа одна; ці
        // два твердження стережуть, щоб літерал не повернувся назад у котресь із місць.
        const string arpProbe = @"Software\ValeraScreenshotTest\SilentProbe";
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(arpProbe, false); } catch { }
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(arpProbe))
            {
                // Refresh на порожньому ключі — саме той шлях, що СИНТЕЗУЄ команди за замовчуванням.
                Arp.Refresh(k, @"C:\fake\ValeraScreenshot.exe", @"C:\fake");
                string quiet = k.GetValue("QuietUninstallString") as string;
                string plain = k.GetValue("UninstallString") as string;
                M("L18 the ARP quiet command ends with the one silent-switch constant",
                    quiet != null && quiet.EndsWith(Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase), quiet);
                M("L19 the plain uninstall command carries no silent switch",
                    plain != null && plain.IndexOf(Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase) < 0, plain);
            }
        }
        catch (Exception ex) { M("L18-L19 silent switch contract", false, ex.Message); }
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ValeraScreenshotTest", false); } catch { }

        // L20-L21: ресурси анотацій. «Мозаїка» володіє власним Bitmap, і його не звільняв ніхто:
        // оверлей диспозив тільки кадр, а списки анотацій губилися разом із формою. Інструктор,
        // що робить 50 знімків із мозаїкою, накопичував сотні незвільнених GDI-обʼєктів за сесію.
        using (var canvas = new System.Drawing.Bitmap(80, 60))
        {
            var pix = new PixelateAnn { A = new System.Drawing.Point(4, 4), B = new System.Drawing.Point(70, 50), Width = 3 };
            pix.Bake(canvas);
            bool baked = pix.Tile != null;
            pix.Dispose();
            M("L20 a pixelate annotation owns a tile and releases it on Dispose",
                baked && pix.Tile == null, baked ? "tile baked then freed" : "Bake produced no tile");
        }

        // L20b: ПОВТОРНЕ печення не лишає попередній тайл. Bake публічний і перезаписує поле;
        // сьогодні його кличуть раз на завершену тягу, але метод не має права покладатися на те,
        // що його викличуть саме так. Перевіряємо не «нема витоку» (цього прямо не спитати), а
        // те, що ПЕРШИЙ Bitmap справді звільнено: звернення до звільненого кидає.
        using (var canvas = new System.Drawing.Bitmap(80, 60))
        {
            var pix2 = new PixelateAnn { A = new System.Drawing.Point(4, 4), B = new System.Drawing.Point(70, 50), Width = 3 };
            pix2.Bake(canvas);
            var firstTile = pix2.Tile;
            pix2.Bake(canvas);
            bool replaced = firstTile != null && !ReferenceEquals(firstTile, pix2.Tile);
            bool firstFreed = false;
            try { int w = firstTile.Width; firstFreed = false; }
            catch (Exception) { firstFreed = true; }
            M("L20b baking twice frees the tile it replaces", replaced && firstFreed,
                !replaced ? "the second Bake reused the same object" : (firstFreed ? "first tile released" : "FIRST TILE STILL ALIVE"));
            pix2.Dispose();
        }
        // Ann мусить бути IDisposable — інакше списки анотацій нікому прибирати.
        M("L21 every annotation is disposable through the base type",
            typeof(IDisposable).IsAssignableFrom(typeof(Ann)), typeof(Ann).Name);

        // L22: краш-дедуп. Виняток в OnPaint не спиняє цикл повідомлень — Windows шле WM_PAINT
        // знову, і той самий стек писався сотні разів на секунду синхронно з UI-потоку.
        string crashDir = Path.Combine(Path.GetTempPath(), "ValeraScreenshotCrashProbe");
        try { if (Directory.Exists(crashDir)) Directory.Delete(crashDir, true); } catch { }
        Directory.CreateDirectory(crashDir);
        string realCfgDir = Config.Dir;
        try
        {
            Config.SetDirForTest(crashDir);
            Diag.ResetCrashDedupForTest();
            var boom = new InvalidOperationException("paint loop");
            for (int i = 0; i < 50; i++) Diag.LogCrash("OnPaint", boom);
            string cl = Path.Combine(crashDir, "crash.log");
            int entries = File.Exists(cl)
                ? System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(cl), "OnPaint:").Count : 0;
            M("L22 a repeating exception is logged once, not on every repaint", entries == 1,
                entries + " entries for 50 identical throws");
        }
        catch (Exception ex) { M("L22 crash dedup", false, ex.Message); }
        finally { Config.SetDirForTest(realCfgDir); }
        try { Directory.Delete(crashDir, true); } catch { }

        // L23: вікно Параметрів мусить уміщатися в робочу область. При масштабі 150 % ClientSize
        // множиться, і підвал із кнопкою «Зберегти» йшов за нижній край екрана — форма FixedDialog,
        // тож дістати кнопку було неможливо (Enter працював, але про це ніхто не знає).
        try
        {
            using (var sf = new SettingsForm(Config.Load()))
            {
                var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                M("L23 the settings window fits inside the screen work area",
                    sf.Height <= wa.Height, sf.Height + " px vs work area " + wa.Height + " px");
            }
        }
        catch (Exception ex) { M("L23 settings window fits the screen", false, ex.Message); }

        // ── L24/L25 — СИМЕТРІЯ ВСТАНОВЛЕННЯ Й ВИДАЛЕННЯ ─────────────────────────────────────
        //
        // Ці дві перевірки мали існувати з першого дня: коментар у setup\Uninstall.cs стверджував
        // «тест LU3 звіряє обидва» СПИСКИ, а грep по всьому дереву давав рівно один збіг — сам той
        // коментар. Тобто найнебезпечніший список у продукті (що саме дозволено ВИДАЛЯТИ) був
        // продубльований у двох файлах, які компілюються в РІЗНІ бінарники, і його узгодженість
        // гарантував рядок тексту. STRUCTURE.md уже писав, чим це закінчується: «сім шляхів
        // усередину проти трьох назовні — саме так вони колись і розійшлися».
        //
        // Ціна розбіжності — не косметика. Деінсталятор знімає теку НЕРЕКУРСИВНО (свідомо: так
        // чужі файли рятуються автоматично). Файл, покладений інсталяцією, але невідомий
        // деінсталятору, не просто лишається — він БЛОКУЄ rmdir, і тека встановлення переживає
        // видалення разом з усім, що в ній.
        string accRootLife = Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location));
        string bsPath = Path.Combine(accRootLife, "build_setup.ps1");
        var payloadDocs = new System.Collections.Generic.List<string>();
        if (File.Exists(bsPath))
        {
            string bs = File.ReadAllText(bsPath, System.Text.Encoding.UTF8);
            int at = bs.IndexOf("$payloadDocs = @(", StringComparison.Ordinal);
            if (at >= 0)
            {
                int close = bs.IndexOf(')', at);
                if (close > at)
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(bs.Substring(at, close - at), "\"([^\"]+)\""))
                        payloadDocs.Add(m.Groups[1].Value);
            }
        }
        var notOwned = new System.Collections.Generic.List<string>();
        foreach (string d in payloadDocs)
            if (Array.IndexOf(Installer.OwnedFiles, d) < 0) notOwned.Add(d);
        M("L24 every doc the installer PLACES is a file the uninstaller may remove",
            payloadDocs.Count > 0 && notOwned.Count == 0,
            payloadDocs.Count == 0 ? "could not read $payloadDocs from build_setup.ps1"
                                   : (notOwned.Count == 0 ? payloadDocs.Count + " docs, all owned"
                                                          : "placed but never removed: " + string.Join(", ", notOwned.ToArray())));

        // Другий список читаємо з ДЖЕРЕЛА: setup\Uninstall.cs компілюється в окремий бінарник і
        // в цю збірку не входить, тож дістатись до нього кодом неможливо — лише текстом.
        string unPath = Path.Combine(accRootLife, "setup\\Uninstall.cs");
        var unOwned = new System.Collections.Generic.List<string>();
        bool unParsed = false;
        if (File.Exists(unPath))
        {
            string un = StripComments(File.ReadAllText(unPath, System.Text.Encoding.UTF8));
            int at = un.IndexOf("OwnedFiles", StringComparison.Ordinal);
            int open = at < 0 ? -1 : un.IndexOf('{', at);
            int close = open < 0 ? -1 : un.IndexOf('}', open);
            if (close > open)
            {
                unParsed = true;
                string body = un.Substring(open, close - open);
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(body, "\"([^\"]+)\""))
                    unOwned.Add(m.Groups[1].Value);
                if (body.Contains("Ident.Exe")) unOwned.Add(Ident.Exe);
                if (body.Contains("Ident.CerFile")) unOwned.Add(Ident.CerFile);
            }
        }
        // Uninstall.exe у списку деінсталятора відсутній СВІДОМО: він зносить сам себе окремою
        // відкладеною командою, а не через цей перелік. Виняток названий, а не мовчазний.
        var missingInUn = new System.Collections.Generic.List<string>();
        foreach (string f in Installer.OwnedFiles)
        {
            if (f == "Uninstall.exe") continue;
            if (unOwned.IndexOf(f) < 0) missingInUn.Add(f);
        }
        M("L25 the uninstaller's own list mirrors the installer's, bar the named exception",
            unParsed && missingInUn.Count == 0,
            !unParsed ? "could not parse OwnedFiles out of setup\\Uninstall.cs"
                      : (missingInUn.Count == 0 ? unOwned.Count + " names mirrored"
                                                : "installed but NOT in the uninstaller: " + string.Join(", ", missingInUn.ToArray())));

        // ── L26: підписка на ДОВГОЖИВУЧУ подію мусить мати відписку ──────────────────────────
        //
        // Статична подія тримає СИЛЬНЕ посилання на підписника. Форма, що підписалась і не
        // відчепилась, лишається живою після закриття — разом зі своїми бітмапами, дескрипторами
        // й замороженим кадром під нею. Цей продукт такий дефект уже возив ДВІЧІ (Theme.Changed
        // і SystemEvents.DisplaySettingsChanged), тож клас доведено живий, а не теоретичний.
        //
        // Анонімний делегат відчепити за іменем НЕМОЖЛИВО в принципі, тому їх рахуємо окремо:
        // рівно два, обидва — краш-гарди в Main, підписані на весь час життя процесу. Третій
        // не з'явиться непоміченим.
        string[] longLived = { "SystemEvents.", "Theme.Changed", "L.Changed",
                               "Application.ThreadException", "AppDomain.CurrentDomain." };
        var noDetach = new System.Collections.Generic.List<string>();
        int anonSubs = 0;
        var anonWhere = new System.Collections.Generic.List<string>();
        string srcL26 = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)), "src");
        if (Directory.Exists(srcL26))
        {
            foreach (string f in Directory.GetFiles(srcL26, "*.cs"))
            {
                string b = StripComments(File.ReadAllText(f, System.Text.Encoding.UTF8));
                string[] ls = b.Split('\n');
                for (int i = 0; i < ls.Length; i++)
                {
                    string t = ls[i];
                    int at = t.IndexOf("+=");
                    if (at < 0) continue;
                    // Беремо САМ вираз події, а не весь текст ліворуч: підписка може стояти
                    // всередині `if (!_hooked) { ... }` на одному рядку, і наївний Substring
                    // приніс би «if (!_hooked) { Theme.Changed». Перша версія цього правила саме
                    // так і впала на здоровому коді — правило, що червоніє даремно, живе тиждень.
                    string left = LastToken(t.Substring(0, at));
                    string right = FirstToken(t.Substring(at + 2));
                    if (left.Length == 0 || right.Length == 0) continue;
                    bool watched = false;
                    // МІСТИТЬ, не «починається з»: OverlayForm пише повне
                    // Microsoft.Win32.SystemEvents.DisplaySettingsChanged, і перевірка за
                    // префіксом пропустила б цей файл цілком.
                    foreach (string p in longLived) if (left.IndexOf(p, StringComparison.Ordinal) >= 0) { watched = true; break; }
                    if (!watched) continue;
                    if (right.StartsWith("delegate") || right.StartsWith("("))
                    { anonSubs++; anonWhere.Add(Path.GetFileName(f) + ":" + (i + 1)); continue; }
                    if (b.IndexOf(left + " -= " + right, StringComparison.Ordinal) < 0)
                        noDetach.Add(Path.GetFileName(f) + ":" + (i + 1) + "  " + left + " += " + right);
                }
            }
        }
        M("L26 every subscription to a long-lived event has a matching unsubscribe",
            noDetach.Count == 0,
            noDetach.Count == 0 ? "all named handlers detach" : noDetach[0]);
        M("L26b the only undetachable handlers are the two process-wide crash guards",
            anonSubs == 2, anonSubs + " anonymous: " + string.Join(", ", anonWhere.ToArray()));

        // маркер лишаємо в тому стані, у якому застали
        try { if (!hadMarker && File.Exists(myMarker)) File.Delete(myMarker); } catch { }

        Console.WriteLine("LIFECYCLE MATRIX: " + _mp + "/" + _mt);
    }

    // Вимірний корпус домену (STD-GATE-02/03/04/10). Міряє ТУ САМУ функцію, яку бачить користувач
    // (Hotkeys.ToText малює підписи в треї та Параметрах), і несе два класи кейсів: «полагодити»
    // і «не чіпати». ВІДСУТНІЙ корпус = ЧЕРВОНИЙ гейт, ніколи «тихо пропущено»: критерій, який
    // зникає разом із файлом, — не критерій (STD-GATE-10).
    const double CorpusFloor = 0.95;

    static void Corpus()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "corpus.tsv");
        if (!File.Exists(path))
        {
            _fail++;
            Console.WriteLine("FAIL  CORPUS MISSING: " + path + " - gate fails closed (STD-GATE-10)");
            return;
        }

        int ok = 0, total = 0;
        foreach (string raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] c = line.Split('\t');
            if (c.Length < 3) continue;
            string[] vm = c[1].Split(':');
            if (vm.Length != 2) continue;

            int vk, mods;
            if (!int.TryParse(vm[0], out vk) || !int.TryParse(vm[1], out mods)) continue;

            total++;
            string got = Hotkeys.ToText(vk, mods);
            if (got == c[2]) ok++;
            else Console.WriteLine("      corpus miss [" + c[0] + "] " + c[1] + " -> '" + got + "' expected '" + c[2] + "'");
        }

        if (total == 0)
        {
            _fail++;
            Console.WriteLine("FAIL  CORPUS EMPTY - nothing measured (STD-GATE-10)");
            return;
        }

        double pct = (double)ok / total;
        Console.WriteLine(string.Format("CORPUS ACCURACY: {0}/{1} = {2:0.0}% (floor {3:0.0}%)",
            ok, total, pct * 100.0, CorpusFloor * 100.0));
        if (pct < CorpusFloor) { _fail++; Console.WriteLine("FAIL  CORPUS BELOW FLOOR - gate fails"); }
        else Check("corpus at or above floor", true, ok + "/" + total);
    }

    // ---- матричні лічильники (STD-GATE-08: корона доводиться матрицею, не happy-path) ----
    private static int _mp, _mt;
    static void M(string name, bool ok, string info) { _mt++; if (ok) _mp++; Check(name, ok, info); }

    // PRIVACY MATRIX (STD-DIAG-01b + STD-DIAG-06). Кожен параметр — окремий кейс, включно з
    // негативними. Один зелений happy-path нічого не доводить: саме так пропустили діру, коли лог
    // писав набране ще шість хвилин після відкликання згоди.
    static void PrivacyMatrix()
    {
        _mp = 0; _mt = 0;
        string marker = Path.Combine(Config.Dir, "debug.on");
        bool hadMarker = File.Exists(marker);
        string logTmp = Path.Combine(Path.GetTempPath(), "ls_privacy_matrix.log");
        try { if (File.Exists(logTmp)) File.Delete(logTmp); } catch { }

        Diag.SetPathForTest(logTmp);
        Diag.SetSkipMarkerCheckForTest(false);   // перевіряємо СПРАВЖНЮ гілку, не тест-шов
        Diag.SetRecheckMsForTest(0);             // вікно 0 -> перевірка щоразу
        Diag.SetSourceForTest("marker");

        // P1: маркер є -> запис дозволено
        try { File.WriteAllText(marker, ""); } catch { }
        Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        M("P1 marker present -> logging allowed", Diag.StillEnabledForTest(), null);

        // P2: маркер зник -> запис глушиться НЕГАЙНО, без перезапуску (це і є відкликання згоди)
        try { File.Delete(marker); } catch { }
        Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        M("P2 marker withdrawn -> logging silenced", !Diag.StillEnabledForTest(), null);

        // P3: прапорець УВІМКНЕНО, але маркер зник -> саме Log() мусить звірити згоду й змовчати.
        // Спершу тут покладалися на вже погашений _on — і тест проходив НАВІТЬ якщо прибрати перевірку
        // з Log(). Мутація DIAG-1 це оголила: тест ішов іншою гілкою, ніж та, яку мав охороняти.
        try { File.Delete(marker); } catch { }
        Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        long before = File.Exists(logTmp) ? new FileInfo(logTmp).Length : 0;
        Diag.Log("MUST-NOT-APPEAR");
        Diag.FlushForTest(300);
        long after = File.Exists(logTmp) ? new FileInfo(logTmp).Length : 0;
        M("P3 Log() re-checks consent and stays silent", after == before, before + "->" + after);

        // P3b: позитивний контроль — із наявним маркером Log() таки ПИШЕ. Без нього «нічого не пише»
        // проходило б тривіально (напр. якби Log() почав завжди виходити на початку).
        try { File.WriteAllText(marker, ""); } catch { }
        Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        long wBefore = File.Exists(logTmp) ? new FileInfo(logTmp).Length : 0;
        Diag.Log("MUST-APPEAR");
        Diag.FlushForTest(600);
        long wAfter = File.Exists(logTmp) ? new FileInfo(logTmp).Length : 0;
        M("P3b with consent present Log() does write", wAfter > wBefore, wBefore + "->" + wAfter);

        // P4: вікно throttle — у його межах повторна перевірка не б'є диск (вимірне, не «на око»)
        try { File.WriteAllText(marker, ""); } catch { }
        Diag.SetEnabledForTest(true); Diag.SetRecheckMsForTest(60000); Diag.ResetMarkerTickForTest();
        bool armed = Diag.StillEnabledForTest();            // підтвердить і зафіксує тік
        try { File.Delete(marker); } catch { }
        M("P4 inside throttle window consent is cached", armed && Diag.StillEnabledForTest(), "60000 ms");

        // P5: за межами вікна той самий зниклий маркер уже глушить
        Diag.SetRecheckMsForTest(0); Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        M("P5 beyond window withdrawal takes effect", !Diag.StillEnabledForTest(), null);

        // P6: env-джерело маркера не має — там нічого перевіряти
        Diag.SetSourceForTest(Ident.EnvDebug); Diag.SetEnabledForTest(true); Diag.ResetMarkerTickForTest();
        M("P6 env source needs no marker", Diag.StillEnabledForTest(), Ident.EnvDebug);
        Diag.SetSourceForTest("marker");

        // P7: TRUTHY вмикає
        bool truthy = Diag.IsTruthy("1") && Diag.IsTruthy("true") && Diag.IsTruthy("YES")
                      && Diag.IsTruthy("on") && Diag.IsTruthy("y");
        M("P7 truthy values arm the flag", truthy, "1/true/yes/on/y");

        // P8: FALSY НЕ вмикає — саме "0" колись тихо озброював кейлог
        bool falsy = !Diag.IsTruthy("0") && !Diag.IsTruthy("false") && !Diag.IsTruthy("")
                     && !Diag.IsTruthy(null) && !Diag.IsTruthy("maybe");
        M("P8 falsy values never arm the flag", falsy, "0/false/empty/null/maybe");

        // P9: LogCrash пише ЗАВЖДИ — навіть коли лог вимкнено (інакше крах не лишає сліду)
        Diag.SetEnabledForTest(false);
        string crashPath = Path.Combine(Config.Dir, "crash.log");
        long cBefore = File.Exists(crashPath) ? new FileInfo(crashPath).Length : 0;
        Diag.LogCrash("matrix-probe", new Exception("synthetic"));
        long cAfter = File.Exists(crashPath) ? new FileInfo(crashPath).Length : 0;
        M("P9 LogCrash writes even with logging off", cAfter > cBefore, cBefore + "->" + cAfter);

        // P10-P12: чисте рішення про згоду — включно з гілкою FAIL-CLOSED, яку файлом не дістати
        M("P10 marker present, check OK -> allowed", Diag.ConsentVerdict(true, false), null);
        M("P11 marker absent, check OK -> silenced", !Diag.ConsentVerdict(false, false), null);
        M("P12 check FAILED -> silenced even if marker seemed present (fail-closed)",
            !Diag.ConsentVerdict(true, true) && !Diag.ConsentVerdict(false, true), "doubt favours silence");

        // прибирання: не лишати озброєного маркера й тестових артефактів
        try { if (!hadMarker && File.Exists(marker)) File.Delete(marker); } catch { }
        try { if (File.Exists(logTmp)) File.Delete(logTmp); } catch { }
        Diag.SetEnabledForTest(false); Diag.SetSkipMarkerCheckForTest(true); Diag.SetRecheckMsForTest(2000);

        Console.WriteLine("PRIVACY MATRIX: " + _mp + "/" + _mt);
    }

    // UPDATER MATRIX (STD-UPD-02/04). Б'є в ЧИСТІ функції Judge/IsOurCert — саме тому мутація
    // здатна їх зловити. Проба через справжній файл цього НЕ доводить: системні бінарники підписані
    // каталогом і відсіюються WinVerifyTrust ДО рядка з піном (STD-GATE-09 ★ОБМЕЖЕННЯ).
    static void UpdaterMatrix()
    {
        _mp = 0; _mt = 0;
        var v230 = new Version("2.3.0"); var v240 = new Version("2.4.0");
        var v250 = new Version("2.5.0"); var v2500 = new Version("2.5.0.0");

        M("U1 newer signed build accepted", Updater.Judge(v2500, v250, v240) == Updater.Verdict.Accept, "bin 2.5.0.0 / man 2.5.0 / cur 2.4.0");
        M("U2 manifest-vs-binary mismatch rejected", Updater.Judge(v2500, new Version("2.6.0"), v240) == Updater.Verdict.VersionMismatch, null);
        M("U3 same version rejected (anti-rollback)", Updater.Judge(v240, v240, v240) == Updater.Verdict.NotNewer, null);
        M("U4 older build rejected (anti-rollback)", Updater.Judge(v230, v230, v240) == Updater.Verdict.NotNewer, "replay of a genuine OLD signed build");
        M("U5 unreadable binary version rejected", Updater.Judge(null, v250, v240) == Updater.Verdict.VersionMismatch, null);

        string pin = Updater.PinnedThumbprintForTest;
        M("U6 OUR thumbprint accepted", Updater.IsOurCert(pin), pin.Substring(0, 12) + "...");
        M("U7 FOREIGN thumbprint rejected", !Updater.IsOurCert("1111111111111111111111111111111111111111"), "this is the real pin proof");
        M("U8 empty/null thumbprint rejected", !Updater.IsOurCert("") && !Updater.IsOurCert(null), null);
        M("U9 pin equals the value bound in standard.bind.json", pin == Ident.Thumbprint, null);
        M("U10 manifest URL is the studio release channel",
            Updater.ManifestUrlForTest.Contains(Ident.Repo) && Updater.ManifestUrlForTest.EndsWith("latest.txt"), null);

        // U11/U12 — ЄДИНЕ, ЩО БУЛО ВАРТЕ РЯТУНКУ з tools\UpdaterProbe.cs (видалено 2026-07-29).
        // Уся решта тієї проби вже покрита вище, і покрита КРАЩЕ: вона била в Judge і IsOurCert
        // напряму, тоді як проба про свій же головний рядок чесно писала «це НЕ доказ піна».
        // А сама вона була мертва й гнила: не збиралась жодним скриптом, не запускалась жодним
        // гейтом, хардкодила `D:\ValeraScreenshot` (тека зветься інакше), посилалась на
        // ShotTest.exe, якого вже не існує, і порівнювала 2.5.0 з 2.5.0 як «новіше за поточне».
        // Тобто впала б на власній арифметиці, якби її колись хтось запустив.
        //
        // Ці ж дві перевірки НЕ дублюють нічого: вони єдині подають у гейт підпису СПРАВЖНІЙ
        // ФАЙЛ, а не версію чи відбиток. Обидві не залежать від середовища — саме тому й живуть.
        string junk = Path.Combine(Path.GetTempPath(), "vs_probe_junk.exe");
        bool junkRejected = false, junkThrew = false;
        try
        {
            // Найчастіша реальна поломка каналу оновлень — не зловмисник, а ПОСЕРЕДНИК:
            // хмарне сховище або корпоративний проксі віддає HTML-заглушку зі статусом 200
            // замість бінарника. Гейт підпису мусить сказати «ні», а не впасти з винятком:
            // падіння тут означає, що застосунок помирає під час автооновлення.
            File.WriteAllText(junk, "<html>Google Drive interstitial</html>");
            junkRejected = !Updater.VerifySignedByUs(junk);
        }
        catch (Exception) { junkThrew = true; }
        finally { try { if (File.Exists(junk)) File.Delete(junk); } catch { } }
        M("U11 non-PE junk served instead of the binary is REJECTED, not crashed on",
            junkRejected && !junkThrew, junkThrew ? "VerifySignedByUs THREW" : "html masquerading as .exe");

        // Ця збірка — гарантовано наявний і гарантовано НЕПІДПИСАНИЙ PE: sign.ps1 підписує
        // застосунок, інсталятор і деінсталятор, і ніколи не чіпає тестовий бінарник. Брати
        // сторонній файл із диска не можна — саме так проба й зогнила, вказавши на видалений exe.
        string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        M("U12 a valid but UNSIGNED binary is rejected", !Updater.VerifySignedByUs(selfExe),
            Path.GetFileName(selfExe) + " is never signed by sign.ps1");

        Console.WriteLine("UPDATER MATRIX: " + _mp + "/" + _mt);
    }

    // OVERLAY MATRIX — редактор поверх замороженого кадру.
    //
    // ЧОМУ ЦЯ МАТРИЦЯ З'ЯВИЛАСЬ. src\OverlayForm.cs — 1248 рядків, найбільший файл продукту й та
    // сама поверхня, у якій користувач працює руками. Прочіс 2026-07-29 показав: її стерегли НУЛЬ
    // мутацій і жодне твердження. Тобто найуживаніша частина застосунку була захищена найгірше —
    // і це, а не якийсь конкретний баг, і є причина відчуття «баги на баг». Тут замикаються ті
    // обіцянки редактора, які людина помічає пальцями, а не в лозі.
    //
    // Вікно НЕ показується: конструктор оверлея не потребує екрана, а жодна перевірка нижче
    // нічого не малює. Кадр — синтетичний Bitmap, не справжній знімок.
    static void OverlayMatrix()
    {
        _mp = 0; _mt = 0;
        var frame = new System.Drawing.Bitmap(800, 600);
        var virt = new System.Drawing.Rectangle(0, 0, 800, 600);

        // ---- ручки виділення ----
        //
        // ★ ПЕРША ВЕРСІЯ ЦІЄЇ ПЕРЕВІРКИ БУЛА ТАВТОЛОГІЄЮ, І МУТАЦІЯ ЦЕ ДОВЕЛА (OVL-6 SURVIVED).
        //   Вона брала точку з HandlePoint і питала HitHandle, чи та точка попадає в ручку i.
        //   Але HitHandle САМ кличе HandlePoint (рядок 287), і малювання кличе її ж (рядок 940) —
        //   тобто розійтися вони не можуть за побудовою. Зсунь HandlePoint на 40 px, і намальоване
        //   з очікуваним поїде РАЗОМ, а тест лишиться зеленим. Він доводив, що функція узгоджена
        //   сама з собою: беззмістовна правда.
        //   Тепер перевіряється ГЕОМЕТРИЧНИЙ КОНТРАКТ проти самого прямокутника, а не проти
        //   функції, що його породила. Ручка мусить лежати ТАМ, де людина її шукає — на розі або
        //   посередині сторони, — і жодні дві не можуть збігтися.
        var sel = new System.Drawing.Rectangle(100, 80, 300, 200);
        var expect = new System.Drawing.Point[] {
            new System.Drawing.Point(sel.Left,  sel.Top),                       // 0 верхній лівий
            new System.Drawing.Point(sel.Left + sel.Width / 2, sel.Top),        // 1 середина верху
            new System.Drawing.Point(sel.Right, sel.Top),                       // 2 верхній правий
            new System.Drawing.Point(sel.Right, sel.Top + sel.Height / 2),      // 3 середина права
            new System.Drawing.Point(sel.Right, sel.Bottom),                    // 4 нижній правий
            new System.Drawing.Point(sel.Left + sel.Width / 2, sel.Bottom),     // 5 середина низу
            new System.Drawing.Point(sel.Left,  sel.Bottom),                    // 6 нижній лівий
            new System.Drawing.Point(sel.Left,  sel.Top + sel.Height / 2),      // 7 середина ліва
        };
        int wrong = -1;
        var seen = new System.Collections.Generic.List<System.Drawing.Point>();
        for (int i = 0; i < 8; i++)
        {
            var p = OverlayForm.TestHandlePoint(i, sel);
            if (p != expect[i] && wrong < 0) wrong = i;
            seen.Add(p);
        }
        bool distinct = true;
        for (int i = 0; i < seen.Count && distinct; i++)
            for (int j = i + 1; j < seen.Count; j++)
                if (seen[i] == seen[j]) { distinct = false; break; }
        M("O1 each resize handle sits exactly where a person reaches for it", wrong < 0 && distinct,
            wrong >= 0 ? ("handle " + wrong + " is at " + seen[wrong] + ", expected " + expect[wrong])
                       : (distinct ? "8 handles on the rect, all distinct" : "two handles share a point"));

        // Хват мусить бути НІ мертвим, НІ жадібним: близький клік бере ручку, далекий — ні.
        // Це вже про HitHandle, і воно НЕ тавтологічне: перевіряється допуск, а не координата.
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(sel, null, Tool.Rect);
            var s = f.TestSelClamped();
            var corner = OverlayForm.TestHandlePoint(0, s);
            bool near = f.TestHitHandle(new System.Drawing.Point(corner.X + 5, corner.Y + 5)) == 0;
            bool far = f.TestHitHandle(new System.Drawing.Point(corner.X + 40, corner.Y + 40)) == -1;
            M("O1b the grip is neither dead nor greedy: 5 px grabs, 40 px does not", near && far,
                "near=" + near + " far=" + far);
        }

        // Протилежні кути мусять давати ДЗЕРКАЛЬНІ курсори. Однаковий курсор на обох діагоналях —
        // класична дрібниця, через яку вікно «відчувається дешевим»: Windows цим показує, у який
        // бік поїде край.
        bool mirrored = OverlayForm.TestHandleCursor(0) == OverlayForm.TestHandleCursor(4)
                     && OverlayForm.TestHandleCursor(2) == OverlayForm.TestHandleCursor(6)
                     && OverlayForm.TestHandleCursor(0) != OverlayForm.TestHandleCursor(2)
                     && OverlayForm.TestHandleCursor(1) == OverlayForm.TestHandleCursor(5)
                     && OverlayForm.TestHandleCursor(3) != OverlayForm.TestHandleCursor(1);
        M("O2 opposite corners get mirrored resize cursors, edges get their own", mirrored,
            "NWSE / NESW / NS / WE");

        // Виділення, що вилізло за кадр, — це захват області, якої на екрані немає.
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(new System.Drawing.Rectangle(-50, -40, 200, 150), null, Tool.Rect);
            var c = f.TestSelClamped();
            M("O3 a selection dragged past the edge is clamped back into the frame",
                c.Left >= 0 && c.Top >= 0 && c.Right <= 800 && c.Bottom <= 600,
                c.ToString());
        }

        // ---- скасування дії: контракт будь-якого редактора ----
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(sel, null, Tool.Rect);
            f.TestCommit(new RectAnn { A = new System.Drawing.Point(1, 1), B = new System.Drawing.Point(9, 9) });
            f.TestCommit(new RectAnn { A = new System.Drawing.Point(2, 2), B = new System.Drawing.Point(8, 8) });
            int after2 = f.TestAnnCount;
            f.TestUndo();
            M("O4 undo removes exactly ONE annotation, not the lot",
                after2 == 2 && f.TestAnnCount == 1, after2 + " -> " + f.TestAnnCount);
            f.TestRedo();
            M("O5 redo puts back what undo took", f.TestAnnCount == 2 && f.TestRedoCount == 0,
                "anns=" + f.TestAnnCount + " redo=" + f.TestRedoCount);
        }

        // Порожнє полотно: Undo/Redo не мають ані падати, ані лізти за межі списку.
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(sel, null, Tool.Rect);
            bool threw = false;
            try { f.TestUndo(); f.TestRedo(); f.TestUndo(); }
            catch (Exception) { threw = true; }
            M("O6 undo and redo on an empty canvas do nothing instead of crashing",
                !threw && f.TestAnnCount == 0 && f.TestRedoCount == 0,
                threw ? "THREW" : "both lists stayed empty");
        }

        // Нова дія після скасування ОБРИВАЄ гілку «повернути». Без цього скасована анотація
        // воскресає пізніше поверх нової роботи — найгірший різновид дефекту редактора, бо
        // виглядає як привид, а не як помилка.
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(sel, null, Tool.Rect);
            f.TestCommit(new RectAnn { A = new System.Drawing.Point(1, 1), B = new System.Drawing.Point(9, 9) });
            f.TestUndo();
            f.TestCommit(new RectAnn { A = new System.Drawing.Point(3, 3), B = new System.Drawing.Point(7, 7) });
            f.TestRedo();
            M("O7 a new annotation discards the redo branch, so nothing resurrects later",
                f.TestAnnCount == 1, f.TestAnnCount + " annotation(s) after redo");
        }

        // ...і обірвана гілка мусить ЗВІЛЬНИТИ ресурси. Мозаїка володіє власним Bitmap; коли
        // список просто чистили, кожна скасована мозаїка лишала його до кінця процесу.
        using (var f = new OverlayForm(frame, virt, new Config()))
        using (var canvas = new System.Drawing.Bitmap(80, 60))
        {
            f.TestSetup(sel, null, Tool.Rect);
            var pix = new PixelateAnn { A = new System.Drawing.Point(4, 4), B = new System.Drawing.Point(70, 50), Width = 3 };
            pix.Bake(canvas);
            bool baked = pix.Tile != null;
            f.TestCommit(pix);
            f.TestUndo();
            f.TestCommit(new RectAnn { A = new System.Drawing.Point(1, 1), B = new System.Drawing.Point(9, 9) });
            M("O8 the discarded redo branch frees the memory it owned", baked && pix.Tile == null,
                baked ? (pix.Tile == null ? "tile released" : "TILE STILL HELD") : "Bake produced no tile");
        }

        // ---- нумеровані кроки ----
        using (var f = new OverlayForm(frame, virt, new Config()))
        {
            f.TestSetup(sel, null, Tool.Step);
            int n1 = f.TestNextStepNumber();
            f.TestCommit(new StepAnn { Pos = new System.Drawing.Point(10, 10), Number = n1 });
            int n2 = f.TestNextStepNumber();
            f.TestCommit(new StepAnn { Pos = new System.Drawing.Point(20, 20), Number = n2 });
            int n3 = f.TestNextStepNumber();
            M("O9 numbered steps count 1, 2, 3 - never skip", n1 == 1 && n2 == 2 && n3 == 3,
                n1 + ", " + n2 + ", " + n3);

            // Скасував крок 2 -> наступний знову 2. Інакше інструкція виходить «1, 3, 4»,
            // і людина потім гадає, куди подівся другий пункт.
            f.TestUndo();
            M("O10 undoing a step frees its number instead of leaving a hole",
                f.TestNextStepNumber() == 2, "next after undo = " + f.TestNextStepNumber());
        }

        try { frame.Dispose(); } catch { }
        Console.WriteLine("OVERLAY MATRIX: " + _mp + "/" + _mt);
    }

    // CAPTURE + SHARE MATRIX.
    //
    // Обидва файли (src\Capture.cs — сам шлях захвату, src\ShareUtil.cs — панель «Поділитися»)
    // до 2026-07-29 не мали ані мутацій, ані тверджень. Capture.cs — це той код, у якому колись
    // жила «брехня про успіх» №4: результат BitBlt відкидався, при провалі виходив ЧОРНИЙ кадр,
    // він тихо лягав у файл, а трей писав «Збережено 1920 x 1080».
    static void CaptureMatrix()
    {
        _mp = 0; _mt = 0;
        string dir = Path.Combine(Path.GetTempPath(), "valerascreenshot_capture");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);

        // Шумний кадр: суцільна заливка стискається однаково за будь-якої якості, тож на ній
        // перевірка JpegQuality була б зеленою й порожньою. Шум ДЕТЕРМІНОВАНИЙ — тест не має
        // права давати різний результат від прогону до прогону.
        var bmp = new System.Drawing.Bitmap(240, 180);
        int seed = 12345;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb((seed >> 16) & 0xFF, (seed >> 8) & 0xFF, seed & 0xFF));
            }

        var cfg = new Config();

        // Магічні байти читаємо з ФАЙЛА. Розширення нічого не доводить: саме "png-у-файлі-.jpg"
        // і є той дефект, який ця перевірка ловить - месенджер такий файл відхилить, а людина
        // думатиме на месенджер.
        string png = Path.Combine(dir, "a.png");
        ScreenCap.Save(bmp, png, cfg);
        byte[] hp = File.ReadAllBytes(png);
        M("C1 a .png really is a PNG, not just a name",
            hp.Length > 8 && hp[0] == 0x89 && hp[1] == 0x50 && hp[2] == 0x4E && hp[3] == 0x47,
            hp.Length + " b, magic " + hp[0].ToString("X2") + hp[1].ToString("X2"));

        string jpg = Path.Combine(dir, "b.jpg");
        ScreenCap.Save(bmp, jpg, cfg);
        byte[] hj = File.ReadAllBytes(jpg);
        M("C2 a .jpg really is a JPEG", hj.Length > 3 && hj[0] == 0xFF && hj[1] == 0xD8 && hj[2] == 0xFF,
            hj.Length + " b, magic " + hj[0].ToString("X2") + hj[1].ToString("X2"));

        // РЕГІСТР. Користувач вписує «.JPG» у шаблон імені так само легко, як «.jpg».
        string up = Path.Combine(dir, "c.JPEG");
        ScreenCap.Save(bmp, up, cfg);
        byte[] hu = File.ReadAllBytes(up);
        M("C3 an uppercase .JPEG is routed by extension too, not silently saved as PNG",
            hu.Length > 3 && hu[0] == 0xFF && hu[1] == 0xD8, hu[0].ToString("X2") + hu[1].ToString("X2"));

        // Якість із налаштувань мусить ДІЯТИ, а не лежати в конфігу для краси.
        string lo = Path.Combine(dir, "lo.jpg"), hi = Path.Combine(dir, "hi.jpg");
        ScreenCap.SaveJpeg(bmp, lo, 5);
        ScreenCap.SaveJpeg(bmp, hi, 100);
        long loLen = new FileInfo(lo).Length, hiLen = new FileInfo(hi).Length;
        M("C4 the JPEG quality setting actually reaches the encoder", hiLen > loLen * 2,
            "q5 = " + loLen + " b vs q100 = " + hiLen + " b");

        // Невідоме розширення НЕ має давати биту заглушку - PNG є безпечним відступом.
        string odd = Path.Combine(dir, "d.dat");
        ScreenCap.Save(bmp, odd, cfg);
        byte[] ho = File.ReadAllBytes(odd);
        M("C5 an unknown extension falls back to PNG instead of writing something unreadable",
            ho.Length > 8 && ho[0] == 0x89 && ho[1] == 0x50, ho[0].ToString("X2") + ho[1].ToString("X2"));

        // C9 — ГРАНИЧНА ТЕКА ЗБЕРЕЖЕННЯ. Пункт Фази 5 («тека лише для читання, повний диск,
        // від'єднаний мережевий диск») лишався невиконаним: замка не було жодного.
        // Сценарій живий: людина вказує тека на мережевій шарі, шара відпадає — і кожен наступний
        // знімок мусить СКАЗАТИ про це, а не зникнути. Усі чотири шляхи збереження в продукті
        // справді ловлять виняток і показують причину; ця перевірка стереже, щоб MakeFilePath
        // його ВЗАГАЛІ кидав. Обгорнути CreateDirectory у порожній catch — і чесність зникає,
        // а замість неї повертається шлях, у який ніхто не запише.
        // Непридатну теку робимо детерміновано: на її місці кладемо ФАЙЛ. Не літера диска й не
        // мережа — те й те залежить від машини, а вердикт, залежний від хоста, гірший за відсутній.
        string blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "x");
        var badCfg = new Config();
        badCfg.SaveDir = Path.Combine(blocker, "shots");   // під файлом теки не буває
        bool threw = false;
        string got = null;
        try { got = badCfg.MakeFilePath(800, 600); }
        catch (Exception) { threw = true; }
        M("C9 an unusable save folder fails LOUDLY instead of returning a path nobody can write",
            threw, threw ? "throws, so the caller can tell the person why" : "silently returned " + got);

        try { bmp.Dispose(); } catch { }
        try { Directory.Delete(dir, true); } catch { }

        // ---- панель «Поділитися» ----
        // Кнопка, яку намальовано, але яка нічого не робить, гірша за відсутню: людина тисне,
        // діалог закривається, і вона вважає, що знімок пішов.
        L.Init("uk");
        var uk = ShareUtil.Detect();
        int broken = 0;
        string firstBroken = null;
        var keys = new System.Collections.Generic.List<string>();
        foreach (var t in uk)
        {
            if (t.Launch == null || string.IsNullOrEmpty(t.Name) || string.IsNullOrEmpty(t.Hint) ||
                string.IsNullOrEmpty(t.Key))
            {
                broken++;
                if (firstBroken == null) firstBroken = (t.Key ?? "(no key)");
            }
            keys.Add(t.Key);
        }
        // Нуль цілей — це НЕ «все гаразд». На машині без месенджерів перевірка порожня, і вона
        // мусить сказати це вголос, а не світитися зеленим: інакше CI, де не встановлено нічого,
        // рапортував би «усі кнопки живі», не перевіривши жодної.
        M("C6 every share button that is drawn can actually be pressed", broken == 0,
            broken > 0 ? "dead target: " + firstBroken
                       : (uk.Count == 0 ? "VACUOUS: no messenger on this machine, 0 targets examined"
                                        : uk.Count + " target(s), all wired"));

        bool dupe = false;
        for (int i = 0; i < keys.Count && !dupe; i++)
            for (int j = i + 1; j < keys.Count; j++)
                if (keys[i] == keys[j]) { dupe = true; break; }
        M("C7 no two share targets share a key (the key picks the glyph)", !dupe,
            keys.Count == 0 ? "no messengers installed here" : string.Join(", ", keys.ToArray()));

        // Мова панелі береться в момент ПОБУДОВИ списку. Якщо його десь закешувати, підказки
        // застрягнуть у мові першого виклику - той самий клас, що й тема у вже відкритих вікнах.
        if (uk.Count > 0)
        {
            L.Init("en");
            var en = ShareUtil.Detect();
            bool changed = en.Count == uk.Count;
            for (int i = 0; i < en.Count && changed; i++)
                if (en[i].Hint == uk[i].Hint) changed = false;
            M("C8 share hints follow the interface language instead of freezing at first build",
                changed, uk.Count > 0 ? (uk[0].Hint + "  ->  " + (en.Count > 0 ? en[0].Hint : "(none)")) : "");
            L.Init("uk");
        }
        else
        {
            Console.WriteLine("      C8 skip - no messenger installed on this machine, nothing to localise");
        }

        Console.WriteLine("CAPTURE MATRIX: " + _mp + "/" + _mt);
    }

    // HONESTY MATRIX (STD-DIAG-03 + «жодної брехні про успіх»).
    //
    // Найдорожчі дефекти цього продукту були не падіннями, а МОВЧАННЯМ: дія не спрацювала,
    // а застосунок рапортував успіх або не рапортував нічого. Тут замикаються ті обіцянки,
    // які мовчання порушує.
    static void HonestyMatrix()
    {
        _mp = 0; _mt = 0;

        // Падіння мусить дійти до ЛЮДИНИ, але не перетворитись на шторм вікон: краш в OnPaint
        // повторюється сотні разів на секунду, і саме через це тут стеля, а не просто «показати».
        M("H1 the first crash of a given cause is shown to the user",
            Diag.CrashDialogVerdict(true, 0), null);
        M("H2 the SAME cause is not shown twice",
            !Diag.CrashDialogVerdict(false, 0), "a repaint storm must not open a window per frame");
        M("H3 there is a ceiling per session",
            !Diag.CrashDialogVerdict(true, 3), "3 dialogs already shown");
        M("H4 different causes below the ceiling are all shown",
            Diag.CrashDialogVerdict(true, 1) && Diag.CrashDialogVerdict(true, 2), null);

        // Копіювання в буфер ПОВЕРТАЄ успіх. Коли метод був void, обидва шляхи знімка
        // рапортували «готово» над мовчазною відмовою.
        M("H5 clipboard copy reports success instead of swallowing it",
            typeof(ClipboardUtil).GetMethod("CopyImage") != null &&
            typeof(ClipboardUtil).GetMethod("CopyImage").ReturnType == typeof(bool), null);

        // ОБИДВА шляхи знімка мусять питати про буфер. Регіон полагодили торік, «весь екран»
        // лишався з `try { CopyImage } catch { }` і балуном «Збережено» поверх відмови —
        // той самий дефект, лише в другій копії коду.
        string appPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)), "src\\App.cs");
        if (File.Exists(appPath))
        {
            string app = StripComments(File.ReadAllText(appPath, System.Text.Encoding.UTF8));
            M("H6 no capture path swallows a clipboard failure",
                !app.Contains("try { ClipboardUtil.CopyImage(shot); } catch { }"),
                "the whole-screen path used to do exactly that");
            M("H7 a second launch shows the running app instead of a modal notice",
                app.Contains("_SHOW") && !app.Contains("уже запущено"),
                "Windows apps bring the running instance forward");
        }

        else
        {
            _fail++;
            Console.WriteLine("FAIL  HONESTY SOURCES MISSING: " + appPath);
        }

        // H12 — ПРАВИЛО, А НЕ ОКРЕМИЙ ВИПАДОК. Методи, що повертають УСПІХ, не мають права
        // викликатись як голий оператор: саме так у продукті з'явилися чотири різні «брехні
        // про успіх», і саме так з'явилася б п'ята. Правило дешеве й точне — воно не вимагає
        // коментаря до кожного з ~140 порожніх catch, а стереже ту єдину їх частину, через яку
        // користувач бачив «готово» над мовчазною відмовою.
        string srcDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)), "src");
        // Native.BitBlt ДОДАНО 2026-07-29. Він повертає успіх, і його результат КОЛИСЬ УЖЕ
        // ігнорувався: при провалі (захищене вікно, DRM-плеєр, ексклюзивний DirectX) виходив
        // ЧОРНИЙ кадр, який тихо лягав у файл, а трей рапортував «Збережено 1920 x 1080».
        // Зараз виклик стоїть під if — але правило H12 існує рівно для того, щоб він там і
        // лишився, а не тому, що сьогодні все гаразд. CLAUDE.md вимагає дописувати сюди кожен
        // новий метод такого роду; цей був пропущений.
        // CopyOver ДОДАНО 2026-08-08: його результат уже ВІДКИДАВСЯ в InstallOverCommand — після
        // 20 невдалих спроб код однаково штампував картку ARP і перезапускав старий exe як
        // підсумок «оновлення» з UAC. Сигнатура некваліфікована, тож сканер тепер пропускає
        // рядки-ОГОЛОШЕННЯ (модифікатор на початку): оголошення не буває голим оператором.
        string[] mustNotBeDiscarded = { "ClipboardUtil.CopyImage(", "_cfg.Save(", "cfg.Save(", "Native.BitBlt(", "CopyOver(" };
        var discarded = new System.Collections.Generic.List<string>();
        if (Directory.Exists(srcDir))
        {
            foreach (string f in Directory.GetFiles(srcDir, "*.cs"))
            {
                if (Path.GetFileName(f) == "Config.cs") continue;   // тут Save оголошено, а не викликано
                string[] lines = StripComments(File.ReadAllText(f, System.Text.Encoding.UTF8)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("private ") || t.StartsWith("public ") ||
                        t.StartsWith("internal ") || t.StartsWith("static ")) continue;
                    foreach (string call in mustNotBeDiscarded)
                    {
                        int at = t.IndexOf(call);
                        if (at < 0) continue;
                        // Результат використано, якщо виклик стоїть праворуч від '=', усередині
                        // if/return, або в тернарному операторі. Голий оператор — це рівно
                        // «<виклик>(...);» на початку рядка.
                        bool used = at > 0 && (t.Contains("=") || t.StartsWith("if") ||
                                               t.StartsWith("return") || t.Contains("?"));
                        if (!used) discarded.Add(Path.GetFileName(f) + ":" + (i + 1) + " " + t);
                    }
                }
            }
        }
        M("H12 a method that returns success is never called as a bare statement",
            discarded.Count == 0,
            discarded.Count == 0 ? "checked " + mustNotBeDiscarded.Length + " signatures" : discarded[0]);
        foreach (string d in discarded) Console.WriteLine("      HON discarded: " + d);

        // H13-H18 — ШВИ ВИПРАВЛЕНЬ 2026-08-08 (аудит 14 дефектів, адверсарна верифікація).
        // Неелевований гейт не відтворить елевовані сценарії (AV-карантин staged-файла, UAC,
        // машинний деінсталл під звичайним користувачем), але може довести, що кожен шов на
        // місці. Текстовий рівень — свідомо: та сама техніка, якою H12 стереже клас дефекту.
        string updHon = StripComments(File.ReadAllText(Path.Combine(srcDir, "Updater.cs"), System.Text.Encoding.UTF8));
        M("H13 apply-update failure leaves a trace and relaunches de-elevated",
            updHon.Contains("Diag.LogCrash(\"apply-update\"") && updHon.Contains("Process.Start(\"explorer.exe\""),
            "Updater.cs: LogCrash(apply-update) + explorer.exe");
        string seedHon = StripComments(File.ReadAllText(Path.Combine(srcDir, "Seed.cs"), System.Text.Encoding.UTF8));
        M("H14 the config address does not trust the caller's rights",
            seedHon.Contains("!IsAdminOnlyDir("), "Seed.cs: гейт проби несе !IsAdminOnlyDir");
        string ovlHon = StripComments(File.ReadAllText(Path.Combine(srcDir, "OverlayForm.cs"), System.Text.Encoding.UTF8));
        M("H15 the Share dialog receives the real clipboard outcome",
            ovlHon.Contains("new ShareForm(path, ClipboardOk)"),
            "OverlayForm.cs: двоаргументний ShareForm (дефолт true компілюється мовчки)");
        string appHon = StripComments(File.ReadAllText(Path.Combine(srcDir, "App.cs"), System.Text.Encoding.UTF8));
        M("H16 secondary hotkeys distinguish wanted from succeeded",
            appHon.Contains("region2Wanted") && appHon.Contains("full2Wanted"),
            "App.cs: RegisterHotkeys розділяє намір і результат");
        string instHon = StripComments(File.ReadAllText(Path.Combine(srcDir, "Installer.cs"), System.Text.Encoding.UTF8));
        int posOkMsg = instHon.IndexOf(" видалено. Знімки збережено у:");
        int posSched = instHon.IndexOf("ScheduleSelfDelete();");
        M("H17 install-over honors CopyOver; uninstall self-deletes only after the dialog",
            instHon.Contains("if (copied) RefreshArp(target);") && posOkMsg >= 0 && posSched > posOkMsg,
            "Installer.cs: RefreshArp під гардом; діалог успіху ПЕРЕД самовидаленням");
        string setupDirHon = Path.Combine(Path.GetDirectoryName(srcDir), "setup");
        string unHon = StripComments(File.ReadAllText(Path.Combine(setupDirHon, "Uninstall.cs"), System.Text.Encoding.UTF8));
        bool unBound = true;
        foreach (string unLn in unHon.Split('\n'))
        {
            string ut = unLn.Trim();
            if (ut.StartsWith("private ") || ut.StartsWith("public ") ||
                ut.StartsWith("internal ") || ut.StartsWith("static ")) continue;
            if (ut.StartsWith("DeleteIfExists(") || ut.StartsWith("DeleteRunValue(") || ut.StartsWith("TryDeleteKey("))
            { unBound = false; break; }
        }
        int posUnOk = unHon.IndexOf(" видалено. Ваші знімки збережено.");
        int posUnCmd = unHon.IndexOf("cmd.exe");
        M("H18 the uninstaller binds every removal result and self-deletes after the dialog",
            unBound && posUnOk >= 0 && posUnCmd > posUnOk,
            "setup\\Uninstall.cs: жодного голого Delete*/TryDeleteKey; діалог перед cmd.exe");

        // H19 — ЗІПСОВАНЕ ЗНАЧЕННЯ НЕ ОБНУЛЯЄ ПОЛЕ. int.TryParse(out поле) при провалі гарантовано
        // пише 0: порожнє «RegionMods=» реєструвало ГОЛУ клавішу системно, «RegionVk=abc» тихо
        // вимикав гарячу клавішу, а Save() після першого ж знімка цементував нулі назавжди.
        // Тепер — темп-змінна, як у JpegQuality/LastWidth; явний 0 лишається законним («вимкнено»).
        string junkDir = Path.Combine(Path.GetTempPath(), "valerascreenshot_junkcfg");
        try
        {
            if (Directory.Exists(junkDir)) Directory.Delete(junkDir, true);
            Directory.CreateDirectory(junkDir);
            Config.SetDirForTest(junkDir);
            File.WriteAllText(Path.Combine(junkDir, "settings.ini"),
                "RegionVk=abc\r\nFull2Mods=xyz\r\nLastColor=zzz\r\nRegion2Vk=0\r\n",
                new System.Text.UTF8Encoding(false));
            var junk = Config.Load();
            M("H19 junk ini values keep defaults, explicit zero still applies",
                junk.RegionVk == 0x34 && junk.Full2Mods == Native.MOD_SHIFT &&
                junk.LastColor == unchecked((int)0xFFE81123) && junk.Region2Vk == 0 && !junk.LoadFailed,
                "RegionVk=" + junk.RegionVk + " Full2Mods=" + junk.Full2Mods + " Region2Vk=" + junk.Region2Vk);
        }
        catch (Exception junkEx) { M("H19 junk ini values keep defaults", false, junkEx.Message); }
        try { Directory.Delete(junkDir, true); } catch { }

        // H20 — ПРЕДИКАТИ МУСЯТЬ ЗБІГАТИСЯ. Bake() мозаїки відмовляється тайлити смугу, де
        // БУДЬ-ЯКИЙ вимір < 2, а IsDegenerate() відкидав лише коли ОБИДВА < 3 — тонкий мазок
        // фіксувався без тайла, і в експорт впечатувався білий пунктир прев'ю замість пікселізації.
        var thin1 = new PixelateAnn(); thin1.A = new System.Drawing.Point(0, 0); thin1.B = new System.Drawing.Point(100, 1);
        var thin2 = new PixelateAnn(); thin2.A = new System.Drawing.Point(0, 0); thin2.B = new System.Drawing.Point(1, 100);
        var small2 = new PixelateAnn(); small2.A = new System.Drawing.Point(0, 0); small2.B = new System.Drawing.Point(2, 2);
        var okAnn = new PixelateAnn(); okAnn.A = new System.Drawing.Point(0, 0); okAnn.B = new System.Drawing.Point(3, 2);
        M("H20 a mosaic too thin to bake is degenerate and cannot be committed",
            thin1.IsDegenerate() && thin2.IsDegenerate() && small2.IsDegenerate() && !okAnn.IsDegenerate(),
            "100x1/1x100/2x2 degenerate, 3x2 — ні");

        // Покалічений конфіг не має тихо перетворюватись на дефолти, які потім затруть оригінал.
        string cfgDir = Path.Combine(Path.GetTempPath(), "valerascreenshot_badcfg");
        try
        {
            if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, true);
            Directory.CreateDirectory(cfgDir);
            Config.SetDirForTest(cfgDir);
            // Байти, які не є валідним UTF-8 текстовим конфігом: читання кине.
            File.WriteAllBytes(Config.IniPath, new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x00 });
            long sizeBefore = new FileInfo(Config.IniPath).Length;
            using (var hold = new FileStream(Config.IniPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var broken = Config.Load();      // файл замкнений іншим процесом -> читання падає
                M("H8 a config that cannot be read is reported, not silently defaulted",
                    broken.LoadFailed, "LoadFailed=" + broken.LoadFailed);

                // Найважливіше твердження цієї пари: поки файл прочитати не вдається, ЗАПИС
                // мусить бути відмовлений. Інакше суміш «частина прочитаного + дефолти» лягає
                // поверх налаштувань користувача після першого ж знімка.
                broken.RegionVk = 0x41;
                bool saved = broken.Save();
                M("H9 an unreadable config is never overwritten", !saved, "Save() returned " + saved);
            }
            M("H10 the original bytes survive the refusal",
                File.Exists(Config.IniPath) && new FileInfo(Config.IniPath).Length == sizeBefore,
                sizeBefore + " bytes");

            // А коли файл уже вільний — оригінал відкладається вбік, і збереження проходить.
            var recovered = Config.Load();
            recovered.LoadFailed = true;         // імітуємо попередній невдалий старт
            bool savedNow = recovered.Save();
            M("H11 once the file is free, the original is set aside and saving works",
                savedNow && File.Exists(Config.IniPath + ".bad"), "Save() returned " + savedNow);
        }
        catch (Exception ex) { M("H8/H9 broken-config handling", false, ex.Message); }
        finally
        {
            Config.SetDirForTest(null);
            try { Directory.Delete(cfgDir, true); } catch { }
        }

        Console.WriteLine("HONESTY MATRIX: " + _mp + "/" + _mt);
    }

    // LOCALISATION MATRIX (STD-LOC-01/02).
    //
    // ЧОМУ ВОНА ІСНУЄ. Шим L.S("укр","eng") був у проєкті з першого дня, і саме тому здавалося,
    // що двомовність «є». Насправді Main жорстко викликав L.Init("uk"), а 219 видимих рядків
    // були зашиті українською ПОВЗ L.S — тобто API існував, а другої мови не існувало.
    // Напівпереклад гірший за одномовність: він мовчазний. Тому це не одноразова робота, а
    // ПОСТІЙНЕ твердження гейта: кожен новий кириличний рядок, доданий повз L.S, червонить збірку.
    //
    // Читаємо ВИХІДНИКИ, а не бінарник: єдиний спосіб побачити рядок, який ніколи не викликається.
    // Немає вихідників поруч — це FAIL, а не тихий пропуск (та сама логіка, що й у CORPUS MISSING).
    static void LocalisationMatrix()
    {
        _mp = 0; _mt = 0;

        // Чисте рішення про мову — окремо від стану, щоб його могла зламати мутація.
        M("N1 an explicit choice wins over the system language",
            L.Decide("uk", false) == UiLang.Uk && L.Decide("en", true) == UiLang.En, null);
        M("N2 'as in Windows' follows the system language",
            L.Decide("auto", true) == UiLang.Uk && L.Decide("auto", false) == UiLang.En, null);
        M("N3 an unknown value falls back to the system, never to a hardcoded language",
            L.Decide("klingon", true) == UiLang.Uk && L.Decide("", false) == UiLang.En, null);

        // L.S справді перемикає, а не завжди віддає українську.
        UiLang was = L.Cur;
        L.Cur = UiLang.En;
        bool en = L.S("укр", "eng") == "eng";
        L.Cur = UiLang.Uk;
        bool uk = L.S("укр", "eng") == "укр";
        L.Cur = was;
        M("N4 L.S returns the string of the CURRENT language", en && uk, null);

        string root = Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location));
        string[] dirs = { Path.Combine(root, "src"), Path.Combine(root, "setup") };
        var files = new System.Collections.Generic.List<string>();
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d))
            {
                _fail++;
                Console.WriteLine("FAIL  LOC SOURCES MISSING: " + d + " - cannot verify localisation");
                Console.WriteLine("LOCALISATION MATRIX: " + _mp + "/" + _mt + " (INCOMPLETE)");
                return;
            }
            files.AddRange(Directory.GetFiles(d, "*.cs"));
        }
        files.Sort();

        var bare = new System.Collections.Generic.List<string>();
        int scanned = 0;
        foreach (string f in files)
        {
            string why = LocExcuse(Path.GetFileName(f));
            if (why != null) { Console.WriteLine("      LOC skip " + Path.GetFileName(f) + " - " + why); continue; }
            scanned++;
            foreach (string hit in BareCyrillic(File.ReadAllText(f, System.Text.Encoding.UTF8)))
                bare.Add(Path.GetFileName(f) + ": " + hit);
        }

        M("N5 no Cyrillic UI string sits outside L.S", bare.Count == 0,
            scanned + " files scanned" + (bare.Count == 0 ? "" : ", first offender: " + bare[0]));
        if (bare.Count > 0)
            foreach (string b in bare) Console.WriteLine("      LOC bare: " + b);

        // N6 СТЕРЕЖЕ САМЕ ПІДКЛЮЧЕННЯ, а не переклад.
        //
        // Мутація LOC-1 (повернути в Main зашите L.Init("uk")) ВИЖИЛА: усі 219 рядків
        // перекладені, L.S працює, LOC-матриця зелена — а застосунок усе одно завжди
        // українською, бо нікому не спало на думку перевірити ОДИН рядок підключення.
        // Це рівно та вада, з якої почалась уся фаза: наявність механізму не доводить,
        // що його ввімкнено. Живу перевірку (запустити застосунок із UiLang=en і подивитись
        // на меню) винесено в Drive; тут — дешевий і точний статичний доказ.
        string appSrc = Path.Combine(root, "src\\App.cs");
        bool wiredToConfig = false, hardcoded = false;
        if (File.Exists(appSrc))
        {
            // КОМЕНТАРІ ВІДКИДАЄМО. Перша версія читала файл цілком і чесно впала на моєму ж
            // коментарі «тут стояло L.Init("uk")» — перевірка мусить дивитись у КОД, інакше
            // вона забороняє описувати в коментарі те, що виправлено.
            string a = StripComments(File.ReadAllText(appSrc, System.Text.Encoding.UTF8));
            wiredToConfig = a.Contains("L.Init(Config.Load().UiLang)");
            hardcoded = a.Contains("L.Init(\"uk\")") || a.Contains("L.Init(\"en\")");
        }
        M("N6 the app takes its language from the setting, not from a literal",
            wiredToConfig && !hardcoded,
            wiredToConfig ? (hardcoded ? "a hardcoded L.Init is still there" : "L.Init(Config.Load().UiLang)")
                          : "L.Init is not wired to the config");

        // N7 — ПОРЯДОК, А НЕ ПЕРЕКЛАД. App.cs:55 робить L.Init(Config.Load().UiLang): конфіг
        // будується РАНІШЕ, ніж застосунок дізнається мову користувача, бо саме з конфіга він її
        // і читає. Значення за замовчуванням, обчислене в полі-ініціалізаторі через L.S, застигає
        // в мові, чинній НА ТОЙ МОМЕНТ, — а це L.Cur = Uk, поки Init не викликано.
        // Наслідок для англомовного користувача на ПЕРШОМУ запуску: інтерфейс англійський, а файли
        // звуться «Знімок_2026-08-05_13-45-00.png». І це не тимчасово: Config.Save() іде після
        // КОЖНОГО знімка й записує український шаблон у settings.ini назавжди.
        // Матриця LOC цього не бачила за побудовою: літерал СТОЇТЬ усередині L.S, правило не
        // порушено — порушено момент виклику.
        L.Init("uk");
        var earlyCfg = new Config();      // так конфіг і будується у справжньому Main
        L.Init("en");                      // ...і лише тепер застосунок дізнається мову
        M("N7 a default built before the language is known still follows the language",
            earlyCfg.Template == "Screenshot_{date}_{time}", earlyCfg.Template);
        L.Init("uk");

        // N7b — ЖИВЕ ПЕРЕМИКАННЯ МОВИ НЕ ЗАМОРОЖУЄ НЕДОТОРКАНИЙ ШАБЛОН (регрес N7 через
        // Параметри). Комбобокс мови застосовує L.Init одразу, а поле шаблону тримало СТАРИЙ
        // дефолт; гейт «це ще дефолт» звіряв його з НОВИМ, не впізнавав — і записував старий
        // дефолт як власний вибір користувача, заморожуючи шаблон у старій мові назавжди.
        // Тут відтворено виправлену послідовність SettingsForm: перед L.Init запам'ятати
        // попередній дефолт і, якщо поле досі тримає САМЕ його, перетекстувати на новий.
        L.Init("uk");
        string tbLive = Config.DefaultTemplate;
        string wasDefault = Config.DefaultTemplate;
        L.Init("en");
        if (tbLive.Trim() == wasDefault) tbLive = Config.DefaultTemplate;
        string collectedLive = (tbLive.Trim().Length == 0 || tbLive.Trim() == Config.DefaultTemplate) ? "" : tbLive.Trim();
        M("N7b a live language switch keeps the untouched default following the language",
            collectedLive == "" && tbLive == "Screenshot_{date}_{time}",
            "collected='" + collectedLive + "' tb=" + tbLive);
        L.Init("uk");

        // N8 — ПРАВИЛО НА КЛАС, а не на випадок. N7 стереже один конкретний шаблон; цей рядок
        // стереже сам ПРИЙОМ, яким той дефект виник: ІНІЦІАЛІЗАТОР ПОЛЯ, що читає стан, який
        // застосунок визначає ПІЗНІШЕ. Ініціалізатор виконується при конструюванні об'єкта, тож
        // значення застигає в тодішній мові (або тодішній палітрі) і більше не змінюється.
        // Глибина дужок відрізняє поле від локальної змінної: у файлі з namespace тіло класу —
        // це рівень 2, усе глибше вже всередині методу й обчислюється при виклику.
        string[] frozenReaders = { "L.S(", "L.Name", "Theme.PageBg", "Theme.CardBg", "Theme.TextPrimary",
                                   "Theme.TextSecondary", "Theme.Accent", "Theme.Body", "Theme.Caption",
                                   "Theme.ControlBg", "Theme.IsDark" };
        var frozen = new System.Collections.Generic.List<string>();
        string srcDirN8 = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)), "src");
        if (Directory.Exists(srcDirN8))
        {
            foreach (string f in Directory.GetFiles(srcDirN8, "*.cs"))
            {
                string body = StripComments(File.ReadAllText(f, System.Text.Encoding.UTF8));
                int fieldDepth = body.Contains("namespace ") ? 2 : 1;
                int depth = 0, staticCtorDepth = -1;
                bool staticCtorOpened = false;
                string[] lines = body.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    // ★ СЛІПА ЗОНА ПЕРШОЇ ВЕРСІЇ N8, знайдена 2026-07-29 у Diag.cs. Вона дивилась
                    //   лише на ініціалізатори ПОЛІВ (глибина fieldDepth), а СТАТИЧНИЙ КОНСТРУКТОР
                    //   — це тіло методу, тобто глибше, і повз правило проходив. Тим часом він
                    //   виконується при першому дотику до типу, а це буває раніше за L.Init: у
                    //   Main стоїть L.Init(Config.Load().UiLang), і Config.Load() на шляху збою
                    //   кличе Diag.Log — тобто ще під час обчислення аргументу.
                    //   Правило, що не бачить половини випадків, дає хибний спокій.
                    if (staticCtorDepth < 0 && depth == fieldDepth &&
                        t.StartsWith("static ") && t.Contains("()") && !t.Contains(";") && !t.Contains("="))
                        staticCtorDepth = depth;
                    bool inStaticCtor = staticCtorDepth >= 0;
                    // МІСТИТЬ ";", а не ЗАКІНЧУЄТЬСЯ на ньому. Перша версія вимагала саме
                    // закінчення — і пропустила справжній випадок у Diag.cs, бо присвоєння там
                    // сидить усередині фігурних дужок того самого рядка:
                    //     if (File.Exists(...)) { _on = true; _source = L.S(...); }
                    // Рядок закінчується на "}", і правило мовчало. Знайдено тим, що мутант
                    // наклали руками й ПЕРЕВІРИЛИ, чи гейт червоніє, а не припустили, що червоніє.
                    if ((depth == fieldDepth || inStaticCtor) && t.Length > 0 && t.Contains("=") && t.Contains(";"))
                        foreach (string w in frozenReaders)
                            if (t.Contains(w))
                            { frozen.Add(Path.GetFileName(f) + ":" + (i + 1) + " " + t); break; }
                    foreach (char ch in lines[i])
                    {
                        if (ch == '{') depth++;
                        else if (ch == '}') depth--;
                    }
                    // Вийшли назад на рівень класу — статичний конструктор скінчився. Без цього
                    // правило вважало б тілом конструктора ВЕСЬ решту файла і червоніло б на
                    // кожному звичайному методі, тобто стало б непридатним за перший же тиждень.
                    //
                    // ...але закривати можна ЛИШЕ після того, як ми туди справді ЗАЙШЛИ. Підпис
                    // `static Diag()` і дужка `{` стоять на РІЗНИХ рядках, тож на рядку підпису
                    // depth ще дорівнює рівню класу — і перша версія закривала область тієї ж
                    // миті, коли її відкрила. Правило виглядало розширеним і лишалося сліпим.
                    if (staticCtorDepth >= 0)
                    {
                        if (depth > staticCtorDepth) staticCtorOpened = true;
                        else if (staticCtorOpened) { staticCtorDepth = -1; staticCtorOpened = false; }
                    }
                }
            }
        }
        // N9 — НАЗВА ПРОДУКТУ ПИШЕТЬСЯ В ОДНОМУ МІСЦІ. Інваріант 1 CLAUDE.md вимагає, щоб
        // айдентика жила в одному джерелі; для ВИДИМОЇ назви з підзаголовком таким джерелом є
        // L.NameFull. До 2026-07-29 той самий рядок був написаний руками ЧОТИРИ рази (App.cs
        // тричі, setup\Setup.cs один). Сьогоднішнє перейменування показало ціну такого дубля
        // прямо: досить проґавити одну копію, і продукт зве себе по-різному в різних вікнах,
        // а виявляється це вже в користувача. Рахуємо ЛІТЕРАЛИ, не згадки: L.NameFull скільки
        // завгодно, сам рядок — рівно один раз, у Loc.cs, де він і оголошений.
        int nameLiterals = 0;
        var nameWhere = new System.Collections.Generic.List<string>();
        foreach (string d in new[] { "src", "setup" })
        {
            string dd = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)), d);
            if (!Directory.Exists(dd)) continue;
            foreach (string f in Directory.GetFiles(dd, "*.cs"))
            {
                if (Path.GetFileName(f) == "Ident.cs") continue;   // технічна айдентика, КОРОНА
                string[] ls = StripComments(File.ReadAllText(f, System.Text.Encoding.UTF8)).Split('\n');
                for (int i = 0; i < ls.Length; i++)
                    if (ls[i].Contains("\"ВАЛЄРА Скріншот — знімки екрана\""))
                    { nameLiterals++; nameWhere.Add(Path.GetFileName(f) + ":" + (i + 1)); }
            }
        }
        M("N9 the visible product name is written in exactly ONE place", nameLiterals == 1,
            nameLiterals == 1 ? "declared once, in " + nameWhere[0]
                              : nameLiterals + " copies: " + string.Join(", ", nameWhere.ToArray()));

        M("N8 no field initialiser freezes the language or the palette at construction time",
            frozen.Count == 0,
            frozen.Count == 0 ? "checked every field in src\\" : frozen[0]);
        if (frozen.Count > 0)
            foreach (string s in frozen) Console.WriteLine("      N8 frozen: " + s);

        Console.WriteLine("LOCALISATION MATRIX: " + _mp + "/" + _mt);
    }

    // Останній ідентифікатор перед "+=" (сам вираз події) і перший після нього (обробник).
    static string LastToken(string s)
    {
        int end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        int start = end;
        while (start > 0)
        {
            char c = s[start - 1];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.') start--;
            else break;
        }
        return s.Substring(start, end - start);
    }

    static string FirstToken(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
        return s.Substring(start, i - start);
    }

    // Файли, звільнені від правила — КОЖЕН із названою причиною. Мовчазний виняток нічим не
    // кращий за мовчазний напівпереклад.
    static string LocExcuse(string name)
    {
        if (name == "Updater.cs") return "frozen crown module, transplanted verbatim from the studio core";
        if (name == "AssemblyInfo.cs") return "assembly metadata: one binary, cannot vary at run time";
        if (name == "Ident.cs") return "technical identity: CROWN, never edited automatically";
        return null;
    }

    // Той самий розбір, що й у сканері розробника: літерал вважається перекладеним, якщо він
    // стоїть УСЕРЕДИНІ списку аргументів виклику L.S( ... ). Перша версія шукала L.S("a","b")
    // регуляркою і НЕ бачила викликів, чиї аргументи склеєні з кількох рядків, — вона оголосила
    // вже перекладений блок неперекладеним, і механічна «правка» за нею зіпсувала робочий код.
    static System.Collections.Generic.List<string> BareCyrillic(string src)
    {
        var found = new System.Collections.Generic.List<string>();
        var parens = new System.Collections.Generic.Stack<bool>();
        int inLs = 0, i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int j = src.IndexOf('\n', i); i = j < 0 ? n : j; continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int j = src.IndexOf("*/", i + 2); i = j < 0 ? n : j + 2; continue;
            }
            if (c == '"')
            {
                int j = i + 1;
                while (j < n && src[j] != '"' && src[j] != '\n')
                {
                    if (src[j] == '\\') j++;
                    j++;
                }
                string lit = src.Substring(i, Math.Min(j + 1, n) - i);
                if (inLs == 0 && HasCyrillic(lit))
                    found.Add(lit.Length > 60 ? lit.Substring(0, 58) + ".." : lit);
                i = j + 1;
                continue;
            }
            if (c == '(')
            {
                bool isLs = i >= 3 && src.Substring(i - 3, 3) == "L.S";
                parens.Push(isLs);
                if (isLs) inLs++;
                i++; continue;
            }
            if (c == ')')
            {
                if (parens.Count > 0 && parens.Pop()) inLs--;
                i++; continue;
            }
            i++;
        }
        return found;
    }

    // Прибрати коментарі, лишивши рядкові літерали цілими (у них теж бувають '//').
    static string StripComments(string src)
    {
        var sb = new StringBuilder();
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int j = src.IndexOf('\n', i);
                i = j < 0 ? n : j;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int j = src.IndexOf("*/", i + 2);
                i = j < 0 ? n : j + 2;
                continue;
            }
            if (c == '"')
            {
                int j = i + 1;
                while (j < n && src[j] != '"' && src[j] != '\n')
                {
                    if (src[j] == '\\') j++;
                    j++;
                }
                j = Math.Min(j + 1, n);
                sb.Append(src, i, j - i);
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    static bool HasCyrillic(string s)
    {
        foreach (char ch in s) if (ch >= 0x0400 && ch <= 0x04FF) return true;
        return false;
    }

    // ACCESSIBILITY MATRIX.
    //
    // ЧОМУ ВОНА ІСНУЄ. До неї в усьому дереві було НУЛЬ згадок AccessibleName / AccessibleRole.
    // Половина інтерфейсу — контроли, які малюють себе САМІ (ToggleSwitch, OfficeButton,
    // ThemedCombo, Card), і для засобів доступності такий контрол за замовчуванням — безіменний
    // прямокутник. Тобто Екранний диктор читав порожнечу там, де людина бачить перемикач.
    // Це не видно ні оком на скріні, ні пруф-гейтом: контраст може бути ідеальний, а елемент
    // не існувати для того, хто на екран не дивиться.
    //
    // Матриця будує СПРАВЖНІ форми (ті самі класи, що бачить користувач) і питає кожен
    // фокусований контрол про його імʼя. Порожнє імʼя = FAIL із переліком винних.
    static void AccessibilityMatrix()
    {
        _mp = 0; _mt = 0;

        // ВИСОКА КОНТРАСТНІСТЬ — чиста функція рішення. Гілку не дістати ні файлом, ні
        // середовищем: її вмикає користувач у налаштуваннях Windows. Без цих чотирьох рядків
        // мутація в ній вижила б назавжди.
        M("A1 high contrast wins over an explicitly chosen light theme",
            Theme.Decide(true, Theme.ThemeMode.Light, false) == Theme.Palette.HighContrast, null);
        M("A2 high contrast wins over an explicitly chosen dark theme",
            Theme.Decide(true, Theme.ThemeMode.Dark, true) == Theme.Palette.HighContrast, null);
        M("A3 without high contrast an explicit choice wins over the system",
            Theme.Decide(false, Theme.ThemeMode.Light, true) == Theme.Palette.Light &&
            Theme.Decide(false, Theme.ThemeMode.Dark, false) == Theme.Palette.Dark, null);
        M("A4 'as in Windows' follows the system",
            Theme.Decide(false, Theme.ThemeMode.System, true) == Theme.Palette.Dark &&
            Theme.Decide(false, Theme.ThemeMode.System, false) == Theme.Palette.Light, null);

        // У режимі високої контрастності палітра мусить прийти З СИСТЕМИ, а не з наших констант:
        // саме системні кольори користувач і обрав як ті, якими здатен читати екран.
        Theme.SetHighContrastForTest(true);
        bool fromSystem = Theme.TextPrimary.ToArgb() == SystemColors.WindowText.ToArgb()
                       && Theme.CardBg.ToArgb() == SystemColors.Window.ToArgb()
                       && Theme.Accent.ToArgb() == SystemColors.Highlight.ToArgb()
                       && Theme.SelectedText.ToArgb() == SystemColors.HighlightText.ToArgb();
        M("A5 high-contrast palette comes from SystemColors, not from our constants", fromSystem,
            "text " + Theme.TextPrimary + " / bg " + Theme.CardBg);
        // Підказки НЕ фарбуються в GrayText: у схемах високої контрастності це колір ВИМКНЕНОГО
        // елемента, законно тьмяний, а наші підказки треба читати.
        M("A6 secondary text is not the disabled colour in high contrast",
            Theme.TextSecondary.ToArgb() != SystemColors.GrayText.ToArgb() ||
            SystemColors.GrayText.ToArgb() == SystemColors.WindowText.ToArgb(), null);
        Theme.SetHighContrastForTest(null);

        // Кожен фокусований контрол справжніх форм мусить мати непорожнє імʼя.
        Theme.Init("light");
        NamesOf("Settings", new SettingsForm(new Config()));
        NamesOf("About", new AboutForm());
        NamesOf("Share", new ShareForm("Znimok_pryklad.png"));

        // Меню трея — окрема поверхня: у дерево контролів воно не входить, тож обхід форм
        // його не бачить у принципі.
        var tm = new TrayMenu(false);
        int unnamedItems = 0;
        foreach (System.Windows.Forms.ToolStripItem it in tm.Strip.Items)
        {
            if (it is System.Windows.Forms.ToolStripSeparator) continue;
            string n = it.AccessibleName;
            if (string.IsNullOrEmpty(n)) n = it.Text;
            if (string.IsNullOrEmpty(n) || n.Trim().Length == 0) unnamedItems++;
        }
        M("A7 every tray menu item has a name", unnamedItems == 0, unnamedItems + " unnamed");
        try { tm.Strip.Dispose(); } catch { }

        // A8/A9 — ІНСТАЛЯТОР. Статичні, і на це є причина, яку варто назвати вголос: пруф-гейт
        // тепер знімає SetupForm, але ставить тему САМ, перед фабрикою. Тобто він доводить, що
        // ФОРМА слухається палітри, і НЕ МОЖЕ довести, що палітру хтось увімкнув — а зламано було
        // саме це: Setup.Main не кликав Theme.Init жодного разу, тож при живому запуску працював
        // `static Theme() { ApplyLight(); }` і перше вікно продукту було світлим ЗАВЖДИ. Знімок
        // такого не бачить за побудовою; один рядок підключення стереже лише читання джерела.
        // Той самий урок, що й у N6: наявність механізму не доводить, що його ввімкнено.
        string accRoot = Path.GetDirectoryName(Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location));
        string setupSrc = Path.Combine(accRoot, "setup\\Setup.cs");
        bool themeWired = false, themedWindow = false;
        if (File.Exists(setupSrc))
        {
            string s = StripComments(File.ReadAllText(setupSrc, System.Text.Encoding.UTF8));
            themeWired = s.Contains("Theme.Init(");
            themedWindow = s.Contains("class SetupForm : ThemedForm");
        }
        M("A8 the installer switches on the theme engine before showing anything", themeWired,
            themeWired ? "Theme.Init present in setup\\Setup.cs"
                       : "setup\\Setup.cs never calls Theme.Init - the installer is light forever");
        M("A9 the installer window follows the theme, title bar included", themedWindow,
            themedWindow ? "SetupForm : ThemedForm"
                         : "SetupForm derives from a bare Form - the title bar stays light in dark mode");

        // A10/A11 — те, що conform НЕ дивиться. Його E1 («жодного MessageBox.Show поза Ui.cs»)
        // сканує лише src\, тож setup\ — артефакт, який показує вікна в найкрихкіший момент
        // (UAC, відмова прав, «встановлено») — з-під правила випадав цілком. Правити conform.ps1
        // не можна: він вендорований і під пломбою, а редагування зробило б ТИХИЙ ФОРК самої
        // перевірки конформності. Тому правило довантажується тут.
        string setupDir = Path.Combine(accRoot, "setup");
        var mbOffenders = new System.Collections.Generic.List<string>();
        var idOffenders = new System.Collections.Generic.List<string>();
        if (Directory.Exists(setupDir))
        {
            string[] setupFiles = Directory.GetFiles(setupDir, "*.cs");
            Array.Sort(setupFiles);
            foreach (string f in setupFiles)
            {
                string fn = Path.GetFileName(f);
                string body = StripComments(File.ReadAllText(f, System.Text.Encoding.UTF8));

                // Uninstall.exe компілюється БЕЗ Ui.cs (окремий маленький бінарник, якому не
                // потрібен ані рушій теми, ані P/Invoke), тож Ui.Msg там фізично недосяжний.
                // Виняток НАЗВАНИЙ і друкується — мовчазний виняток нічим не кращий за порушення.
                if (fn == "Uninstall.cs")
                    Console.WriteLine("      A10 skip Uninstall.cs - built without Ui.cs, so Ui.Msg is out of reach there");
                else if (body.Contains("MessageBox.Show"))
                    mbOffenders.Add(fn);

                // Ident.AppId - це РЕЄСТР, mutex, ім'я процесу й ім'я файла. Людині його не
                // показують ніде, крім двох вікон деінсталятора, де він і показувався:
                // «Видалити ValeraScreenshot з теки…». Видима назва - L.Name, і вона ще й
                // перекладається.
                // ЗАБОРОНА ЗА ЗАМОВЧУВАННЯМ, дозволи перелічені. Перша версія знала лише про
                // GetProcessesByName і чесно впала на DeleteSubKeyTree - теж технічному. Це не
                // хиба перевірки, а її робота: кожен новий контекст мусить бути дописаний сюди
                // РУКОЮ, бо саме тоді хтось на секунду думає «а це точно не для ока?».
                foreach (string ln in body.Split('\n'))
                {
                    if (ln.IndexOf("Ident.AppId", StringComparison.Ordinal) < 0) continue;
                    if (ln.IndexOf("GetProcessesByName", StringComparison.Ordinal) >= 0) continue;  // ім'я процесу
                    if (ln.IndexOf("DeleteSubKeyTree", StringComparison.Ordinal) >= 0) continue;    // ключ реєстру
                    idOffenders.Add(fn + ": " + ln.Trim());
                }
            }
        }
        M("A10 the installer routes dialogs through Ui.Msg like the rest of the product",
            mbOffenders.Count == 0,
            mbOffenders.Count == 0 ? "no direct MessageBox.Show in setup\\ (bar the named exception)"
                                   : "direct MessageBox.Show in: " + string.Join(", ", mbOffenders.ToArray()));
        M("A11 the package never shows the technical identity to a person",
            idOffenders.Count == 0,
            idOffenders.Count == 0 ? "Ident.AppId stays in technical contexts; the eye gets L.Name"
                                   : "visible Ident.AppId - " + idOffenders[0]);

        Console.WriteLine("ACCESSIBILITY MATRIX: " + _mp + "/" + _mt);
    }

    // Ім'я для засобів доступності: явне AccessibleName, інакше власний Text. Порожньо — дефект.
    static void NamesOf(string label, System.Windows.Forms.Form f)
    {
        var unnamed = new System.Collections.Generic.List<string>();
        int focusable = 0;
        CollectUnnamed(f, ref focusable, unnamed);
        M("A8." + label + " every focusable control announces a name",
            unnamed.Count == 0,
            focusable + " focusable" + (unnamed.Count == 0 ? "" : ", unnamed: " + string.Join(", ", unnamed.ToArray())));
        try { f.Dispose(); } catch { }
    }

    static void CollectUnnamed(System.Windows.Forms.Control root, ref int focusable,
                               System.Collections.Generic.List<string> unnamed)
    {
        foreach (System.Windows.Forms.Control c in root.Controls)
        {
            // Клавіатурний обхід ходить саме по TabStop-контролах — рівно вони й мусять
            // назватися. Мітки читаються разом із полем, до якого належать.
            if (c.TabStop)
            {
                focusable++;
                string name = c.AccessibleName;
                if (string.IsNullOrEmpty(name)) name = c.Text;
                if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                    unnamed.Add(c.GetType().Name + "@" + c.Left + "," + c.Top);
            }
            CollectUnnamed(c, ref focusable, unnamed);
        }
    }
}
