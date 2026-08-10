using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ValeraScreenshot
{
    // Поле захоплення гарячої клавіші: клік у поле → натисніть комбінацію.
    // PrtScr особливий: система не шле WM_KEYDOWN для VK_SNAPSHOT — ловимо KeyUp.
    // Успадковує PaddedTextBox (внутрішні відступи проти обрізання тексту).
    internal class HotkeyBox : PaddedTextBox
    {
        public int Vk;
        public int Mods;

        public HotkeyBox(int vk, int mods)
        {
            Vk = vk; Mods = mods;
            ReadOnly = true;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
            ShortcutsEnabled = false;
            Render();
        }

        private void Render() { Text = Hotkeys.ToText(Vk, Mods); }

        private static int WinMods(Keys mods)
        {
            int m = 0;
            if ((mods & Keys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((mods & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
            if ((mods & Keys.Alt) != 0) m |= Native.MOD_ALT;
            return m;
        }

        private void Take(Keys code, Keys mods)
        {
            if (code == Keys.Delete || code == Keys.Back)
            {
                Vk = 0; Mods = 0; Render(); return; // прибрати клавішу (напр., запасну)
            }
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                code == Keys.LWin || code == Keys.RWin) return;
            Vk = (int)code;
            Mods = WinMods(mods);
            Render();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            Take(e.KeyCode, e.Modifiers);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PrintScreen)
            {
                e.Handled = true;
                Take(Keys.PrintScreen, e.Modifiers);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
    }

    internal class SettingsForm : ThemedForm
    {
        private readonly Config _cfg;

        private HotkeyBox _hkRegion, _hkFull, _hkRegion2, _hkFull2;
        private PaddedTextBox _tbDir, _tbTemplate;
        private ComboBox _cbFormat;
        private NumericUpDown _numQuality;
        private ToggleSwitch _tgCursor, _tgCopy, _tgSound, _tgBalloon, _tgStartup;
        private ComboBox _cbTheme, _cbLang;
        private bool _loading;                 // гасить живий перегляд теми під час LoadValues
        private readonly string _themeOnOpen;  // до чого повертати на «Скасувати»
        private readonly string _langOnOpen;

        private const int FormW = 560;
        private const int CardX = 18;
        private const int CardW = 524;
        private const int Pad = 16;

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            _themeOnOpen = cfg.UiTheme;
            _langOnOpen = cfg.UiLang;
            Text = L.S("Параметри — ВАЛЄРА Скріншот", "Settings — VALERA Screenshot");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.PageBg;
            Font = Theme.Body;
            ClientSize = new Size(FormW, 712);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadValues();
            ClampToWorkArea();
        }

        // ВІКНО МУСИТЬ ВМІЩАТИСЯ В ЕКРАН. AutoScaleMode.Dpi множить ClientSize на масштаб, тож при
        // 150 % висота 712 стає ~1068 px + рамка ≈ 1107 — а робоча висота ноутбука 1920×1080 з
        // панеллю завдань близько 1040. Форма FixedDialog, підвал пришпилений знизу, отже кнопка
        // «Зберегти» опинялася ЗА КРАЄМ ЕКРАНА і натиснути її було неможливо. Обхід існував
        // (AcceptButton = Enter), але користувач його не бачить.
        // Тіло вже має AutoScroll, тож достатньо не дати вікну перерости робочу область.
        private void ClampToWorkArea()
        {
            try
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int maxH = wa.Height - 40;
                if (Height > maxH)
                {
                    Height = maxH;
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + 20);
                }
            }
            catch { }
        }

        private void BuildUi()
        {
            // ---- header: логотип + заголовок + опис ----
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.PageBg };
            var pic = new PictureBox { Left = CardX, Top = 19, Width = 40, Height = 40, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { pic.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            header.Controls.Add(pic);
            header.Controls.Add(Ui.Title(L.S("Параметри", "Settings"), 70, 16, 420));
            header.Controls.Add(Ui.Caption(L.Name + " " + Ver.Number + L.S(" · локальні знімки екрана · без мережі", " · local screenshots · no network"), 72, 50, 460));
            header.Paint += delegate(object s, PaintEventArgs e)
            { using (var pen = new Pen(Theme.CardBorder)) e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
            Controls.Add(header);

            // ---- footer: лінія + кнопки ----
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.PageBg };
            footer.Paint += delegate(object s, PaintEventArgs e)
            { using (var pen = new Pen(Theme.CardBorder)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var ok = Ui.Btn(L.S("Зберегти", "Save"), FormW - CardX - 116, 13, 116, BtnKind.Primary);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { Collect(); };
            var cancel = Ui.Btn(L.S("Скасувати", "Cancel"), FormW - CardX - 116 - 110 - 10, 13, 110, BtnKind.Secondary);
            cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(ok); footer.Controls.Add(cancel);
            Controls.Add(footer);
            AcceptButton = ok; CancelButton = cancel;

            // ---- scrollable content ----
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PageBg, AutoScroll = true, Padding = new Padding(0, 4, 0, 8) };
            Controls.Add(content);
            content.BringToFront();

            int y = 6;

            // === Гарячі клавіші ===
            content.Controls.Add(Ui.Section(L.S("Гарячі клавіші", "Hotkeys"), CardX, y, CardW)); y += 26;
            var c1 = NewCard(content, y, L.S("Гарячі клавіші", "Hotkeys"));
            int cy = 10;
            int bxW = 104, bx2 = CardW - Pad - bxW, bx1 = bx2 - 10 - bxW;
            c1.Controls.Add(Ui.Caption(L.S("основна", "primary"), bx1, cy, bxW));
            c1.Controls.Add(Ui.Caption(L.S("запасна (для ПК)", "secondary (desktop)"), bx2, cy, bxW + 6));
            cy += 20;

            c1.Controls.Add(Ui.Body(L.S("Знімок області", "Region capture"), Pad, cy + 6, 240));
            _hkRegion = new HotkeyBox(_cfg.RegionVk, _cfg.RegionMods) { Left = bx1, Top = cy + 3, Width = bxW, AccessibleName = L.S("Знімок області — основна клавіша", "Region capture — primary hotkey") };
            _hkRegion2 = new HotkeyBox(_cfg.Region2Vk, _cfg.Region2Mods) { Left = bx2, Top = cy + 3, Width = bxW, AccessibleName = L.S("Знімок області — запасна клавіша", "Region capture — secondary hotkey") };
            c1.Controls.Add(_hkRegion); c1.Controls.Add(_hkRegion2);
            cy += 38; AddDivider(c1, cy); cy += 3;

            c1.Controls.Add(Ui.Body(L.S("Весь екран (у теку)", "Whole screen (to folder)"), Pad, cy + 6, 240));
            _hkFull = new HotkeyBox(_cfg.FullVk, _cfg.FullMods) { Left = bx1, Top = cy + 3, Width = bxW, AccessibleName = L.S("Весь екран — основна клавіша", "Whole screen — primary hotkey") };
            _hkFull2 = new HotkeyBox(_cfg.Full2Vk, _cfg.Full2Mods) { Left = bx2, Top = cy + 3, Width = bxW, AccessibleName = L.S("Весь екран — запасна клавіша", "Whole screen — secondary hotkey") };
            c1.Controls.Add(_hkFull); c1.Controls.Add(_hkFull2);
            cy += 40;
            c1.Controls.Add(Ui.Caption(L.S("Дві клавіші на дію (ноут + ПК). Клік у поле → нова, Del — прибрати.", "Two hotkeys per action (laptop + desktop). Click a field to set a new one, Del clears it."), Pad, cy, CardW - 2 * Pad));
            cy += 22;
            FinishCard(c1, cy);
            y += c1.Height + 18;

            // === Збереження ===
            content.Controls.Add(Ui.Section(L.S("Збереження", "Saving"), CardX, y, CardW)); y += 26;
            var c2 = NewCard(content, y, L.S("Збереження", "Saving"));
            cy = 12;
            c2.Controls.Add(Ui.Body(L.S("Тека скріншотів", "Screenshots folder"), Pad, cy, 240)); cy += 22;
            _tbDir = Ui.Input(Pad, cy, CardW - 2 * Pad - 100);
            _tbDir.AccessibleName = L.S("Тека скріншотів", "Screenshots folder");
            c2.Controls.Add(_tbDir);
            var bBrowse = Ui.Btn(L.S("Огляд…", "Browse…"), CardW - Pad - 92, cy - 1, 92, BtnKind.Secondary);
            bBrowse.AccessibleName = L.S("Огляд — обрати теку скріншотів", "Browse — choose the screenshots folder");
            bBrowse.Height = 26;
            bBrowse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = _cfg.EffectiveSaveDir;
                    if (dlg.ShowDialog(this) == DialogResult.OK) _tbDir.Text = dlg.SelectedPath;
                }
            };
            c2.Controls.Add(bBrowse);
            cy += 28;
            c2.Controls.Add(Ui.Caption(L.S("Порожньо = тека Screenshots поруч із застосунком", "Empty = a Screenshots folder next to the application"), Pad, cy, CardW - 2 * Pad));
            cy += 22; AddDivider(c2, cy); cy += 6;

            c2.Controls.Add(Ui.Body(L.S("Шаблон імені файла", "File name template"), Pad, cy, 240)); cy += 22;
            _tbTemplate = Ui.Input(Pad, cy, CardW - 2 * Pad);
            _tbTemplate.AccessibleName = L.S("Шаблон імені файла", "File name template");
            c2.Controls.Add(_tbTemplate);
            cy += 28;
            c2.Controls.Add(Ui.Caption(L.S("Мітки: {date}  {time}  {w}  {h}", "Tags: {date}  {time}  {w}  {h}"), Pad, cy, CardW - 2 * Pad));
            cy += 22; AddDivider(c2, cy); cy += 8;

            c2.Controls.Add(Ui.Body(L.S("Формат", "Format"), Pad, cy + 4, 80));
            _cbFormat = new ThemedCombo { Left = Pad + 84, Top = cy, Width = 96, AccessibleName = L.S("Формат файла", "File format") };
            _cbFormat.Items.Add("PNG"); _cbFormat.Items.Add("JPG");
            c2.Controls.Add(_cbFormat);
            c2.Controls.Add(Ui.Body(L.S("Якість JPG", "JPG quality"), Pad + 210, cy + 4, 90));
            _numQuality = new NumericUpDown { Left = Pad + 300, Top = cy + 1, Width = 64, Minimum = 10, Maximum = 100, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.ControlBg, ForeColor = Theme.TextPrimary, AccessibleName = L.S("Якість JPG", "JPG quality") };
            Ui.HookThemedBorder(_numQuality);   // та сама чорна системна рамка, що й у полів вводу
            Ui.HookThemedSpin(_numQuality);     // стрілки WinForms малює системними кольорами
            c2.Controls.Add(_numQuality);
            cy += 38; AddDivider(c2, cy); cy += 1;

            _tgCursor = ToggleRow(c2, ref cy, L.S("Вмальовувати курсор у знімок", "Draw the mouse cursor into the capture"), null);
            _tgCopy = ToggleRow(c2, ref cy, L.S("Після збереження копіювати в буфер", "Copy to the clipboard after saving"), null);
            _tgSound = ToggleRow(c2, ref cy, L.S("Звук затвора", "Shutter sound"), null);
            FinishCard(c2, cy);
            y += c2.Height + 18;

            // === Система ===
            content.Controls.Add(Ui.Section(L.S("Система", "System"), CardX, y, CardW)); y += 26;
            var c3 = NewCard(content, y, L.S("Система", "System"));
            cy = 8;
            _tgBalloon = ToggleRow(c3, ref cy, L.S("Сповіщення після знімка", "Notification after a capture"), null);
            _tgStartup = ToggleRow(c3, ref cy, L.S("Запускати разом із Windows", "Start with Windows"), null);
            AddDivider(c3, cy); cy += 10;

            c3.Controls.Add(Ui.Body(L.S("Тема інтерфейсу", "Interface theme"), Pad, cy + 4, 200));
            _cbTheme = new ThemedCombo { Left = CardW - Pad - 170, Top = cy, Width = 170, AccessibleName = L.S("Тема інтерфейсу", "Interface theme") };
            _cbTheme.Items.Add(L.S("Як у Windows", "As in Windows"));
            _cbTheme.Items.Add(L.S("Світла", "Light"));
            _cbTheme.Items.Add(L.S("Темна", "Dark"));
            // ЖИВИЙ ПЕРЕГЛЯД. Доти тема застосовувалась лише на «Зберегти», тобто побачити
            // вибір можна було лише погодившись із ним наосліп. Тепер вікно перетемлюється
            // одразу, а «Скасувати» повертає ту тему, з якою вікно відкривали.
            _cbTheme.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                Theme.Init(SelectedTheme());
            };
            c3.Controls.Add(_cbTheme);
            cy += 34;
            AddDivider(c3, cy); cy += 8;

            c3.Controls.Add(Ui.Body(L.S("Мова інтерфейсу", "Interface language"), Pad, cy + 4, 200));
            _cbLang = new ThemedCombo { Left = CardW - Pad - 170, Top = cy, Width = 170,
                                        AccessibleName = L.S("Мова інтерфейсу", "Interface language") };
            _cbLang.Items.Add(L.S("Як у Windows", "As in Windows"));
            _cbLang.Items.Add(L.NativeName("uk"));
            _cbLang.Items.Add(L.NativeName("en"));
            // Мова перемикається ЖИВЦЕМ, як і тема: інакше побачити вибір можна було б лише
            // погодившись із ним наосліп. «Скасувати» повертає ту мову, з якою вікно відкривали.
            _cbLang.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                // ★ N7 ПОВЕРТАВСЯ ЖИВИМ ПЕРЕМИКАЧЕМ. L.Init міняє Config.DefaultTemplate ПІД
                //   полем шаблону, а саме поле лишалося в старій мові. Collect() далі чесно
                //   порівнював старий дефолт із НОВИМ, бачив «не дорівнює» і записував
                //   незайманий дефолт як власний вибір користувача — шаблон застигав у мові
                //   моменту відкриття вікна назавжди. Тому дефолт знімаємо ДО перемикання, і
                //   якщо поле досі тримає саме його — пересаджуємо на дефолт нової мови, щоб
                //   гард «це ще дефолт» у Collect() знову бачив рівність.
                string wasDefault = Config.DefaultTemplate;
                L.Init(SelectedLang());
                if (_tbTemplate.Text.Trim() == wasDefault)
                {
                    _tbTemplate.Text = Config.DefaultTemplate;
                    _tbTemplate.SelectionStart = 0;   // як у LoadValues: перша літера видима
                }
            };
            c3.Controls.Add(_cbLang);
            cy += 34;
            // ★ ЖИВЕ ПЕРЕМИКАННЯ МОВИ НЕ МІНЯЛО НІЧОГО ВИДИМОГО: меню трея перебудовується,
            //   але воно закрите, а написи ЦЬОГО вікна збудовані один раз — користувач бачив
            //   нуль реакції й робив висновок «налаштування зламане». Повна перебудова текстів
            //   форми — окремий проєкт; чесний мінімум — сказати, чого чекати. Підпис стоїть
            //   ОБОМА мовами одразу (другий рядок — дзеркало першого), бо після перемикання
            //   він сам лишається в старій мові, якої користувач може не читати.
            c3.Controls.Add(Ui.Caption(L.S("Мова перемикається одразу; написи цього вікна — після повторного відкриття.",
                                           "The language switches at once; this window's texts — after it is reopened."), Pad, cy, CardW - 2 * Pad));
            c3.Controls.Add(Ui.Caption(L.S("The language switches at once; this window's texts — after it is reopened.",
                                           "Мова перемикається одразу; написи цього вікна — після повторного відкриття."), Pad, cy + 16, CardW - 2 * Pad));
            cy += 38;
            AddDivider(c3, cy); cy += 8;
            c3.Controls.Add(Ui.Caption(L.S("Якщо PrtScr відкриває Snipping Tool замість ВАЛЄРА Скріншот:", "If PrtScr opens Snipping Tool instead of VALERA Screenshot:"), Pad, cy + 8, 330));
            var bFree = Ui.Btn(L.S("Звільнити PrtScr", "Free up PrtScr"), CardW - Pad - 170, cy + 2, 170, BtnKind.Secondary);
            bFree.AccessibleDescription = L.S("Вимикає перехоплення PrtScr програмою «Ножиці» у Windows 11", "Turns off the Snipping Tool grabbing PrtScr on Windows 11");
            bFree.Click += delegate { FreePrtScr(); };
            c3.Controls.Add(bFree);
            cy += 40;
            FinishCard(c3, cy);
            y += c3.Height + 8;
        }

        // ---- builders (картки + рядки з роздільниками) ----
        private Card NewCard(Panel parent, int y, string title)
        {
            var c = new Card { Left = CardX, Top = y, Width = CardW, AccessibleName = title };
            parent.Controls.Add(c);
            return c;
        }

        private static void FinishCard(Card c, int contentBottom) { c.Height = contentBottom + 8; }

        private ToggleSwitch ToggleRow(Card c, ref int cy, string title, string caption)
        {
            if (cy > 10) { AddDivider(c, cy); cy += 1; }
            bool hasCap = caption != null;
            int rowH = hasCap ? 54 : 44;
            c.Controls.Add(Ui.Body(title, Pad, cy + (hasCap ? 9 : 13), CardW - 90));
            if (hasCap) c.Controls.Add(Ui.Caption(caption, Pad, cy + 30, CardW - 90));
            // Імʼя тумблера — це підпис РЯДКА: сам контрол тексту не має, тож без цього
            // Екранний диктор оголошував би просто «прапорець», без жодної підказки, який саме.
            var tog = new ToggleSwitch { BackColor = Theme.CardBg, AccessibleName = title };
            tog.Left = CardW - Pad - tog.Width;
            tog.Top = cy + (rowH - tog.Height) / 2;
            c.Controls.Add(tog);
            cy += rowH;
            return tog;
        }

        private static void AddDivider(Card c, int y)
        {
            c.Controls.Add(new Panel { Left = Pad, Top = y, Width = CardW - 2 * Pad, Height = 1, BackColor = Theme.Divider });
        }

        private void FreePrtScr()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard"))
                    key.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
                Ui.Msg(this,
                    L.S("Готово: PrtScr більше не відкриватиме Snipping Tool.\n", "Done: PrtScr will no longer open Snipping Tool.\n") +
                    L.S("Натисніть «Зберегти» — ВАЛЄРА Скріншот перереєструє клавіші.", "Press 'Save' — VALERA Screenshot will re-register the hotkeys."),
                    "ValeraScreenshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Ui.Msg(this, L.S("Не вдалося: ", "Failed: ") + ex.Message, L.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string SelectedTheme()
        {
            return _cbTheme.SelectedIndex == 1 ? "light" : _cbTheme.SelectedIndex == 2 ? "dark" : "auto";
        }

        private string SelectedLang()
        {
            return _cbLang.SelectedIndex == 1 ? "uk" : _cbLang.SelectedIndex == 2 ? "en" : "auto";
        }

        // «Скасувати» мусить скасовувати ВСЕ, включно з живим переглядом теми. Інакше вікно
        // закривається, налаштування не збережені, а застосунок лишається в чужій темі —
        // тобто кнопка бреше.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (DialogResult != DialogResult.OK)
            {
                if (Theme.Mode != Theme.ParseMode(_themeOnOpen)) Theme.Init(_themeOnOpen);
                if (L.Cur != L.Decide(_langOnOpen, L.SystemIsUkrainian())) L.Init(_langOnOpen);
            }
            base.OnFormClosed(e);
        }

        private void LoadValues()
        {
            _loading = true;
            _tbDir.Text = _cfg.SaveDir;
            _tbTemplate.Text = _cfg.Template;
            _cbFormat.SelectedIndex = _cfg.Format == "jpg" ? 1 : 0;
            _numQuality.Value = _cfg.JpegQuality;
            _tgCursor.Checked = _cfg.IncludeCursor;
            _tgCopy.Checked = _cfg.CopyAfterSave;
            _tgSound.Checked = _cfg.PlaySound;
            _tgBalloon.Checked = _cfg.ShowBalloon;
            _tgStartup.Checked = _cfg.StartWithWindows;
            _cbTheme.SelectedIndex = _cfg.UiTheme == "light" ? 1 : _cfg.UiTheme == "dark" ? 2 : 0;
            _cbLang.SelectedIndex = _cfg.UiLang == "uk" ? 1 : _cfg.UiLang == "en" ? 2 : 0;
            // курсор у полях — на початок (щоб довгий шлях не ховав першу літеру)
            _tbDir.SelectionStart = 0; _tbTemplate.SelectionStart = 0;
            _loading = false;
        }

        private void Collect()
        {
            _cfg.RegionVk = _hkRegion.Vk; _cfg.RegionMods = _hkRegion.Mods;
            _cfg.FullVk = _hkFull.Vk; _cfg.FullMods = _hkFull.Mods;
            _cfg.Region2Vk = _hkRegion2.Vk; _cfg.Region2Mods = _hkRegion2.Mods;
            _cfg.Full2Vk = _hkFull2.Vk; _cfg.Full2Mods = _hkFull2.Mods;
            _cfg.SaveDir = _tbDir.Text.Trim();
            // Якщо в полі стоїть РІВНО чинний дефолт — не записуємо його як власний вибір
            // користувача. Інакше достатньо було б відкрити Параметри й натиснути «Зберегти»,
            // щоб шаблон назавжди застряг у мові того моменту (див. N7 і коментар у Config.cs).
            string tpl = _tbTemplate.Text.Trim();
            _cfg.Template = (tpl.Length == 0 || tpl == Config.DefaultTemplate) ? "" : tpl;
            _cfg.Format = _cbFormat.SelectedIndex == 1 ? "jpg" : "png";
            _cfg.JpegQuality = (int)_numQuality.Value;
            _cfg.IncludeCursor = _tgCursor.Checked;
            _cfg.CopyAfterSave = _tgCopy.Checked;
            _cfg.PlaySound = _tgSound.Checked;
            _cfg.ShowBalloon = _tgBalloon.Checked;
            _cfg.StartWithWindows = _tgStartup.Checked;
            _cfg.UiTheme = SelectedTheme();
            _cfg.UiLang = SelectedLang();
            Theme.Init(_cfg.UiTheme);
            L.Init(_cfg.UiLang);   // застосувати одразу, щоб наступні вікна вже були в новій темі
        }
    }
}
