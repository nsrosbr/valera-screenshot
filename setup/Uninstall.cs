using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using ValeraScreenshot;
using Microsoft.Win32;

// Деінсталятор ValeraScreenshot: лежить у теці встановлення. Прибирає ярлики, автозапуск,
// запис у «Програмах», діагностичний лог і ТІЛЬКИ ті файли, які поклала інсталяція.
// Тихий режим: Uninstall.exe /S або /silent (ARP реєструє саме /silent).
internal static class UninstallMain
{
    // Файли, які кладе інсталяція. Дзеркало Installer.OwnedFiles — деінсталятор компілюється
    // окремо від src\, тож список продубльовано свідомо.
    //
    // ★ ТУТ СТОЯЛО «тест LU3 звіряє обидва». ТАКОГО ТЕСТУ НЕ ІСНУВАЛО — ні LU3, ні будь-якого
    //   іншого: грep по всьому дереву давав рівно один збіг, оцей коментар. Тобто найнебезпечніший
    //   список у продукті (що саме дозволено ВИДАЛЯТИ) був продубльований, розходився вже тоді
    //   (Uninstall.exe є в одному й немає в іншому) — і його «перевірку» гарантував рядок тексту.
    //   Звірку написано 2026-07-29: L24 (payload -> Installer.OwnedFiles) і L25 (цей список ->
    //   Installer.OwnedFiles). Uninstall.exe тут відсутній СВІДОМО: деінсталятор зносить себе
    //   окремою командою нижче, а не через цей список, і L25 про цей виняток знає поіменно.
    internal static readonly string[] OwnedFiles =
    {
        Ident.Exe, Ident.CerFile,
        "MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md"
    };

    [STAThread]
    static int Main(string[] args)
    {
        // Приймати ОБИДВА написання. ARP-картка реєструє QuietUninstallString із «/silent»
        // (Arp.cs / Setup.cs), а тут розбирався лише «/S» -> будь-яке тихе видалення
        // (winget, скрипт розгортання, «Видалити» без підтвердження) відкривало модальне
        // вікно на машині, де його нікому натиснути.
        bool silent = false, elevated = false;
        foreach (var a in args)
        {
            string s = a.Trim();
            if (s.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                s.Equals(Ident.SilentSwitch, StringComparison.OrdinalIgnoreCase) ||
                s.Equals("-silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            // Службовий маркер повторного запуску з підняттям прав (див. нижче): підтвердження
            // вже дано в першому екземплярі, і другого підняття не буде НІКОЛИ — інакше відмова,
            // яку права не лікують, зациклила б UAC.
            if (s.Equals("/elevated", StringComparison.OrdinalIgnoreCase)) elevated = true;
        }

        // Інсталятор і деінсталятор — окремі бінарники й окремі процеси: конфіга
        // застосунку в них ще (або вже) може не бути, тож мова береться з системи.
        L.Init("auto");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        if (!silent && !elevated)   // піднятій копії підтвердження не треба: його вже дано
        {
            // ★ ТУТ СТОЯВ Ident.AppId — «ValeraScreenshot». Це технічна айдентика: ім'я процесу,
            //   mutex, ключ реєстру, ім'я файла. Людині вона не показується НІДЕ, крім цих двох
            //   вікон, де показувалась: «Видалити ValeraScreenshot з теки…» замість «Видалити
            //   ВАЛЄРА Скріншот…». Видима назва живе в L.Name і перекладається разом з усім.
            //   Нижче Ident.AppId ЛИШАЄТЬСЯ там, де він і має бути — у GetProcessesByName.
            var r = MessageBox.Show(
                L.S("Видалити ", "Uninstall ") + L.Name + L.S(" з теки\n", " from the folder\n") + dir + "?\n\n" +
                L.S("Ваші знімки не видаляються в жодному разі.", "Your screenshots are never deleted, in any case."),
                L.Name + L.S(" — видалення", " — uninstall"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return 0;
        }

        try
        {
            foreach (var p in Process.GetProcessesByName(Ident.AppId))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }
        catch { }

        // Підсумок видалення. Помічники більше не ковтають відмови мовчки (див. їх нижче):
        // кожен крок докладається в ok, і лише справжнє «все зникло» дає вікно успіху.
        bool ok = true;

        // ярлики (обидва розташування — і користувацьке, і спільне)
        ok &= DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Ident.Lnk));
        ok &= DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Ident.Lnk));
        ok &= DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Ident.Lnk));
        ok &= DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Ident.Lnk));

        // автозапуск — в ОБОХ вуликах. HKLM тут не чіпався взагалі, тож інсталяція під адміном
        // лишала по собі робочий запис автозапуску на видалену програму.
        ok &= DeleteRunValue(Registry.CurrentUser);
        ok &= DeleteRunValue(Registry.LocalMachine);

        // запис у «Програмах» (обидва вулики)
        ok &= TryDeleteKey(Registry.CurrentUser);
        ok &= TryDeleteKey(Registry.LocalMachine);

        // Діагностичний лог і його маркер — БЕЗУМОВНО (STD-DIAG-01 / STD-LIFE-04). Вони живуть у
        // %APPDATA%, а не в теці встановлення, тож попередня версія лишала їх на диску назавжди:
        // користувач вмикав діагностику, видаляв програму — і лог із подіями лишався.
        string appData = Ident.AppDataDir;
        ok &= DeleteIfExists(Path.Combine(appData, "debug.log"));
        ok &= DeleteIfExists(Path.Combine(appData, "debug.log.1"));
        ok &= DeleteIfExists(Path.Combine(appData, "debug.on"));
        ok &= DeleteIfExists(Path.Combine(appData, "crash.log"));

        // Файли: ТІЛЬКИ свої. Доти тут стояло «видалити все, крім Screenshots і себе» — для
        // інсталяції в спільну теку (D:\Tools\ValeraScreenshot поруч з чужим) це знищувало чуже.
        string self = Path.Combine(dir, "Uninstall.exe");
        foreach (string name in OwnedFiles) ok &= DeleteIfExists(Path.Combine(dir, name));

        // БРЕХНЯ ПРО УСПІХ (виправлено 2026-08-08). Помічники ковтали кожну відмову, а нижче
        // стояло безумовне «видалено. Ваші знімки збережено»: звичайний користувач на машинній
        // інсталяції (Program Files, HKLM-картка, спільний ярлик) чув це, коли НЕ видалилось
        // НІЧОГО. Тепер на відмові ДОСТУПУ — ОДНА спроба перезапуститись із підняттям прав
        // (та сама схема runas, що в SetupForm), а якщо UAC відхилено чи підняття не помогло —
        // чесний перелік того, що лишилося. Вікно успіху бачить лише справжній успіх.
        if (!ok && AccessDenied && !elevated && !silent)
        {
            try
            {
                Process.Start(new ProcessStartInfo(self, "/elevated") { UseShellExecute = true, Verb = "runas" });
                return 0;   // решту роботи — і підсумкове вікно — веде піднята копія
            }
            catch { }   // «Ні» в UAC — падаємо в чесний звіт нижче
        }
        if (!ok)
        {
            if (!silent)
                MessageBox.Show(
                    L.Name + L.S(" видалено НЕ повністю. Не вдалося прибрати:\n\n", " was NOT fully removed. Could not remove:\n\n") +
                    string.Join("\n", Failed.ToArray()) +
                    L.S("\n\nЗапустіть Uninstall.exe від імені адміністратора.", "\n\nRun Uninstall.exe as administrator."),
                    L.Name + L.S(" — видалення", " — uninstall"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;   // самовидалення НЕ плануємо: Uninstall.exe лишається, буде чим повторити
        }

        if (!silent)
            MessageBox.Show(L.Name + L.S(" видалено. Ваші знімки збережено.", " was removed. Your screenshots were kept."),
                L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

        // Теку знімаємо НЕРЕКУРСИВНО: `rmdir` без /s спрацює лише коли в ній не лишилось нічого,
        // крім Uninstall.exe, який зникне тим самим рядком. Будь-що чуже — знімки, документи,
        // підтеки — автоматично рятує теку. Рекурсивного видалення тут більше немає.
        //
        // ПОРЯДОК (виправлено 2026-08-08): cmd стартував ДО модального вікна вище, і його
        // фіксовані 2 секунди спливали, поки вікно тримало процес живим — del бив у ще
        // заблокований exe, і Uninstall.exe разом із текою переживали КОЖНЕ інтерактивне
        // видалення. Тепер cmd стартує після закриття вікна, коли процесу лишається тільки return.
        string cmd = "/c timeout /t 2 >nul & del /f /q \"" + self + "\" & rmdir \"" + dir + "\" 2>nul";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
        return 0;
    }

    // ЧЕСНІСТЬ ПОМІЧНИКІВ (2026-08-08). Раніше всі три повертали void і ковтали будь-яку
    // відмову — саме на них трималася «брехня про успіх» головного вікна. Тепер кожен повертає
    // успіх; відмова записується ПОІМЕННО в Failed (щоб чесний звіт назвав, що лишилося),
    // а відмова ДОСТУПУ ще й зводить AccessDenied — лише її лікує перезапуск із підняттям прав.
    private static readonly System.Collections.Generic.List<string> Failed = new System.Collections.Generic.List<string>();
    private static bool AccessDenied;

    private static bool Fail(string what, Exception ex)
    {
        if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) AccessDenied = true;
        Failed.Add(what);
        return false;
    }

    private static bool DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) { return Fail(path, ex); }
    }

    private static bool DeleteRunValue(RegistryKey root)
    {
        try
        {
            // Спершу ЧИТАННЯ: якщо запису нема, то й видаляти нема чого. Інакше сам факт
            // відкриття HKLM на запис під звичайним користувачем рахувався б відмовою — і на
            // per-user інсталяції, якої HKLM взагалі не стосується, даремно смикав би UAC.
            using (var probe = root.OpenSubKey(Ident.RunKey))
                if (probe == null || probe.GetValue(Ident.RunValue) == null) return true;
            using (var k = root.OpenSubKey(Ident.RunKey, true))
                if (k != null && k.GetValue(Ident.RunValue) != null) k.DeleteValue(Ident.RunValue, false);
            return true;
        }
        catch (Exception ex) { return Fail(root.Name + "\\" + Ident.RunKey + " -> " + Ident.RunValue, ex); }
    }

    private static bool TryDeleteKey(RegistryKey root)
    {
        try
        {
            // Той самий пробний ХІД ЧИТАННЯМ, що і в DeleteRunValue, і з тієї ж причини.
            // Шлях картки — ЛИШЕ з Ident.UninstallKey (інваріант 1): склеєний руками
            // @"...\Uninstall\" + Ident.AppId — це друга копія тієї самої адреси, і A11
            // справедливо ловить її як айдентику поза технічним контекстом.
            using (var probe = root.OpenSubKey(Ident.UninstallKey))
                if (probe == null) return true;   // картки в цьому вулику нема — нема і відмови
            using (var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
                if (k != null) k.DeleteSubKeyTree(Ident.AppId, false);
            return true;
        }
        catch (Exception ex) { return Fail(root.Name + "\\" + Ident.UninstallKey, ex); }
    }
}
