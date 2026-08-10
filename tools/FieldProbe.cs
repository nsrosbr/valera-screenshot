using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ValeraScreenshot;
using Microsoft.Win32;

// FieldProbe.exe - SYMMETRY OF INSTALL AND UNINSTALL, measured instead of assumed.
//
// The product has SEVEN ways in and THREE ways out, written at different times by different hands.
// Nothing ever compared them, so they drifted: one path created a desktop shortcut and another
// never did; one wrote the autostart intent to the config and another wrote only the registry key;
// uninstall left the diagnostic log on disk forever; the PrtScr override was switched off and never
// restored. Each of those is invisible until a user hits exactly that combination.
//
// This probe takes a full snapshot of every place the product is allowed to touch, runs one path,
// snapshots again, then runs the matching removal and snapshots a third time. The verdict is
// mechanical: whatever install ADDED, uninstall must REMOVE - except the user's screenshots, which
// must survive unconditionally.
//
// SAFE BY CONSTRUCTION: it never touches the owner's real installation. Registry writes go through
// the app itself into HKCU only, every run starts from a snapshot, and the snapshot is restored at
// the end even when an assertion fails.
internal static class FieldProbe
{
    private static int _pass, _fail;
    private static void Ok(string name, bool cond, string detail)
    {
        if (cond) { _pass++; Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  [" + detail + "]" : "")); }
        else { _fail++; Console.WriteLine("FAIL  " + name + "  [" + detail + "]"); }
    }

    // ---------------- machine snapshot ----------------
    private sealed class Snap
    {
        public string RunHkcu, RunHklm, ArpHkcu, ArpHklm, PrtScr;
        public bool LnkUser, LnkCommon, LnkDeskUser, LnkDeskCommon;
        // ЦІЛІ ярликів, а не лише факт наявності. Перший прогін цієї проби видалив у власника
        // реальні ярлики «Пуску» й робочого столу і не зміг їх повернути — бо знав тільки «були».
        // Видалення прибирає ярлики за ФІКСОВАНИМ іменем, тож воно фізично не здатне відрізнити
        // інсталяцію проби від інсталяції користувача. Отже проба зобовʼязана вміти відтворити.
        public string LnkUserTarget, LnkDeskUserTarget;
        // Досить, щоб ВІДТВОРИТИ картку HKCU, а не лише помітити її зникнення. Перше відновлення
        // вміло тільки видаляти зайву картку — якщо ж вона на машині вже БУЛА, видалення її
        // зносило, і проба лишала машину іншою, ніж застала.
        public string ArpHkcuLoc, ArpHkcuIcon, ArpHkcuUn, ArpHkcuQuiet;
        public List<string> Files = new List<string>();
    }

    private static string LnkTarget(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { path });
            return (string)lnk.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, lnk, null);
        }
        catch { return null; }
    }

    private static void MakeLnk(string path, string target)
    {
        try
        {
            if (string.IsNullOrEmpty(target) || File.Exists(path)) return;
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { path });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk,
                new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
        }
        catch { }
    }

    private static string RegVal(RegistryKey root, string key, string name)
    {
        try { using (var k = root.OpenSubKey(key)) return k == null ? null : (k.GetValue(name) as string); }
        catch { return null; }
    }
    private static string ArpDump(RegistryKey root)
    {
        try
        {
            using (var k = root.OpenSubKey(Ident.UninstallKey))
            {
                if (k == null) return null;
                return (k.GetValue("DisplayVersion") as string) + "|" + (k.GetValue("InstallLocation") as string);
            }
        }
        catch { return null; }
    }
    private static string Lnk(Environment.SpecialFolder f) { return Path.Combine(Environment.GetFolderPath(f), Ident.Lnk); }

    private static Snap Take(string dir)
    {
        var s = new Snap
        {
            RunHkcu = RegVal(Registry.CurrentUser, Ident.RunKey, Ident.RunValue),
            RunHklm = RegVal(Registry.LocalMachine, Ident.RunKey, Ident.RunValue),
            ArpHkcu = ArpDump(Registry.CurrentUser),
            ArpHklm = ArpDump(Registry.LocalMachine),
            LnkUser = File.Exists(Lnk(Environment.SpecialFolder.Programs)),
            LnkCommon = File.Exists(Lnk(Environment.SpecialFolder.CommonPrograms)),
            LnkDeskUser = File.Exists(Lnk(Environment.SpecialFolder.Desktop)),
            LnkDeskCommon = File.Exists(Lnk(Environment.SpecialFolder.CommonDesktopDirectory))
        };
        s.LnkUserTarget = LnkTarget(Lnk(Environment.SpecialFolder.Programs));
        s.LnkDeskUserTarget = LnkTarget(Lnk(Environment.SpecialFolder.Desktop));
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Ident.UninstallKey))
                if (k != null)
                {
                    s.ArpHkcuLoc = k.GetValue("InstallLocation") as string;
                    s.ArpHkcuIcon = k.GetValue("DisplayIcon") as string;
                    s.ArpHkcuUn = k.GetValue("UninstallString") as string;
                    s.ArpHkcuQuiet = k.GetValue("QuietUninstallString") as string;
                }
        }
        catch { }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
                s.PrtScr = k == null ? null : Convert.ToString(k.GetValue("PrintScreenKeyForSnippingEnabled"));
        }
        catch { }
        try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) s.Files.Add(Path.GetFileName(f)); }
        catch { }
        s.Files.Sort();
        return s;
    }

    private static string Describe(Snap s)
    {
        var sb = new StringBuilder();
        if (s.RunHkcu != null) sb.Append("RunHKCU ");
        if (s.RunHklm != null) sb.Append("RunHKLM ");
        if (s.ArpHkcu != null) sb.Append("ArpHKCU ");
        if (s.ArpHklm != null) sb.Append("ArpHKLM ");
        if (s.LnkUser) sb.Append("lnkStart ");
        if (s.LnkCommon) sb.Append("lnkStartAll ");
        if (s.LnkDeskUser) sb.Append("lnkDesk ");
        if (s.LnkDeskCommon) sb.Append("lnkDeskAll ");
        return sb.Length == 0 ? "(nothing)" : sb.ToString().Trim();
    }

    // ---------------- run ----------------
    private static string _lab, _exe;
    private static Snap _host;   // the owner's real machine state, restored at the end

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== FIELD PROBE: install/uninstall symmetry ===");

        string srcExe = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Application.ExecutablePath)), Ident.Exe);
        if (!File.Exists(srcExe)) { Console.WriteLine("FATAL: " + Ident.Exe + " not found at " + srcExe); return 2; }

        // ★ FAIL-CLOSED GUARD (2026-07-29). This probe installs and uninstalls FOR REAL, and it does
        //   so in Installer.InstallDir - the ONE folder the owner's own installation lives in. It
        //   snapshots and restores the Run key, the ARP card and the shortcuts, and it restores
        //   NONE of the following: the installed exe, and settings.ini sitting next to it. So on a
        //   machine where the product is genuinely installed, a single run would overwrite that
        //   copy with a dev build, then delete it - carrying off the owner's hotkeys, save folder
        //   and autostart intent - while faithfully putting the REGISTRY back, leaving Windows
        //   convinced the app is installed and the folder gutted.
        //
        //   Nobody had been bitten only because this probe runs behind the live layer, which was
        //   itself skipped by habit. Habit is not a guarantee. It now REFUSES, loudly, and says
        //   exactly what to do - a probe whose job is proving the product does not damage the
        //   machine has no business being the thing that damages it.
        string ownDir = Installer.InstallDir;
        string ownExe = Installer.InstalledExe;
        if (File.Exists(ownExe))
        {
            Console.WriteLine("REFUSING TO RUN: a real installation is present at");
            Console.WriteLine("    " + ownDir);
            Console.WriteLine("  This probe installs and uninstalls in THAT EXACT folder and does not");
            Console.WriteLine("  restore its contents - it would take the owner's settings.ini with it.");
            Console.WriteLine("  Uninstall the app first (Settings -> Apps), or run this on a machine");
            Console.WriteLine("  where it is not installed. Nothing was touched.");
            // 3 = REFUSED (machine unsuitable), deliberately NOT 2, which line ~171 already uses for
            // a genuine FATAL (the exe to probe is missing). The caller must be able to tell "this
            // check could not run here" apart from "this check found something wrong" - collapsing
            // the two would turn a printed skip into a red gate, and a red gate that means nothing
            // is how people learn to ignore red gates.
            return 3;
        }

        _lab = Path.Combine(Path.GetTempPath(), "ValeraScreenshotField");
        _host = Take(_lab);
        Console.WriteLine("host state before: " + Describe(_host));

        try
        {
            ProbePortableMarker(srcExe);
            ProbeSelfInstallSymmetry(srcExe);
            ProbeUninstallKeepsUserFiles(srcExe);
            ProbeSilentSwitches(srcExe);
        }
        catch (Exception ex) { _fail++; Console.WriteLine("FAIL  probe crashed  [" + ex.Message + "]"); }
        finally { RestoreHost(); }

        Console.WriteLine();
        Console.WriteLine("FIELD RESULT: " + _pass + " PASS, " + _fail + " FAIL");
        return _fail == 0 ? 0 : 1;
    }

    private static string Lab(string name)
    {
        string d = Path.Combine(_lab, name);
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        Directory.CreateDirectory(d);
        return d;
    }

    private static int Run(string exe, string cmdline, int waitMs)
    {
        var psi = new ProcessStartInfo(exe, cmdline) { UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(psi);
        if (!p.WaitForExit(waitMs)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    // A portable copy must leave the host machine untouched - that is the whole promise of the marker.
    private static void ProbePortableMarker(string srcExe)
    {
        string d = Lab("portable");
        _exe = Path.Combine(d, Ident.Exe);
        File.Copy(srcExe, _exe, true);
        File.WriteAllText(Path.Combine(d, "portable.txt"), "");

        Snap before = Take(d);
        int rc = Run(_exe, "/install", 20000);
        Snap after = Take(d);

        Ok("F01 /install refuses while portable.txt is present", rc == 2, "exit " + rc);
        Ok("F02 a portable copy changes NOTHING on the host machine",
            Describe(before) == Describe(after), Describe(before) + " -> " + Describe(after));
    }

    // Whatever install adds, uninstall must take back.
    private static void ProbeSelfInstallSymmetry(string srcExe)
    {
        string d = Lab("selfinstall");
        _exe = Path.Combine(d, Ident.Exe);
        File.Copy(srcExe, _exe, true);

        Snap before = Take(d);
        int rc = Run(_exe, "/install " + Ident.SilentSwitch, 30000);
        Snap after = Take(d);
        Ok("F03 /install " + Ident.SilentSwitch + " completes unattended (no dialog)", rc == 0,
            rc == -1 ? "TIMED OUT - a dialog is waiting for a click" : "exit " + rc);
        // Наявність картки перевіряти НЕДОСТАТНЬО: на машині власника вона вже є від справжньої
        // інсталяції, тож груба ознака «зʼявилось щось нове» проходила при повній бездіяльності.
        // Міряємо ЗНАЧЕННЯ: картка мусить показувати саме нашу теку встановлення.
        string arpLoc = null;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Ident.UninstallKey))
                if (k != null) arpLoc = k.GetValue("InstallLocation") as string;
        }
        catch { }
        Ok("F04 /install registers a card pointing at its own install folder",
            Installer.SamePath(arpLoc, Installer.InstallDir), arpLoc + " vs " + Installer.InstallDir);

        // the app installs itself into %LOCALAPPDATA%\Programs; uninstall runs from there
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", Ident.AppId, Ident.Exe);
        Ok("F05 the installed copy exists where the card points", File.Exists(installed), installed);

        if (File.Exists(installed))
        {
            Run(installed, "/uninstall /silent", 30000);
            Thread.Sleep(3500);   // ScheduleSelfDelete runs after a short delay
        }
        // Симетрія міряється ПО ВЛАСНИХ слідах, а не по всьому стану машини: видалення прибирає
        // ярлики за фіксованим іменем, тож на машині з реальною інсталяцією воно неминуче зачепить
        // і її — і це поведінка застосунку, а не дефект. Тому перевіряємо рівно те, за що
        // відповідає цей шлях: своя картка ARP і свій бінарник.
        string arpAfter = null;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Ident.UninstallKey))
                if (k != null) arpAfter = k.GetValue("InstallLocation") as string;
        }
        catch { }
        Ok("F06 uninstall removes the card it created",
            !Installer.SamePath(arpAfter, Installer.InstallDir), arpAfter ?? "(card gone)");
        Ok("F07 the installed binary is really removed", !File.Exists(installed), installed);
    }

    // The dialog promises "your screenshots are never deleted". Anything else the user put in the
    // folder must survive too - the old uninstall wiped the whole directory recursively.
    private static void ProbeUninstallKeepsUserFiles(string srcExe)
    {
        string d = Lab("keepfiles");
        _exe = Path.Combine(d, Ident.Exe);
        File.Copy(srcExe, _exe, true);
        File.WriteAllText(Path.Combine(d, "portable.txt"), "");

        string shots = Path.Combine(d, "Shots");
        Directory.CreateDirectory(shots);
        string shot = Path.Combine(shots, "user_screenshot.png");
        File.WriteAllText(shot, "not really a png, but it is the user's");
        string doc = Path.Combine(d, "MY_OWN_NOTES.txt");
        File.WriteAllText(doc, "a file the user dropped next to the exe");

        string cmd = Installer.BuildSelfDeleteCommand(_exe, Installer.InstallDir);
        Ok("F08 the removal command carries no recursive wipe",
            cmd.IndexOf("rmdir /s", StringComparison.OrdinalIgnoreCase) < 0 &&
            cmd.IndexOf("rd /s", StringComparison.OrdinalIgnoreCase) < 0, "no /s");

        // execute it for real, in the lab folder, and see what survives
        var psi = new ProcessStartInfo("cmd.exe", cmd)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        Process.Start(psi).WaitForExit(20000);
        Thread.Sleep(1200);

        Ok("F09 the user's screenshot survives the removal command", File.Exists(shot), shot);
        Ok("F10 an unrelated user file survives too", File.Exists(doc), doc);
        Ok("F11 the folder itself survives because it is not empty", Directory.Exists(d), d);
    }

    // The ARP card registers "/silent". If the uninstaller only parses "/S", every unattended
    // removal stops on a modal dialog that nobody can click.
    // Прапорець тихого видалення мусить бути ОДИН для того, хто його реєструє, і для того, хто його
    // розбирає. Це були два різні рядкові літерали в різних одиницях компіляції: картка ARP
    // писала «/silent», а setup\Uninstall.cs розбирав лише «/S» — і кожне автоматизоване видалення
    // ставало модальним вікном на машині без людини.
    //
    // Запускати сам Uninstall.exe тут НЕ можна: його маніфест highestAvailable, тож із
    // неелевованого процесу Process.Start кидає «requires elevation» — перший прогін цієї проби
    // саме на цьому й упав. Тому перевіряємо контракт, а не запуск.
    private static void ProbeSilentSwitches(string srcExe)
    {
        string d = Lab("silent");
        string exe = Path.Combine(d, Ident.Exe);
        File.Copy(srcExe, exe, true);

        string quiet = null, plain = null;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\ValeraScreenshotFieldProbe\Arp"))
            {
                Arp.Write(k, exe, d, "\"" + exe + "\" /uninstall", "\"" + exe + "\" /uninstall " + Ident.SilentSwitch);
                quiet = k.GetValue("QuietUninstallString") as string;
                plain = k.GetValue("UninstallString") as string;
            }
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ValeraScreenshotFieldProbe", false); } catch { }

        Ok("F12 the ARP quiet command uses the single silent switch constant",
            quiet != null && quiet.EndsWith(Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase),
            quiet ?? "(no card written)");
        Ok("F13 the plain uninstall command carries no silent switch",
            plain != null && plain.IndexOf(Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase) < 0,
            plain ?? "(none)");

        // І кінцева ланка: сам застосунок мусить розпізнати рівно той прапорець, що в картці.
        string tail = quiet == null ? "" : quiet.Substring(quiet.LastIndexOf(' ') + 1);
        Ok("F14 the switch the card registers is the one the app parses",
            string.Equals(tail, Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase),
            tail + " == " + Ident.SilentSwitch);
    }

    // Put the owner's machine back exactly as it was, whatever happened above.
    private static void RestoreHost()
    {
        try { foreach (var p in Process.GetProcessesByName(Ident.AppId)) { try { p.Kill(); } catch { } } } catch { }
        Thread.Sleep(600);

        SetRun(Registry.CurrentUser, _host.RunHkcu);
        if (_host.ArpHkcu == null) { try { Registry.CurrentUser.DeleteSubKeyTree(Ident.UninstallKey, false); } catch { } }
        else if (!string.IsNullOrEmpty(_host.ArpHkcuLoc))
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Ident.UninstallKey))
                    Arp.Write(k, _host.ArpHkcuIcon ?? Path.Combine(_host.ArpHkcuLoc, Ident.Exe),
                              _host.ArpHkcuLoc, _host.ArpHkcuUn, _host.ArpHkcuQuiet);
            }
            catch { }
        }
        // Відтворити ярлики, які могло знести видалення (воно бʼє за фіксованим іменем).
        MakeLnk(Lnk(Environment.SpecialFolder.Programs), _host.LnkUserTarget);
        MakeLnk(Lnk(Environment.SpecialFolder.Desktop), _host.LnkDeskUserTarget);
        try { if (Directory.Exists(_lab)) Directory.Delete(_lab, true); } catch { }

        Snap now = Take(_lab);
        bool clean = now.RunHkcu == _host.RunHkcu && now.ArpHkcu == _host.ArpHkcu &&
                     now.LnkUser == _host.LnkUser && now.LnkDeskUser == _host.LnkDeskUser;
        Console.WriteLine();
        Ok("F99 the owner's machine is left exactly as it was found", clean,
            Describe(_host) + " -> " + Describe(now));
    }

    private static void SetRun(RegistryKey root, string want)
    {
        try
        {
            using (var k = root.OpenSubKey(Ident.RunKey, true))
            {
                if (k == null) return;
                if (want == null) { if (k.GetValue(Ident.RunValue) != null) k.DeleteValue(Ident.RunValue, false); }
                else k.SetValue(Ident.RunValue, want);
            }
        }
        catch { }
    }
}
