using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ValeraScreenshot
{
    // «Про програму»: портрет АВТОРА зліва, назва, опис, можливості, автор + контакт.
    // Це особистий бренд автора — жодної організації тут немає й не було.
    internal class AboutForm : ThemedForm
    {
        public AboutForm()
        {
            Text = L.S("Про програму", "About");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.PageBg;
            Font = Theme.Body;
            ClientSize = new Size(512, 438);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int x = 26;

            // --- портрет автора зліва (3:4; тло під тему, щоб краї не «вирізались») ---
            var pic = new PictureBox { Left = x, Top = 24, Width = 120, Height = 160, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.PageBg, BorderStyle = BorderStyle.None };
            pic.Image = LoadPhoto();
            Controls.Add(pic);

            int tx = 158;
            Controls.Add(new Label { Text = L.Name, Left = tx, Top = 30, Width = 300, Height = 32, Font = Theme.Title, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent });
            Controls.Add(Ui.Caption(L.S("Локальні знімки екрана в нативній роздільності", "Local screenshots at native resolution"), tx, 64, 320));
            Controls.Add(Ui.Caption(L.S("версія ", "version ") + Ver.Display, tx, 82, 320));
            // Чесно про мережу: єдиний вихідний трафік — перевірка оновлень, і лише за натисканням.
            Controls.Add(new Label { Text = L.S("БЕЗ телеметрії. БЕЗ хмари.\nУсе локально; мережа — лише перевірка оновлень.", "NO telemetry. NO cloud.\nAll local; network only checks for updates."), Left = tx, Top = 108, Width = 330, Height = 40, Font = Theme.Body, ForeColor = Theme.TextSecondary, BackColor = Color.Transparent });

            Controls.Add(Sep(x, 198, 512 - 2 * x));

            // ОДИН L.S на весь блок, без вкладених. Мій механічний прохід локалізації обгорнув
            // кожен рядок ОКРЕМО — просто тому, що сканер не бачив зовнішнього виклику, чиї
            // аргументи склеєні з кількох рядків. Вкладені L.S усередині L.S працювали, але
            // читались як помилка й запрошували на наступну.
            string features = L.S(
                "•  Знімок області / усього екрана — 1:1 фізичні пікселі (PerMonitorV2)\n" +
                "•  13 інструментів: стрілка, текст, мозаїка, кроки 1-2-3, маршрут по карті…\n" +
                "•  Поділитися: WhatsApp / Telegram / Viber / Signal / пошта\n" +
                "•  Дві клавіші на дію (ноут + ПК), налаштовні",
                "•  Region or whole-screen capture — 1:1 physical pixels (PerMonitorV2)\n" +
                "•  13 tools: arrow, text, mosaic, numbered steps, route over a map…\n" +
                "•  Share to: WhatsApp / Telegram / Viber / Signal / email\n" +
                "•  Two hotkeys per action (laptop + desktop), both configurable");
            Controls.Add(new Label { Text = features, Left = x, Top = 208, Width = 512 - 2 * x, Height = 84, Font = Theme.Body, ForeColor = Theme.TextPrimary, BackColor = Color.Transparent });

            Controls.Add(Sep(x, 300, 512 - 2 * x));

            // --- автор + контакт ---
            Controls.Add(new Label { Text = L.S("Автор:  Павло Ісаєв", "Author:  Pavlo Isaiev"), Left = x, Top = 312, Width = 430, Height = 20, Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Regular, GraphicsUnit.Point), ForeColor = Theme.TextPrimary, BackColor = Color.Transparent });
            string cContact = L.S("Контакт:  ", "Contact:  ");
            // ForeColor темить НЕ-лінкову частину («Контакт:») — без цього LinkLabel малює її
            // системним чорним, невидимим у темній темі. VisitedLinkColor так само пінимо в акцент.
            var blog = new LinkLabel { Text = cContact + "caussa.blog", Left = x, Top = 334, Width = 430, Height = 20, Font = Theme.Body, BackColor = Color.Transparent, ForeColor = Theme.TextPrimary, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentPressed, VisitedLinkColor = Theme.Accent };
            blog.LinkArea = new LinkArea(cContact.Length, "caussa.blog".Length);
            blog.LinkClicked += delegate { OpenUrl("https://caussa.blog"); };
            Controls.Add(blog);
            Controls.Add(Ui.Caption("© 2026 · " + L.S("Павло Ісаєв", "Pavlo Isaiev"), x, 358, 430));

            // --- кнопки ---
            int by = 388;
            var close = Ui.Btn(L.S("Закрити", "Close"), ClientSize.Width - x - 110, by, 110, BtnKind.Primary);
            close.DialogResult = DialogResult.OK;
            var folder = Ui.Btn(L.S("Тека скріншотів", "Screenshots folder"), ClientSize.Width - x - 110 - 170 - 10, by, 170, BtnKind.Secondary);
            folder.Click += delegate
            {
                string d = "";
                try
                {
                    d = Config.Load().EffectiveSaveDir;
                    Directory.CreateDirectory(d);
                    Process.Start("explorer.exe", "\"" + d + "\"");
                }
                catch (Exception ex)   // друга мовчазна кнопка-пустушка тієї ж природи
                {
                    Ui.Msg(this, L.S("Не вдалося відкрити теку знімків:\n", "Could not open the screenshots folder:\n") + d + "\n\n" + ex.Message,
                        L.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(close); Controls.Add(folder);
            AcceptButton = close;
            CancelButton = close;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static Panel Sep(int x, int y, int w)
        {
            return new Panel { Left = x, Top = y, Width = w, Height = 1, BackColor = Theme.CardBorder };
        }

        private static Image LoadPhoto()
        {
            // Темна тема бере обрамлений портрет (не розчиняється в темному тлі); світла — версію
            // з розмитими краями, що зливається зі світлим вікном. Фолбек — світлий портрет.
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var s = Theme.IsDark ? asm.GetManifestResourceStream("authorphoto_dark") : null;
                if (s == null) s = asm.GetManifestResourceStream("authorphoto");
                if (s == null) return null;
                using (s)
                {
                    var ms = new MemoryStream();
                    s.CopyTo(ms); ms.Position = 0;
                    using (var tmp = Image.FromStream(ms)) return new Bitmap(tmp);
                }
            }
            catch { return null; }
        }
    }
}
