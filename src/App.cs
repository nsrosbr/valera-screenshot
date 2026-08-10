using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ValeraScreenshot
{
    internal static class Hotkeys
    {
        public static string ToText(int vk, int mods)
        {
            string s = "";
            if ((mods & Native.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((mods & Native.MOD_SHIFT) != 0) s += "Shift+";
            if ((mods & Native.MOD_ALT) != 0) s += "Alt+";
            if ((mods & Native.MOD_WIN) != 0) s += "Win+";
            if (vk == 0) return "—";
            var k = (Keys)vk;
            string name;
            if (k == Keys.PrintScreen) name = "PrtScr";
            else
            {
                name = k.ToString();
                if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            }
            return s + name;
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Порядок за STD-LIFE-01: VisualStyles -> краш-гарди -> L.Init -> аргументи -> МУТЕКС ->
            // CleanupOld -> Config -> форма. Аргументи мусять оброблятися ДО мутекса: apply-update і
            // install-over за визначенням стартують другим екземпляром, поки перший ще живий, — за
            // мутексом вони заблокували б самі себе й оновлення не застосувалося б ніколи.
            Native.EnsureDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Краш-гарди: будь-яке падіння лишає слід у crash.log (STD-DIAG-03), навіть коли лог off.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { OnCrash("ThreadException", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { OnCrash("UnhandledException", e.ExceptionObject as Exception); };

            // МОВА — З КОНФІГА, не зашита. Тут стояло L.Init("uk") від першого дня, тож шим
            // L.S існував, а другої мови в продукті не існувало. Конфіг читається саме тут, а
            // не пізніше: нижче йдуть командні дієслівці (/install, /uninstall), і вони теж
            // показують текст користувачу.
            L.Init(Config.Load().UiLang);

            string a0 = args.Length > 0 ? args[0] : "";
            if (a0 == "apply-update") { Updater.ApplyUpdateCommand(args); return 0; }
            if (a0 == "install-over") { Installer.InstallOverCommand(args); return 0; }
            // Гард портативу тут стояв усюди, крім цієї гілки: /install був ЄДИНИМ життєвим
            // шляхом без нього і ставив застосунок у систему попри portable.txt — при тому що
            // сам маркер друком обіцяє «нуль записів у реєстр, жодних ярликів».
            if (a0 == "/install" || a0 == "install")
            {
                // Відмова МОВЧАЗНА, лише кодом виходу. Тут спершу стояло вікно Ui.Msg — і воно
                // повісило автоматичний прогін: командний дієслівець запускають скрипти, а
                // модальне вікно нікому натиснути. Пояснення друкує install.ps1, який цей код і
                // читає; він же прибирає маркер, коли користувач справді хоче встановити.
                if (Installer.IsPortable()) return 2;   // portable.txt -> refuse, install.ps1 explains
                // /silent мусить існувати САМЕ ТУТ: без нього дієслівець завжди закінчувався
                // модальним «встановлено», тож будь-який скрипт висів на вікні, якого нікому
                // натиснути. Польова проба зловила це таймаутом, а не здогадкою.
                Installer.Install(Array.IndexOf(args, Ident.SilentSwitch) >= 0);
                return 0;
            }
            // Реєстрація картки ARP для стороннього інсталятора (install.ps1). Існує саме щоб
            // той не тримав власну копію полів: одна копія вже розійшлася з продуктом
            // (версія «1.0.0», відсутній QuietUninstallString) — STD-LIFE-04, єдиний писар.
            if (a0 == "/install-card")
            {
                if (Installer.IsPortable()) return 2;
                string cardDir = args.Length > 1 ? args[1]
                                 : Path.GetDirectoryName(Application.ExecutablePath);
                return Installer.WriteArpCardFor(cardDir) ? 0 : 1;
            }
            if (a0 == "/uninstall" || a0 == "uninstall")
            {
                bool silentUn = Array.IndexOf(args, Ident.SilentSwitch) >= 0;
                bool keepCfg = Array.IndexOf(args, "/keepconfig") >= 0;
                Installer.Uninstall(silentUn, !keepCfg);
                return 0;
            }

            // Безголове захоплення всього екрана — для QA і скриптів (без UI).
            bool headless = false;
            string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--capture-full") headless = true;
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[i + 1];
            }
            if (headless) return HeadlessFull(outPath);

            // Самовстановлення (STD-LIFE-03): запущений «звідкись» exe стає в систему й працює звідти;
            // новіший ОНОВЛЮЄ наявну інсталяцію на місці, а не плодить другу копію. portable.txt поруч
            // із exe це повністю вимикає. true = керування передано встановленій копії, ми виходимо.
            if (Installer.EnsureInstalled()) return 0;

            bool created;
            using (var mutex = new Mutex(true, Ident.Mutex, out created))
            {
                if (!created)
                {
                    // ДРУГИЙ ЗАПУСК ПОКАЗУЄ ПЕРШИЙ, а не читає нотацію.
                    //
                    // Тут стояло модальне «ValeraScreenshot уже запущено — іконка в треї». Так не
                    // поводиться жоден застосунок Windows: повторний запуск виводить наперед
                    // те, що вже працює. У застосунку в треї «наперед» означає підказку біля
                    // іконки — власного вікна в нього немає.
                    //
                    // Ім'я повідомлення виводиться з мутекса, а не заводиться новим полем
                    // айдентики: технічна айдентика — CROWN (§18.1 п.3), і чіпати Ident.cs
                    // заради цього не можна й не треба.
                    Native.PostMessage(Native.HWND_BROADCAST,
                        Native.RegisterWindowMessage(Ident.Mutex + "_SHOW"), IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                Updater.CleanupOld();   // прибрати лишений <exe>.old після попереднього оновлення
                Theme.Init(Config.Load().UiTheme);   // ДО конструювання форм, інакше вони візьмуть стару палітру
                using (var ctx = new TrayApp())
                {
                    Application.Run(ctx);
                }
            }
            return 0;
        }

        // STD-DIAG-03. Слід у файлі — обов'язковий, але недостатній: користувач бачив рівно
        // НІЧОГО. Тепер він бачить, що сталося, і має один клік до журналу. Стелю й дедуп
        // тримає Diag: краш-шторм в OnPaint інакше відкрив би сотні вікон поспіль.
        private static void OnCrash(string where, Exception ex)
        {
            Diag.LogCrash(where, ex);
            if (!Diag.ShouldTellUser(where, ex)) return;
            try
            {
                string msg = L.S("Сталася помилка, і дію не виконано.", "Something went wrong and the action did not complete.")
                    + "\n\n" + (ex == null ? where : ex.Message)
                    + "\n\n" + L.S("Подробиці записано у журнал збоїв. Відкрити його?",
                                   "The details were written to the crash log. Open it?");
                if (Ui.Msg(msg, L.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    Process.Start("notepad.exe", "\"" + Diag.CrashLogPath + "\"");
            }
            catch { }   // вікно про падіння не має права стати другим падінням
        }

        private static int HeadlessFull(string outPath)
        {
            try
            {
                var cfg = Config.Load();
                using (var shot = ScreenCap.Grab(cfg.IncludeCursor))
                {
                    string path = outPath;
                    if (string.IsNullOrEmpty(path))
                    {
                        path = cfg.MakeFilePath(shot.Width, shot.Height);
                    }
                    else
                    {
                        string d = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    }
                    ScreenCap.Save(shot, path, cfg);
                    File.WriteAllText(Path.Combine(Config.Dir, "_last_capture.txt"),
                        shot.Width + "x" + shot.Height + " " + path);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Config.Dir, "_last_capture.txt"), "ERROR " + ex.Message);
                }
                catch { }
                return 1;
            }
        }
    }

    // Приховане вікно-приймач WM_HOTKEY.
    internal class HotkeyWindow : NativeWindow
    {
        public Action<int> HotkeyPressed;
        public Action ShowRequested;   // другий запуск попросив показати вже працюючий екземпляр

        private readonly int _wmShow;

        public HotkeyWindow()
        {
            _wmShow = Native.RegisterWindowMessage(Ident.Mutex + "_SHOW");
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null)
                HotkeyPressed(m.WParam.ToInt32());
            else if (_wmShow != 0 && m.Msg == _wmShow && ShowRequested != null)
                ShowRequested();
            base.WndProc(ref m);
        }
    }

    // Меню трея, зібране БЕЗ обробників. Існує окремо рівно з однієї причини: пруф-гейт мусить
    // знімати ТЕ САМЕ меню, яке бачить користувач, а не свою копію з тими ж на вигляд пунктами.
    // Друга копія списку пунктів розійшлася б із першою на першій же зміні — той самий клас
    // дубля, проти якого існує Ident.cs. Обробники чіпляє TrayApp, пруф-гейт не чіпляє нічого.
    internal sealed class TrayMenu
    {
        public readonly ContextMenuStrip Strip;
        public readonly ToolStripMenuItem Region, Full, Folder, Settings, About, Update, DiagItem, Exit;

        public TrayMenu(bool diagEnabled)
        {
            Strip = new ContextMenuStrip();
            // Меню малювалось системним ToolStripProfessionalRenderer, тобто лишалось СВІТЛИМ
            // у темній темі. Жоден тест цього не бачив: меню не форма й у дерево контролів
            // не входить, тож обхід дерева його не досягав.
            Strip.RenderMode = ToolStripRenderMode.Professional;
            Strip.Renderer = new ThemedMenuRenderer();
            Strip.BackColor = Theme.CardBg;
            Strip.ForeColor = Theme.TextPrimary;
            // ShowImageMargin лишається УВІМКНЕНИМ: у цьому полі малюється галочка пункту
            // «Записувати діагностику». Я був вимкнув його заради вигляду — і тумблер
            // діагностики втратив ЄДИНИЙ спосіб показати свій стан.

            var header = new ToolStripMenuItem(L.NameFull);
            header.Enabled = false;
            Strip.Items.Add(header);
            Strip.Items.Add(new ToolStripSeparator());

            Region = Add(L.S("Знімок області…", "Capture a region…"));
            Full = Add(L.S("Знімок усього екрана", "Capture the whole screen"));
            Strip.Items.Add(new ToolStripSeparator());

            Folder = Add(L.S("Відкрити теку скріншотів", "Open the screenshots folder"));
            Settings = Add(L.S("Параметри…", "Settings…"));
            About = Add(L.S("Про програму", "About"));
            Update = Add(L.S("Перевірити оновлення…", "Check for updates…"));

            DiagItem = Add(L.S("Записувати діагностику", "Write a diagnostic log"));
            DiagItem.CheckOnClick = true;
            DiagItem.Checked = diagEnabled;

            Strip.Items.Add(new ToolStripSeparator());
            Exit = Add(L.S("Вихід", "Exit"));
        }

        private ToolStripMenuItem Add(string text)
        {
            var it = new ToolStripMenuItem(text);
            Strip.Items.Add(it);
            return it;
        }
    }

    internal class TrayApp : ApplicationContext
    {
        private const int HK_REGION = 1, HK_FULL = 2;
        private const int HK_REGION2 = 3, HK_FULL2 = 4; // запасні клавіші (ПК: PrtScr)

        private readonly Config _cfg;
        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hk;
        private bool _capturing;
        private string _lastSavedPath;
        private ToolStripMenuItem _miRegion, _miFull;

        public TrayApp()
        {
            _cfg = Config.Load();

            _tray = new NotifyIcon();
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = L.NameFull;
            _tray.Visible = true;
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += delegate { CaptureRegion(); };
            _tray.BalloonTipClicked += delegate
            {
                if (_lastSavedPath != null && File.Exists(_lastSavedPath)) RevealInExplorer(_lastSavedPath);
            };

            // Тема «Як у Windows» — ДЕФОЛТ. Досі Theme.SystemChanged() не викликалась НІЗВІДКИ:
            // користувач перемикав тему Windows, а ValeraScreenshot лишався у старій до перезапуску.
            // Категорія General накриває і зміну AppsUseLightTheme, і зміну кольору акценту.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Theme.Changed += OnThemeChanged;
            L.Changed += OnLangChanged;

            _hk = new HotkeyWindow();
            _hk.HotkeyPressed = OnHotkey;
            // Повторний запуск: показуємо, що застосунок працює, і нагадуємо клавіші. Балун
            // іде через Warn(), а не Balloon(): тумблер «Сповіщення після знімка» вимикає
            // рекламу успіху, а не відповідь на пряму дію користувача.
            _hk.ShowRequested = delegate
            {
                Warn(L.S("ВАЛЄРА Скріншот уже працює", "VALERA Screenshot is already running"),
                     Hotkeys.ToText(_cfg.RegionVk, _cfg.RegionMods) + L.S(" — знімок області. Права кнопка по іконці — меню.",
                                                                          " — capture a region. Right-click the icon for the menu."));
            };
            RegisterHotkeys(true);
            ApplyStartup();
            Installer.SelfHeal(_cfg);   // автозапуск/картка ARP мусять вказувати на ЦЮ версію

            if (_cfg.FirstRun)
            {
                Balloon(L.S("ВАЛЄРА Скріншот запущено", "VALERA Screenshot is running"),
                    Hotkeys.ToText(_cfg.RegionVk, _cfg.RegionMods) + L.S(" — знімок області, ", " — capture a region, ") +
                    Hotkeys.ToText(_cfg.FullVk, _cfg.FullMods) + L.S(" — весь екран. ", " — whole screen. ") +
                    L.S("Права кнопка по іконці — параметри.", "Right-click the tray icon for settings."));
                // Результат не відкидаємо: якщо цей запис не проходить, вітальний балун
                // з'являтиметься при КОЖНОМУ старті, і причина була б невідома нікому.
                if (!_cfg.Save()) Diag.Log("first-run flag not persisted: settings.ini is not writable");
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new TrayMenu(Diag.Enabled);
            var m = menu.Strip;
            _miRegion = menu.Region;
            _miFull = menu.Full;

            _miRegion.Click += delegate { CaptureRegion(); };
            _miFull.Click += delegate { CaptureFull(); };

            var miFolder = menu.Folder;
            miFolder.Click += delegate
            {
                // Класична «зламана кнопка»: тека на відключеному USB чи мережевому диску, або
                // невалідний шлях у settings.ini — і пункт меню тихо не робив нічого.
                try
                {
                    Directory.CreateDirectory(_cfg.EffectiveSaveDir);
                    Process.Start("explorer.exe", "\"" + _cfg.EffectiveSaveDir + "\"");
                }
                catch (Exception ex)
                {
                    Ui.Msg(L.S("Не вдалося відкрити теку знімків:\n", "Could not open the screenshots folder:\n") + _cfg.EffectiveSaveDir +
                        "\n\n" + ex.Message + L.S("\n\nЗмініть теку в Параметрах.", "\n\nChange the folder in Settings."),
                        L.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            menu.Settings.Click += delegate { OpenSettings(); };
            menu.About.Click += delegate { using (var f = new AboutForm()) f.ShowDialog(); };
            menu.Update.Click += delegate { Updater.CheckInteractive(); };
            var miDiag = menu.DiagItem;
            miDiag.Click += delegate { Diag.SetEnabled(miDiag.Checked); };
            menu.Exit.Click += delegate { ExitApp(); };

            UpdateMenuText();
            return m;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.VisualStyle &&
                e.Category != UserPreferenceCategory.Color) return;
            Theme.SystemChanged();   // сам вирішує, чи ми взагалі слідуємо за системою
        }

        // Меню будується ОДИН раз при старті, тож після зміни палітри воно лишилось би в старих
        // кольорах. Рендерер читає Theme під час малювання — досить перемалювати; але BackColor
        // меню WinForms кешує, тому його оновлюємо явно.
        private void OnThemeChanged()
        {
            var m = _tray.ContextMenuStrip;
            if (m == null) return;
            m.BackColor = Theme.CardBg;
            m.ForeColor = Theme.TextPrimary;
            m.Invalidate();
        }

        // Меню трея й підказка іконки будуються ОДИН раз при старті. Тема обходиться
        // перемальовуванням, а от текст пунктів після зміни мови треба будувати наново —
        // інакше меню лишалося б українським до перезапуску, тобто «жива» зміна мови була б
        // живою лише наполовину. Той самий клас вади, що й Theme.Changed без підписників.
        private void OnLangChanged()
        {
            try
            {
                var old = _tray.ContextMenuStrip;
                _tray.ContextMenuStrip = BuildMenu();
                if (old != null) old.Dispose();
                _tray.Text = L.NameFull;
            }
            catch (Exception ex) { Diag.Log("rebuild tray menu: " + ex.Message); }
        }

        private void UpdateMenuText()
        {
            if (_miRegion != null) _miRegion.ShortcutKeyDisplayString = Hotkeys.ToText(_cfg.RegionVk, _cfg.RegionMods);
            if (_miFull != null) _miFull.ShortcutKeyDisplayString = Hotkeys.ToText(_cfg.FullVk, _cfg.FullMods);
        }

        // Реєстрація глобальних клавіш. Дві клавіші на дію — щоб працювало і на ПК,
        // і на ноуті: основна (дефолт Ctrl+Shift+4/3) + запасна (дефолт PrtScr / Shift+PrtScr).
        // Запасна пропускається, якщо Vk=0 або збігається з основною.
        // Без MOD_NOREPEAT: прапорець входить в ідентичність хоткея; авто-повтор гасить _capturing.
        private void RegisterHotkeys(bool startup)
        {
            Native.UnregisterHotKey(_hk.Handle, HK_REGION);
            Native.UnregisterHotKey(_hk.Handle, HK_FULL);
            Native.UnregisterHotKey(_hk.Handle, HK_REGION2);
            Native.UnregisterHotKey(_hk.Handle, HK_FULL2);

            bool regionOk = Native.RegisterHotKey(_hk.Handle, HK_REGION, _cfg.RegionMods, _cfg.RegionVk);
            bool fullOk = Native.RegisterHotKey(_hk.Handle, HK_FULL, _cfg.FullMods, _cfg.FullVk);

            // ★ «НЕ ЗАДАНА» — НЕ ТЕ САМЕ, ЩО «НЕ ВДАЛОСЯ». Тут стояло region2Ok=false ДО
            //   реєстрації, і прапорець піднімався лише коли запасну справді пробували. Тож
            //   запасна, яку користувач СВІДОМО прибрав (Del у полі, Vk=0 — задокументований
            //   стан «вимкнено») або яка дублює основну, лишала false — і нижче при КОЖНОМУ
            //   старті вискакував Warn «Запасну клавішу не зареєстровано», якого не глушить
            //   жоден тумблер. Скаржитися є на що лише тоді, коли клавіша ЗАДАНА, а
            //   RegisterHotKey відмовив, — тому «хотіли» й «вдалося» тепер розведені.
            bool region2Wanted = _cfg.Region2Vk != 0 && !(_cfg.Region2Vk == _cfg.RegionVk && _cfg.Region2Mods == _cfg.RegionMods);
            bool full2Wanted = _cfg.Full2Vk != 0 && !(_cfg.Full2Vk == _cfg.FullVk && _cfg.Full2Mods == _cfg.FullMods);
            bool region2Ok = !region2Wanted || Native.RegisterHotKey(_hk.Handle, HK_REGION2, _cfg.Region2Mods, _cfg.Region2Vk);
            bool full2Ok = !full2Wanted || Native.RegisterHotKey(_hk.Handle, HK_FULL2, _cfg.Full2Mods, _cfg.Full2Vk);

            try
            {
                File.WriteAllText(Path.Combine(Config.Dir, "_hotkeys.txt"),
                    "region=" + regionOk + " (" + Hotkeys.ToText(_cfg.RegionVk, _cfg.RegionMods) + ")\r\n" +
                    "full=" + fullOk + " (" + Hotkeys.ToText(_cfg.FullVk, _cfg.FullMods) + ")\r\n" +
                    "region2=" + (region2Wanted ? region2Ok.ToString() : "skipped") + " (" + Hotkeys.ToText(_cfg.Region2Vk, _cfg.Region2Mods) + ")\r\n" +
                    "full2=" + (full2Wanted ? full2Ok.ToString() : "skipped") + " (" + Hotkeys.ToText(_cfg.Full2Vk, _cfg.Full2Mods) + ")\r\n",
                    System.Text.Encoding.UTF8);
            }
            catch { }

            // `&& !startup` тут не було випадковим — але наслідок був катастрофічний: старт це
            // ~99 % випадків (автозапуск при вході в Windows, коли конкурент уже висить у треї й
            // уже забрав комбінацію), і саме тоді провал не показувався НІКОЛИ. Користувач
            // тиснув клавішу десятки разів; єдиним слідом був рядок у _hotkeys.txt.
            // Запасні клавіші не перевірялися взагалі, хоча в Win11 PrtScr за замовчуванням
            // тримає Snipping Tool — тобто типова конфігурація мовчки не працювала.
            if (!regionOk || !fullOk)
                Warn(L.S("Гарячу клавішу не зареєстровано", "Hotkey not registered"),
                    L.S("Комбінацію тримає інша програма. Оберіть іншу в Параметрах.", "Another program holds that combination. Pick a different one in Settings."));
            else if (!region2Ok || !full2Ok)
            {
                // ★ Текст називав PrtScr БЕЗУМОВНО: запасною може стояти будь-яка комбінація,
                //   і тоді порада «Звільнити PrtScr» вела користувача лагодити не те.
                //   Повідомлення називає клавішу, ЩО СПРАВДІ впала, а рецепт про Snipping Tool
                //   дається лише коли впав саме PrtScr.
                string failed =
                    (!region2Ok ? Hotkeys.ToText(_cfg.Region2Vk, _cfg.Region2Mods) : "") +
                    (!region2Ok && !full2Ok ? ", " : "") +
                    (!full2Ok ? Hotkeys.ToText(_cfg.Full2Vk, _cfg.Full2Mods) : "");
                bool prtScr = (!region2Ok && _cfg.Region2Vk == (int)Keys.PrintScreen) ||
                              (!full2Ok && _cfg.Full2Vk == (int)Keys.PrintScreen);
                Warn(L.S("Запасну клавішу не зареєстровано", "Secondary hotkey not registered"),
                    failed + L.S(" — зайнято іншою програмою. ", " — taken by another program. ") +
                    (prtScr
                        ? L.S("У Windows 11 PrtScr тримає Snipping Tool: Параметри → «Звільнити PrtScr». ",
                              "On Windows 11 that is Snipping Tool: Settings -> 'Free up PrtScr'. ")
                        : L.S("Оберіть іншу в Параметрах. ", "Pick a different one in Settings. ")) +
                    L.S("Основні клавіші працюють.", "The primary hotkeys still work."));
            }
            UpdateMenuText();
        }

        private void OnHotkey(int id)
        {
            if (id == HK_REGION || id == HK_REGION2) CaptureRegion();
            else if (id == HK_FULL || id == HK_FULL2) CaptureFull();
        }

        private void CaptureRegion()
        {
            if (_capturing) return;
            _capturing = true;
            try
            {
                Bitmap shot = ScreenCap.Grab(_cfg.IncludeCursor);
                Rectangle v = ScreenCap.VirtualScreen();
                using (var f = new OverlayForm(shot, v, _cfg)) // форма володіє shot
                {
                    f.ShowDialog();
                    switch (f.Result)
                    {
                        case OverlayResult.Copied:
                            Sound();
                            Balloon(L.S("Скопійовано в буфер", "Copied to the clipboard"), f.ResultSize.Width + " × " + f.ResultSize.Height + " px");
                            break;
                        case OverlayResult.Saved:
                            _lastSavedPath = f.SavedPath;
                            Sound();
                            if (!f.ClipboardOk)
                                // Файл є, буфер — ні. Мовчати про це не можна: користувач іде
                                // в чат, тисне Ctrl+V і вставляє попередній вміст буфера.
                                Warn(L.S("Збережено, але буфер обміну зайнятий", "Saved, but the clipboard is busy"),
                                    Path.GetFileName(f.SavedPath) + L.S(" — файл на місці, у буфер не лягло.", " — the file is there, the clipboard is not."));
                            else
                                Balloon(L.S("Збережено ", "Saved ") + f.ResultSize.Width + " × " + f.ResultSize.Height,
                                    Path.GetFileName(f.SavedPath) + L.S("  (клік — відкрити теку)", "  (click to open the folder)"));
                            break;
                        case OverlayResult.Printed:
                            Balloon(L.S("Надіслано на друк", "Sent to the printer"), f.ResultSize.Width + " × " + f.ResultSize.Height + " px");
                            break;
                        case OverlayResult.Shared:
                            _lastSavedPath = f.SavedPath;
                            Sound();
                            if (!f.ClipboardOk)
                                Warn(L.S("Месенджер відкрито, але буфер зайнятий", "The messenger opened, but the clipboard is busy"),
                                    L.S("Ctrl+V вставить не той знімок. Файл: ", "Ctrl+V would paste the wrong image. File: ") + Path.GetFileName(f.SavedPath));
                            else
                                Balloon(L.S("Знімок у буфері й у файлі", "The capture is on the clipboard and in a file"),
                                    L.S("У чаті месенджера натисніть Ctrl+V. Файл: ", "Press Ctrl+V in the messenger chat. File: ") + Path.GetFileName(f.SavedPath));
                            break;
                    }
                }
                // Колір і товщина останнього разу. Відмова тут не варта вікна — вона нічого не
                // ламає в поточному знімку, — але й мовчати про неї не можна: саме так
                // «не запам'ятовується колір» перетворюється на непояснюваний польовий баг.
                if (!_cfg.Save()) Diag.Log("last colour/width not persisted: settings.ini is not writable");
            }
            catch (Exception ex)
            {
                Warn(L.S("Помилка знімка", "Capture failed"), ex.Message);
            }
            finally { _capturing = false; }
        }

        private void CaptureFull()
        {
            if (_capturing) return;
            _capturing = true;
            try
            {
                using (var shot = ScreenCap.Grab(_cfg.IncludeCursor))
                {
                    string path = _cfg.MakeFilePath(shot.Width, shot.Height);
                    ScreenCap.Save(shot, path, _cfg);
                    // ★ ТУТ СТОЯЛА ТА САМА БРЕХНЯ ПРО УСПІХ, яку вже виправили для знімка
                    //   ОБЛАСТІ — і не помітили, що шлях «весь екран» другий і окремий.
                    //   Було: `try { ClipboardUtil.CopyImage(shot); } catch { }`, а балун одразу
                    //   казав «Збережено весь екран». Користувач ішов у чат, тиснув Ctrl+V і
                    //   вставляв ПОПЕРЕДНІЙ вміст буфера, не знаючи, що копіювання не відбулося.
                    //   Буфер обміну тримає інший процес частіше, ніж здається: будь-який
                    //   менеджер буфера, RDP-сесія, віддалений стіл.
                    bool clipOk = true;
                    if (_cfg.CopyAfterSave)
                    {
                        try { clipOk = ClipboardUtil.CopyImage(shot); }
                        catch (Exception cex) { clipOk = false; Diag.Log("clipboard (full screen): " + cex.Message); }
                    }
                    _lastSavedPath = path;
                    Sound();
                    if (!clipOk)
                        Warn(L.S("Збережено, але буфер обміну зайнятий", "Saved, but the clipboard is busy"),
                            Path.GetFileName(path) + L.S(" — файл на місці, у буфер не лягло.",
                                                         " — the file is there, the clipboard is not."));
                    else
                        Balloon(L.S("Збережено весь екран — ", "Whole screen saved — ") + shot.Width + " × " + shot.Height,
                            Path.GetFileName(path) + L.S("  (клік — відкрити теку)", "  (click to open the folder)"));
                }
            }
            catch (Exception ex)
            {
                Warn(L.S("Помилка знімка", "Capture failed"), ex.Message);
            }
            finally { _capturing = false; }
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm(_cfg))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    // Раніше результат Save() відкидався: вікно закривалося як за успіху, а
                    // налаштування мовчки не долітали до диска (тека лише для читання, повний
                    // диск, антивірус тримає файл) і на наступному старті поверталися старі.
                    if (!_cfg.Save())
                        Ui.Msg(L.S("Не вдалося зберегти налаштування у файл:\n", "Could not save the settings to a file:\n") + Config.IniPath +
                            L.S("\n\nЗміни діють до перезапуску програми.", "\n\nThe changes apply until the program is restarted."),
                            L.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RegisterHotkeys(false);
                    ApplyStartup();
                }
            }
        }

        private void ApplyStartup()
        {
            Installer.SetAutostart(_cfg.StartWithWindows);   // єдиний писар ключа Run (STD-LIFE-02)
        }

        private static void RevealInExplorer(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        private void Sound()
        {
            if (_cfg.PlaySound) { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }
        }

        // УСПІХ — гейтується тумблером «Сповіщення після знімка». Це і є те, що тумблер обіцяє.
        private void Balloon(string title, string text)
        {
            if (!_cfg.ShowBalloon) return;
            try { _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.None); } catch { }
        }

        // ПОМИЛКА — показується ЗАВЖДИ, з іконкою помилки.
        //
        // Доти помилки йшли тим самим Balloon(), тобто над ними стояв `if (!_cfg.ShowBalloon)`.
        // Користувач вимикав тумблер, підписаний «Сповіщення після знімка», щоб не смикали
        // спливаючі підтвердження, — і разом із рекламою успіху глушив «Помилка знімка» та
        // «Гарячу клавішу не зареєстровано». Тобто єдиний канал діагностики вимикався опцією
        // про зручність. Приховати підтвердження і приховати відмову — різні наміри.
        private void Warn(string title, string text)
        {
            try { _tray.ShowBalloonTip(7000, title, text, ToolTipIcon.Error); } catch { }
        }

        private void ExitApp()
        {
            // Обидві події СТАТИЧНІ: без відписки TrayApp лишається живим після виходу.
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
            try { Theme.Changed -= OnThemeChanged; } catch { }
            try { L.Changed -= OnLangChanged; } catch { }
            try
            {
                Native.UnregisterHotKey(_hk.Handle, HK_REGION);
                Native.UnregisterHotKey(_hk.Handle, HK_FULL);
                Native.UnregisterHotKey(_hk.Handle, HK_REGION2);
                Native.UnregisterHotKey(_hk.Handle, HK_FULL2);
            }
            catch { }
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Дублює відписку з ExitApp навмисно: у процес можна вийти й повз ExitApp
                // (закриття сесії Windows), а статична подія переживає форму.
                try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
                try { Theme.Changed -= OnThemeChanged; } catch { }
                try { L.Changed -= OnLangChanged; } catch { }
                try { _tray.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
