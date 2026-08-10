using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ValeraScreenshot;

// ProofGate — ВОРОТА ВІЗУАЛЬНИХ ДОКАЗІВ (STD-PROOF-01, STD-GATE-01).
//
// ЧОМУ ВІН ІСНУЄ. До нього рендер-пруфи робили ShotTest.exe і ThemeProof.exe — обидва
// ЗБИРАЛИСЬ у build.ps1 -All і НЕ ЗАПУСКАЛИСЬ ЖОДНИМ ГЕЙТОМ. Наслідок, виміряний на диску
// 2026-07-29: _preview_about_light.png був БАЙТ-У-БАЙТ рівний _preview_about_dark.png
// (sha256 f2036edf… в обох), _preview_share_* — так само, а _preview_settings_light.png ніс
// темні картки з темним текстом на темному тлі, тобто був нечитний. Повторний запуск того
// самого exe дав інші, правильні файли. Отже пруфи були не лише хибні, а й НЕДЕТЕРМІНОВАНІ,
// і ніщо не могло цього побачити: артефакт, який ніхто не міряє, гниє мовчки.
//
// ЩО ВІН РОБИТЬ ІНАКШЕ:
//  1. ВИДАЛЯЄ пруф перед прогоном і вимагає його появи після. У ShotTest збій рендера
//     ковтався (catch -> Console.WriteLine) і лишав СТАРИЙ файл — а старий файл на диску
//     читається оком як успіх. Тепер це червоний гейт.
//  2. Знімає з ЕКРАНА (CopyFromScreen), а не DrawToBitmap: заголовок вікна малює DWM, тож
//     «біла кромка» темної теми на DrawToBitmap-скрінах невидима В ПРИНЦИПІ.
//  3. МІРЯЄ, а не показує. Обходить дерево контролів живої форми, переводить кожен у
//     координати знімка й рахує контраст текст/тло за WCAG 2.1 (AA = 4.5:1), належність тла
//     до чинної палітри і «чужі» суцільні блоки не з тієї теми. Око власника лишається
//     фінальною інстанцією (STD-PROOF-01), але воно більше не єдина інстанція.
//
// Шляхи виводяться з розташування exe (тека переносима) — ShotTest/ThemeProof хардкодили
// D:\ValeraScreenshot і зламалися б у будь-якій іншій теці.
internal static class ProofGate
{
    private static int _pass, _fail;
    private static readonly StringBuilder Rep = new StringBuilder();
    private static string _root;
    private static string _imgDir;

    // Поріг WCAG 2.1: 4.5:1 для звичайного тексту (1.4.3), 3:1 для великого тексту й для
    // нетекстових елементів інтерфейсу (1.4.11).
    private const double AaText = 4.5;
    private const double AaLarge = 3.0;

    // ЗНІМОК ВІКНА БЕЗ ЗАХОПЛЕННЯ ЕКРАНА.
    //
    // Перша версія піднімала вікно TopMost, активувала його й знімала CopyFromScreen — тобто на
    // час прогону забирала екран у власника. Наказ власника: «тести не займай екран».
    // PrintWindow просить САМЕ ВІКНО намалювати себе у наш контекст: воно може лишатися за
    // чужими вікнами, не активним і навіть за межами видимої області. PW_RENDERFULLCONTENT
    // (0x2, Win8.1+) домальовує вміст, який малює DWM, — інакше сучасні рамки виходять порожніми.
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ФОКУС ВЛАСНИКА НЕДОТОРКАННИЙ.
    //
    // Form.Show() за замовчуванням АКТИВУЄ вікно — і робить це навіть тоді, коли вікно стоїть
    // за межами видимої області. Наслідок власник відчув одразу: набір тексту в іншій програмі
    // переривався на кожному знімку. Вікна поза екраном — цього мало; вікно ще й не має права
    // ставати активним. WS_EX_NOACTIVATE вішається на хендл ДО показу: створюємо вікно
    // (звернення до .Handle), знімаємо з нього право активуватись, і лише тоді показуємо.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static void ShowWithoutStealingFocus(Form f)
    {
        IntPtr h = f.Handle;   // вікно створене, але ще не показане
        try { SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE); }
        catch { }
        f.Show();
    }

    private static bool PrintWindowTo(Form f, Bitmap bmp)
    {
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { return PrintWindow(f.Handle, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    // РЕНДЕР — ЦЕ ЗАПИТ, А НЕ ГАРАНТІЯ.
    //
    // І PrintWindow, і DrawToBitmap можуть повернути порожній або напівнамальований кадр, поки
    // вікно ще не скомпоноване, — і роблять це тим охочіше, чим більше зайнята машина. Виміряно:
    // той самий гейт із того самого дерева давав 52/0 запущений напряму і 48/4 запущений як
    // дочірній процес; окремо ловилось меню, повністю чорне («#000000 на #000000» по всіх дев'яти
    // пунктах) у одному прогоні з двох.
    //
    // Фіксована пауза цього не лікує — вона лише пересуває межу. Тому кадр ПЕРЕВІРЯЄТЬСЯ: у
    // справжньому вікні неминуче більше кількох кольорів. Не вийшло — чекаємо довше й просимо
    // ще раз, до п'яти разів. Якщо не вийшло й тоді — це справжня поломка, і вона чесно червона.
    private static bool RenderStable(Func<Bitmap, bool> render, Bitmap target, string name, Rectangle roi)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            bool asked = false;
            try { asked = render(target); }
            catch (Exception ex) { Line("   render attempt " + attempt + " threw: " + ex.Message); }
            if (asked && !IsDegenerate(target, roi))
            {
                if (attempt > 1) Line("   (rendered on attempt " + attempt + ")");
                return true;
            }
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25 * attempt); }
        }
        Fail(name + ": the window would not render after 5 attempts (blank or flat frame)");
        return false;
    }

    // «Виродженим» вважаємо кадр, у якому менше дев'яти різних кольорів: жодна наша поверхня
    // не буває такою — навіть найпростіше меню має тло, рамку, текст і згладжування.
    //
    // ДИВИМОСЬ САМЕ В КЛІЄНТСЬКУ ОБЛАСТЬ, а не в увесь кадр. Виміряно: PrintWindow здатний
    // намалювати рамку й заголовок і лишити клієнтську частину суцільно БІЛОЮ — кольорів у
    // такому кадрі більше дев'яти, тож перевірка «по всьому вікну» його пропускала, і пруф
    // рапортував «#FFFFFF на #FFFFFF» по всіх мітках вікна «Про програму».
    private static bool IsDegenerate(Bitmap b, Rectangle roi)
    {
        Rectangle r = Rectangle.Intersect(roi, new Rectangle(0, 0, b.Width, b.Height));
        if (r.Width < 4 || r.Height < 4) r = new Rectangle(0, 0, b.Width, b.Height);
        var seen = new Dictionary<int, bool>();
        for (int y = r.Top; y < r.Bottom; y += 3)
            for (int x = r.Left; x < r.Right; x += 3)
            {
                seen[b.GetPixel(x, y).ToArgb()] = true;
                if (seen.Count > 8) return false;
            }
        return true;
    }

    [STAThread]
    private static int Main()
    {
        Native.EnsureDpiAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _root = Path.GetDirectoryName(Path.GetDirectoryName(Application.ExecutablePath));
        _imgDir = Path.Combine(_root, "docs\\img");

        // ВЛАСНИЙ вимикач, не спільний із Drive. Перша версія читала VALERASCREENSHOT_SKIP_DRIVE — і
        // мутаційний прогін, який глушить Drive заради швидкості, разом із ним глушив ВЕСЬ
        // візуальний гейт. Наслідок виміряно: усі чотири UI-мутації (темна картка у світлій темі,
        // рамка 1.53:1, біле лице комбо, світлий скролбар) були рапортовані SURVIVED — тобто
        // «гейт цього не стереже», хоча він стереже. Один вимикач на два різні критерії — це
        // рівно той клас збою, проти якого написаний STD-GATE-10.
        // Скіп ДРУКУЄТЬСЯ: критерій, що зникає тихо, не критерій.
        if (Environment.GetEnvironmentVariable("VALERASCREENSHOT_SKIP_PROOF") == "1")
        {
            Console.WriteLine("PROOF GATE SKIPPED (VALERASCREENSHOT_SKIP_PROOF=1) - VISUAL promises are UNVERIFIED in this run");
            return 0;
        }

        Line("=== PROOF GATE: ValeraScreenshot " + Ver.Number + " ===");
        Line("root: " + _root);
        Line("");

        try { Directory.CreateDirectory(_imgDir); }
        catch (Exception ex) { Console.WriteLine("cannot create docs\\img: " + ex.Message); return 1; }

        // Мишу НЕ чіпаємо: вікна тепер за межами екрана, hover на них не потрапляє, а рухати
        // курсор власника заради пруфу — це і є «займати екран».

        var shots = new List<Shot>();

        foreach (string mode in new[] { "light", "dark" })
        {
            Theme.Init(mode);

            // Форма передається ФАБРИКОЮ, а не готовим екземпляром: аргумент обчислюється ДО
            // виклику, тож готова форма конструювалась би в темі ПОПЕРЕДНЬОГО знімка.
            shots.Add(Capture("settings_" + mode, mode, "uk", delegate { return (Form)new SettingsForm(new Config()); }, null));
            shots.Add(Capture("about_" + mode, mode, "uk", delegate { return (Form)new AboutForm(); }, null));
            shots.Add(Capture("share_" + mode, mode, "uk",
                delegate { return (Form)new ShareForm(Path.Combine(_root, "Screenshots\\Znimok_pryklad.png")); }, null));

            // РОЗКРИТИЙ СПИСОК. Вікно списку комбобокса малює САМА СИСТЕМА — наш OnDrawItem
            // фарбує лише рядки всередині. Це поверхня, яку користувач бачить щоразу, коли
            // міняє формат файла, і жоден знімок закритої форми її не показує.
            shots.Add(Capture("combo_" + mode, mode, "uk", delegate { return (Form)new SettingsForm(new Config()); }, OpenFirstCombo));
            shots.Add(CaptureMenu("menu_" + mode, mode, "uk"));

            // ІНСТАЛЯТОР. Перше вікно, яке людина бачить від цього продукту, — і єдина поверхня,
            // якої не знімав ЖОДЕН пруф (STD-UI-07 стояв у STRUCTURE.md як відкритий борг).
            // Знімати його стало можливо тільки тепер: він живе в окремому бінарнику, і щоб
            // дістати SetupForm, у збірку гейта довелося взяти setup\Setup.cs.
            // Те, що знайшлося тієї ж миті: Setup.Main НЕ кликав Theme.Init, а Theme має
            // `static Theme() { ApplyLight(); }` — тобто інсталятор був СВІТЛИЙ ЗАВЖДИ, і в
            // темній Windows, і у високій контрастності. Плюс SetupForm успадковувалась від
            // голої Form, тож смуга заголовка лишалась світлою навіть якби палітру полагодили.
            shots.Add(Capture("setup_" + mode, mode, "uk", delegate { return (Form)new SetupForm(); }, null));
        }

        // АНГЛІЙСЬКА. Не косметика: англійський рядок систематично довший за український
        // («Взяти» -> «Capture the whole screen»), тож саме тут вилазять обрізані підписи й
        // текст, що не вміщається в кнопку. Гейт міряє ті самі контрасти, а око власника
        // дивиться на docs\img\*_en.png.
        shots.Add(Capture("settings_en", "light", "en", delegate { return (Form)new SettingsForm(new Config()); }, null));
        shots.Add(Capture("about_en", "light", "en", delegate { return (Form)new AboutForm(); }, null));
        shots.Add(Capture("share_en", "light", "en",
            delegate { return (Form)new ShareForm(Path.Combine(_root, "Screenshots\\Sample.png")); }, null));
        shots.Add(CaptureMenu("menu_en", "light", "en"));
        // Англійський інсталятор — найтісніше місце в продукті: «Звільнити PrtScr від Snipping
        // Tool» проти «Free PrtScr from Snipping Tool» в одній і тій самій картці фіксованої
        // ширини, і жодного переносу рядка в CheckBox.
        shots.Add(Capture("setup_en", "light", "en", delegate { return (Form)new SetupForm(); }, null));

        // ВИСОКА КОНТРАСТНІСТЬ. Увімкнути її в самій Windows заради знімка не можна — це змінило б
        // робочий стіл власника, — тож палітру перемикає тест-шов. Перевірки «тло з тієї теми» і
        // «чужий блок» тут не застосовні за визначенням: кольори задає СИСТЕМА, і чорна рамка на
        // білому в цьому режимі — не дефект, а правильна поведінка.
        // Натомість перевіряємо те, що тут єдино й важить: чи не лишився в кадрі НАШ фірмовий
        // колір. Якщо десь у коді синій #0F6CBD зашитий повз палітру, у високій контрастності він
        // видасть себе одразу — і саме тоді, коли ламає гарантію контрасту, яку дала система.
        Theme.SetHighContrastForTest(true);
        shots.Add(Capture("highcontrast", "hc", "uk", delegate { return (Form)new SettingsForm(new Config()); }, null));
        shots.Add(CaptureMenu("menu_hc", "hc", "uk"));
        // Для інсталятора висока контрастність — не косметика, а найгостріший випадок: людина,
        // яка її ввімкнула, інакше просто не бачить дрібного сірого підпису, а саме з такого
        // підпису складається картка «Опції». Доки Theme.Init тут не викликався, вона діставала
        // нашу пастельну палітру замість системної.
        shots.Add(Capture("setup_hc", "hc", "uk", delegate { return (Form)new SetupForm(); }, null));
        Theme.SetHighContrastForTest(null);

        // Оверлей — ОКО-ONLY: він малює поверх ЧУЖОГО кадру (робочий стіл користувача), тож
        // «належність тла до палітри» для нього не має сенсу за визначенням. Міряємо лише те,
        // що можна: файл є, він не порожній, і він змінюється між темами.
        Theme.Init("light");
        shots.Add(CaptureOverlay("overlay_light"));
        Theme.Init("dark");
        shots.Add(CaptureOverlay("overlay_dark"));

        // ПЕРЕВІРКА, ЯКА Й БУЛА ПРОҐАВЛЕНА: світлий і темний варіанти НЕ МОЖУТЬ бути одним
        // файлом. Саме цю рівність (sha256 f2036edf… в обох) не побачив жоден гейт.
        // ★ ТА САМА ПЕРЕВІРКА, АЛЕ В ОСІ МОВИ. Без неї «англійські» пруфи about і menu лежали
        //   на диску БАЙТ-У-БАЙТ українськими, і ніщо цього не бачило: гейт порівнював лише
        //   світле з темним. Одна ось перевірена — не значить перевірені всі.
        Line("");
        Line("-- UKRAINIAN vs ENGLISH --");
        foreach (string form in new[] { "settings", "about", "share", "menu", "setup" })
        {
            Shot uk = Find(shots, form + "_light"), en = Find(shots, form + "_en");
            if (uk == null || en == null) { Fail(form + ": missing uk or en shot"); continue; }
            if (uk.Sha == en.Sha)
                Fail(form + ": the English proof is the SAME FILE as the Ukrainian one (sha " +
                     uk.Sha.Substring(0, 8) + ") - the language did not switch");
            else
                Pass(form + ": uk != en (" + uk.Sha.Substring(0, 8) + " vs " + en.Sha.Substring(0, 8) + ")");
        }

        Line("");
        Line("-- LIGHT vs DARK --");
        // ★ "setup" ДОДАНО СЮДИ Й ДО ОСІ МОВИ ОКРЕМИМ РУХОМ. Знімок сам собою нічого не стереже:
        //   обидва списки перелічені ПОІМЕННО, тож нова поверхня з'являється в кадрі, але лишається
        //   поза порівнянням, доки її туди не вписали. Рівно так світлий пруф і був байт-копією
        //   темного — файл існував, його ніхто ні з чим не звіряв.
        foreach (string form in new[] { "settings", "about", "share", "combo", "menu", "overlay", "setup" })
        {
            Shot l = Find(shots, form + "_light"), d = Find(shots, form + "_dark");
            if (l == null || d == null) { Fail(form + ": missing light or dark shot"); continue; }
            if (l.Sha == d.Sha) Fail(form + ": light and dark proofs are the SAME FILE (sha " + l.Sha.Substring(0, 8) + ")");
            else Pass(form + ": light != dark (" + l.Sha.Substring(0, 8) + " vs " + d.Sha.Substring(0, 8) + ")");

            if (l.MeanLum <= d.MeanLum)
                Fail(form + ": light proof is not brighter than dark (" +
                     l.MeanLum.ToString("F3") + " <= " + d.MeanLum.ToString("F3") + ")");
            else
                Pass(form + ": mean luminance light " + l.MeanLum.ToString("F3") +
                     " > dark " + d.MeanLum.ToString("F3"));
        }

        Line("");
        Line("=== PROOF GATE: " + _pass + " PASS / " + _fail + " FAIL ===");
        if (_fail == 0) Line("All visual promises are MEASURED. The owner's eye remains the final check (STD-PROOF-01).");

        string report = Path.Combine(_root, "_proof_report.txt");
        try { File.WriteAllText(report, Rep.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { Console.WriteLine("report write failed: " + ex.Message); }

        Console.WriteLine(Rep.ToString());
        Console.WriteLine(_fail == 0 ? "PROOF GATE: ALL VISUAL CHECKS PASSED" : "PROOF GATE: FAILED");
        return _fail == 0 ? 0 : 1;
    }

    private sealed class Shot
    {
        public string Name;
        public string Sha;
        public double MeanLum;
    }

    private static Shot Find(List<Shot> l, string name)
    {
        foreach (Shot s in l) if (s.Name == name) return s;
        return null;
    }

    // ---------------------------------------------------------------- захват + вимір

    // Розкрити перший комбобокс форми (у Параметрах це «Формат»).
    private static void OpenFirstCombo(Form f)
    {
        ComboBox cb = FindCombo(f);
        if (cb == null) { Fail("combo proof: no ComboBox found in the form"); return; }
        cb.Focus();
        cb.DroppedDown = true;
    }

    private static ComboBox FindCombo(Control root)
    {
        foreach (Control c in root.Controls)
        {
            var cb = c as ComboBox;
            if (cb != null) return cb;
            ComboBox deep = FindCombo(c);
            if (deep != null) return deep;
        }
        return null;
    }

    private static Shot Capture(string name, string mode, string lang, Func<Form> factory, Action<Form> beforeShot)
    {
        var shot = new Shot { Name = name };
        string path = Path.Combine(_root, "_preview_" + name + ".png");

        // ВИДАЛИТИ ПЕРЕД. Без цього збій рендера лишає вчорашній файл, і пруф бреше мовчки.
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Fail(name + ": cannot delete stale proof - " + ex.Message); }

        Line("");
        Line("-- " + name + " --");

        // ТЕМА І МОВА СТАВЛЯТЬСЯ ПЕРЕД КОЖНИМ ЗНІМКОМ, не один раз на прохід.
        // Форма Параметрів законно повертає тему конфіга, коли її закрили НЕ кнопкою «Зберегти»
        // (живий перегляд мусить скасовуватись разом із рештою). Прохід, який ставив тему лише
        // на початку, після першої ж форми йшов у темі з конфіга — і світлі пруфи знову стали
        // байт-копіями темних. Гейт це впіймав; саме для цього він і є.
        Theme.Init(mode);
        // ТА САМА ПРИЧИНА, ЩО Й ДЛЯ ТЕМИ, І ТОЙ САМИЙ НАСЛІДОК. Форма Параметрів законно
        // відкочує МОВУ, коли її закрили не кнопкою «Зберегти». Прохід, який ставив мову лише
        // на початку, після першої ж форми йшов українською — і «англійські» пруфи about і menu
        // виявились БАЙТ-У-БАЙТ українськими. Це буквально той самий дефект, з якого почався
        // весь цикл, лише в іншій осі.
        L.Init(lang);
        Form f = factory();

        Bitmap bmp = null;
        Point origin = Point.Empty;
        try
        {
            // Вікно НЕ активується й НЕ піднімається поверх чужих: PrintWindow малює його
            // на вимогу. Ставимо його за межами робочої області, щоб воно навіть не блимнуло
            // на екрані власника.
            f.StartPosition = FormStartPosition.Manual;
            f.Location = OffscreenSpot();
            f.ShowInTaskbar = false;
            ShowWithoutStealingFocus(f);
            Settle();
            // ДЕТЕРМІНОВАНИЙ СТАН (2026-08-08). Залежно від того, кому дістався фокус при показі,
            // TextBox міг зніматися з ВИДІЛЕНИМ текстом — білим на системному Highlight — і гейт
            // міряв контраст стану виділення замість стану спокою: 4,499:1 проти 15,5:1, червоний
            // через волосину і невідтворюваний від прогону до прогону. Пруф міряє те, що бачить
            // користувач у спокої; кольори виділення — пара ОС, не палітра продукту.
            Calm(f);
            if (beforeShot != null) { beforeShot(f); Settle(); }

            // Знімаємо РІВНО вікно, разом із заголовком: DWM малює його поза клієнтською
            // областю, і саме там живе «біла кромка», якої DrawToBitmap не бачить взагалі.
            origin = new Point(f.Left, f.Top);
            bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format24bppRgb);
            Point ctl = f.PointToScreen(Point.Empty);
            var clientRoi = new Rectangle(ctl.X - origin.X, ctl.Y - origin.Y,
                                          f.ClientSize.Width, f.ClientSize.Height);
            RenderStable(delegate(Bitmap t) { return PrintWindowTo(f, t); }, bmp, name, clientRoi);

            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Fail(name + ": capture failed - " + ex.Message);
            try { f.Close(); } catch { }
            return shot;
        }

        // ВИМАГАТИ ПІСЛЯ. Файл мусить існувати і бути осмисленого розміру.
        if (!File.Exists(path)) { Fail(name + ": proof file not written"); }
        else
        {
            var fi = new FileInfo(path);
            if (fi.Length < 2000) Fail(name + ": proof file suspiciously small (" + fi.Length + " b)");
            else Pass(name + ": written, " + fi.Length.ToString("N0") + " b");
            shot.Sha = Sha256(path);
        }

        bool hc = mode == "hc";
        bool dark = mode == "dark";
        shot.MeanLum = MeanLuminance(bmp);
        Line("   mean luminance: " + shot.MeanLum.ToString("F3"));

        // 1. Смуга заголовка. Це і є та сама «біла кромка»: Theme.IsDark може бути true, а
        //    заголовок системний і білий. Пробу беремо посередині ширини — там ні іконки, ні
        //    кнопок керування, лише тло смуги.
        if (!hc) CheckTitleBar(name, bmp, dark);

        // 2. Кожен видимий контрол — контраст і належність тла до палітри.
        var clientTopLeft = f.PointToScreen(Point.Empty);
        var clientRect = new Rectangle(clientTopLeft.X - origin.X, clientTopLeft.Y - origin.Y,
                                       f.ClientSize.Width, f.ClientSize.Height);
        int checkedControls = 0;
        var skipRects = new List<Rectangle>();
        WalkControls(f, f, bmp, origin, dark, name, ref checkedControls, skipRects, clientRect, hc);
        Line("   controls measured: " + checkedControls);
        if (checkedControls < 8) Fail(name + ": only " + checkedControls + " controls measured - walk is not reaching the tree");
        else Pass(name + ": " + checkedControls + " controls measured");

        // 3. «Чужий блок»: суцільна ділянка не з тієї теми (саме так виглядала темна картка у
        //    світлому пруфі). Текстові гліфи в цей фільтр не потрапляють — вони дрібні й тонкі.
        //    У високій контрастності натомість шукаємо НАШ фірмовий колір: його там бути не може.
        if (hc) CheckNoBrandColour(name, bmp, clientRect);
        else CheckAlienBlocks(name, bmp, clientRect, dark, skipRects);

        // Кураторська копія для репозиторію (docs\img\ відстежується git-ом, а _preview_*.png ні).
        try { File.Copy(path, Path.Combine(_imgDir, name + ".png"), true); }
        catch (Exception ex) { Fail(name + ": cannot copy into docs\\img - " + ex.Message); }

        try { f.Close(); } catch { }
        if (bmp != null) bmp.Dispose();
        return shot;
    }

    private static void Settle()
    {
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(25); }
    }

    // Місце ПОЗА видимою областю всіх моніторів. Вікно там існує, має хендл і малює себе на
    // вимогу PrintWindow, але власник його не бачить і воно нічого не перекриває.
    private static Point OffscreenSpot()
    {
        Rectangle v = SystemInformation.VirtualScreen;
        return new Point(v.Right + 200, v.Top + 100);
    }

    // МЕНЮ ТРЕЯ. Поверхня, з якої починається все, крім гарячих клавіш, — і жоден пруф ніколи
    // її не знімав: меню не форма, у дерево контролів воно не входить, тож обхід дерева його
    // не бачить у принципі. Знімається САМЕ те меню, що й у застосунку (спільний клас TrayMenu),
    // а не схожа на нього копія.
    private static Shot CaptureMenu(string name, string mode, string lang)
    {
        var shot = new Shot { Name = name };
        string path = Path.Combine(_root, "_preview_" + name + ".png");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        Line("");
        Line("-- " + name + " --");
        Theme.Init(mode);

        Point off = OffscreenSpot();
        var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = off,
            Size = new Size(10, 10),
            ShowInTaskbar = false,
            BackColor = Theme.PageBg
        };
        var menu = new TrayMenu(true);   // діагностика УВІМКНЕНА: інакше галочка не малюється й не міряється
        Bitmap bmp = null;
        try
        {
            ShowWithoutStealingFocus(host);
            Settle();

            // МЕНЮ НЕ ПОКАЗУЄМО ВЗАГАЛІ. ToolStripDropDown.Show() вводить процес у РЕЖИМ МЕНЮ:
            // система віддає йому ввід і забирає його в активного вікна — саме через це набір
            // тексту в іншій програмі переривався. Для знімка показ не потрібен: досить створити
            // вікно й порахувати розкладку, а далі DrawToBitmap.
            var strip = menu.Strip;
            strip.AutoClose = false;
            IntPtr sh = strip.Handle;
            try { SetWindowLong(sh, GWL_EXSTYLE, GetWindowLong(sh, GWL_EXSTYLE) | WS_EX_NOACTIVATE); } catch { }
            strip.PerformLayout();
            strip.Size = strip.PreferredSize;
            Settle();

            Rectangle mr = new Rectangle(0, 0, strip.Width, strip.Height);
            bmp = new Bitmap(mr.Width, mr.Height, PixelFormat.Format24bppRgb);
            // ToolStripDropDown — шарувате вікно, і PrintWindow віддає для нього то кадр, то
            // суцільний чорний. DrawToBitmap надійніший, а втрачати тут нема чого: у випадного
            // меню немає рамки, яку малює DWM. Але й він не гарантія — тому через RenderStable.
            var box = new Rectangle(0, 0, mr.Width, mr.Height);
            RenderStable(delegate(Bitmap t) { strip.DrawToBitmap(t, box); return true; }, bmp, name, box);
            bmp.Save(path, ImageFormat.Png);

            bool hcMenu = mode == "hc";
            bool dark = mode == "dark";
            shot.MeanLum = MeanLuminance(bmp);
            Line("   mean luminance: " + shot.MeanLum.ToString("F3"));

            // Меню — суцільна поверхня теми: тут «чужий блок» ловить білу підкладку прямо.
            var whole = new Rectangle(0, 0, bmp.Width, bmp.Height);
            if (hcMenu) CheckNoBrandColour(name, bmp, whole);
            else CheckAlienBlocks(name, bmp, whole, dark, new List<Rectangle>());

            // Контраст кожного пункту, включно з ВИМКНЕНИМ заголовком: системний рендерер
            // малює disabled-текст сірим зі СВІТЛОЇ палітри, і на темному тлі його не видно.
            int measured = 0;
            foreach (ToolStripItem it in menu.Strip.Items)
            {
                if (it is ToolStripSeparator || string.IsNullOrEmpty(it.Text)) continue;
                Rectangle r = it.Bounds;
                r = new Rectangle(r.X, r.Y, r.Width, r.Height);
                if (!Inside(bmp, r) || r.Width < 8 || r.Height < 8) continue;
                Color tbg, tfg; double tk;
                AnalyzeText(bmp, r, out tbg, out tfg, out tk);
                measured++;
                string what = "menu item '" + Short(it.Text) + "'" + (it.Enabled ? "" : " (disabled)");
                if (tk + 0.001 < AaText)
                    Fail(name + ": " + what + " TEXT contrast " + tk.ToString("F2") + ":1 < 4.5:1 (fg " +
                         Hex(tfg) + " on bg " + Hex(tbg) + ")");
                else
                    Line("   ok  " + what + "  " + tk.ToString("F2") + ":1  " + Hex(tfg) + " on " + Hex(tbg));
            }
            if (measured < 6) Fail(name + ": only " + measured + " menu items measured");
            else Pass(name + ": " + measured + " menu items measured");
        }
        catch (Exception ex)
        {
            Fail(name + ": capture failed - " + ex.Message);
        }
        finally
        {
            try { menu.Strip.Close(); } catch { }
            try { menu.Strip.Dispose(); } catch { }
            try { host.Close(); } catch { }
        }

        if (!File.Exists(path)) { Fail(name + ": proof file not written"); return shot; }
        shot.Sha = Sha256(path);
        Pass(name + ": written, " + new FileInfo(path).Length.ToString("N0") + " b");
        try { File.Copy(path, Path.Combine(_imgDir, name + ".png"), true); }
        catch (Exception ex) { Fail(name + ": cannot copy into docs\\img - " + ex.Message); }
        if (bmp != null) bmp.Dispose();
        return shot;
    }

    // ------------------------------------------------------------------- перевірки

    private static void CheckTitleBar(string name, Bitmap bmp, bool dark)
    {
        // Смуга заголовка йде одразу під зовнішньою рамкою вікна. Беремо тонкий зріз по центру:
        // там немає ні іконки, ні кнопок керування — лише тло смуги.
        int y0 = 6, y1 = 16;
        int x0 = bmp.Width / 2 - 40, x1 = bmp.Width / 2 + 40;
        if (y1 >= bmp.Height || x1 >= bmp.Width) { Fail(name + ": bitmap too small for a title-bar probe"); return; }

        double sum = 0; int n = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) { sum += Lum(bmp.GetPixel(x, y)); n++; }
        double lum = sum / n;

        bool ok = dark ? lum < 0.30 : lum > 0.60;
        string msg = name + ": title bar luminance " + lum.ToString("F3") + (dark ? " (dark expected < 0.30)" : " (light expected > 0.60)");
        if (ok) Pass(msg); else Fail(msg + "  <-- the white edge is back");
    }

    private static void WalkControls(Control root, Form form, Bitmap bmp, Point origin, bool dark,
                                     string name, ref int measured, List<Rectangle> skipRects, Rectangle clip,
                                     bool highContrast)
    {
        foreach (Control c in root.Controls)
        {
            if (!c.Visible) { continue; }

            Rectangle r;
            try
            {
                // ВІКОННИЙ прямокутник, не клієнтський. PointToScreen віддає початок КЛІЄНТСЬКОЇ
                // області: у контрола з рамкою (комбобокс, поле) вона зсунута всередину, тож
                // прямокутник «клієнтський початок + віконний розмір» промахувався по рамці —
                // саме через це рамка комбо мірялась як 1.00:1, тобто «її немає».
                r = c.Parent != null
                    ? c.Parent.RectangleToScreen(c.Bounds)
                    : new Rectangle(c.PointToScreen(Point.Empty), c.Size);
                r = new Rectangle(r.X - origin.X, r.Y - origin.Y, r.Width, r.Height);
            }
            catch { continue; }

            // Зображення — це ДАНІ (іконка, портрет автора), не поверхня теми. Їх виключаємо
            // і з контрасту, і зі сканера «чужих блоків»: інакше кожен темний логотип у світлій
            // темі був би дефектом, а дефектом він не є.
            if (c is PictureBox) { skipRects.Add(r); continue; }

            // ЛИШЕ ПОВНІСТЮ ВИДИМІ КОНТРОЛИ. Панель Параметрів має AutoScroll, тож секція
            // «Система» лежить нижче за видиму частину — а її екранний прямокутник при цьому
            // накладається на ПІДВАЛ із синьою кнопкою «Зберегти». Перша версія міряла ті
            // пікселі як текст мітки й рапортувала 1.16:1 на цілком читабельному написі.
            // Контрол, який користувач зараз не бачить, не має ні проходити, ні валити пруф.
            bool fullyVisible = clip.Contains(r);
            if (fullyVisible && Inside(bmp, r) && r.Width >= 8 && r.Height >= 8)
            {
                if (MeasureControl(c, bmp, r, dark, name, highContrast)) measured++;
            }

            Rectangle childClip = clip;
            try
            {
                Rectangle cc = c.RectangleToScreen(c.ClientRectangle);
                cc = new Rectangle(cc.X - origin.X, cc.Y - origin.Y, cc.Width, cc.Height);
                childClip = Rectangle.Intersect(clip, cc);
            }
            catch { }
            if (childClip.Width > 0 && childClip.Height > 0)
                WalkControls(c, form, bmp, origin, dark, name, ref measured, skipRects, childClip, highContrast);
        }
    }

    // true, якщо контрол справді виміряно (а не пропущено як декоративний).
    private static bool MeasureControl(Control c, Bitmap bmp, Rectangle r, bool dark, string name, bool highContrast)
    {
        // Роздільники (Panel висотою 1 px), порожні панелі й підкладки — декор, не елемент
        // інтерфейсу за WCAG 1.4.11. Міряти їх контраст означало б вимагати 3:1 від лінії,
        // яка навмисно ледь помітна.
        bool isPanelLike = (c is Panel || c is Card) && !(c is ToggleSwitch);
        bool hasText = !string.IsNullOrEmpty(c.Text) && c.Text.Trim().Length > 0;
        // Внутрішні частини NumericUpDown (UpDownEdit, UpDownButtons) власної межі не мають —
        // її малює батько. Міряти в них рамку означало б вимагати 3:1 від того, чого немає.
        bool insideNumeric = c.Parent is NumericUpDown;
        bool isField = !insideNumeric && (c is ComboBox || c is TextBox || c is NumericUpDown);
        bool isToggle = c is ToggleSwitch;

        if (isPanelLike && !hasText) return false;
        if (!hasText && !isField && !isToggle) return false;

        string what = c.GetType().Name + " '" + Short(hasText ? c.Text : c.Name) + "'";

        Color bg, fg;
        double contrast;
        Analyze(bmp, r, out bg, out fg, out contrast);

        // (а) ТЛО МУСИТЬ НАЛЕЖАТИ ЧИННІЙ ПАЛІТРІ. Це буквально скарга власника: «темні поля
        //     у світлій темі». Акцентні поверхні (кнопка Primary, увімкнений тумблер) — законний
        //     виняток: вони темні у світлій темі за задумом.
        // У високій контрастності «належність до палітри» безпредметна: палітра і є системна.
        if (!highContrast && !IsAccentish(bg))
        {
            double bgl = Lum(bg);
            bool bgOk = dark ? bgl < 0.50 : bgl >= 0.50;
            if (!bgOk)
                Fail(name + ": " + what + " background is from the WRONG theme (luminance " +
                     bgl.ToString("F3") + ", " + Hex(bg) + ")");
        }

        // (б) КОНТРАСТ ТЕКСТУ — рахується у ТІЙ ділянці, де текст справді малюється.
        //     Перша версія брала весь прямокутник контрола і на кнопці «Signal» упіймала не
        //     текст, а кольорову позначку бренду (#3A76F0, 4.18:1) — тобто рапортувала дефект
        //     там, де його немає, і водночас могла б сховати НЕЧИТНИЙ текст за яскравою
        //     позначкою. Контрол сам називає свою текстову зону через IProofInk.
        if (hasText)
        {
            Rectangle ink = r;
            var seam = c as IProofInk;
            if (seam != null)
            {
                Rectangle k = seam.InkRect;
                ink = new Rectangle(r.X + k.X, r.Y + k.Y, k.Width, k.Height);
                if (!Inside(bmp, ink) || ink.Width < 4 || ink.Height < 4) ink = r;
            }
            Color tbg, tfg; double tk;
            AnalyzeText(bmp, ink, out tbg, out tfg, out tk);

            double need = LargeText(c) ? AaLarge : AaText;
            // НАЗВАНИЙ ВИНЯТОК (2026-08-08), за прикладом A10, не мовчазний. У високій
            // контрастності акцентні елементи ЗОБОВ'ЯЗАНІ малюватися системною парою
            // Highlight/HighlightText — саме це стереже скан фірмового кольору. Контраст цієї
            // пари належить ОС, не продукту: дефолтний #0078D7 із чисто-білим дає рівно 4,499:1,
            // на волосину під порогом. Червоніти тут — вимагати від продукту зламати схему
            // користувача; тому пара впізнається і засвідчується, а не карається.
            bool systemPair = Near(tbg, SystemColors.Highlight, 8) && Near(tfg, SystemColors.HighlightText, 8);
            if (systemPair)
                Line("   ok  " + what + "  text " + tk.ToString("F2") + ":1 on the SYSTEM Highlight pair (named exemption)");
            else if (tk + 0.001 < need)
                Fail(name + ": " + what + " TEXT contrast " + tk.ToString("F2") + ":1 < " +
                     need.ToString("F1") + ":1  (fg " + Hex(tfg) + " on bg " + Hex(tbg) + ")");
            else
                Line("   ok  " + what + "  text " + tk.ToString("F2") + ":1  " + Hex(tfg) + " on " + Hex(tbg));
        }

        // (в) ТУМБЛЕР — нетекстовий елемент керування: WCAG 1.4.11 вимагає 3:1 від його форми.
        if (isToggle)
        {
            if (contrast + 0.001 < AaLarge)
                Fail(name + ": " + what + " component contrast " + contrast.ToString("F2") + ":1 < 3.0:1");
            else
                Line("   ok  " + what + "  component " + contrast.ToString("F2") + ":1");
        }

        // (г) МЕЖА ПОЛЯ ВВОДУ. Порожнє поле не має тексту, тож єдине, що каже користувачу «сюди
        //     можна писати», — його рамка. WinForms малює FixedSingle системним COLOR_WINDOWFRAME
        //     (чорним): у світлій темі це різка чорна лінія не з палітри, у темній — рамка
        //     зливається з карткою і поле зникає. Міряємо рамку проти тла ПОРУЧ із полем.
        if (isField)
        {
            double edge = EdgeContrast(bmp, r);
            if (edge + 0.001 < AaLarge)
                Fail(name + ": " + what + " border contrast " + edge.ToString("F2") +
                     ":1 < 3.0:1 - the field has no visible boundary");
            else
                Line("   ok  " + what + "  border " + edge.ToString("F2") + ":1");
        }

        return true;
    }

    // Контраст рамки контрола проти того, що намальовано ОДРАЗУ ЗА нею.
    // Кільце пробуємо на трьох глибинах: рамка не завжди лежить рівно на межі вікна (у
    // комбобокса вона на піксель усередині клієнтської області). Беремо найкращу з трьох —
    // якщо ЖОДНА не дає 3:1, у поля справді немає видимої межі.
    private static double EdgeContrast(Bitmap b, Rectangle r)
    {
        Color outside = ModeOfRing(b, Rectangle.Inflate(r, 3, 3), 0);
        double best = 1.0;
        for (int inset = 0; inset <= 2; inset++)
        {
            double k = Contrast(ModeOfRing(b, r, inset), outside);
            if (k > best) best = k;
        }
        return best;
    }

    // Найчастіший колір «кільця» завтовшки 1 px по периметру прямокутника, зменшеного на inset.
    private static Color ModeOfRing(Bitmap b, Rectangle r, int inset)
    {
        var hist = new Dictionary<int, int>();
        Rectangle q = Rectangle.Inflate(r, -inset, -inset);
        for (int x = q.Left; x < q.Right; x++)
        {
            Bump(hist, b, x, q.Top);
            Bump(hist, b, x, q.Bottom - 1);
        }
        for (int y = q.Top; y < q.Bottom; y++)
        {
            Bump(hist, b, q.Left, y);
            Bump(hist, b, q.Right - 1, y);
        }
        int bestKey = Color.Black.ToArgb(), bestN = -1;
        foreach (KeyValuePair<int, int> kv in hist)
            if (kv.Value > bestN) { bestN = kv.Value; bestKey = kv.Key; }
        return Color.FromArgb(bestKey);
    }

    private static void Bump(Dictionary<int, int> hist, Bitmap b, int x, int y)
    {
        if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return;
        int key = b.GetPixel(x, y).ToArgb();
        int n;
        hist[key] = hist.TryGetValue(key, out n) ? n + 1 : 1;
    }

    private static bool LargeText(Control c)
    {
        try { return c.Font != null && (c.Font.SizeInPoints >= 18f || (c.Font.SizeInPoints >= 14f && c.Font.Bold)); }
        catch { return false; }
    }

    // Суцільний блок «не з тієї теми». Гліфи тексту сюди не проходять: вони тонкі, і кожна
    // літера — окрема дрібна компонента. Картка ж, поле чи спінер дають блок у сотні пікселів.
    private static void CheckAlienBlocks(string name, Bitmap bmp, Rectangle client, bool dark, List<Rectangle> skip)
    {
        int w = bmp.Width, h = bmp.Height;
        var wrong = new bool[w, h];
        for (int y = Math.Max(0, client.Top); y < Math.Min(h, client.Bottom); y++)
        {
            for (int x = Math.Max(0, client.Left); x < Math.Min(w, client.Right); x++)
            {
                if (InAny(skip, x, y)) continue;
                Color p = bmp.GetPixel(x, y);
                if (IsAccentish(p)) continue;
                double l = Lum(p);
                // Пороги стоять НИЖЧЕ/ВИЩЕ за законні елементи палітри, а не поруч із ними.
                // Рамка поля мусить мати 3:1 до тла, тобто у світлій темі вона неминуче темна
                // (#8A8A8A = 0.254). Ловимо лише ПОВЕРХНІ чужої теми: #2B2B2B = 0.024,
                // #333333 = 0.033 у світлій; #FFFFFF = 1.0, #F0F0F0 = 0.87 у темній.
                wrong[x, y] = dark ? l > 0.55 : l < 0.10;
            }
        }

        // Збираємо ВСІ блоки, не лише найбільший: перша версія рапортувала один — і світлий
        // квадрат комбобокса ховав за собою так само світлі стрілки NumericUpDown поруч.
        var blocks = new List<Rectangle>();
        var seen = new bool[w, h];
        var stack = new Stack<Point>();
        for (int y = client.Top; y < Math.Min(h, client.Bottom); y++)
        {
            for (int x = Math.Max(0, client.Left); x < Math.Min(w, client.Right); x++)
            {
                if (!wrong[x, y] || seen[x, y]) continue;
                int area = 0, minX = x, maxX = x, minY = y, maxY = y;
                stack.Push(new Point(x, y)); seen[x, y] = true;
                while (stack.Count > 0)
                {
                    Point q = stack.Pop();
                    area++;
                    if (q.X < minX) minX = q.X; if (q.X > maxX) maxX = q.X;
                    if (q.Y < minY) minY = q.Y; if (q.Y > maxY) maxY = q.Y;
                    Push(stack, seen, wrong, q.X + 1, q.Y, w, h);
                    Push(stack, seen, wrong, q.X - 1, q.Y, w, h);
                    Push(stack, seen, wrong, q.X, q.Y + 1, w, h);
                    Push(stack, seen, wrong, q.X, q.Y - 1, w, h);
                }
                int bw = maxX - minX + 1, bh = maxY - minY + 1;
                // Блок = і площа велика, і обидва виміри товсті. Слово тексту може дати площу,
                // але не дає товщини: його висота ~11 px при товщині штриха 1-2 px.
                if (area >= 250 && Math.Min(bw, bh) >= 10)
                    blocks.Add(new Rectangle(minX, minY, bw, bh));
            }
        }

        if (blocks.Count > 0)
            foreach (Rectangle bx in blocks)
                Fail(name + ": solid " + (dark ? "LIGHT" : "DARK") + " block at " + bx.X + "," + bx.Y +
                     " (" + bx.Width + "x" + bx.Height + ") - a control from the wrong theme");
        else
            Pass(name + ": no alien-theme block in the client area");
    }

    // У режимі високої контрастності НАШ фірмовий колір у кадрі — це доказ, що якийсь контрол
    // малює себе повз палітру. Саме тоді це найдорожче: користувач явно сказав системі, якими
    // кольорами він здатен читати екран, а зашитий синій цю домовленість ламає.
    // Наші акценти (#0F6CBD світлий, #4CA0E0 темний) не є системними кольорами в жодній схемі
    // високої контрастності, тож збіг тут однозначний.
    private static void CheckNoBrandColour(string name, Bitmap bmp, Rectangle roi)
    {
        Color[] brand = { Color.FromArgb(0x0F, 0x6C, 0xBD), Color.FromArgb(0x4C, 0xA0, 0xE0),
                          Color.FromArgb(0xEB, 0xF3, 0xFC), Color.FromArgb(0x2A, 0x3E, 0x4F) };
        int hits = 0;
        Rectangle r = Rectangle.Intersect(roi, new Rectangle(0, 0, bmp.Width, bmp.Height));
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                Color p = bmp.GetPixel(x, y);
                for (int i = 0; i < brand.Length; i++)
                    if (Math.Abs(p.R - brand[i].R) <= 6 && Math.Abs(p.G - brand[i].G) <= 6 &&
                        Math.Abs(p.B - brand[i].B) <= 6) { hits++; break; }
            }
        if (hits > 60)
            Fail(name + ": " + hits + " px carry OUR brand colour - a control ignores the high-contrast palette");
        else
            Pass(name + ": no hardcoded brand colour survives high contrast (" + hits + " px)");
    }

    private static void Push(Stack<Point> st, bool[,] seen, bool[,] wrong, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        if (seen[x, y] || !wrong[x, y]) return;
        seen[x, y] = true;
        st.Push(new Point(x, y));
    }

    private static bool InAny(List<Rectangle> rs, int x, int y)
    {
        for (int i = 0; i < rs.Count; i++) if (rs[i].Contains(x, y)) return true;
        return false;
    }

    // ------------------------------------------------------- аналіз ділянки знімка

    // Тло = найчастіший колір ділянки. Передній план = колір із найбільшим контрастом до тла
    // серед тих, що трапляються досить часто, щоб бути ядром гліфа, а не бахромою згладжування.
    //
    // КОЛЬОРИ КВАНТУЮТЬСЯ (крок 16). Перша версія рахувала ТОЧНІ значення — і промахувалась на
    // короткому тексті: ClearType малює субпіксельно, тож рядок «Signal» не має жодного великого
    // згустку рівно #242424, він розсипаний по десятках близьких відтінків. Через це вимірник
    // не бачив тексту взагалі й брав за нього рамку кнопки (1.53:1) — тобто рапортував дефект
    // там, де його не було, і сховав би справжній.
    // Зняти виділення в усіх текстових полях перед знімком: виділений текст малюється парою
    // Highlight/HighlightText ОС, і його контраст — не властивість палітри продукту.
    private static void Calm(Control c)
    {
        var tb = c as TextBoxBase;
        if (tb != null) { tb.SelectionStart = tb.TextLength; tb.SelectionLength = 0; }
        foreach (Control k in c.Controls) Calm(k);
    }

    private static bool Near(Color a, Color b, int tol)
    {
        return Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
    }

    private static void Analyze(Bitmap bmp, Rectangle r, out Color bg, out Color fg, out double contrast)
    {
        var count = new Dictionary<int, int>();
        var sumR = new Dictionary<int, long>();
        var sumG = new Dictionary<int, long>();
        var sumB = new Dictionary<int, long>();

        int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
        int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                Color p = bmp.GetPixel(x, y);
                int key = ((p.R >> 4) << 8) | ((p.G >> 4) << 4) | (p.B >> 4);
                int n;
                count[key] = count.TryGetValue(key, out n) ? n + 1 : 1;
                long s;
                sumR[key] = (sumR.TryGetValue(key, out s) ? s : 0) + p.R;
                sumG[key] = (sumG.TryGetValue(key, out s) ? s : 0) + p.G;
                sumB[key] = (sumB.TryGetValue(key, out s) ? s : 0) + p.B;
            }

        int total = Math.Max(1, (x1 - x0) * (y1 - y0));
        int bestKey = 0, bestN = -1;
        foreach (KeyValuePair<int, int> kv in count)
            if (kv.Value > bestN) { bestN = kv.Value; bestKey = kv.Key; }
        bg = Mean(bestKey, count, sumR, sumG, sumB);

        int floor = Math.Max(8, total / 200);   // 0.5 % ділянки: ядро гліфа проходить, бахрома ні
        fg = bg; contrast = 1.0;
        foreach (KeyValuePair<int, int> kv in count)
        {
            if (kv.Value < floor) continue;
            Color c = Mean(kv.Key, count, sumR, sumG, sumB);
            double k = Contrast(c, bg);
            if (k > contrast) { contrast = k; fg = c; }
        }
    }

    private static Color Mean(int key, Dictionary<int, int> count,
                              Dictionary<int, long> sr, Dictionary<int, long> sg, Dictionary<int, long> sb)
    {
        int n = Math.Max(1, count[key]);
        return Color.FromArgb((int)(sr[key] / n), (int)(sg[key] / n), (int)(sb[key] / n));
    }

    // Контраст ТЕКСТУ. Ключова відмінність від Analyze: спершу прямокутник СТИСКАЄТЬСЯ до
    // реальних меж чорнила.
    // Без цього поріг «колір мусить займати 0.5 % ділянки» рахувався від УСІЄЇ площі контрола:
    // на кнопці 360x40 зі словом «Signal» текст займає ~1 % від своєї власної рамки, але лише
    // 0.2 % від кнопки — і вимірник не бачив тексту зовсім, рапортуючи 1.00:1 на цілком
    // читабельній кнопці. Тло беремо з ПОВНОГО прямокутника (там воно домінує), чорнило —
    // зі стиснутого.
    private static void AnalyzeText(Bitmap bmp, Rectangle r, out Color bg, out Color fg, out double contrast)
    {
        Color dummyFg; double dummyK;
        Analyze(bmp, r, out bg, out dummyFg, out dummyK);

        int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top);
        int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                Color p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - bg.R) <= 24 && Math.Abs(p.G - bg.G) <= 24 && Math.Abs(p.B - bg.B) <= 24) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }

        Rectangle tight = (maxX >= minX && maxY >= minY)
            ? Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1)
            : r;

        Color tbg, tfg; double tk;
        Analyze(bmp, tight, out tbg, out tfg, out tk);
        fg = tfg;
        contrast = Contrast(fg, bg);
    }

    private static bool Inside(Bitmap b, Rectangle r)
    {
        return r.Left >= 0 && r.Top >= 0 && r.Right <= b.Width && r.Bottom <= b.Height;
    }

    private static double MeanLuminance(Bitmap b)
    {
        double sum = 0; int n = 0;
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2) { sum += Lum(b.GetPixel(x, y)); n++; }
        return n == 0 ? 0 : sum / n;
    }

    // Акцент — законна поверхня будь-якої теми (синя кнопка темна навіть у світлій темі).
    private static bool IsAccentish(Color c)
    {
        return Near(c, Theme.Accent) || Near(c, Theme.AccentHover) || Near(c, Theme.AccentPressed) ||
               Near(c, Theme.SelectedBg);
    }

    private static bool Near(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db <= 60 * 60;
    }

    // WCAG 2.1 relative luminance + contrast ratio.
    private static double Ch(int v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Lum(Color c) { return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B); }

    private static double Contrast(Color a, Color b)
    {
        double la = Lum(a), lb = Lum(b);
        if (la < lb) { double t = la; la = lb; lb = t; }
        return (la + 0.05) / (lb + 0.05);
    }

    private static string Hex(Color c)
    {
        return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    }

    private static string Short(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 28 ? s : s.Substring(0, 26) + "..";
    }

    private static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    // ------------------------------------------------------------------- оверлей

    // Оверлей малює поверх ЧУЖОГО кадру, тож перевірки палітри до нього не застосовні за
    // визначенням. Тут доводимо лише те, що доводиться: файл є, він змістовний, і він РІЗНИЙ
    // у двох темах (тобто тулбар справді читає палітру, а не прикидається).
    private static Shot CaptureOverlay(string name)
    {
        var shot = new Shot { Name = name };
        string path = Path.Combine(_root, "_preview_" + name + ".png");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        Line("");
        Line("-- " + name + " (EYE-ONLY: overlay paints over a foreign frame) --");

        Bitmap desk = MakeDesktop(1600, 900);
        var cfg = new Config();
        Point off = OffscreenSpot();
        var f = new OverlayForm(desk, new Rectangle(off.X, off.Y, 1600, 900), cfg);

        var pen = new PenAnn { Color = Color.FromArgb(0xE8, 0x11, 0x23), Width = 3 };
        pen.Points.AddRange(new[] { new Point(430, 520), new Point(470, 470), new Point(520, 530), new Point(575, 465) });
        var mk = new MarkerAnn { Color = Color.Yellow, Width = 3 };
        mk.Points.AddRange(new[] { new Point(420, 300), new Point(700, 300) });

        var px = new PixelateAnn { Color = Color.White, Width = 3, A = new Point(950, 280), B = new Point(1110, 350) };
        px.Bake(desk);

        var route = new RouteAnn { Color = Color.FromArgb(0xE8, 0x11, 0x23), Width = 4 };
        route.Points.Add(new Point(440, 560));
        route.Points.Add(new Point(560, 500));
        route.Points.Add(new Point(640, 540));
        route.Points.Add(new Point(760, 470));
        route.Points.Add(new Point(860, 520));

        f.TestSetup(new Rectangle(380, 240, 760, 400), new Ann[]
        {
            mk,
            new RectAnn { Color = Color.FromArgb(0x00, 0x78, 0xD7), Width = 3, A = new Point(640, 340), B = new Point(880, 440) },
            new FillRectAnn { Color = Color.FromArgb(0xFF, 0xB9, 0x00), Width = 2, A = new Point(420, 340), B = new Point(600, 430) },
            new TextAnn { Color = Color.FromArgb(0xE8, 0x11, 0x23), Width = 3, Pos = new Point(650, 470), Text = "ValeraScreenshot" },
            px,
            new StepAnn { Color = Color.FromArgb(0x0F, 0x6C, 0xBD), Width = 3, Pos = new Point(445, 285), Number = 1 },
            new StepAnn { Color = Color.FromArgb(0x0F, 0x6C, 0xBD), Width = 3, Pos = new Point(660, 300), Number = 2 },
            route,
        }, Tool.Route);

        try
        {
            // Оверлей за визначенням повноекранний і TopMost — показати його означає накрити
            // весь стіл власника. Тому він показується ПОЗА видимою областю: _virt задано
            // офскрін-прямокутником, а координати виділення й анотацій зсунуто на ту саму
            // дельту, тож картинка виходить та сама, а екран лишається власникові.
            f.StartPosition = FormStartPosition.Manual;
            ShowWithoutStealingFocus(f);
            Settle();
            var b = new Bitmap(1600, 900, PixelFormat.Format24bppRgb);
            // Оверлей — єдина форма з UserPaint + повністю власним малюванням, і PrintWindow
            // повертає для неї порожній кадр (виміряно: 4317 b, яскравість 0.000). DrawToBitmap
            // йде іншим шляхом (WM_PRINTCLIENT) і малює її правильно. Тут це нічого не коштує:
            // у оверлея немає рамки й заголовка, тобто нема того єдиного, чого DrawToBitmap
            // не бачить.
            var overlayBox = new Rectangle(0, 0, 1600, 900);
            RenderStable(delegate(Bitmap t) { f.DrawToBitmap(t, overlayBox); return true; }, b, name, overlayBox);
            b.Save(path, ImageFormat.Png);
            shot.MeanLum = MeanLuminance(b);
            b.Dispose();
        }
        catch (Exception ex)
        {
            Fail(name + ": capture failed - " + ex.Message);
            try { f.Close(); } catch { }
            return shot;
        }
        try { f.Close(); } catch { }

        if (!File.Exists(path)) { Fail(name + ": proof file not written"); return shot; }
        var fi = new FileInfo(path);
        if (fi.Length < 20000) Fail(name + ": proof file suspiciously small (" + fi.Length + " b)");
        else Pass(name + ": written, " + fi.Length.ToString("N0") + " b");
        shot.Sha = Sha256(path);

        try { File.Copy(path, Path.Combine(_imgDir, name + ".png"), true); }
        catch (Exception ex) { Fail(name + ": cannot copy into docs\\img - " + ex.Message); }
        return shot;
    }

    private static Bitmap MakeDesktop(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            using (var grad = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                Color.FromArgb(0x1E, 0x2E, 0x4F), Color.FromArgb(0x4A, 0x66, 0x8C), 65f))
                g.FillRectangle(grad, 0, 0, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var win = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF6)))
            using (var bar = new SolidBrush(Color.FromArgb(0xDC, 0xDE, 0xE4)))
            using (var txt = new SolidBrush(Color.FromArgb(0x50, 0x52, 0x58)))
            using (var f1 = new Font("Segoe UI", 12f))
            {
                g.FillRectangle(win, 120, 90, 700, 520);
                g.FillRectangle(bar, 120, 90, 700, 36);
                g.DrawString(L.S("Документ — приклад вікна", "Document - sample window"), f1, txt, 132, 98);
                for (int i = 0; i < 9; i++)
                    g.FillRectangle(bar, 150, 160 + i * 44, 560 - (i % 3) * 90, 16);

                g.FillRectangle(win, 900, 180, 560, 500);
                g.FillRectangle(bar, 900, 180, 560, 36);
                g.DrawString(L.S("Браузер — приклад вікна", "Browser - sample window"), f1, txt, 912, 188);
                for (int i = 0; i < 8; i++)
                    g.FillRectangle(bar, 930, 250 + i * 50, 480 - (i % 4) * 60, 18);
            }
        }
        return b;
    }

    // ------------------------------------------------------------------- облік

    private static void Pass(string s) { _pass++; Line("PASS " + s); }
    private static void Fail(string s) { _fail++; Line("FAIL " + s); }
    private static void Line(string s) { Rep.AppendLine(s); }
}
