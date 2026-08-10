using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ValeraScreenshot
{
    // Тема у стилі Microsoft Office / Fluent 2: білі картки на світлому тлі,
    // акцент #0F6CBD, кнопки із заокругленням 4 px, стримані бордери.
    internal static class Theme
    {
        public enum ThemeMode { System, Light, Dark }
        public static ThemeMode Mode { get; private set; }
        private static bool _dark;
        public static bool IsDark { get { return _dark; } }

        // Спрацьовує, коли палітра справді змінилась у рантаймі.
        public static event Action Changed;

        // Палітра — МУТАБЕЛЬНА (перемикається ApplyLight/ApplyDark). Мальовані вручну контроли
        // (Card, ToggleSwitch, тулбар оверлея) читають ці поля ПІД ЧАС МАЛЮВАННЯ, тож Invalidate()
        // перетемлює їх безкоштовно. Через це вони мусять лишатися полями, а не readonly-константами.
        public static Color PageBg, CardBg, CardBorder, TextPrimary, TextSecondary,
            Accent, AccentHover, AccentPressed, AccentText,
            SubtleHover, SubtlePressed, SelectedBg,
            ControlBg, ControlBorder, ToggleOff, Divider,
            // Колір тексту НА підсвіченому/наведеному тлі. У світлій і темній темах це той самий
            // TextPrimary (підсвітка там ледь помітна), але у високій контрастності підсвітка —
            // це системний Highlight, і на ньому читається ЛИШЕ HighlightText. Без окремого поля
            // наведений пункт меню в цьому режимі ставав нечитним.
            SelectedText;

        static Theme() { ApplyLight(); }   // розумний дефолт, поки не викликано Init()

        // mode: "light" | "dark" | будь-що інше ("auto") = слідувати за налаштуванням Windows.
        public static ThemeMode ParseMode(string mode)
        {
            return string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Light
                 : string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark
                 : ThemeMode.System;
        }

        public static void Init(string mode)
        {
            Mode = ParseMode(mode);
            Resolve();
        }

        // Викликається, коли змінилася тема ОС АБО режим високої контрастності; перетемлює лише
        // якщо є що міняти. Високу контрастність поважаємо ЗАВЖДИ, хоч би яку тему обрав
        // користувач: вона не оформлення, а засіб доступності.
        public static void SystemChanged() { Resolve(); }

        public enum Palette { Light, Dark, HighContrast }

        // ЧИСТЕ рішення, окремо від малювання. Так само зроблено Diag.ConsentVerdict, і з тієї ж
        // причини: гілку «висока контрастність» не дістати ні файлом, ні змінною середовища —
        // її вмикає користувач у налаштуваннях Windows. Без чистої функції ця гілка не мала б
        // жодного тесту й мутація в ній вижила б назавжди.
        public static Palette Decide(bool highContrast, ThemeMode mode, bool systemDark)
        {
            if (highContrast) return Palette.HighContrast;
            if (mode == ThemeMode.Dark) return Palette.Dark;
            if (mode == ThemeMode.Light) return Palette.Light;
            return systemDark ? Palette.Dark : Palette.Light;
        }

        private static bool _resolvedOnce;
        private static Palette _cur;
        public static Palette Current { get { return _cur; } }

        // Шов ДЛЯ ПРУФУ Й ТЕСТІВ: увімкнути високу контрастність у самій Windows заради знімка
        // не можна — це змінило б робочий стіл власника. null = читати систему.
        private static bool? _hcOverrideForTest;
        internal static void SetHighContrastForTest(bool? value) { _hcOverrideForTest = value; _resolvedOnce = false; Resolve(); }

        public static bool SystemHighContrast()
        {
            if (_hcOverrideForTest.HasValue) return _hcOverrideForTest.Value;
            try { return SystemInformation.HighContrast; }
            catch { return false; }
        }

        private static void Resolve()
        {
            Palette p = Decide(SystemHighContrast(), Mode, SystemUsesDark());
            if (_resolvedOnce && p == _cur) return;   // палітра та сама — нікого не турбуємо
            _resolvedOnce = true;
            _cur = p;
            if (p == Palette.HighContrast) ApplyHighContrast();
            else if (p == Palette.Dark) ApplyDark();
            else ApplyLight();
            // ★ Init РАНІШЕ НЕ СПОВІЩАВ. Через це «застосувати одразу» діяло лише на вікна, які
            //   ще не створені: перемикач теми в Параметрах міняв палітру, а САМЕ вікно
            //   Параметрів лишалось у старій. Тепер подія йде з обох шляхів — і з явного
            //   вибору користувача, і зі зміни теми Windows.
            if (Changed != null) Changed();
        }

        // HKCU AppsUseLightTheme == 0 -> темна.
        public static bool SystemUsesDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null) { object v = k.GetValue("AppsUseLightTheme"); if (v is int) return (int)v == 0; }
                }
            }
            catch { }
            return false;
        }

        private static void ApplyLight()
        {
            _dark = false;
            PageBg = Color.FromArgb(0xFA, 0xFA, 0xFA);
            CardBg = Color.White;
            CardBorder = Color.FromArgb(0xE0, 0xE0, 0xE0);
            TextPrimary = Color.FromArgb(0x24, 0x24, 0x24);
            TextSecondary = Color.FromArgb(0x61, 0x61, 0x61);
            Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);
            AccentHover = Color.FromArgb(0x11, 0x5E, 0xA3);
            AccentPressed = Color.FromArgb(0x0F, 0x54, 0x8C);
            AccentText = Color.White;
            SubtleHover = Color.FromArgb(0xF5, 0xF5, 0xF5);
            SubtlePressed = Color.FromArgb(0xEB, 0xEB, 0xEB);
            SelectedBg = Color.FromArgb(0xEB, 0xF3, 0xFC);
            SelectedText = TextPrimary;
            ControlBg = Color.White;
            // WCAG 2.1 SC 1.4.11: межа, якою користувач упізнає поле вводу чи кнопку, мусить
            // мати 3:1 до сусіднього тла. Було #D1D1D1 — це 1.53:1 до білої картки, тобто
            // рамки фактично не було видно. #8A8A8A дає 3.45:1 (виміряно пруф-гейтом).
            ControlBorder = Color.FromArgb(0x8A, 0x8A, 0x8A);
            ToggleOff = Color.FromArgb(0x8A, 0x8A, 0x8A);
            Divider = Color.FromArgb(0xEB, 0xEB, 0xEB);
        }

        private static void ApplyDark()
        {
            _dark = true;
            PageBg = Color.FromArgb(0x20, 0x20, 0x20);
            CardBg = Color.FromArgb(0x2B, 0x2B, 0x2B);
            CardBorder = Color.FromArgb(0x3D, 0x3D, 0x3D);
            TextPrimary = Color.FromArgb(0xF0, 0xF0, 0xF0);
            TextSecondary = Color.FromArgb(0xA6, 0xA6, 0xA6);
            Accent = Color.FromArgb(0x4C, 0xA0, 0xE0);      // світліший синій: на темному тлі #0F6CBD «тоне»
            AccentHover = Color.FromArgb(0x62, 0xB0, 0xEA);
            AccentPressed = Color.FromArgb(0x3C, 0x8C, 0xC8);
            AccentText = Color.FromArgb(0x10, 0x10, 0x10);  // текст НА акценті — темний, інакше не читається
            SubtleHover = Color.FromArgb(0x38, 0x38, 0x38);
            SubtlePressed = Color.FromArgb(0x45, 0x45, 0x45);
            SelectedBg = Color.FromArgb(0x2A, 0x3E, 0x4F);
            SelectedText = TextPrimary;
            ControlBg = Color.FromArgb(0x33, 0x33, 0x33);
            // Те саме правило 3:1, дзеркально: #4A4A4A давав 1.60:1 до картки #2B2B2B —
            // порожнє поле в темній темі було невидиме. #7A7A7A дає 3.29:1.
            ControlBorder = Color.FromArgb(0x7A, 0x7A, 0x7A);
            ToggleOff = Color.FromArgb(0x7A, 0x7A, 0x7A);
            Divider = Color.FromArgb(0x3A, 0x3A, 0x3A);
        }

        // ВИСОКА КОНТРАСТНІСТЬ. Тут будь-яка ВЛАСНА палітра — дефект, а не оформлення: користувач
        // явно сказав системі, якими кольорами він здатен читати екран, і Windows гарантує між
        // ними контраст. Наш «гарний» синій акцент або сіра підказка цю гарантію ламають, і
        // ламають саме тим людям, яким вона єдина й потрібна.
        // Тому всі кольори беруться з SystemColors — вони йдуть за обраною темою високої
        // контрастності (їх чотири стандартні, плюс власні користувацькі).
        private static void ApplyHighContrast()
        {
            _dark = SystemColors.Window.GetBrightness() < 0.5f;
            PageBg = SystemColors.Control;
            CardBg = SystemColors.Window;
            CardBorder = SystemColors.WindowFrame;
            TextPrimary = SystemColors.WindowText;
            // НЕ GrayText: у схемах високої контрастності це колір ВИМКНЕНОГО елемента, і він
            // законно тьмяний. Наші «підказки» — не вимкнені елементи, а текст, який треба
            // прочитати; у цьому режимі вони просто зливаються за виглядом з основним текстом,
            // і це правильно.
            TextSecondary = SystemColors.WindowText;
            Accent = SystemColors.Highlight;
            AccentHover = SystemColors.Highlight;
            AccentPressed = SystemColors.Highlight;
            AccentText = SystemColors.HighlightText;
            SubtleHover = SystemColors.Highlight;
            SubtlePressed = SystemColors.Highlight;
            SelectedBg = SystemColors.Highlight;
            SelectedText = SystemColors.HighlightText;
            ControlBg = SystemColors.Window;
            ControlBorder = SystemColors.WindowFrame;
            ToggleOff = SystemColors.WindowText;
            Divider = SystemColors.WindowFrame;
        }

        public static readonly Font Title = new Font("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Section = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Body = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Caption = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Button = new Font("Segoe UI Semibold", 9.75f, FontStyle.Regular, GraphicsUnit.Point);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Шов для ПРУФ-ГЕЙТА: контрол сам називає прямокутник, у якому малює ТЕКСТ.
    // Потрібен там, де в контролі є ще й декоративна графіка: на кнопці «Signal» вимірник
    // інакше брав кольорову позначку бренду замість тексту й рапортував хибний дефект —
    // а в дзеркальному випадку сховав би НЕЧИТНИЙ текст за яскравою позначкою.
    // Координати — відносно клієнтської області контрола.
    internal interface IProofInk
    {
        Rectangle InkRect { get; }
    }

    // Форма, чий ЗАГОЛОВОК слідує за темою застосунку (immersive dark mode, Win10 20H1+/Win11).
    // Без цього темний застосунок отримує білий заголовок — «білу кромку», видну на кожному скріні.
    internal class ThemedForm : Form
    {
        private bool _hooked;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.SetDarkTitleBar(Handle, Theme.IsDark);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // ТЛО ФОРМИ теж перечитується з палітри, не лише кольори дітей. Конструктор форми
            // ставить BackColor один раз; якщо між конструюванням і показом тема змінилася
            // (а вона змінюється: перемикач у Параметрах, тема Windows, відкат «Скасувати»),
            // вікно виходило з ТЛОМ старої теми і текстом нової. Це буквально «темні поля у
            // світлій темі», і зловив це пруф-гейт: #616161 на #202020, тобто 2.63:1.
            BackColor = Theme.PageBg;
            if (IsHandleCreated) Ui.SetDarkTitleBar(Handle, Theme.IsDark);
            // Дочірні хендли існують лише тут, а не в OnHandleCreated форми. Саме тому
            // ApplyScrollTheme ніколи не діставався до панелі з AutoScroll: її скролбар
            // створюється разом із хендлом панелі, тобто пізніше за хендл форми.
            Ui.Retheme(this);
            if (!_hooked) { Theme.Changed += OnThemeChanged; _hooked = true; }
        }

        // Подія Theme.Changed існувала з першого дня теми і НЕ МАЛА ЖОДНОГО ПІДПИСНИКА, як і
        // Ui.Retheme не мала жодного зовнішнього виклику. Тобто перемикання теми діяло лише на
        // вікна, ЩЕ НЕ СТВОРЕНІ: усе відкрите лишалось у старій палітрі. Це і є «темні поля у
        // світлій темі» з боку користувача, який перемкнув тему при відкритому вікні.
        private void OnThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke((Action)OnThemeChanged); } catch { } return; }
            BackColor = Theme.PageBg;
            if (IsHandleCreated) Ui.SetDarkTitleBar(Handle, Theme.IsDark);
            Ui.Retheme(this);
        }

        protected override void Dispose(bool disposing)
        {
            // Theme.Changed — СТАТИЧНА подія. Без відписки закрита форма лишається живою через
            // посилання з делегата: витік гірший за той, що лікували. Той самий шов уже стоїть
            // у OverlayForm для SystemEvents.DisplaySettingsChanged.
            if (disposing && _hooked) { Theme.Changed -= OnThemeChanged; _hooked = false; }
            base.Dispose(disposing);
        }
    }

    // Меню трея — ГОЛОВНА поверхня застосунку: усе, крім гарячих клавіш, починається з нього.
    // Воно малювалось системним ToolStripProfessionalRenderer, тобто лишалось світлим у темній
    // темі. Тут палітра меню читається з Theme на кожному малюванні.
    internal class ThemedColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.CardBg; } }
        public override Color MenuItemSelected { get { return Theme.SubtleHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.SubtleHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.SubtleHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.SubtlePressed; } }
        public override Color MenuItemPressedGradientMiddle { get { return Theme.SubtlePressed; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.SubtlePressed; } }
        public override Color MenuItemBorder { get { return Theme.ControlBorder; } }
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color ImageMarginGradientBegin { get { return Theme.CardBg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.CardBg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.CardBg; } }
        public override Color SeparatorDark { get { return Theme.Divider; } }
        public override Color SeparatorLight { get { return Theme.Divider; } }
        public override Color CheckBackground { get { return Theme.SelectedBg; } }
        public override Color CheckSelectedBackground { get { return Theme.SelectedBg; } }
        public override Color CheckPressedBackground { get { return Theme.SelectedBg; } }
        public override Color ToolStripBorder { get { return Theme.CardBorder; } }
    }

    internal class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled)
            {
                // На підсвіченому пункті колір беремо окремий: у високій контрастності підсвітка
                // — це системний Highlight, на якому читається лише HighlightText.
                e.TextColor = e.Item.Selected ? Theme.SelectedText : Theme.TextPrimary;
                base.OnRenderItemText(e);
                return;
            }
            // ВИМКНЕНИЙ пункт малюємо САМІ. Базовий рендерер ігнорує e.TextColor і кличе
            // ControlPaint.DrawStringDisabled, який змішує текст із системним сірим: пруф-гейт
            // виміряв 2.68:1 (#6A6C6B на #2B2B2B). У нас цей пункт — не «недоступна дія», а
            // ЗАГОЛОВОК меню, тобто інформація, яку треба читати.
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle,
                Theme.TextSecondary, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextPrimary;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y, e.ImageRectangle.Width, e.ImageRectangle.Height);
            using (var b = new SolidBrush(Theme.SelectedBg)) e.Graphics.FillRectangle(b, r);
            using (var p = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var p = new Pen(Theme.TextPrimary, 1.8f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(p, new[]
                {
                    new Point(r.X + 4, r.Y + r.Height / 2),
                    new Point(r.X + r.Width / 2 - 1, r.Bottom - 5),
                    new Point(r.Right - 4, r.Y + 4)
                });
            }
        }
    }

    // Заокруглена «картка», як групи в сучасних Параметрах Office/Windows.
    internal class Card : Panel
    {
        public Card()
        {
            BackColor = Theme.CardBg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            // Картка — це група елементів, а не декоративна панель. Роль Grouping дає
            // Екранному диктору змогу назвати секцію («Гарячі клавіші»), заходячи в неї;
            // імʼя ставиться в місці створення.
            AccessibleRole = AccessibleRole.Grouping;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent != null) e.Graphics.Clear(Parent.BackColor);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(r, 8))
            using (var b = new SolidBrush(Theme.CardBg))
            using (var pen = new Pen(Theme.CardBorder))
            {
                e.Graphics.FillPath(b, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    // Системний flat-рендер лишає в темній темі БІЛЕ лице комбобокса — його не лікує ні BackColor,
    // ні FlatStyle. Єдиний надійний шлях — малювати самому (STD-UI-06).
    internal class ThemedCombo : ComboBox
    {
        public ThemedCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            Font = Theme.Body;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Theme.SelectedBg : Theme.ControlBg))
                e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, e.Bounds,
                sel ? Theme.SelectedText : Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // МАЛЮЄМО ЗАМІСТЬ СИСТЕМИ, а не поверх неї.
        //
        // Овнер-дров у ComboBox стосується лише ЕЛЕМЕНТІВ СПИСКУ (OnDrawItem). Закрите лице
        // малює сам Windows, а для FlatStyle.Flat WinForms ще й домальовує рамку й кнопку
        // списку СИСТЕМНИМИ кольорами — і робить це ПІСЛЯ нашого OnPaint. Пруф-гейт зафіксував
        // наслідок числом: у темній темі комбо «Формат» давало суцільний СВІТЛИЙ блок 96x26 px
        // (біла рамка + біла кнопка з чорним трикутником) — класичне «біле лице комбобокса»
        // зі STD-UI-06. Тому WM_PAINT перехоплюється цілком: система до пікселів не доходить.
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public int rcPaintLeft, rcPaintTop, rcPaintRight, rcPaintBottom;
            public int fRestore, fIncUpdate;
            public int res0, res1, res2, res3, res4, res5, res6, res7;
        }

        private const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }  // без мигання
            if (m.Msg == WM_PAINT)
            {
                var ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(m.HWnd, ref ps);
                try
                {
                    if (hdc != IntPtr.Zero)
                        using (var g = Graphics.FromHdc(hdc)) PaintFace(g);
                }
                catch { }
                finally { EndPaint(m.HWnd, ref ps); }
                return;
            }
            base.WndProc(ref m);
        }

        private void PaintFace(Graphics g)
        {
            using (var bg = new SolidBrush(Theme.ControlBg)) g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Focused ? Theme.Accent : Theme.ControlBorder))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(6, 0, Width - 24, Height),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.TextPrimary))
            {
                float cx = Width - 13, cy = Height / 2f - 1;
                g.FillPolygon(b, new[] { new PointF(cx - 4, cy - 1), new PointF(cx + 4, cy - 1), new PointF(cx, cy + 4) });
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    internal enum BtnKind { Primary, Secondary, Subtle }

    // Кнопка у стилі Office: заокруглення 4 px, кастомний рендер, три види.
    // Glyph ≠ Empty — кольорова кругла позначка зліва (для списків цілей).
    internal class OfficeButton : Button, IProofInk
    {
        public BtnKind Kind = BtnKind.Secondary;
        public Color Glyph = Color.Empty;
        private bool _hover, _down;

        // ЄДИНЕ джерело розкладки тексту: ним малює OnPaint і його ж міряє пруф-гейт.
        // Дублювати цю арифметику у вимірнику означало б завести другу правду про те,
        // де текст, — тобто рівно той клас дубля, проти якого існує Ident.cs.
        public Rectangle InkRect
        {
            get
            {
                // Верх і низ підтиснуті на 3 px, щоб у зону тексту не потрапляла ВЛАСНА рамка
                // кнопки: вимірник інакше приймав її за текст (на «Signal» він рапортував
                // 3.45:1 замість справжніх 15.5:1). Симетричний відступ не зсуває ні центроване,
                // ні ліве вирівнювання.
                if (TextAlign != ContentAlignment.MiddleLeft)
                    return new Rectangle(3, 3, Math.Max(1, Width - 6), Math.Max(1, Height - 6));
                int pad = Glyph != Color.Empty ? 44 : 14;
                return new Rectangle(pad, 3, Math.Max(1, Width - pad - 6), Math.Max(1, Height - 6));
            }
        }

        public OfficeButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.Button;
            Height = 32;
            Cursor = Cursors.Hand;
            // Контрол малює себе САМ, тож для засобів доступності він за замовчуванням —
            // безіменний прямокутник. Роль оголошуємо явно; імʼя береться з Text (Button.
            // AccessibilityObject робить це сам), а де тексту мало — дописуємо AccessibleName
            // у місці створення.
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Parent != null) g.Clear(Parent.BackColor);

            Color bg, fg, border = Color.Empty;
            switch (Kind)
            {
                // Кольори БЕРУТЬСЯ З ПАЛІТРИ під час малювання. Доти тут стояв Color.White, і в темній
                // темі «Скасувати»/«Огляд…» ставали світлим текстом на світлому тлі — нечитними.
                case BtnKind.Primary:
                    bg = _down ? Theme.AccentPressed : (_hover ? Theme.AccentHover : Theme.Accent);
                    fg = Theme.AccentText;
                    break;
                case BtnKind.Subtle:
                    bg = _down ? Theme.SubtlePressed : (_hover ? Theme.SubtleHover : (Parent != null ? Parent.BackColor : Theme.CardBg));
                    // У високій контрастності «легка підсвітка» — це системний Highlight, тобто
                    // насичений колір: текст на ньому мусить бути HighlightText, інакше наведена
                    // кнопка стає нечитною рівно для тих, кому цей режим і потрібен.
                    fg = (_hover || _down) ? Theme.SelectedText : Theme.TextPrimary;
                    break;
                default:
                    bg = _down ? Theme.SubtlePressed : (_hover ? Theme.SubtleHover : Theme.ControlBg);
                    fg = (_hover || _down) ? Theme.SelectedText : Theme.TextPrimary;
                    border = Theme.ControlBorder;
                    break;
            }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 4))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border)) g.DrawPath(pen, path);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                        g.DrawPath(pen, path);
            }

            var textR = InkRect;
            var flags = TextAlign == ContentAlignment.MiddleLeft
                ? TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                : TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            if (Glyph != Color.Empty)
            {
                using (var gb = new SolidBrush(Glyph))
                    g.FillEllipse(gb, 14, Height / 2 - 8, 16, 16);
                using (var ring = new Pen(Color.FromArgb(60, 0, 0, 0)))
                    g.DrawEllipse(ring, 14, Height / 2 - 8, 16, 16);
            }
            TextRenderer.DrawText(g, Text, Font, textR, fg, flags);
        }
    }

    // Пігулка-перемикач Fluent (кольори тягне з Theme).
    internal class ToggleSwitch : Control
    {
        private bool _on;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            Size = new Size(40, 20);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        // БЕЗ ЦЬОГО ЕКРАННИЙ ДИКТОР НЕ ЧУЄ СТАНУ. ToggleSwitch — не CheckBox, а власний Control:
        // роль можна оголосити властивістю, а ось «увімкнено/вимкнено» система нізвідки не
        // візьме. Тобто незряча людина чула б назву перемикача й не чула, у якому він стані —
        // тумблер для неї не існує як тумблер.
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ToggleAccessibleObject(this);
        }

        private sealed class ToggleAccessibleObject : Control.ControlAccessibleObject
        {
            private readonly ToggleSwitch _t;
            public ToggleAccessibleObject(ToggleSwitch t) : base(t) { _t = t; }

            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = base.State | AccessibleStates.Focusable;
                    if (_t.Checked) s |= AccessibleStates.Checked;
                    if (_t.Focused) s |= AccessibleStates.Focused;
                    return s;
                }
            }

            public override string DefaultAction
            {
                get { return _t.Checked ? L.S("Вимкнути", "Turn off") : L.S("Увімкнути", "Turn on"); }
            }

            public override void DoDefaultAction() { _t.Checked = !_t.Checked; }
        }

        public bool Checked
        {
            get { return _on; }
            set { if (_on != value) { _on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Parent != null) g.Clear(Parent.BackColor);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, r.Height / 2))
            {
                if (_on)
                {
                    using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                }
                else
                {
                    using (var pen = new Pen(Theme.ToggleOff, 1.4f)) g.DrawPath(pen, path);
                }
            }

            int pad = Math.Max(2, r.Height / 6);
            int kd = r.Height - 2 * pad;
            int kx = _on ? r.Right - kd - pad : r.Left + pad;
            int ky = r.Top + pad;
            using (var b = new SolidBrush(_on ? Theme.AccentText : Theme.ToggleOff))
                g.FillEllipse(b, kx, ky, kd, kd);

            if (Focused)
            {
                using (var pen = new Pen(Theme.Accent, 1f) { DashStyle = DashStyle.Dot })
                using (var path = Theme.Rounded(new Rectangle(r.X, r.Y, r.Width, r.Height), r.Height / 2))
                    g.DrawPath(pen, path);
            }
        }
    }

    internal static class Ui
    {
        public static Label Title(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y, Width = w, Height = 34, Font = Theme.Title, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent };
        }

        public static Label Section(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y, Width = w, Height = 22, Font = Theme.Section, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent };
        }

        public static Label Body(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y, Width = w, Font = Theme.Body, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent, AutoSize = false, Height = 20 };
        }

        public static Label Caption(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y, Width = w, Font = Theme.Caption, ForeColor = Theme.TextSecondary, BackColor = Color.Transparent, AutoSize = false, Height = 16 };
        }

        public static OfficeButton Btn(string text, int x, int y, int w, BtnKind kind)
        {
            return new OfficeButton { Text = text, Left = x, Top = y, Width = w, Kind = kind };
        }

        public static void StyleInput(TextBox t)
        {
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.Body;
            t.BackColor = Theme.ControlBg;
            t.ForeColor = Theme.TextPrimary;
        }

        public static PaddedTextBox Input(int x, int y, int w)
        {
            return new PaddedTextBox { Left = x, Top = y, Width = w };
        }

        // P/Invoke ТЕМИ живе саме тут, а не в Native.cs: build_setup.ps1 компілює Setup.cs+Ui.cs і
        // НЕ включає Native.cs — винесеш туди, і інсталятор перестане збиратися (STD-CODE-04).
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Зробити заголовок вікна темним/світлим під тему (прибирає білу кромку).
        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            try
            {
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+/Win11); 19 — фолбек до 20H1.
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        // Темні фікси системних контролів: без них у темній темі лишається БІЛЕ лице комбобокса,
        // світла рамка FixedSingle і світлий скролбар — тобто «темна тема», яку видно як зламану.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        // ★ ТУТ СТОЯЛИ НЕДОКУМЕНТОВАНІ ОРДИНАЛИ uxtheme (#135 SetPreferredAppMode,
        //   #133 AllowDarkModeForWindow, #136 FlushMenuThemes) — звичний спосіб перемкнути
        //   «тему процесу» для частин вікна, які малює система. Я їх ПРИБРАВ, і ось чому:
        //   мутація UI-4 (примусово ForceLight) лишалась SURVIVED навіть після того, як пруф
        //   покрив і скролбар, і РОЗКРИТИЙ список комбобокса. Прямий експеримент підтвердив:
        //   вимкнення обох ординалів не міняє ЖОДНОГО пікселя — і скролбар, і вікно списку
        //   темнить сам SetWindowTheme(hwnd, "DarkMode_Explorer"), а він задокументований.
        //   Везти в реліз недокументований виклик, який не можна показати в дії, — це ризик
        //   без вигоди. Що працює — лишилось; що не доведене — пішло.
        public static void ApplyScrollTheme(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            // КОМБОБОКС СЮДИ НЕ МОЖНА. Він у нас повністю owner-draw, і "DarkMode_Explorer"
            // вмикає СИСТЕМНЕ малювання кнопки списку — вона лягає поверх нашого малюнка й
            // виходить світлий квадрат на темному полі. Пруф-гейт зловив це як «суцільний
            // світлий блок 96x26 px» рівно там, де стоїть комбо «Формат».
            if (c is ComboBox) return;
            try { SetWindowTheme(c.Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { }
        }

        // Кнопки-стрілки NumericUpDown малює сам WinForms через ControlPaint.DrawScrollButton
        // СИСТЕМНИМИ кольорами. Ні BackColor, ні uxtheme на них не діють: у темній темі
        // лишається світлий блок збоку від числа. Перемальовуємо їх поверх, тим самим прийомом
        // підкласу вікна, що й рамку поля.
        public static void HookThemedSpin(NumericUpDown nud)
        {
            if (nud == null) return;
            foreach (Control child in nud.Controls)
            {
                if (child is TextBox) continue;   // UpDownEdit — його малюємо звичайними кольорами
                new ThemedSpinHook(child);
                return;
            }
        }

        private sealed class ThemedSpinHook : NativeWindow
        {
            private const int WM_PAINT = 0x000F;
            private readonly Control _c;

            public ThemedSpinHook(Control c)
            {
                _c = c;
                if (c.IsHandleCreated) AssignHandle(c.Handle);
                c.HandleCreated += delegate { AssignHandle(_c.Handle); };
                c.HandleDestroyed += delegate { ReleaseHandle(); };
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != WM_PAINT) return;
                try
                {
                    using (var g = Graphics.FromHwnd(m.HWnd))
                    {
                        int w = _c.Width, h = _c.Height, half = h / 2;
                        using (var bg = new SolidBrush(Theme.ControlBg))
                            g.FillRectangle(bg, 0, 0, w, h);
                        using (var pen = new Pen(Theme.ControlBorder))
                            g.DrawLine(pen, 0, half, w - 1, half);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(Theme.TextPrimary))
                        {
                            float cx = w / 2f;
                            g.FillPolygon(b, new[] { new PointF(cx - 3.5f, half / 2f + 2), new PointF(cx + 3.5f, half / 2f + 2), new PointF(cx, half / 2f - 2) });
                            float cy2 = half + half / 2f;
                            g.FillPolygon(b, new[] { new PointF(cx - 3.5f, cy2 - 2), new PointF(cx + 3.5f, cy2 - 2), new PointF(cx, cy2 + 2) });
                        }
                    }
                }
                catch { }   // стрілки — косметика: їхня невдача не має ламати введення числа
            }
        }

        // STD-UI-06. WinForms малює BorderStyle.FixedSingle системним COLOR_WINDOWFRAME, тобто
        // ЧОРНИМ. Наслідок видно на обох темах, і обидва боки виміряв пруф-гейт:
        //   світла — різка чорна рамка, якої немає в палітрі (суцільний темний блок 492x25 px),
        //   темна  — рамка зливається з карткою, і порожнє поле стає невидимим (1.1:1).
        // Лікується лише перемальовуванням у НЕклієнтській області: BackColor рамки не чіпає.
        public static void HookThemedBorder(Control c)
        {
            if (c == null) return;
            new ThemedBorderHook(c);
        }

        private sealed class ThemedBorderHook : NativeWindow
        {
            private const int WM_NCPAINT = 0x0085, WM_PAINT = 0x000F,
                              WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_SIZE = 0x0005;

            [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            private readonly Control _c;

            public ThemedBorderHook(Control c)
            {
                _c = c;
                if (c.IsHandleCreated) AssignHandle(c.Handle);
                c.HandleCreated += delegate { AssignHandle(_c.Handle); };
                c.HandleDestroyed += delegate { ReleaseHandle(); };
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != WM_NCPAINT && m.Msg != WM_PAINT && m.Msg != WM_SETFOCUS &&
                    m.Msg != WM_KILLFOCUS && m.Msg != WM_SIZE) return;
                IntPtr hdc = IntPtr.Zero;
                try
                {
                    hdc = GetWindowDC(m.HWnd);
                    if (hdc == IntPtr.Zero) return;
                    using (var g = Graphics.FromHdc(hdc))
                    // Фокус підсвічується акцентом: це і сучасний вигляд, і видима межа
                    // клавіатурної навігації (WCAG 2.4.7), якої системна рамка не давала.
                    using (var pen = new Pen(_c.Focused ? Theme.Accent : Theme.ControlBorder))
                        g.DrawRectangle(pen, 0, 0, _c.Width - 1, _c.Height - 1);
                }
                catch { }   // рамка — косметика: жодна її невдача не має валити ввід у поле
                finally { if (hdc != IntPtr.Zero) ReleaseDC(m.HWnd, hdc); }
            }
        }

        // Рекурсивно вирівняти вже створені контроли під поточну палітру.
        public static void Retheme(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                var cb = c as ComboBox;
                var tb = c as TextBox;
                var nud = c as NumericUpDown;
                if (cb != null || tb != null || nud != null)
                {
                    c.BackColor = Theme.ControlBg;
                    c.ForeColor = Theme.TextPrimary;
                }
                else if (c is Label) c.ForeColor = (c.Font == Theme.Caption) ? Theme.TextSecondary : Theme.TextPrimary;
                // CheckBox/RadioButton не потрапляли В ЖОДНУ гілку: їм діставався лише
                // ApplyScrollTheme, який темнить СИСТЕМНИЙ гліф, а підпис лишався в кольорі,
                // проставленому в конструкторі. У застосунку це не було видно (там ToggleSwitch),
                // зате в ІНСТАЛЯТОРІ їх чотири — і при зміні теми вони лишалися б з чужим текстом.
                else if (c is CheckBox || c is RadioButton) c.ForeColor = Theme.TextPrimary;
                else if (c is Card) c.BackColor = Theme.CardBg;
                else if (c is Panel && !(c is ToggleSwitch))
                {
                    // Роздільник — це Panel заввишки 1 px, пофарбована в Divider. Тло сторінки —
                    // усе інше. Розрізняємо за висотою, бо іншої різниці між ними немає.
                    c.BackColor = c.Height <= 2 ? Theme.Divider : Theme.PageBg;
                }
                ApplyScrollTheme(c);
                Retheme(c);
            }
            root.Invalidate(true);
        }

        // STD-UI-01: єдина точка діалогів. Ніде більше не викликати MessageBox.Show напряму.
        public static DialogResult Msg(string text)
        {
            return MessageBox.Show(text, L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static DialogResult Msg(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(text, caption, buttons, icon);
        }

        public static DialogResult Msg(IWin32Window owner, string text, string caption)
        {
            return MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static DialogResult Msg(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(owner, text, caption, buttons, icon);
        }
    }

    // TextBox із внутрішніми відступами (EM_SETMARGINS): лікує класичний баг WinForms,
    // коли перша літера обрізається біля лівого бордера FixedSingle. Плюс — вигляд як в Office.
    internal class PaddedTextBox : TextBox
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int EM_SETMARGINS = 0xD3;
        private const int EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;
        private int _left = 7, _right = 7;

        public PaddedTextBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
            Font = Theme.Body;
            BackColor = Theme.ControlBg;
            ForeColor = Theme.TextPrimary;
            // Системна рамка FixedSingle — чорна (COLOR_WINDOWFRAME) в обох темах. Перемальовуємо
            // її з палітри, інакше поле або кричить чорним у світлій темі, або зникає в темній.
            Ui.HookThemedBorder(this);
        }

        public void SetMargins(int left, int right) { _left = left; _right = right; ApplyMargins(); }

        private void ApplyMargins()
        {
            if (!IsHandleCreated) return;
            int packed = (_left & 0xFFFF) | ((_right & 0xFFFF) << 16);
            SendMessage(Handle, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), (IntPtr)packed);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyMargins(); }
    }

}
