using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ValeraScreenshot
{
    // Manual, opt-in updater. Fetches a small text manifest over HTTPS, and applies an update ONLY
    // if the downloaded .exe carries a VALID Authenticode signature from OUR publisher certificate.
    // That signature gate means a tampered or foreign file is rejected even if the download location
    // (e.g. a shared Google Drive folder) is compromised — the private key never leaves the dev box.
    internal static class Updater
    {
        // Pinned publisher certificate thumbprint (see sign.ps1 / ValeraScreenshotCodeSign.cer).
        private const string TrustedThumbprint = "A30B626AF77DD5FC2FD04C11DE1D5ADAA56E8FBE";

        // Built-in manifest URL (GitHub Releases "latest" alias — never changes across versions).
        // Overridable per install via %APPDATA%\ValeraScreenshot\update_url.txt (first non-comment line).
        private const string DefaultManifestUrl = "https://github.com/nsrosbr/valera-screenshot/releases/latest/download/latest.txt";

        internal static string Title { get { return L.Name + L.S(" — оновлення", " — update"); } }

        // Тест-шви для двох КОРОННИХ інваріантів. Вони були private const — тобто найдорожчі
        // значення проєкту (пін серта, адреса каналу) неможливо було перевірити тестом, і аудит
        // 2026-07-20 показав, що жодного тесту на них не існує. Ці два геттери нічого не змінюють
        // у поведінці; вони лише роблять інваріант ВИМІРНИМ.
        internal static string PinnedThumbprintForTest { get { return TrustedThumbprint; } }
        internal static string ManifestUrlForTest { get { return DefaultManifestUrl; } }

        public static string ManifestUrl()
        {
            try
            {
                string f = Path.Combine(Config.Dir, "update_url.txt");
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var t = line.Trim();
                        if (t.Length > 0 && !t.StartsWith("#")) return t;
                    }
            }
            catch { }
            return DefaultManifestUrl;
        }

        // Startup check (Config.CheckUpdatesOnStart). SILENT by contract: it fetches the small text
        // manifest and, ONLY if a strictly newer version exists, calls onNewer(version, notes) so the
        // tray can show a balloon. No dialogs, no error popups — a missing network, a captive portal or
        // a broken manifest must never interrupt a user who just logged into Windows. Nothing is
        // downloaded and nothing is applied here: the actual update still runs through the full
        // interactive path (signature + pinned thumbprint + anti-rollback + one UAC prompt) when the
        // user clicks. That keeps the single network touch to one HTTPS GET of a few hundred bytes.
        public static void CheckOnStart(Action<string, string> onNewer)
        {
            var th = new Thread(delegate ()
            {
                try
                {
                    string url = ManifestUrl();
                    if (string.IsNullOrEmpty(url)) return;
                    EnableTls();
                    string manifest = HttpGetString(url, 12000);
                    string verStr, exeUrl, sha, notes;
                    ParseManifest(manifest, out verStr, out exeUrl, out sha, out notes);
                    if (string.IsNullOrEmpty(verStr) || string.IsNullOrEmpty(exeUrl)) return;

                    Version cur, nw;
                    if (!Version.TryParse(Ver.Number, out cur)) return;
                    if (!Version.TryParse(verStr, out nw)) return;
                    if (nw <= cur) { Diag.Log("оновлення при старті: у вас найновіша (" + Ver.Number + ")"); return; }

                    if (!string.IsNullOrEmpty(notes)) notes = notes.Replace("\\n", "\r\n");
                    Diag.Log("оновлення при старті: доступна " + verStr);
                    if (onNewer != null) onNewer(verStr, notes);
                }
                catch (Exception ex) { Diag.Log("оновлення при старті: не вдалося перевірити (" + ex.Message + ")"); }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        // Called from the tray. Does all network work on a background thread so the UI never blocks.
        public static void CheckInteractive()
        {
            string url = ManifestUrl();
            if (string.IsNullOrEmpty(url))
            {
                Ui.Msg(
                    L.S("Адресу оновлень ще не налаштовано.\r\n\r\n" +
                        "1) Трей -> «Відкрити теку налаштувань».\r\n" +
                        "2) Створіть файл  update_url.txt  і впишіть у ньому ПРЯМЕ посилання на маніфест\r\n" +
                        "   latest.txt (напр. з Google Drive: https://drive.google.com/uc?export=download&id=...).\r\n" +
                        "3) Знову натисніть «Перевірити оновлення…».",
                        "The update address is not configured yet.\r\n\r\n" +
                        "1) Tray -> “Open settings folder”.\r\n" +
                        "2) Create a file  update_url.txt  and put a DIRECT link to the manifest\r\n" +
                        "   latest.txt in it (e.g. from Google Drive: https://drive.google.com/uc?export=download&id=...).\r\n" +
                        "3) Press “Check for updates…” again."),
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var th = new Thread(delegate ()
            {
                try { DoCheck(url); }
                catch (Exception ex) { Ui.Msg(L.S("Не вдалося перевірити оновлення:\r\n", "Could not check for updates:\r\n") + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        private static void DoCheck(string url)
        {
            EnableTls();
            string manifest = HttpGetString(url, 12000);
            string verStr, exeUrl, sha, notes;
            ParseManifest(manifest, out verStr, out exeUrl, out sha, out notes);
            if (!string.IsNullOrEmpty(notes)) notes = notes.Replace("\\n", "\r\n"); // manifest encodes newlines as \n
            if (string.IsNullOrEmpty(verStr) || string.IsNullOrEmpty(exeUrl))
            { Ui.Msg(L.S("Маніфест оновлення порожній або пошкоджений.", "The update manifest is empty or corrupted."), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            Version cur, nw;
            if (!Version.TryParse(Ver.Number, out cur)) cur = new Version(0, 0);
            if (!Version.TryParse(verStr, out nw))
            { Ui.Msg(L.S("Невірний номер версії у маніфесті: ", "Invalid version number in the manifest: ") + verStr, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            if (nw <= cur)
            { Ui.Msg(L.S("У вас найновіша версія (", "You have the latest version (") + Ver.Number + ").", Title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            if (Ui.Msg(
                    L.S("Доступна нова версія ", "A new version is available: ") + verStr + L.S(" (у вас ", " (you have ") + Ver.Number + ").\r\n\r\n" +
                    (string.IsNullOrEmpty(notes) ? "" : (notes + "\r\n\r\n")) +
                    L.S("Завантажити й оновити зараз?", "Download and update now?"),
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string tmp = Path.Combine(Path.GetTempPath(), "ValeraScreenshot_update_" + Guid.NewGuid().ToString("N") + ".exe");
            HttpGetFile(exeUrl, tmp, 60000);

            // Integrity: optional SHA-256, then the REQUIRED signature/thumbprint gate.
            if (!string.IsNullOrEmpty(sha) && !Sha256Equals(tmp, sha))
            { SafeDelete(tmp); Ui.Msg(L.S("Контрольна сума не збіглася — файл відхилено.", "Checksum mismatch — file rejected."), Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (!VerifySignedByUs(tmp))
            { SafeDelete(tmp); Ui.Msg(L.S("Підпис файлу невірний або відсутній — оновлення ВІДХИЛЕНО.\r\n(Захист від підміни: приймаються лише файли, підписані оригінальним сертифікатом видавця.)", "The file signature is invalid or missing — the update is REJECTED.\r\n(Tamper protection: only files signed with the original publisher certificate are accepted.)"), Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            // Anti-rollback (H-2): bind the manifest's version CLAIM to the actual binary and require it to
            // be strictly newer than what we run. A pinned-cert signature only proves "signed by us", not
            // "newer" — without this, a compromised mirror could replay a genuine OLD signed (vulnerable)
            // build and pass every gate. We compare Major.Minor.Build (the manifest is 3-part; the binary
            // FileVersion is 4-part like 2.9.14.0), ignoring the revision.
            Verdict v = Judge(ReadFileVersion(tmp), nw, cur);
            if (v == Verdict.VersionMismatch)
            { SafeDelete(tmp); Ui.Msg(L.S("Версія завантаженого файлу не збігається з маніфестом — оновлення відхилено.", "The downloaded file's version does not match the manifest — update rejected."), Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (v == Verdict.NotNewer)
            { SafeDelete(tmp); Ui.Msg(L.S("Завантажений файл не новіший за поточну версію — відхилено (захист від відкату).", "The downloaded file is not newer than the current version — rejected (rollback protection)."), Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            string target = Application.ExecutablePath;
            if (Ui.Msg(L.S("Оновлення перевірено й підпис коректний.\r\nЗастосунок перезапуститься на версію ", "The update is verified and the signature is correct.\r\nThe app will restart to version ") + verStr + L.S(". Продовжити?", ". Continue?"),
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            { SafeDelete(tmp); return; }

            ApplyAndRestart(tmp, target);
        }

        // ---------- anti-rollback decision (H-2), as ONE testable function ----------
        // Раніше ці два порівняння жили просто в DoCheck, а тести перевіряли ВЛАСНУ копію тієї самої
        // арифметики через Norm3. Мутаційний прогін 2026-07-20 це викрив: обидві умови в DoCheck
        // можна було вимкнути (`if (false)`), і гейт лишався зеленим — тести доводили свою копію,
        // а не бойовий шлях. Тепер рішення одне, і воно ж перевіряється.
        internal enum Verdict { Accept, VersionMismatch, NotNewer }

        internal static Verdict Judge(Version bin, Version manifest, Version current)
        {
            // Маніфест каже «версія X» — бінарник мусить БУТИ версією X. Інакше маніфест і файл
            // розходяться, і підпис нічого не доводить про те, що саме нам підсунули.
            if (bin == null || manifest == null || Norm3(bin) != Norm3(manifest)) return Verdict.VersionMismatch;
            // Строго новіше. Рівне НЕ приймаємо: інакше зламане дзеркало могло б нескінченно
            // «оновлювати» на ту саму збірку, а старіше — це вже класична атака відкату.
            if (current != null && Norm3(bin) <= Norm3(current)) return Verdict.NotNewer;
            return Verdict.Accept;
        }

        // ★ САМЕ ЦЕ ПОРІВНЯННЯ і є пін — найдорожчий рядок продукту: воно вирішує, чий код нам
        // дозволено виконати на чужих машинах. Раніше воно жило всередині VerifySignedByUs, і
        // мутаційний прогін 2026-07-20 двічі показав, що його можна замінити на `return true`, а
        // гейт лишиться зеленим. Тестом через файл цю гілку взагалі не дістати надійно: системні
        // бінарники Windows підписані КАТАЛОГОМ, тож WinVerifyTrust відсіює їх раніше, і до
        // порівняння виконання не доходить. Тому рішення винесене сюди — чисте й перевіряєме.
        internal static bool IsOurCert(string certHashHex)
        {
            return string.Equals(certHashHex, TrustedThumbprint, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- integrity ----------
        // Verify the file has a VALID Authenticode signature AND it was signed by our pinned cert.
        internal static bool VerifySignedByUs(string path)
        {
            try
            {
                uint res = WinVerifyTrustFile(path);
                // 0 = fully trusted; 0x800B0109 = signature intact but root not trusted (self-signed) — OK.
                if (res != 0 && res != 0x800B0109) return false;
                var cert = X509Certificate.CreateFromSignedFile(path); // throws if unsigned
                return IsOurCert(cert.GetCertHashString());
            }
            catch { return false; }
        }

        private static bool Sha256Equals(string path, string expectedHex)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return string.Equals(sb.ToString(), expectedHex.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        // ---------- apply (swap the running exe, then restart) ----------
        // The verified payload is applied by a SECOND instance of THIS already-trusted exe (launched via
        // the "apply-update" command), NOT by an external batch. That second instance RE-VERIFIES the
        // staged file at the moment of the copy, under a deny-write lock — closing the download-staging
        // TOCTOU: a same-user process can no longer swap the temp file after our first check to get
        // unsigned code installed (and, under elevation, into a protected folder with admin rights).
        private static void ApplyAndRestart(string tmp, string target)
        {
            bool needElevation = !CanWriteDir(Path.GetDirectoryName(target)); // e.g. installed under Program Files
            int pid = Process.GetCurrentProcess().Id;
            string self = Application.ExecutablePath; // == target: our trusted on-disk exe (protected when elevated)
            string args = "apply-update \"" + tmp + "\" \"" + target + "\" " + pid + " \"" + Sha256Hex(tmp) + "\"";

            var psi = new ProcessStartInfo(self, args)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = needElevation
            };
            if (needElevation) psi.Verb = "runas"; // one UAC prompt to write into a protected folder
            try { Process.Start(psi); }
            catch (Exception ex) { SafeDelete(tmp); Ui.Msg(L.S("Не вдалося застосувати оновлення:\r\n", "Could not apply the update:\r\n") + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            Environment.Exit(0);
        }

        // Runs in the SECOND instance (elevated when the install dir is protected). Waits for the parent
        // to exit, RE-VERIFIES the staged file at apply time under a deny-write lock, then swaps it into
        // place via rename-in-place (a running exe can be renamed, not overwritten) with rollback on
        // failure, and relaunches. Invoked from Program.Main before the single-instance guard.
        //   args: [0]="apply-update" [1]=tmp [2]=target [3]=parentPid [4]=sha256hex
        public static void ApplyUpdateCommand(string[] args)
        {
            if (args == null || args.Length < 4) return;
            string tmp = args[1];
            string target = args[2];
            int pid; int.TryParse(args[3], out pid);
            string sha = args.Length >= 5 ? args[4] : null;

            try
            {
                WaitForExit(pid, 30000); // let the old process release its own exe

                // Re-verify under a deny-write/deny-delete lock so tmp cannot be swapped between the
                // check and the copy. WinVerifyTrust / SHA / File.Copy all open tmp for READ, which our
                // FileShare.Read lock permits — but a writer/deleter (an attacker) is blocked.
                using (var guard = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!VerifySignedByUs(tmp) || (!string.IsNullOrEmpty(sha) && !Sha256Equals(tmp, sha)))
                    {
                        Ui.Msg(L.S("Файл оновлення не пройшов повторну перевірку підпису — оновлення скасовано.",
                            "The update file failed re-verification — the update was cancelled."), Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        guard.Close(); SafeDelete(tmp); return;
                    }

                    string old = target + ".old";
                    SafeDelete(old);
                    bool moved = false;
                    try
                    {
                        File.Move(target, old); moved = true;   // rename the (possibly running) exe out of the way
                        File.Copy(tmp, target, false);          // write the verified new exe into place
                    }
                    catch (Exception ex)
                    {
                        if (moved && !File.Exists(target)) { try { File.Move(old, target); } catch { } } // rollback
                        Ui.Msg(L.S("Не вдалося записати оновлення:\r\n", "Could not write the update:\r\n") + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        guard.Close(); SafeDelete(tmp); return;
                    }
                }

                // ★ 2026-08-08, правка замороженого ядра дозволена власником (інваріант 3; запис у
                // docs\_MAINTENANCE_LOG.md). Тут стояв голий Process.Start у try{}catch{}: після
                // гілки Verb=runas (ApplyAndRestart) новий трей успадковував адмін-токен — UIPI
                // ламає drag-and-drop з Провідника, а елевація «через плече» запускала застосунок
                // від ІНШОГО користувача, з чужим %APPDATA% і чужим HKCU-автозапуском. Тепер —
                // де-елевовано через explorer.exe, як Installer.LaunchDeElevated і Setup.cs. І
                // невдача перезапуску більше не ковтається: оновлення ВЖЕ застосовано, тож
                // мовчання тут означало б «трей зник» одразу після вікна «підпис коректний».
                try { LaunchDeElevated(target); } // relaunch new build — de-elevated
                catch (Exception rex)
                {
                    Diag.LogCrash("apply-update relaunch", rex);
                    Ui.Msg(L.S("Оновлення встановлено, але не вдалося перезапустити застосунок:\r\n",
                               "The update is installed, but the app could not be restarted:\r\n") + rex.Message +
                           L.S("\r\n\r\nЗапустіть його вручну: ", "\r\n\r\nStart it manually: ") + target,
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                SafeDelete(tmp); // the renamed <exe>.old is cleaned up by the freshly-launched instance
            }
            catch (Exception ex)
            {
                // ★ 2026-08-08, правка дозволена власником (інваріант 3; запис у
                // docs\_MAINTENANCE_LOG.md). Тут стояв порожній catch — і це не «пастка прибирання
                // за собою», а тиха смерть оновлення, яке користувач ЩОЙНО підтвердив. Перший
                // екземпляр уже вийшов через Environment.Exit(0), і коли guard не відкривав tmp
                // (антивірус закарантинив/видалив/заблокував файл у %TEMP%), процес помирав ДО
                // обох Ui.Msg усередині try: ні вікна, ні сліду в лозі, ні перезапуску — трей
                // просто зникав. Тепер відмова лишає слід (LogCrash пише завжди), старий exe
                // повертається до життя, і користувач читає, що саме сталося.
                Diag.LogCrash("apply-update", ex);
                // Типовий шлях сюди — guard не відкрився, тобто до File.Move ми не дійшли і target
                // досі старий робочий exe. Де-елевовано з тієї ж причини, що й вище.
                bool relaunched = false;
                try { LaunchDeElevated(target); relaunched = true; }
                catch (Exception rex) { Diag.LogCrash("apply-update relaunch-old", rex); }
                Ui.Msg(L.S("Не вдалося застосувати оновлення: проміжний файл оновлення недоступний або " +
                           "пошкоджений (можливо, його заблокував чи видалив антивірус):\r\n",
                           "Could not apply the update: the staged update file is unavailable or " +
                           "damaged (an antivirus may have locked or deleted it):\r\n")
                       + tmp + "\r\n\r\n" + ex.Message + "\r\n\r\n"
                       + (relaunched
                           ? L.S("Поточну версію запущено знову — застосунок працює далі без оновлення.",
                                 "The current version has been relaunched — the app keeps working without the update.")
                           : L.S("Перезапустити поточну версію теж не вдалося — запустіть застосунок вручну: ",
                                 "Relaunching the current version also failed — start the app manually: ") + target),
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                SafeDelete(tmp);
            }
        }

        // Запуск НЕ від адміністратора з елевованого процесу apply-update (дзеркало
        // Installer.LaunchDeElevated — той private у чужому класі; правка 2026-08-08, див. вище).
        // Прямий Process.Start успадкував би права, і трей-застосунок жив би адміністратором:
        // зайві права, зламаний drag-and-drop. Explorer.exe працює на середньому рівні цілісності,
        // тож запущений ним процес отримує звичайні права користувача. На відміну від Installer,
        // невдача тут НЕ ковтається: якщо і explorer, і прямий запуск впали — виняток летить до
        // викликача, бо за ним стоїть «трей зник», і користувач мусить про це прочитати.
        private static void LaunchDeElevated(string exe)
        {
            try { Process.Start("explorer.exe", "\"" + exe + "\""); }
            catch { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } // остання спроба; її виняток — назовні
        }

        // Delete a leftover <exe>.old from a previous rename-in-place update. Called on startup.
        public static void CleanupOld()
        {
            try { SafeDelete(Application.ExecutablePath + ".old"); } catch { }
        }

        private static void WaitForExit(int pid, int timeoutMs)
        {
            if (pid <= 0) return;
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(timeoutMs); }
            catch { /* already gone */ }
        }

        // FileVersion of a binary, as a Version (for the anti-rollback binding). Null if unreadable.
        internal static Version ReadFileVersion(string path)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                Version v;
                return (!string.IsNullOrEmpty(fvi.FileVersion) && Version.TryParse(fvi.FileVersion.Trim(), out v)) ? v : null;
            }
            catch { return null; }
        }

        // Normalize to Major.Minor.Build (manifest is 3-part; binary FileVersion is 4-part), clamping any
        // missing component to 0 so equality/ordering is well-defined.
        internal static Version Norm3(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        private static string Sha256Hex(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static bool CanWriteDir(string dir)
        {
            try
            {
                string t = Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(t, "x");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        // ---------- http ----------
        private static void EnableTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768; } catch { } // Tls12 | Tls11
        }

        private static string HttpGetString(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs; req.UserAgent = "ValeraScreenshot/" + Ver.Number; req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static void HttpGetFile(string url, string dest, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "ValeraScreenshot/" + Ver.Number; req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[16384]; int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
            // Reject an HTML page masquerading as the exe (Google Drive confirm/interstitial page):
            // a real Windows executable starts with "MZ".
            using (var fs = File.OpenRead(dest))
            {
                int a = fs.ReadByte(), b = fs.ReadByte();
                if (!(a == 0x4D && b == 0x5A))
                { fs.Close(); SafeDelete(dest); throw new Exception(L.S("Завантажено не .exe (ймовірно, сторінка Google Drive). Перевірте, що посилання ПРЯМЕ (uc?export=download).", "Downloaded file is not an .exe (probably a Google Drive page). Make sure the link is DIRECT (uc?export=download).")); }
            }
        }

        private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static void ParseManifest(string text, out string ver, out string url, out string sha, out string notes)
        {
            ver = url = sha = notes = null;
            if (text == null) return;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "version") ver = v;
                else if (k == "url") url = v;
                else if (k == "sha256") sha = v;
                else if (k == "notes") notes = v;
            }
        }

        // ---------- WinVerifyTrust P/Invoke ----------
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        private static uint WinVerifyTrustFile(string path)
        {
            var fi = new WINTRUST_FILE_INFO();
            fi.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
            fi.pcwszFilePath = path;
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            Marshal.StructureToPtr(fi, pFile, false);

            var wd = new WINTRUST_DATA();
            wd.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
            wd.dwUIChoice = 2;            // WTD_UI_NONE
            wd.fdwRevocationChecks = 0;   // WTD_REVOKE_NONE
            wd.dwUnionChoice = 1;         // WTD_CHOICE_FILE
            wd.pFile = pFile;
            wd.dwStateAction = 1;         // WTD_STATEACTION_VERIFY
            wd.dwProvFlags = 0x00000010;  // WTD_SAFER_FLAG
            IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
            Marshal.StructureToPtr(wd, pData, false);

            uint result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
                wd = (WINTRUST_DATA)Marshal.PtrToStructure(pData, typeof(WINTRUST_DATA));
                wd.dwStateAction = 2;     // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(wd, pData, false);
                WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
                Marshal.FreeHGlobal(pData);
            }
            return result;
        }
    }
}
