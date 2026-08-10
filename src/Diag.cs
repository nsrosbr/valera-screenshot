using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace ValeraScreenshot
{
    // Легка діагностика для звітів про баги з реального вжитку. Вимкнена за замовчуванням (opt-in).
    // Коли УВІМКНЕНА — логує ПОДІЇ (знімок області/екрана, збереження, дію апдейтера, помилки), а
    // НЕ вміст знімків. Локальна, обмежена за розміром, ротується. Увесь дисковий I/O — на фоновому
    // потоці-писарі: гарячі шляхи лише кладуть рядок у чергу (неблокуюче) і ніколи не стоять на лозі.
    // LogCrash — окремий, СИНХРОННИЙ, пише ЗАВЖДИ (навіть коли лог off): щоб падіння лишало слід.
    internal static class Diag
    {
        private static volatile bool _on;
        private static string _path;
        private static string _source = "";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static long _maxBytes = 2 * 1024 * 1024; // ротація на ~2 МБ з одним бекапом .1
        private static int _markerCheckedTick;           // коли востаннє звіряли маркер згоди
        private static int _markerRecheckMs = 2000;      // вікно throttle — вимірне й зафіксоване тестом
        private static bool _skipMarkerCheck;            // лише тест-гарнес (пише в тимчасовий файл)

        private static readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), 20000);
        private static Thread _writer;
        private static readonly object _startLock = new object();

        static Diag()
        {
            try
            {
                _path = Path.Combine(Config.Dir, "debug.log");
                // ТОКЕН, не готовий текст. Цей рядок — СТАТИЧНИЙ КОНСТРУКТОР, і він виконується
                // при першому дотику до Diag. А перший дотик буває РАНІШЕ за L.Init: Main робить
                // L.Init(Config.Load().UiLang), і Config.Load() на шляху збою кличе Diag.Log —
                // тобто всередині обчислення аргументу, поки мова ще дефолтна (українська).
                // Готовий текст тут застигав би назавжди, і англомовний користувач читав би в
                // діагностичному лозі «джерело: маркер debug.on». Той самий клас, що й
                // Config.Template; правило N8 його НЕ бачило, бо дивилось лише на ініціалізатори
                // полів, а тіло статичного конструктора лежить глибше за дужками. Тепер бачить.
                if (File.Exists(Path.Combine(Config.Dir, "debug.on"))) { _on = true; _source = "marker"; }
                else if (EnvEnabled()) { _on = true; _source = Ident.EnvDebug; }   // технічна назва змінної, не текст для ока
            }
            catch { _on = false; }
        }

        public static bool Enabled { get { return _on; } }
        public static string LogPath { get { return _path; } }

        // Токен -> текст, У МОМЕНТ ПОКАЗУ. Саме тому джерело зберігається токеном: рішення «чим
        // увімкнено лог» ухвалюється раніше, ніж стає відома мова, а читає його людина — потім.
        // Ident.EnvDebug лишається як є: це ім'я змінної середовища, технічна назва, не текст.
        private static string SourceText()
        {
            switch (_source)
            {
                case "marker": return L.S("маркер debug.on", "debug.on marker");
                case "tray": return L.S("трей", "tray");
                default: return _source;
            }
        }

        // TRUTHY-значення VALERASCREENSHOT_DEBUG (1/true/yes/on/y) вмикає лог. Раніше будь-яке непорожнє
        // значення (навіть "0") тихо озброювало діагностику; тепер "0"/"false" коректно = вимкнено.
        private static bool EnvEnabled()
        {
            try { return IsTruthy(Environment.GetEnvironmentVariable(Ident.EnvDebug)); }
            catch { return false; }
        }

        internal static bool IsTruthy(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "y": return true;
                default: return false;
            }
        }

        // Перемкнути лог у рантаймі (з трею) — без рестарту. Персиститься маркером debug.on.
        public static void SetEnabled(bool on)
        {
            try
            {
                string marker = Path.Combine(Config.Dir, "debug.on");
                if (on)
                {
                    Directory.CreateDirectory(Config.Dir);
                    if (!File.Exists(marker)) File.WriteAllText(marker, "", Utf8);
                    _source = "tray";
                    _on = true;
                    Header();
                }
                else
                {
                    if (File.Exists(marker)) File.Delete(marker);
                    _on = false;
                }
            }
            catch { }
        }

        // Стерти лог (і ротований бекап). Викликається діями трею й при деінсталяції.
        public static void ClearLog()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            try { string b = _path + ".1"; if (File.Exists(b)) File.Delete(b); } catch { }
        }

        private static void Header()
        {
            Log("====================================================================");
            Log(Ident.AppId + " " + Ver.Display + L.S(" — ДІАГНОСТИЧНИЙ ЛОГ УВІМКНЕНО (", " — DIAGNOSTIC LOG ENABLED (") + SourceText() + ")");
            Log(L.S("Логуються ПОДІЇ (знімок/збереження/оновлення/помилки), НЕ вміст знімків.", "EVENTS are logged (capture/save/update/errors), NOT the contents of your screenshots."));
            Log(L.S("Тримайте лог ЛОКАЛЬНО; вимикайте після відтворення. Ліміт ~2 МБ (з ротацією).", "Keep the log LOCAL; switch it off once the problem is reproduced. Limit ~2 MB (rotated)."));
            Log("====================================================================");
        }

        // Маркер debug.on — ЄДИНЕ джерело правди про те, чи писати лог. Доти стан жив лише в пам'яті:
        // маркер, видалений ззовні (скриптом, інсталятором, самим користувачем), НЕ зупиняв запис —
        // процес писав далі до перезапуску. Згода, яку неможливо відкликати, — це не згода, тому
        // перевірка стоїть на самому шляху запису. Увімкнення через env маркера не має — там нічого перевіряти.
        private static bool StillEnabled()
        {
            if (_skipMarkerCheck) return true;   // тест-шов: гарнес пише у ТИМЧАСОВИЙ файл, не в %APPDATA%
            if (_source == Ident.EnvDebug) return true;
            int now = Environment.TickCount;
            if (unchecked(now - _markerCheckedTick) < _markerRecheckMs) return true;

            bool exists = false, checkFailed = false;
            try { exists = File.Exists(Path.Combine(Config.Dir, "debug.on")); }
            catch { checkFailed = true; }

            if (!ConsentVerdict(exists, checkFailed))
            {
                // Маркер справді зник -> гасимо прапорець негайно. А от при ЗБОЇ перевірки _on не
                // чіпаємо і тік не оновлюємо: збій зазвичай транзієнтний, наступний виклик перевірить
                // знову й робота сама відновиться — але цей рядок усе одно НЕ пишеться.
                if (!checkFailed) _on = false;
                return false;
            }
            _markerCheckedTick = now;   // підтверджено — наступна перевірка через вікно
            return true;
        }

        // Чисте рішення про згоду, винесене з будь-якого I/O. Без цього гілку FAIL-CLOSED не дістати
        // ні файлом, ні середовищем (File.Exists не кидає), і мутаційний тест «виживав би назавжди» —
        // тобто обіцянка приватності лишалась би недоведеною. Сумнів тлумачиться на користь мовчання.
        internal static bool ConsentVerdict(bool markerExists, bool checkFailed)
        {
            if (checkFailed) return false;   // FAIL-CLOSED
            return markerExists;
        }

        public static void Log(string msg)
        {
            if (!_on) return;
            if (!StillEnabled()) return;
            EnsureWriter();
            _queue.TryAdd(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
        }

        // СИНХРОННИЙ, пише ЗАВЖДИ (навіть коли лог off): краш-гард у Main його викликає, а процес,
        // що падає, може не дожити до злиття черги фонового писаря. Тому — прямий запис.
        private static string _lastCrashKey = "";
        private static int _crashCount;
        private const int MaxCrashEntries = 200;
        private const long MaxCrashBytes = 1024 * 1024;

        public static void LogCrash(string where, Exception ex)
        {
            try
            {
                // ДЕДУП + СТЕЛЯ + РОТАЦІЯ. Не було нічого з трьох, а запис синхронний і на
                // UI-потоці: виняток в OnPaint не спиняє цикл повідомлень, Windows одразу шле
                // WM_PAINT знову, і той самий стек писався сотні разів на секунду. crash.log ріс
                // без межі, UI виглядав замороженим, а повноекранний TopMost-оверлей лишався
                // поверх усього — до інших вікон було не дістатися. debug.log таку ротацію мав
                // від початку; crash.log — найважливіший файл — не мав жодної.
                string key = where + "|" + (ex == null ? "null" : ex.GetType().Name + "|" + ex.Message);
                if (key == _lastCrashKey) return;
                _lastCrashKey = key;
                if (++_crashCount > MaxCrashEntries) return;

                Directory.CreateDirectory(Config.Dir);
                string p = Path.Combine(Config.Dir, "crash.log");
                try
                {
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > MaxCrashBytes)
                    {
                        string bak = p + ".1";
                        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                        try { File.Move(p, bak); } catch { }
                    }
                }
                catch { }
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Ident.AppId + " " +
                    Ver.Number + "  " + where + ":\r\n" + (ex == null ? "(null)" : ex.ToString()) + "\r\n\r\n", Utf8);
            }
            catch { }
        }

        internal static void ResetCrashDedupForTest() { _lastCrashKey = ""; _crashCount = 0; _shown = 0; }

        // ★ STD-DIAG-03: ПАДІННЯ МУСИТЬ БУТИ ВИДНО ЛЮДИНІ, а не лише файлу.
        //
        // Досі краш-гард лише писав у crash.log. Для користувача це виглядало так: дія просто не
        // спрацювала, вікно не з'явилось, застосунок «нічого не зробив». Він не знає ні що сталося,
        // ні що десь є файл, ні що про це варто повідомити. Мовчазне падіння — найгірший різновид
        // брехні про успіх, бо тут навіть немає повідомлення, яке можна було б назвати хибним.
        //
        // РІШЕННЯ ОКРЕМО ВІД ПОКАЗУ (чиста функція) — рівно тому ж, чому окремі ConsentVerdict і
        // Theme.Decide: вікно в тесті не покажеш, а от рішення «показувати чи ні» перевірити треба.
        // Правило: перший раз для КОЖНОЇ причини, і не більше MaxCrashDialogs за сеанс. Без стелі
        // краш-шторм у OnPaint (той самий, від якого тут дедуп) відкрив би сотні вікон поспіль і
        // машину довелося б гасити мишею наосліп.
        private static int _shown;
        private const int MaxCrashDialogs = 3;

        internal static bool CrashDialogVerdict(bool firstTimeForThisCause, int alreadyShown)
        {
            if (!firstTimeForThisCause) return false;
            return alreadyShown < MaxCrashDialogs;
        }

        // true = це падіння варте вікна. Викликається краш-гардом ПІСЛЯ LogCrash.
        public static bool ShouldTellUser(string where, Exception ex)
        {
            try
            {
                string key = where + "|" + (ex == null ? "null" : ex.GetType().Name + "|" + ex.Message);
                bool first = key != _lastShownKey;
                if (!CrashDialogVerdict(first, _shown)) return false;
                _lastShownKey = key;
                _shown++;
                return true;
            }
            catch { return false; }   // рішення про вікно не має права саме стати падінням
        }

        private static string _lastShownKey = "";

        public static string CrashLogPath { get { return Path.Combine(Config.Dir, "crash.log"); } }

        private static void EnsureWriter()
        {
            if (_writer != null) return;
            lock (_startLock)
            {
                if (_writer != null) return;
                var t = new Thread(WriterLoop) { IsBackground = true, Name = "ValeraScreenshot-DiagWriter" };
                t.Start();
                _writer = t;
                _queue.TryAdd(DateTime.Now.ToString("HH:mm:ss.fff") + L.S("  --- сесія логу, ", "  --- log session, ") + Ver.Display +
                              (string.IsNullOrEmpty(_source) ? "" : L.S(", джерело: ", ", source: ") + SourceText()) + " ---\r\n");
            }
        }

        private static void WriterLoop()
        {
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                var sb = new StringBuilder(first);
                string more;
                while (_queue.TryTake(out more)) sb.Append(more);
                string block = sb.ToString();
                try
                {
                    RotateIfNeeded(block.Length);
                    File.AppendAllText(_path, block, Utf8);
                }
                catch { }
            }
        }

        private static void RotateIfNeeded(int incoming)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length + incoming > _maxBytes)
                {
                    string bak = _path + ".1";
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                    try { File.Move(_path, bak); } catch { }
                }
            }
            catch { }
        }

        // ---- тестові шви (лише для тест-харнеса тієї ж збірки) ----
        internal static void SetPathForTest(string p) { _path = p; }
        internal static void SetSkipMarkerCheckForTest(bool skip) { _skipMarkerCheck = skip; }
        internal static void SetRecheckMsForTest(int ms) { _markerRecheckMs = ms; }
        internal static void SetSourceForTest(string s) { _source = s; }
        internal static void ResetMarkerTickForTest() { _markerCheckedTick = 0; }
        internal static bool StillEnabledForTest() { return StillEnabled(); }
        internal static void SetMaxBytesForTest(long n) { _maxBytes = n; }
        internal static void SetEnabledForTest(bool on) { _on = on; }
        internal static void FlushForTest(int timeoutMs)
        {
            int waited = 0;
            while (_queue.Count > 0 && waited < timeoutMs) { Thread.Sleep(10); waited += 10; }
            Thread.Sleep(40);
        }
    }
}
