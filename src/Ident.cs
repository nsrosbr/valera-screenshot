using System;
using System.IO;

namespace ValeraScreenshot
{
    // STD-IDENT-01: ЄДИНЕ джерело кожної константи айдентики. Ніщо інше не переписує руками назву /
    // ім'я exe / ключ Run / ключ деінсталяції / mutex / id ресурсу — усе читає їх звідси. Саме розкид
    // цих констант по файлах, що «не бачать одне одного», породив баг трьох дескрипторів у сусідньому
    // застосунку (Installer, що зареєстрував ЧУЖИЙ продукт). Значення звірені зі standard.bind.json.
    internal static class Ident
    {
        public const string AppId        = "ValeraScreenshot";
        public const string AppName      = AppId;   // те саме; ім'я, під яким застосунок відомий системі
        public const string DisplayName  = "ВАЛЄРА Скріншот — знімки екрана";
        public const string Publisher    = "Павло Ісаєв";
        public const string Lnk          = AppId + ".lnk";

        // Посилання в картці «Параметри -> Програми». Порожній HelpLink прибирає кнопку довідки,
        // тому вони тут, а не роз'їхані по інсталяційних шляхах.
        public const string SiteUrl      = "https://github.com/nsrosbr/valera-screenshot";
        public const string HelpUrl      = "https://github.com/nsrosbr/valera-screenshot#readme";
        public const string UpdatesUrl   = "https://github.com/nsrosbr/valera-screenshot/releases";
        public const string Exe          = "ValeraScreenshot.exe";
        public const string ResourceId   = "valerascreenshot_exe";
        public const string Repo         = "nsrosbr/valera-screenshot";
        public const string Mutex        = "ValeraScreenshot_SingleInstance_{A17D}";
        // ТЕГ НОВИЙ, не успадкований. Я був лишив {7C41} від колишньої айдентики,
        // міркуючи, що рядок мутекса й так змінився через префікс. Перевірка C2 показала,
        // що це помилка судження: реєстр студії досі числить {7C41} за попереднім
        // застосунком, а правило вимагає УНІКАЛЬНОГО hex на кожен застосунок — саме щоб
        // два продукти ніколи не ділили один замок одиничного екземпляра.
        // {A17D} не збігається з {9F2A}, {4D7B}, {B36E} і {7C41}.
        public const string EnvDebug     = "VALERASCREENSHOT_DEBUG";
        public const string RunValue     = "ValeraScreenshot"; // ім'я значення в HKCU\...\Run (єдиний писар — ApplyStartup)
        public const string RunKey       = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ValeraScreenshot";
        public const string CerFile      = "ValeraScreenshotCodeSign.cer";

        // Прапорець тихого видалення. Був рядковим літералом у ДВОХ місцях, і вони розійшлися:
        // картка ARP реєструвала QuietUninstallString із «/silent», а setup\Uninstall.cs розбирав
        // лише «/S» -> будь-яке автоматизоване видалення (winget, скрипт розгортання, «Видалити»
        // без підтвердження) відкривало модальне вікно там, де його нікому натиснути.
        public const string SilentSwitch = "/silent";
        public const string Thumbprint   = "A30B626AF77DD5FC2FD04C11DE1D5ADAA56E8FBE";

        public static string AppDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId); }
        }
    }
}
