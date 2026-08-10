using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using ValeraScreenshot;
using Microsoft.Win32;

// Інсталятор ValeraScreenshot: один exe із вшитим пакетом (payload.zip).
// Дефолтна тека — Program Files (x86)\ValeraScreenshot (з підйомом прав UAC);
// без адмінправ чесно пропонує %LOCALAPPDATA%\Programs\ValeraScreenshot.
// Тихий режим: Setup.exe /S [/D=тека] [/autostart] [/desktop] [/freeprtscr] [/nolaunch]
internal static class SetupMain
{
    [STAThread]
    static int Main(string[] args)
    {
        bool silent = false, autostart = false, desktop = false, freePrt = false, nolaunch = false;
        string dir = null;
        foreach (var a in args)
        {
            var s = a.Trim();
            if (s.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (s.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = s.Substring(3).Trim('"');
            else if (s.Equals("/autostart", StringComparison.OrdinalIgnoreCase)) autostart = true;
            else if (s.Equals("/desktop", StringComparison.OrdinalIgnoreCase)) desktop = true;
            else if (s.Equals("/freeprtscr", StringComparison.OrdinalIgnoreCase)) freePrt = true;
            else if (s.Equals("/nolaunch", StringComparison.OrdinalIgnoreCase)) nolaunch = true;
        }

        // Інсталятор і деінсталятор — окремі бінарники й окремі процеси: конфіга
        // застосунку в них ще (або вже) може не бути, тож мова береться з системи.
        L.Init("auto");
        // ★ БЕЗ ЦЬОГО РЯДКА ІНСТАЛЯТОР БУВ СВІТЛИМ ЗАВЖДИ. Theme має статичний конструктор
        // `static Theme() { ApplyLight(); }` — розумний дефолт, поки не викликано Init(). Застосунок
        // Init() кличе, інсталятор — ні, тож перше вікно, яке бачить людина, ігнорувало і темну
        // тему Windows, і High Contrast. Для High Contrast це не косметика: користувач умикає його
        // тому, що інакше не бачить, а діставав пастельну картку з сірим підписом.
        Theme.Init("auto");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (silent)
        {
            try
            {
                Installer.Run(string.IsNullOrEmpty(dir) ? Installer.DefaultDir() : dir,
                              autostart, desktop, freePrt, !nolaunch, null);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ValeraScreenshot-setup-error.txt"), ex.ToString()); } catch { }
                return 1;
            }
        }

        Application.Run(new SetupForm());
        return 0;
    }
}

internal static class Installer
{
    public static bool IsAdmin()
    {
        try
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string DefaultDir()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(pf, "ValeraScreenshot");
    }

    public static string UserDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\ValeraScreenshot");
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Повне встановлення. progress — колбек для рядка стану (може бути null).
    public static void Run(string dir, bool autostart, bool desktop, bool freePrt, bool launch,
                           Action<string> progress)
    {
        Action<string> log = progress ?? delegate { };

        log(L.S("Зупинка запущеного ВАЛЄРА Скріншот…", "Stopping the running VALERA Screenshot…"));
        try
        {
            foreach (var p in Process.GetProcessesByName("ValeraScreenshot"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }
        catch { }

        log(L.S("Розпакування файлів…", "Unpacking files…"));
        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        // Ресурс-пакет інсталятора: id з standard.bind.json (valerascreenshot_exe). Це zip з exe+доками+cer+uninstall.
        using (var rs = asm.GetManifestResourceStream("valerascreenshot_exe"))
        {
            if (rs == null) throw new InvalidOperationException(L.S("valerascreenshot_exe (пакет) не вшито в інсталятор", "valerascreenshot_exe (the payload) is not embedded in this installer"));
            using (var zip = new ZipArchive(rs, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.Name.Length == 0) continue; // тека
                    string target = Path.Combine(dir, e.FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = e.Open())
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
                        src.CopyTo(dst);
                }
            }
        }
        Directory.CreateDirectory(Path.Combine(dir, "Screenshots"));

        string exe = Path.Combine(dir, "ValeraScreenshot.exe");
        bool admin = IsAdmin();

        log(L.S("Ярлик у меню «Пуск»…", "Start menu shortcut…"));
        string startDir = admin
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "");
        MakeShortcut(Path.Combine(startDir, "ValeraScreenshot.lnk"), exe, dir);

        if (desktop)
        {
            log(L.S("Ярлик на робочому столі…", "Desktop shortcut…"));
            MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Ident.Lnk), exe, dir);
        }

        if (autostart)
        {
            log(L.S("Автозапуск…", "Autostart…"));
            // СПЕРШУ конфіг, потім ключ. Намір автозапуску живе в settings.ini застосунку;
            // ключ Run — проєкція, яку застосунок вирівнює по конфігу на кожному старті.
            // Без засіву конфіга перший же запуск читав дефолтне StartWithWindows=False і
            // видаляв щойно записаний ключ — чекбокс інсталятора «зникав».
            // Адресу дає Config.DirFor(dir) — ТА САМА функція, якою застосунок шукатиме конфіг.
            // Тут стояла жорстко зашита %APPDATA%, і для встановлення без адмінправ (тека в
            // профілі ЗАПИСУВАНА -> конфіг лежить поруч з exe) засів ішов повз, а баг лишався.
            // ★ ПОПРАВКА 2026-08-08: «та сама функція» виявилась необхідною, але не достатньою —
            //   вона відповідала правами ВИКЛИКАЧА. Елевований Run проходив пробу запису в
            //   Program Files і сіяв намір туди, куди неелевований застосунок не заглядає:
            //   чекбокс знову «зникав», тепер на дефолтному адмінському шляху, а в Program Files
            //   лишався settings.ini-сирота. Виправлено в корені: Seed.ConfigDirFor для
            //   адмінських тек відповідає %APPDATA% незалежно від прав (чому — там), тож
            //   елевований засів лягає рівно туди, де шукатиме застосунок, який нижче
            //   запускається de-elevated через explorer.exe. Випадок елевації під ІНШИМ
            //   обліковим записом і далі страхує засів ДО підняття прав у SetupForm.Install.
            Seed.SeedAutostartIni(Path.Combine(Seed.ConfigDirFor(dir), "settings.ini"));
            using (var k = Registry.CurrentUser.OpenSubKey(Ident.RunKey, true))
                if (k != null) k.SetValue(Ident.RunValue, "\"" + exe + "\"");
        }

        if (freePrt)
        {
            log(L.S("Звільнення PrtScr від Snipping Tool…", "Freeing PrtScr from Snipping Tool…"));
            using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard"))
                k.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
        }

        // Картка ARP пишеться з ОДНОГО місця — Arp.cs, спільного з self-install (STD-LIFE-04).
        // Доти тут лежала власна копія з захардкодженою айдентикою, і вона вже розійшлася з
        // застосунком: показувала застарілого видавця після того, як метадані стали
        // нейтральними. Рівно той клас дубля, проти якого існує Ident.cs.
        log(L.S("Реєстрація в «Програмах»…", "Registering in Apps & features…"));
        var root = admin ? Registry.LocalMachine : Registry.CurrentUser;
        string uninstExe = Path.Combine(dir, "Uninstall.exe");
        using (var k = root.CreateSubKey(Ident.UninstallKey))
            Arp.Write(k, exe, dir, "\"" + uninstExe + "\"", "\"" + uninstExe + "\" " + Ident.SilentSwitch);

        if (launch)
        {
            log(L.S("Запуск…", "Starting…"));
            // Інсталятор часто працює з піднятими правами (UAC) — застосунок же має
            // жити в сесії користувача. Запуск через explorer.exe знімає elevation.
            try { Process.Start("explorer.exe", "\"" + exe + "\""); } catch { }
        }
        log(L.S("Готово.", "Done."));
    }

    private static void MakeShortcut(string lnkPath, string exe, string workDir)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = exe;
            sc.WorkingDirectory = workDir;
            sc.IconLocation = exe + ",0";
            sc.Description = L.NameFull;
            sc.Save();
        }
        catch { }
    }
}

// Майстер встановлення — одна сторінка в стилі Office.
// ★ ThemedForm, НЕ Form. Успадкування від голої Form означало: заголовок вікна лишається
// СВІТЛИМ навіть у темній палітрі (та сама «біла кромка», яку ThemedForm лікує в застосунку),
// тло не перечитується, і Retheme не проходить по дереву — тобто чотири чекбокси лишалися б
// з кольором тексту, проставленим у конструкторі.
internal class SetupForm : ThemedForm
{
    private PaddedTextBox _tbDir;
    private CheckBox _cbDesktop, _cbAutostart, _cbFreePrt, _cbLaunch;
    private OfficeButton _btnInstall, _btnCancel;
    private Label _status;

    public SetupForm()
    {
        Text = L.S("Встановлення ВАЛЄРА Скріншот ", "Installing VALERA Screenshot ") + Ver.Number;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.PageBg;
        Font = Theme.Body;
        ClientSize = new Size(520, 428);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int pad = 24, w = ClientSize.Width - pad * 2;
        Controls.Add(Ui.Title(L.S("Встановлення ВАЛЄРА Скріншот ", "Installing VALERA Screenshot ") + Ver.Number, pad, 18, w));
        Controls.Add(Ui.Caption(L.S("Локальні знімки екрана · без мережі · © 2026 Павло Ісаєв", "Local screen captures · no network · © 2026 Pavlo Isaiev"), pad, 56, w));

        var card = new Card { Left = pad, Top = 88, Width = w, Height = 250 };
        Controls.Add(card);

        card.Controls.Add(Ui.Section(L.S("Тека встановлення", "Install folder"), 18, 16, 300));
        _tbDir = Ui.Input(18, 44, card.Width - 130);
        _tbDir.Text = Installer.DefaultDir();
        card.Controls.Add(_tbDir);
        var browse = Ui.Btn(L.S("Огляд…", "Browse…"), card.Width - 104, 42, 86, BtnKind.Secondary);
        browse.Height = 28;
        browse.Click += delegate
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _tbDir.Text = Path.Combine(dlg.SelectedPath, "ValeraScreenshot");
            }
        };
        card.Controls.Add(browse);
        card.Controls.Add(Ui.Caption(L.S("Якщо тека потребує прав адміністратора — інсталятор запитає підняття (UAC).", "If the folder needs administrator rights, the installer will ask for elevation (UAC)."), 18, 74, card.Width - 36));

        card.Controls.Add(Ui.Section(L.S("Опції", "Options"), 18, 104, 300));
        _cbDesktop = MakeCheck(card, L.S("Ярлик на робочому столі", "Desktop shortcut"), 132, true);
        _cbAutostart = MakeCheck(card, L.S("Запускати разом із Windows", "Start with Windows"), 158, false);
        _cbFreePrt = MakeCheck(card, L.S("Звільнити PrtScr від Snipping Tool", "Free PrtScr from Snipping Tool"), 184, false);
        _cbLaunch = MakeCheck(card, L.S("Запустити після встановлення", "Launch after installing"), 210, true);

        _status = Ui.Body("", pad, 348, w);
        _status.ForeColor = Theme.TextSecondary;
        Controls.Add(_status);

        _btnInstall = Ui.Btn(L.S("Встановити", "Install"), ClientSize.Width - pad - 232, 378, 120, BtnKind.Primary);
        _btnInstall.Click += delegate { Install(); };
        Controls.Add(_btnInstall);

        _btnCancel = Ui.Btn(L.S("Скасувати", "Cancel"), ClientSize.Width - pad - 104, 378, 104, BtnKind.Secondary);
        _btnCancel.Click += delegate { Close(); };
        Controls.Add(_btnCancel);

        AcceptButton = _btnInstall;
        CancelButton = _btnCancel;
    }

    private CheckBox MakeCheck(Card card, string text, int y, bool val)
    {
        var cb = new CheckBox
        {
            Text = text, Left = 18, Top = y, Width = card.Width - 36, Height = 22,
            Checked = val, Font = Theme.Body, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent
        };
        card.Controls.Add(cb);
        return cb;
    }

    private void Install()
    {
        string dir = _tbDir.Text.Trim();
        if (dir.Length == 0) { _status.Text = L.S("Вкажіть теку встановлення.", "Please specify an install folder."); return; }

        // тека без прав запису → підняття або чесний відступ у профіль
        if (!Installer.CanWrite(dir) && !Installer.IsAdmin())
        {
            var r = Ui.Msg(this,
                L.S("Тека «", "The folder '") + dir + L.S("» потребує прав адміністратора.\n\n", "' needs administrator rights.\n\n") +
                L.S("ТАК — перезапустити інсталятор із підняттям прав (UAC).\n", "YES — restart the installer elevated (UAC).\n") +
                L.S("НІ — встановити без адмінправ у:\n", "NO — install without admin rights into:\n") + Installer.UserDir(),
                L.S("ВАЛЄРА Скріншот — потрібні права", "VALERA Screenshot — rights required"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                try
                {
                    // конфіг сіється ЩЕ ДО підняття прав — у сесії користувача. Елевований процес
                    // теж посіє (ідемпотентно), але при елевації під ІНШИМ обліковим записом його
                    // %APPDATA% — чужий, і без цього рядка чекбокс знову б «зникав».
                    if (_cbAutostart.Checked)
                        Seed.SeedAutostartIni(Path.Combine(Seed.ConfigDirFor(dir), "settings.ini"));
                    var psi = new ProcessStartInfo(Application.ExecutablePath)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = "/S \"/D=" + dir + "\"" +
                                    (_cbAutostart.Checked ? " /autostart" : "") +
                                    (_cbDesktop.Checked ? " /desktop" : "") +
                                    (_cbFreePrt.Checked ? " /freeprtscr" : "") +
                                    (_cbLaunch.Checked ? "" : " /nolaunch")
                    };
                    Process.Start(psi);
                    Close();
                    return;
                }
                catch
                {
                    _status.Text = L.S("Підняття прав відхилено — оберіть іншу теку.", "Elevation was declined — choose a different folder.");
                    return;
                }
            }
            dir = Installer.UserDir();
            _tbDir.Text = dir;
        }

        _btnInstall.Enabled = false;
        try
        {
            Installer.Run(dir, _cbAutostart.Checked, _cbDesktop.Checked, _cbFreePrt.Checked,
                _cbLaunch.Checked,
                delegate(string s) { _status.Text = s; _status.Refresh(); Application.DoEvents(); });
            _status.Text = L.S("Готово! ВАЛЄРА Скріншот встановлено в ", "Done! VALERA Screenshot was installed into ") + dir;
            _btnCancel.Text = L.S("Закрити", "Close");
            // ★ Заголовком тут стояв ЛІТЕРАЛ "ValeraScreenshot" — технічний ідентифікатор із
            //   Ident.AppId, а не назва продукту. Тобто останнє, що інсталятор каже людині,
            //   називало застосунок не так, як усе інше вікно на два рядки вище (L.Name ->
            //   «ВАЛЄРА Скріншот»), і не так, як ярлик, який щойно з'явився в неї на столі.
            //   Технічна айдентика — для реєстру, mutex і імені файла; для ока є L.Name.
            Ui.Msg(this,
                L.Name + " " + Ver.Number + L.S(" встановлено.\n\n", " is installed.\n\n") +
                L.S("Ctrl+Shift+4 — знімок області\nCtrl+Shift+3 — весь екран\n\n", "Ctrl+Shift+4 — capture a region\nCtrl+Shift+3 — whole screen\n\n") +
                L.S("Мануал: MANUAL.txt у теці встановлення.", "Manual: MANUAL.txt in the install folder."),
                L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = L.S("Помилка: ", "Error: ") + ex.Message;
            _btnInstall.Enabled = true;
        }
    }
}
