using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ValeraScreenshot
{
    // Діалог «Поділитися»: показує ЗНАЙДЕНІ на машині месенджери/пошту.
    // Файл збережено завжди; чи ліг знімок ще й у буфер — каже clipboardOk,
    // і підпис діалогу мусить це віддзеркалювати (кнопка лише відкриває застосунок).
    internal class ShareForm : ThemedForm
    {
        private readonly string _filePath;

        // Чи справді відкрили якийсь канал. Оверлей ставив Result = Shared безумовно, тож трей
        // рапортував про месенджер навіть коли діалог закрили хрестиком.
        public bool LaunchedTarget;

        public ShareForm(string filePath, bool clipboardOk = true)
        {
            _filePath = filePath;

            Text = L.S("Поділитися знімком", "Share screenshot");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.PageBg;
            Font = Theme.Body;
            TopMost = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var targets = ShareUtil.Detect();

            int w = 420, pad = 20;
            Controls.Add(Ui.Title(L.S("Поділитися", "Share"), pad, 14, w - pad));
            // «Знімок уже в буфері» стверджувалося БЕЗУМОВНО: оверлей знав результат CopyImage
            // (ClipboardOk), але сюди його не передавав, і при зайнятому буфері (RDP, Teams,
            // менеджер буфера) діалог сам радив Ctrl+V — користувач ішов у чат і вставляв
            // ПОПЕРЕДНІЙ вміст. П'ята поверхня «брехні про успіх». Файл на диску справжній,
            // тож у відмові чесно радимо надіслати САМЕ ФАЙЛ.
            Controls.Add(Ui.Caption(clipboardOk
                ? L.S("Знімок уже в буфері обміну — у чаті достатньо Ctrl+V.", "The screenshot is already on the clipboard — Ctrl+V in the chat is enough.")
                : L.S("Буфер обміну зайнятий — Ctrl+V вставить не той знімок. Надішліть файл.", "The clipboard is busy — Ctrl+V would paste the wrong image. Send the file."),
                pad, 50, w - pad));
            Controls.Add(Ui.Caption(L.S("Файл: ", "File: ") + Path.GetFileName(_filePath), pad, 68, w - pad));

            int y = 96;
            var card = new Card { Left = pad, Top = y, Width = w - pad * 2 + 8, Height = 10 };
            Controls.Add(card);

            int by = 14;
            if (targets.Count == 0)
            {
                card.Controls.Add(Ui.Body(L.S("Месенджерів не знайдено.", "No messengers found."), 16, by, card.Width - 32));
                card.Controls.Add(Ui.Caption(L.S("Встановіть WhatsApp / Telegram / Viber / Signal або поштовий клієнт.", "Install WhatsApp / Telegram / Viber / Signal or an email client."), 16, by + 24, card.Width - 32));
                by += 52;
            }
            else
            {
                foreach (var t in targets)
                {
                    var target = t; // замикання C#5
                    var b = new OfficeButton
                    {
                        Text = target.Name,
                        Left = 14, Top = by, Width = card.Width - 28, Height = 40,
                        Kind = BtnKind.Secondary,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Glyph = GlyphColor(target.Key)
                    };
                    // Тут стояло `try { target.Launch(...); } catch { }` — а сам Launch ковтав
                    // помилку вдруге (ShareUtil.Open). Клік по «Telegram» просто закривав діалог,
                    // хоч би застосунок був видалений або заблокований політикою. Дві порожні
                    // пастки поспіль на одній дії користувача.
                    b.Click += delegate
                    {
                        bool ok;
                        try { ok = target.Launch(_filePath); }
                        catch (Exception ex) { ok = false; Diag.Log("share " + target.Key + ": " + ex.Message); }
                        if (!ok)
                        {
                            Ui.Msg(this, L.S("Не вдалося відкрити «", "Could not open '") + target.Name + "».\n\n" +
                                L.S("Знімок збережено у файл:\n", "The screenshot was saved to a file:\n") + _filePath,
                                L.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;   // діалог лишається відкритим — можна обрати інший канал
                        }
                        LaunchedTarget = true;
                        Close();
                    };
                    card.Controls.Add(b);
                    card.Controls.Add(Ui.Caption(target.Hint, 18, by + 40, card.Width - 36));
                    by += 62;
                }
            }
            card.Height = by + 8;

            int fy = card.Bottom + 14;
            var folder = Ui.Btn(L.S("Відкрити теку файла", "Open the file's folder"), pad, fy, 180, BtnKind.Secondary);
            folder.Click += delegate
            {
                // Мовчазна кнопка-пустушка: помилка Explorer'а ковталась, і клік не робив нічого.
                try { Process.Start("explorer.exe", "/select,\"" + _filePath + "\""); }
                catch (Exception ex)
                {
                    Ui.Msg(this, L.S("Не вдалося відкрити теку: ", "Could not open the folder: ") + ex.Message, L.Name,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(folder);

            var close = Ui.Btn(L.S("Закрити", "Close"), w - pad - 110 + 8, fy, 110, BtnKind.Primary);
            close.DialogResult = DialogResult.OK;
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;

            ClientSize = new Size(w + 8, fy + 48);
        }

        // Фірмовий колір позначки цілі.
        private static Color GlyphColor(string key)
        {
            switch (key)
            {
                case "whatsapp": return Color.FromArgb(0x25, 0xD3, 0x66);
                case "telegram": return Color.FromArgb(0x2A, 0xA3, 0xEF);
                case "viber": return Color.FromArgb(0x7C, 0x52, 0x9E);
                case "signal": return Color.FromArgb(0x3A, 0x76, 0xF0);
                default: return Theme.Accent;
            }
        }
    }
}
