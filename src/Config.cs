using System;
using System.IO;
using System.Text;

namespace ValeraScreenshot
{
    // Налаштування. Портативно: settings.ini поруч з exe («усе в одній теці»);
    // якщо тека не записувана — запасний варіант %APPDATA%\ValeraScreenshot.
    internal class Config
    {
        // Гарячі клавіші (Win32-модифікатори: ALT=1, CTRL=2, SHIFT=4, WIN=8).
        // ДВІ клавіші на дію — щоб покрити і ноут, і ПК одночасно:
        //   основна Ctrl+Shift+4/3 (є на будь-якій клавіатурі, працює на ноуті без PrtScr);
        //   запасна PrtScr / Shift+PrtScr (звична на ПК; Vk=0 = вимкнено).
        public int RegionVk = 0x34;                   // Ctrl+Shift+4 — знімок області
        public int RegionMods = Native.MOD_CONTROL | Native.MOD_SHIFT;
        public int FullVk = 0x33;                     // Ctrl+Shift+3 — весь екран
        public int FullMods = Native.MOD_CONTROL | Native.MOD_SHIFT;
        public int Region2Vk = Native.VK_SNAPSHOT;    // запасна: PrtScr
        public int Region2Mods = 0;
        public int Full2Vk = Native.VK_SNAPSHOT;      // запасна: Shift+PrtScr
        public int Full2Mods = Native.MOD_SHIFT;

        public string SaveDir = "";                   // порожньо = <тека застосунку>\Screenshots
        // ШАБЛОН ІМЕНІ — ВЛАСТИВІСТЬ, А НЕ ПОЛЕ-ІНІЦІАЛІЗАТОР, і це не стиль, а виправлення.
        // App.Main робить L.Init(Config.Load().UiLang): конфіг будується РАНІШЕ, ніж застосунок
        // дізнається мову, бо саме з конфіга він її і читає. Поле-ініціалізатор із L.S виконувався
        // б у той момент, коли L.Cur ще дефолтна (українська), і застигав би там назавжди.
        // Наслідок був такий: англомовний користувач на першому запуску отримував англійський
        // інтерфейс і файли «Знімок_2026-08-05_13-45-00.png» — а Save() іде після КОЖНОГО знімка,
        // тож український шаблон одразу осідав у settings.ini.
        // Матриця LOC цього не ловила ЗА ПОБУДОВОЮ: літерал стоїть усередині L.S, правило не
        // порушене — порушений момент виклику. Ловить тепер N7.
        // Порожній _template означає «користувач не чіпав», і тоді відповідь дається В МОМЕНТ
        // ЗАПИТУ, поточною мовою. Save() пише саме _template, тож недоторканий конфіг лишається
        // мово-залежним, а не консервує чиюсь мову в файлі.
        private string _template = "";
        // ОДНЕ джерело дефолту. Форма Параметрів мусить упізнати «це ще дефолт», щоб не записати
        // його як власний вибір користувача — і для цього вона мусить звірятися з ТИМ САМИМ
        // рядком. Друга копія літерала в SettingsForm тихо розійшлася б із цією при першій же
        // правці формулювання, і тоді шаблон знову застигав би в мові: N7 позеленів би, а дефект
        // повернувся. Дубль завів я сам сьогодні ж, лагодячи N7.
        public static string DefaultTemplate
        {
            get { return L.S("Знімок_{date}_{time}", "Screenshot_{date}_{time}"); }
        }
        public string Template
        {
            get { return _template.Length > 0 ? _template : DefaultTemplate; }
            set { _template = value == null ? "" : value; }
        }
        // Чи користувач справді задав свій шаблон (потрібно формі Параметрів, щоб не
        // заморожувати дефолт лише через те, що вікно відкрили й натиснули «Зберегти»).
        internal bool TemplateIsCustom { get { return _template.Length > 0; } }
        public string Format = "png";                 // png | jpg
        public int JpegQuality = 92;
        public bool IncludeCursor = false;
        public bool CopyAfterSave = true;
        public bool PlaySound = false;
        public bool ShowBalloon = true;
        public bool StartWithWindows = false;
        public int LastColor = unchecked((int)0xFFE81123); // червоний Windows
        public int LastWidth = 3;
        public string UiTheme = "auto";   // auto | light | dark (auto слідує за темою Windows)
        public string UiLang = "auto";    // auto | uk | en  (auto слідує за мовою Windows)

        public bool FirstRun = false; // не зберігається

        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        // ДЕ ЛЕЖИТЬ КОНФІГ КОПІЇ, ЩО СТОЇТЬ У <exeDir>. Винесено в окрему функцію, бо це не
        // константа, а рішення від прав доступу — і саме через це «зникав» чекбокс автозапуску:
        // інсталятор сіяв намір за однією адресою (%APPDATA%), а встановлена копія читала за
        // іншою (поруч із собою), бачила дефолтне False і стирала щойно записаний ключ Run.
        // Тепер адресу рахує ОДНА функція, і всі сівачі (Setup.cs, Installer.cs, install.ps1)
        // питають її про ТУ САМУ теку, куди справді ставлять застосунок.
        public static string DirFor(string exeDir) { return Seed.ConfigDirFor(exeDir); }

        // Тестовий шов: перенаправити теку конфіга/логів у пісочницю, щоб перевірки краш-логу
        // не писали в справжній профіль користувача.
        internal static void SetDirForTest(string d) { _dir = d; }

        private static string _dir;
        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                _dir = DirFor(BaseDir);
                return _dir;
            }
        }

        public static string IniPath { get { return Path.Combine(Dir, "settings.ini"); } }

        public string EffectiveSaveDir
        {
            get { return string.IsNullOrEmpty(SaveDir) ? Path.Combine(Dir, "Screenshots") : SaveDir; }
        }

        // Ім'я файла за шаблоном; гарантує унікальність (суфікс -2, -3, …).
        public string MakeFilePath(int w, int h)
        {
            DateTime now = DateTime.Now;
            string name = Template
                .Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HH-mm-ss"))
                .Replace("{w}", w.ToString())
                .Replace("{h}", h.ToString());
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Trim().Length == 0) name = L.S("Знімок", "Screenshot");
            string ext = Format == "jpg" ? ".jpg" : ".png";
            string dir = EffectiveSaveDir;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ext);
            int i = 2;
            while (File.Exists(path)) { path = Path.Combine(dir, name + "-" + i + ext); i++; }
            return path;
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                c.FirstRun = !File.Exists(IniPath);
                if (File.Exists(IniPath))
                {
                    foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        c.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ ТИХА ВТРАТА НАЛАШТУВАНЬ. Було просто `catch { }`: якщо читання падало на
                //   середині файла (диск, права, покалічене кодування), частина ключів уже
                //   застосована, решта — ні, і застосунок мовчки працював із СУМІШШЮ. Далі
                //   будь-яке збереження (а воно йде після КОЖНОГО знімка) записувало цю суміш
                //   поверх файла користувача — його гарячі клавіші й тека зникали назавжди,
                //   і жодне повідомлення про це не з'являлося.
                //   Тепер невдале читання лише ПОЗНАЧАЄТЬСЯ, а рішення про долю файла ухвалює
                //   Save(): він відкладає нечитабельний оригінал убік і пише лише тоді, коли це
                //   вдалося. Не вдалося — чесна відмова, файл цілий.
                c.LoadFailed = true;
                // Копію тут НЕ робимо: коли файл замкнений іншим процесом (найчастіша причина
                // збою читання), скопіювати його теж неможливо — це показало твердження H9.
                // Оригінал відкладає вбік Save(), і лише якщо це вдалося, він пише новий.
                try { Diag.Log("config load failed: " + ex.Message + " (settings.ini will not be overwritten)"); }
                catch { }
            }
            return c;
        }

        // Читання конфіга не вдалося, тож частина значень — дефолтні, і об'єкт НЕ відображає
        // того, що на диску. Save() бачить цей прапорець і відмовляється затирати оригінал,
        // поки не відкладе його вбік як settings.ini.bad.
        public bool LoadFailed;

        private void Apply(string key, string val)
        {
            switch (key)
            {
                // int.TryParse(val, out Поле) при НЕВДАЧІ записує 0 просто в поле — сміття в
                // ini не ігнорувалося, а зануляло дефолт: Vk=0 тихо вимикає гарячу клавішу,
                // а Mods=0 при цілому Vk реєструє ГОЛУ клавішу — система віддає '4' нам,
                // забравши її в усіх застосунків. Save() іде після кожного знімка, тож нулі
                // одразу консервувалися в файлі. Тому — той самий патерн, що нижче в
                // JpegQuality/LastWidth: парс у тимчасову, присвоєння лише за успіху.
                case "RegionVk": { int v; if (int.TryParse(val, out v)) RegionVk = v; } break;
                case "RegionMods": { int v; if (int.TryParse(val, out v)) RegionMods = v; } break;
                case "FullVk": { int v; if (int.TryParse(val, out v)) FullVk = v; } break;
                case "FullMods": { int v; if (int.TryParse(val, out v)) FullMods = v; } break;
                case "Region2Vk": { int v; if (int.TryParse(val, out v)) Region2Vk = v; } break;
                case "Region2Mods": { int v; if (int.TryParse(val, out v)) Region2Mods = v; } break;
                case "Full2Vk": { int v; if (int.TryParse(val, out v)) Full2Vk = v; } break;
                case "Full2Mods": { int v; if (int.TryParse(val, out v)) Full2Mods = v; } break;
                case "SaveDir": SaveDir = val; break;
                case "Template": if (val.Length > 0) Template = val; break;
                case "Format": Format = (val == "jpg") ? "jpg" : "png"; break;
                case "JpegQuality":
                    int q; if (int.TryParse(val, out q) && q >= 10 && q <= 100) JpegQuality = q; break;
                case "IncludeCursor": IncludeCursor = ParseBool(val); break;
                case "CopyAfterSave": CopyAfterSave = ParseBool(val); break;
                case "PlaySound": PlaySound = ParseBool(val); break;
                case "ShowBalloon": ShowBalloon = ParseBool(val); break;
                case "StartWithWindows": StartWithWindows = ParseBool(val); break;
                case "LastColor": { int v; if (int.TryParse(val, out v)) LastColor = v; } break;
                case "LastWidth":
                    int w; if (int.TryParse(val, out w) && w >= 1 && w <= 16) LastWidth = w; break;
                case "UiTheme":
                    if (val == "light" || val == "dark" || val == "auto") UiTheme = val; break;
                case "UiLang":
                    if (val == "uk" || val == "en" || val == "auto") UiLang = val; break;
            }
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "on";
        }

        // true = справді лягло на диск. Було void із порожнім catch: користувач тиснув «Зберегти»
        // в Параметрах, вікно закривалося як за успіху, а на наступному старті гарячі клавіші,
        // тека й тема поверталися до старих. Викликається ще й після КОЖНОГО знімка, тож відмова
        // була масовою і повністю невидимою.
        public bool Save()
        {
            try
            {
                // ★ НІКОЛИ НЕ ЗАТИРАТИ ФАЙЛ, ЯКОГО НЕ ЗМОГЛИ ПРОЧИТАТИ.
                //
                //   Load() при збої лишає в об'єкті СУМІШ прочитаного й дефолтів. Save
                //   викликається після КОЖНОГО знімка, тож без цього гарду перше ж збереження
                //   тихо записувало ту суміш поверх файла користувача — його гарячі клавіші й
                //   тека зникали назавжди.
                //   Перша версія гарду копіювала оригінал у .bad усередині Load. Твердження H9
                //   показало, що це не захист: коли файл замкнений іншим процесом (а це і є
                //   найчастіша причина збою читання), копія теж не виходить, і затирання
                //   лишалось попереду. Тому рішення перенесено СЮДИ: спершу відкласти оригінал
                //   убік, і лише якщо це вдалося — писати. Не вдалося відкласти — чесна відмова,
                //   яку користувач побачить, а файл лишається цілим.
                if (LoadFailed && File.Exists(IniPath))
                {
                    string bad = IniPath + ".bad";
                    try
                    {
                        if (File.Exists(bad)) File.Delete(bad);
                        File.Move(IniPath, bad);
                    }
                    catch (Exception mex)
                    {
                        try { Diag.Log("config save refused: unreadable settings.ini could not be set aside (" + mex.Message + ")"); }
                        catch { }
                        return false;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("# ValeraScreenshot settings");
                sb.AppendLine("RegionVk=" + RegionVk);
                sb.AppendLine("RegionMods=" + RegionMods);
                sb.AppendLine("FullVk=" + FullVk);
                sb.AppendLine("FullMods=" + FullMods);
                sb.AppendLine("Region2Vk=" + Region2Vk);
                sb.AppendLine("Region2Mods=" + Region2Mods);
                sb.AppendLine("Full2Vk=" + Full2Vk);
                sb.AppendLine("Full2Mods=" + Full2Mods);
                sb.AppendLine("SaveDir=" + SaveDir);
                sb.AppendLine("Template=" + _template);   // RAW: порожньо = «не чіпав», хай слідує за мовою
                sb.AppendLine("Format=" + Format);
                sb.AppendLine("JpegQuality=" + JpegQuality);
                sb.AppendLine("IncludeCursor=" + IncludeCursor);
                sb.AppendLine("CopyAfterSave=" + CopyAfterSave);
                sb.AppendLine("PlaySound=" + PlaySound);
                sb.AppendLine("ShowBalloon=" + ShowBalloon);
                sb.AppendLine("StartWithWindows=" + StartWithWindows);
                sb.AppendLine("LastColor=" + LastColor);
                sb.AppendLine("LastWidth=" + LastWidth);
                sb.AppendLine("UiTheme=" + UiTheme);
                sb.AppendLine("UiLang=" + UiLang);

                // Атомарний запис (STD-CFG-01): пишемо в temp, потім File.Replace — щоб обрив
                // під час запису не лишив напівписаний settings.ini і не втратив налаштування.
                Directory.CreateDirectory(Path.GetDirectoryName(IniPath));
                string tmp = IniPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(IniPath))
                {
                    try { File.Replace(tmp, IniPath, null); }
                    catch { File.Copy(tmp, IniPath, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, IniPath);
                return true;
            }
            catch (Exception ex) { Diag.Log("Config.Save: " + ex.Message); return false; }
        }
    }
}
