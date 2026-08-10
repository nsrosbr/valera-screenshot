using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ValeraScreenshot
{
    // Self-install / clean-uninstall. Registers in Windows "Apps & features" so there is a
    // proper uninstaller; uninstall removes the exe, config, Start-menu shortcut and startup entry.
    internal static class Installer
    {
        // Айдентика — з Ident.cs, єдиного джерела (STD-IDENT-01), спільного з setup\Setup.cs.
        private const string AppName = Ident.AppName;
        private const string DisplayName = Ident.DisplayName;
        private const string UninstallKey = Ident.UninstallKey;
        private const string RunKey = Ident.RunKey;

        // Намір автозапуску живе в КОНФІГУ; ключ Run — лише його проєкція, яку ApplyStartup
        // приводить у відповідність на кожному старті. Хто пише ключ, не записавши конфіг,
        // програє перший же запуск: дефолтне StartWithWindows=False слухняно видаляє ключ.
        // Саме так «зник» чекбокс інсталятора. Тому будь-який установлювач спершу сіє конфіг.
        // Патч текстовий і точковий: інші рядки settings.ini (гарячі клавіші, тека, тема)
        // належать застосунку і НЕ переписуються.
        public static void SeedAutostartIni(string iniPath) { Seed.SeedAutostartIni(iniPath); }

        // The SINGLE source of truth for the autostart Run entry (STD-LIFE-02). The settings toggle
        // and uninstall both call this; the Run key is opened for write nowhere else, so two copies
        // of the logic cannot diverge on the first change.
        public static void SetAutostart(bool enabled) { SetAutostart(enabled, null); }

        // exePath = на ЩО має вказувати автозапуск. Раніше завжди писався Application.ExecutablePath,
        // тобто шлях процесу, який щойно себе встановив — зазвичай завантажений файл у «Завантаженнях».
        // Користувач видаляв його, і автозапуск указував у порожнечу.
        public static void SetAutostart(bool enabled, string exePath)
        {
            if (IsPortable()) return;   // portable: never write autostart (STD-LIFE-03)
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (run == null) return;
                    if (enabled)
                        run.SetValue(AppName, "\"" + (string.IsNullOrEmpty(exePath) ? Application.ExecutablePath : exePath) + "\"");
                    else if (run.GetValue(AppName) != null)
                        run.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
        }
        public static string InstalledExe { get { return Path.Combine(InstallDir, Ident.Exe); } }
        private static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Ident.Lnk); }
        }

        // Портативний режим — файл-маркер поруч із exe. Явний вибір користувача, що ВИМИКАЄ будь-яке
        // самовстановлення і будь-який запис у реєстр (потрібен і для флешки, і для розробки).
        public static bool IsPortable()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                return File.Exists(Path.Combine(dir, "portable.txt")) ||
                       File.Exists(Path.Combine(dir, "portable.flag"));
            }
            catch { return false; }
        }

        // ДЕ застосунок справді встановлено. Джерело правди — InstallLocation у картці ARP: спершу
        // per-user (HKCU), потім машинна інсталяція від Setup.exe (HKLM). null = не встановлено ніде.
        public static string FindInstalledExe()
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = root.OpenSubKey(UninstallKey))
                    {
                        if (k == null) continue;
                        string loc = k.GetValue("InstallLocation") as string;
                        if (string.IsNullOrEmpty(loc)) continue;
                        string exe = Path.Combine(loc, Ident.Exe);
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            try { if (File.Exists(InstalledExe)) return InstalledExe; } catch { }
            return null;
        }

        public static bool IsInstalledCopy()
        {
            try { return SamePath(Application.ExecutablePath, FindInstalledExe()); }
            catch { return false; }
        }

        public static string RegisteredUninstallCommand()
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = root.OpenSubKey(UninstallKey))
                    {
                        if (k == null) continue;
                        string cmd = k.GetValue("UninstallString") as string;
                        if (!string.IsNullOrEmpty(cmd)) return cmd;
                    }
                }
                catch { }
            }
            return null;
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        internal static System.Version FileVer(string path)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                System.Version v;
                return (!string.IsNullOrEmpty(fvi.FileVersion) && System.Version.TryParse(fvi.FileVersion.Trim(), out v)) ? v : null;
            }
            catch { return null; }
        }

        // Рішення життєвого циклу. true = ми передали керування встановленій копії й ЦЕЙ процес має
        // тихо завершитись; false = ми і є встановлена копія (або портатив) — працюємо далі.
        // Без цього в полі виходили ДВІ версії поряд: у системі стара, збоку свіжа, мутекс гасив
        // системну як «уже запущено», а автозапуск «не завжди працював».
        public static bool EnsureInstalled()
        {
            if (IsPortable()) return false;   // явний вибір користувача — нічого не чіпаємо
            try
            {
                string me = Application.ExecutablePath;
                string installed = FindInstalledExe();

                if (SamePath(me, installed)) return false;          // ми і є встановлена копія
                if (installed == null)
                {
                    Install(true);
                    // Конфіг ВСТАНОВЛЕНОЇ копії, а не свій власний. Тут стояло Config.Dir — тобто
                    // тека процесу, який зараз біжить (часто «Завантаження»): намір сідав туди,
                    // встановлена копія його не бачила і першим стартом стирала ключ Run.
                    SeedAutostartIni(Path.Combine(Config.DirFor(InstallDir), "settings.ini"));
                    SetAutostart(true, InstalledExe);
                    Launch(InstalledExe);
                    return true;
                }

                System.Version mine = FileVer(me), theirs = FileVer(installed);
                if (mine != null && theirs != null && mine > theirs)
                {
                    // Ми новіші -> оновлюємо ВСТАНОВЛЕНУ копію на місці, а не плодимо другу.
                    if (CopyOver(me, installed)) { RefreshArp(installed); Launch(installed); return true; }
                    if (Elevate("install-over", installed)) return true;   // один UAC для Program Files
                    return false;
                }

                Launch(installed);
                return true;
            }
            catch { return false; }
        }

        // Елевована гілка EnsureInstalled: замінити встановлену копію собою. Викликається з Main.
        public static void InstallOverCommand(string[] args)
        {
            if (args == null || args.Length < 2) return;
            string target = args[1];
            try
            {
                // БРЕХНЯ ПРО УСПІХ (виправлено 2026-08-08): підсумок 20 спроб CopyOver тут
                // ВІДКИДАВСЯ — після всіх відмов код однаково штампував в ARP версію, якої
                // НЕМА на диску, і мовчки перезапускав СТАРИЙ exe. Користувач схвалював UAC
                // на оновлення і без жодного слова отримував стару версію з новою цифрою в
                // «Програмах». Тепер підсумок зберігається: без копії — жодного RefreshArp,
                // чесне вікно і запис у Diag; старий exe запускаємо в будь-якому разі, щоб
                // застосунок лишився робочим.
                bool copied = false;
                for (int i = 0; i < 20 && !(copied = CopyOver(Application.ExecutablePath, target)); i++)
                    System.Threading.Thread.Sleep(250);
                if (copied) RefreshArp(target);
                else
                {
                    Diag.Log(L.S("оновлення поверх: файл не замінено після 20 спроб: ",
                                 "install-over: file not replaced after 20 attempts: ") + target);
                    Ui.Msg(L.S("Не вдалося оновити встановлену копію:\n", "Could not update the installed copy:\n") + target +
                           L.S("\n\nПрацює попередня версія.", "\n\nThe previous version keeps running."),
                        L.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                LaunchDeElevated(target);
            }
            catch { }
        }

        private static bool CopyOver(string src, string dst)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(AppName))
                {
                    try { if (p.Id != Process.GetCurrentProcess().Id && SamePath(p.MainModule.FileName, dst)) { p.Kill(); p.WaitForExit(3000); } }
                    catch { }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                return true;
            }
            catch { return false; }
        }

        private static bool Elevate(string verb, string arg)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, verb + " \"" + arg + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi);
                return true;
            }
            catch { return false; }   // користувач натиснув «Ні» в UAC
        }

        private static void Launch(string exe)
        {
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
        }

        // Запуск НЕ від адміністратора з елевованого процесу. Прямий Process.Start успадкував би права,
        // і трей-застосунок жив би адміністратором: зайві права, зламаний drag-and-drop, і його не
        // може зупинити навіть звичайний скрипт складання. Explorer.exe працює на середньому рівні
        // цілісності, тож запущений ним процес отримує звичайні права користувача.
        private static void LaunchDeElevated(string exe)
        {
            try { Process.Start("explorer.exe", "\"" + exe + "\""); }
            catch { Launch(exe); }
        }

        // Самолікування при кожному старті встановленої копії: автозапис міг лишитися від старого
        // розташування, а картка ARP мусить показувати ВЕРСІЮ, ЩО ПРАЦЮЄ (оновлення міняє exe, не картку).
        public static void SelfHeal(Config cfg)
        {
            if (IsPortable()) return;   // portable: self-heal touches no registry
            string me = Application.ExecutablePath;

            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string cur = run == null ? null : run.GetValue(AppName) as string;
                    bool broken = cfg.StartWithWindows &&
                                  (string.IsNullOrEmpty(cur) || !SamePath(cur.Trim('"'), me));
                    if (broken) { SetAutostart(true); Diag.Log(L.S("самолікування: автозапуск -> ", "self-heal: autostart -> ") + me); }
                }
            }
            catch { }

            try
            {
                using (var run = Registry.LocalMachine.OpenSubKey(RunKey, true))
                {
                    string cur = run == null ? null : run.GetValue(AppName) as string;
                    if (!string.IsNullOrEmpty(cur) && !File.Exists(cur.Trim('"')))
                    {
                        try { run.DeleteValue(AppName, false); Diag.Log(L.S("самолікування: прибрано мертвий HKLM-автозапуск", "self-heal: removed a dead HKLM autostart entry")); }
                        catch { Diag.Log(L.S("самолікування: мертвий HKLM-автозапуск (потрібні права адміністратора)", "self-heal: dead HKLM autostart entry (administrator rights required)")); }
                    }
                }
            }
            catch { }

            try
            {
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using (var k = root.OpenSubKey(UninstallKey, true))
                    {
                        if (k == null) continue;
                        string loc = k.GetValue("InstallLocation") as string;
                        if (string.IsNullOrEmpty(loc) || !SamePath(Path.Combine(loc, Ident.Exe), me)) continue;
                        if ((k.GetValue("DisplayVersion") as string) == Ver.Number) continue;
                        Arp.Refresh(k, me, Path.GetDirectoryName(me));   // команду видалення НЕ чіпаємо
                        Diag.Log(L.S("самолікування: картка ARP -> ", "self-heal: ARP card -> ") + Ver.Number);
                    }
                }
            }
            catch { }
        }

        private static void RefreshArp(string exe)
        {
            try
            {
                string dir = Path.GetDirectoryName(exe);
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using (var k = root.OpenSubKey(UninstallKey, true))
                    {
                        if (k == null) continue;
                        string loc = k.GetValue("InstallLocation") as string;
                        if (string.IsNullOrEmpty(loc) || !SamePath(Path.Combine(loc, Ident.Exe), exe)) continue;
                        Arp.Refresh(k, exe, dir);
                    }
                }
            }
            catch { }
        }

        public static void Install(bool silent)
        {
            try
            {
                string src = Application.ExecutablePath;
                Directory.CreateDirectory(InstallDir);
                if (!string.Equals(src, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, InstalledExe, true);

                CreateShortcut(ShortcutPath, InstalledExe, InstallDir);

                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                    Arp.Write(k, InstalledExe, InstallDir,
                        "\"" + InstalledExe + "\" /uninstall",
                        "\"" + InstalledExe + "\" /uninstall " + Ident.SilentSwitch);

                if (!silent)
                {
                    try { Process.Start(InstalledExe); } catch { }
                    Ui.Msg(L.Name + L.S(" встановлено.\n\nЗапущено з іконкою в треї. Видалити можна через\n«Параметри → Програми» або деінсталятором.",
                        " installed.\n\nRunning with an icon in the tray. You can remove it via\n“Settings → Apps” or the uninstaller."),
                        L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Ui.Msg(L.S("Помилка встановлення: ", "Installation error: ") + ex.Message, L.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void Uninstall(bool silent, bool removeConfig)
        {
            if (!silent)
            {
                var r = Ui.Msg(
                    L.S("Видалити ", "Uninstall ") + L.Name + L.S("?\n\n" +
                        "«Так» — видалити програму РАЗОМ із налаштуваннями (гарячі клавіші, тека збереження, параметри).\n\n" +
                        "«Ні» — видалити програму, але ЗАЛИШИТИ налаштування.\n\n" +
                        "Ваші ЗНІМКИ не видаляються в жодному разі.",
                        "?\n\n" +
                        "“Yes” — remove the program TOGETHER WITH its settings (hotkeys, save folder, options).\n\n" +
                        "“No” — remove the program but KEEP its settings.\n\n" +
                        "Your SCREENSHOTS are never deleted."),
                    L.Name + L.S(" — видалення", " — uninstall"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                removeConfig = (r == DialogResult.Yes);

                if (NeedsElevationToUninstall() && ElevateUninstall(removeConfig)) return;
            }

            // 1) зупинити всі інші екземпляри
            try
            {
                int me = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName(AppName))
                {
                    try { if (p.Id != me) { p.Kill(); p.WaitForExit(3000); } } catch { }
                }
            }
            catch { }

            // 2) ярлики (per-user І common), автозапуск в ОБОХ гілках, картка ARP в обох гілках.
            //    Раніше прибиралося лише per-user, тож для інсталяції в Program Files «Видалити»
            //    майже нічого не робило: ярлик, HKLM-автозапуск і сама тека лишалися на місці.
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try
            {
                string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Ident.Lnk);
                if (File.Exists(common)) File.Delete(common);
                string deskAll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Ident.Lnk);
                if (File.Exists(deskAll)) File.Delete(deskAll);
                string deskUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Ident.Lnk);
                if (File.Exists(deskUser)) File.Delete(deskUser);
            }
            catch { }
            SetAutostartForce(false);   // через єдиного писаря, але й для портативу під час видалення
            try
            {
                using (var run = Registry.LocalMachine.OpenSubKey(RunKey, true))
                    if (run != null && run.GetValue(AppName) != null) run.DeleteValue(AppName, false);
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }

            // 3) ЗАВЖДИ прибрати діагностичний лог і його маркер — навіть коли налаштування лишаємо.
            try { Diag.ClearLog(); } catch { }
            try { string m = Path.Combine(Config.Dir, "debug.on"); if (File.Exists(m)) File.Delete(m); } catch { }

            // 3b) налаштування — лише за повного видалення. НІКОЛИ не чіпаємо теку знімків: у нашій
            //     портативній моделі Config.Dir часто збігається з текою застосунку, і сліпе
            //     Directory.Delete(Config.Dir, true) знищило б усі знімки користувача.
            if (removeConfig)
            {
                try { if (File.Exists(Config.IniPath)) File.Delete(Config.IniPath); } catch { }
                try { string h = Path.Combine(Config.Dir, "_hotkeys.txt"); if (File.Exists(h)) File.Delete(h); } catch { }
                try { string c = Path.Combine(Config.Dir, "crash.log"); if (File.Exists(c)) File.Delete(c); } catch { }
            }

            // ПОРЯДОК (виправлено 2026-08-08): самовидалення планувалося ДО модального вікна,
            // а згенерований cmd чекає фіксовані 2 секунди — вони спливали, поки вікно тримало
            // процес (і його exe) живим, del бив у ще заблокований файл, і exe разом із текою
            // переживали КОЖНЕ інтерактивне видалення. Тепер cmd стартує після закриття вікна:
            // далі лише return з Main, і 2 секунд вистачає з запасом. Та сама правка порядку —
            // в setup\Uninstall.cs.
            if (!silent)
                Ui.Msg(
                    L.Name + L.S(" видалено. Знімки збережено у:\n", " removed. Screenshots kept at:\n") + SafeSaveDir(),
                    L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

            ScheduleSelfDelete();
        }

        // Автозапуск під час ВИДАЛЕННЯ треба зняти навіть якщо поруч лежить portable.txt: маркер
        // означає «не додавай себе в систему», а не «лиши по собі сміття в реєстрі».
        private static void SetAutostartForce(bool enabled)
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (run == null) return;
                    if (enabled) run.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else if (run.GetValue(AppName) != null) run.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        private static string SafeSaveDir()
        {
            try { return Config.Load().EffectiveSaveDir; } catch { return Config.Dir; }
        }

        public static bool NeedsElevationToUninstall()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!SamePath(dir, InstallDir))
                {
                    string probe = Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllText(probe, "x"); File.Delete(probe); }
                    catch { return true; }
                }
                using (var k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                    if (k != null)
                    {
                        try { using (var w = Registry.LocalMachine.OpenSubKey(UninstallKey, true)) { if (w == null) return true; } }
                        catch { return true; }
                    }
            }
            catch { }
            return false;
        }

        public static bool ElevateUninstall(bool removeConfig)
        {
            return Elevate("uninstall", removeConfig ? Ident.SilentSwitch : Ident.SilentSwitch + " /keepconfig");
        }

        // Картка ARP на вимогу стороннього інсталятора (install.ps1 -> ValeraScreenshot.exe /install-card).
        // Команда видалення веде в uninstall.ps1, якщо той лежить поруч: прибирати має той, хто
        // ставив. Інакше — власний /uninstall.
        public static bool WriteArpCardFor(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string exe = Path.Combine(dir, Ident.Exe);
                if (!File.Exists(exe)) return false;
                string ps1 = Path.Combine(dir, "uninstall.ps1");
                string un, quiet;
                if (File.Exists(ps1))
                {
                    string head = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"";
                    un = head;
                    quiet = head + " -Silent";
                }
                else
                {
                    un = "\"" + exe + "\" /uninstall";
                    quiet = "\"" + exe + "\" /uninstall " + Ident.SilentSwitch;
                }
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                    Arp.Write(k, exe, dir, un, quiet);
                return true;
            }
            catch { return false; }
        }

        // Файли, які кладе САМЕ інсталяція (Installer.Install / setup\Setup.cs). Видаляти можна
        // тільки їх — усе інше в теці належить користувачу.
        // ★ MANUAL_EN.txt / README_EN.md ДОДАНО 2026-07-29 разом із їх появою в payload-і
        //   інсталятора. Списки мусять іти в ногу: файл, покладений інсталяцією, але невідомий
        //   деінсталятору, не просто лишається сміттям — він ще й блокує НЕРЕКУРСИВНИЙ rmdir,
        //   тобто тека встановлення переживає видалення. Звіряють L24/L25.
        internal static readonly string[] OwnedFiles =
        {
            Ident.Exe, Ident.CerFile, "Uninstall.exe",
            "MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md"
        };

        // Прибирає ЛИШЕ те, що поклали ми, і знімає теку НЕРЕКУРСИВНО.
        //
        // Тут був найгірший дефект продукту: `rmdir /s /q "<тека exe>"`. Гард вимагав, щоб тека
        // знімків ІСНУВАЛА, тож на копії, якою ще не робили жодного знімка, він не спрацьовував —
        // і команда зносила рекурсивно теку, де просто лежав exe. Портатив у «Документах» ->
        // `/uninstall` стирав «Документи». А діалог видалення при цьому обіцяє: «Ваші ЗНІМКИ не
        // видаляються в жодному разі».
        //
        // Тепер безпека не залежить від жодної умови: рекурсивного видалення НЕМАЄ взагалі.
        // `rmdir` без /s знімає теку тільки якщо вона порожня — тобто якщо в ній не лишилось нічого,
        // крім наших файлів. Будь-що чуже (знімки, документи, підтеки) автоматично рятує теку.
        // Команда винесена в ЧИСТУ функцію, щоб її можна було перевірити рядком, не видаляючи
        // нічого насправді (STD-GATE-08). Інакше єдиний спосіб протестувати найнебезпечніший
        // код продукту — дати йому щось стерти.
        internal static string BuildSelfDeleteCommand(string self, string installDir)
        {
            string liveDir = Path.GetDirectoryName(self);
            bool separate = !SamePath(liveDir, installDir);

            var sb = new System.Text.StringBuilder();
            sb.Append("/c timeout /t 2 /nobreak >nul");

            // 1) наші файли в теці встановлення
            foreach (string name in OwnedFiles)
                sb.Append(" & del /f /q \"").Append(Path.Combine(installDir, name)).Append("\"");
            // 2) наші файли в теці, звідки нас запустили (портатив/розпакована копія)
            if (separate)
            {
                foreach (string name in OwnedFiles)
                    sb.Append(" & del /f /q \"").Append(Path.Combine(liveDir, name)).Append("\"");
                sb.Append(" & del /f /q \"").Append(self).Append("\"");
            }
            // 3) теки — НЕРЕКУРСИВНО. Не порожня -> команда просто не спрацює, і це правильно.
            sb.Append(" & rmdir \"").Append(installDir).Append("\" 2>nul");
            if (separate)
                sb.Append(" & rmdir \"").Append(liveDir).Append("\" 2>nul");
            return sb.ToString();
        }

        private static void ScheduleSelfDelete()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    BuildSelfDeleteCommand(Application.ExecutablePath, InstallDir))
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static void CreateShortcut(string lnkPath, string target, string workDir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { DisplayName });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }
    }
}
