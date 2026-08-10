using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ValeraScreenshot
{
    // Ціль «Поділитися»: знайдений на машині месенджер/клієнт і спосіб його відкрити.
    internal class ShareTarget
    {
        public string Key;      // ключ глифа
        public string Name;     // підпис кнопки
        public string Hint;     // пояснення під кнопкою
        public Func<string, bool> Launch; // приймає шлях до файла; повертає УСПІХ запуску
    }

    internal static class ShareUtil
    {
        // Детект встановленого: протокол у HKCR або відомий шлях exe.
        private static bool HasProtocol(string scheme)
        {
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey(scheme))
                    return k != null && k.GetValue("URL Protocol") != null;
            }
            catch { return false; }
        }

        private static string FindExe(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        // Повертає УСПІХ. Був void із порожнім catch — а вище по стеку ShareForm ковтав удруге,
        // тож клік по месенджеру просто закривав діалог, і користувач не дізнавався нічого.
        private static bool Open(string what)
        {
            try { Process.Start(what); return true; }
            catch (Exception ex) { Diag.Log("share open " + what + ": " + ex.Message); return false; }
        }

        // Список знайдених цілей. Зображення вже в буфері обміну —
        // у месенджері достатньо Ctrl+V, тому Launch лише відкриває застосунок.
        public static List<ShareTarget> Detect()
        {
            var list = new List<ShareTarget>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (HasProtocol("whatsapp"))
                list.Add(new ShareTarget
                {
                    Key = "whatsapp", Name = "WhatsApp",
                    Hint = L.S("відкриється чат — натисніть Ctrl+V", "the chat opens — press Ctrl+V"),
                    Launch = delegate(string f) { return Open("whatsapp://"); }
                });

            string tg = FindExe(Path.Combine(roaming, @"Telegram Desktop\Telegram.exe"));
            if (tg != null || HasProtocol("tg"))
                list.Add(new ShareTarget
                {
                    Key = "telegram", Name = "Telegram",
                    Hint = L.S("відкриється застосунок — Ctrl+V у чат", "the app opens — press Ctrl+V in the chat"),
                    Launch = delegate(string f) { return tg != null ? Open(tg) : Open("tg://"); }
                });

            string viber = FindExe(Path.Combine(local, @"Viber\Viber.exe"));
            if (viber != null || HasProtocol("viber"))
                list.Add(new ShareTarget
                {
                    Key = "viber", Name = "Viber",
                    Hint = L.S("відкриється застосунок — Ctrl+V у чат", "the app opens — press Ctrl+V in the chat"),
                    Launch = delegate(string f) { return viber != null ? Open(viber) : Open("viber://"); }
                });

            string signal = FindExe(Path.Combine(local, @"Programs\signal-desktop\Signal.exe"));
            if (signal != null)
                list.Add(new ShareTarget
                {
                    Key = "signal", Name = "Signal",
                    Hint = L.S("відкриється застосунок — Ctrl+V у чат", "the app opens — press Ctrl+V in the chat"),
                    Launch = delegate(string f) { return Open(signal); }
                });

            if (HasMapiClient())
                list.Add(new ShareTarget
                {
                    Key = "mail", Name = L.S("Електронна пошта", "Email"),
                    Hint = L.S("новий лист із файлом у вкладенні", "a new message with the file attached"),
                    Launch = SendByMapi
                });

            return list;
        }

        private static bool HasMapiClient()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Clients\Mail"))
                {
                    if (k == null) return false;
                    var v = k.GetValue("") as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        // ---- Simple MAPI: лист із вкладенням через типовий поштовий клієнт ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class MapiMessage
        {
            public int Reserved;
            public string Subject;
            public string NoteText;
            public string MessageType;
            public string DateReceived;
            public string ConversationID;
            public int Flags;
            public IntPtr Originator;
            public int RecipCount;
            public IntPtr Recips;
            public int FileCount;
            public IntPtr Files;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class MapiFileDesc
        {
            public int Reserved;
            public int Flags;
            public int Position = -1;
            public string PathName;
            public string FileName;
            public IntPtr FileType;
        }

        [DllImport("MAPI32.DLL", CharSet = CharSet.Ansi)]
        private static extern int MAPISendMail(IntPtr session, IntPtr hwnd, MapiMessage message, int flags, int reserved);

        private const int MAPI_LOGON_UI = 1;
        private const int MAPI_DIALOG = 8;

        // Повертає, чи ВДАЛОСЯ віддати листа поштовому клієнту.
        //
        // Було: void; код повернення MAPISendMail просто відкидався, усе тіло — в порожньому catch,
        // ще й на фоновому потоці, де виняток убив би процес мовчки. Плюс збірка /platform:anycpu
        // без app.config означає 64-бітний процес, а Simple MAPI до 32-бітного Outlook/Thunderbird
        // із нього не дістає — тобто кнопка «Електронна пошта» могла бути непрацездатною в
        // принципі, і користувач бачив би рівно нічого.
        public static bool SendByMapi(string filePath)
        {
            int rc = -1;
            Exception failure = null;
            // MAPI_DIALOG блокує — виносимо в окремий STA-потік, щоб не заморозити UI.
            var t = new Thread(delegate()
            {
                IntPtr fileBuf = IntPtr.Zero;
                try
                {
                    var fd = new MapiFileDesc
                    {
                        PathName = filePath,
                        FileName = Path.GetFileName(filePath)
                    };
                    int size = Marshal.SizeOf(typeof(MapiFileDesc));
                    fileBuf = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(fd, fileBuf, false);

                    var msg = new MapiMessage
                    {
                        Subject = Path.GetFileNameWithoutExtension(filePath),
                        NoteText = "",
                        FileCount = 1,
                        Files = fileBuf
                    };
                    rc = MAPISendMail(IntPtr.Zero, IntPtr.Zero, msg, MAPI_LOGON_UI | MAPI_DIALOG, 0);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    if (fileBuf != IntPtr.Zero)
                    {
                        Marshal.DestroyStructure(fileBuf, typeof(MapiFileDesc));
                        Marshal.FreeHGlobal(fileBuf);
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();

            // Чекаємо рівно стільки, щоб зловити НЕГАЙНУ відмову (немає MAPI, не та розрядність).
            // Якщо клієнт відкрив вікно листа, потік лишається жити — це успіх, а не зависання.
            if (!t.Join(2500)) return true;
            if (failure != null) { Diag.Log("MAPI: " + failure.Message); return false; }
            // 0 = SUCCESS_SUCCESS, 1 = USER_ABORT (користувач сам закрив лист — теж не помилка)
            if (rc != 0 && rc != 1) { Diag.Log("MAPI rc=" + rc); return false; }
            return true;
        }
    }
}
