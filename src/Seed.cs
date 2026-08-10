using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValeraScreenshot
{
    // СПІЛЬНЕ ЯДРО ДЛЯ ДВОХ АРТЕФАКТІВ. ValeraScreenshot.exe і ValeraScreenshotSetup.exe компілюються з різних
    // наборів файлів, тож усе, що мусить збігатися між ними, або лежить тут, або розходиться.
    //
    // Тут живе відповідь на питання «де конфіг цієї копії» — і саме через дві різні відповіді на
    // нього зникав чекбокс «Запускати разом із Windows»: інсталятор сіяв намір у %APPDATA%, а
    // встановлена копія читала settings.ini поруч із собою (тека в профілі записувана), бачила
    // дефолтне False і стирала щойно записаний ключ Run. Одна функція — одна відповідь.
    internal static class Seed
    {
        // Тека конфіга для копії, що стоїть у exeDir: поруч з exe, якщо туди можна писати
        // (портативна модель «усе в одній робочій теці»), інакше %APPDATA%\<AppId>.
        //
        // ★ ВІДПОВІДЬ НЕ МАЄ ПРАВА ЗАЛЕЖАТИ ВІД ТОГО, ХТО ПИТАЄ (2026-08-08). Проба записом
        //   каже правду про права ПОТОЧНОГО процесу, а питання стоїть про застосунок, який
        //   працює БЕЗ підняття прав. ЕЛЕВОВАНИЙ інсталятор пробу в Program Files проходив і
        //   сіяв StartWithWindows=True саме туди; застосунок, запущений de-elevated через
        //   explorer.exe, ту саму пробу провалював, читав %APPDATA% (порожньо), брав дефолтне
        //   False — і ApplyStartup слухняно видаляв щойно записаний ключ Run. Тобто чекбокс
        //   інсталятора самознищувався на ДЕФОЛТНОМУ адмінському шляху, а в Program Files
        //   лишався settings.ini-сирота, якого жоден неелевований запуск не прочитає.
        //   Тому теки, куди пишуть лише адміністратори (Program Files обох розрядностей,
        //   Windows), виключено ЩЕ ДО проби: для них відповідь — %APPDATA%, хоч би з якими
        //   правами прийшов викликач. Неелевованому це не міняє нічого (його проба там і так
        //   падала); міняється рівно те, що елевований сівач і неелевований читач більше
        //   НЕ МОЖУТЬ розійтися в адресі. Засів до елевації (SetupForm.Install) лишається
        //   потрібним для елевації під ІНШИМ обліковим записом — там чужий уже сам %APPDATA%.
        public static string ConfigDirFor(string exeDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir) && !IsAdminOnlyDir(exeDir))
                {
                    string probe = Path.Combine(exeDir, ".write_probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return exeDir.TrimEnd('\\');
                }
            }
            catch { }
            string appData = Ident.AppDataDir;
            try { Directory.CreateDirectory(appData); } catch { }
            return appData;
        }

        // Системні теки, де запис дозволено лише адміністраторам, — рівно ті місця, де проба
        // записом дає елевованому і неелевованому процесу РІЗНІ відповіді, тож пробувати там
        // заборонено, а не марно. ProgramW6432 — бо 32-бітному процесу GetFolderPath(ProgramFiles)
        // повертає x86-теку, і 64-бітний Program Files інакше випав би зі списку.
        // internal, не private — щоб гейт міг звірити сам список, не маючи адмінправ на живу пробу.
        internal static bool IsAdminOnlyDir(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                string[] roots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetEnvironmentVariable("ProgramW6432"),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                };
                foreach (string r in roots)
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    string root = r.TrimEnd('\\');
                    if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }   // шлях не розібрався -> хай вирішує проба, як і до цієї правки
            return false;
        }

        // Записує НАМІР автозапуску в конфіг. Точковий патч одного рядка: решта settings.ini
        // (гарячі клавіші, тека збереження, тема) належить застосунку і не переписується.
        // Читання СУВОРО як UTF-8 — тим самим кодуванням, яким пише Config.Save. Читання
        // «як вийде» вже одного разу подвоїло кодування кирилиці й зіпсувало шлях до теки знімків.
        public static void SeedAutostartIni(string iniPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(iniPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var lines = new List<string>();
                bool found = false;
                if (File.Exists(iniPath))
                    foreach (string ln in File.ReadAllLines(iniPath, Encoding.UTF8))
                    {
                        if (ln.StartsWith("StartWithWindows=", StringComparison.OrdinalIgnoreCase))
                        { lines.Add("StartWithWindows=True"); found = true; }
                        else lines.Add(ln);
                    }
                if (!found) lines.Add("StartWithWindows=True");
                File.WriteAllLines(iniPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
