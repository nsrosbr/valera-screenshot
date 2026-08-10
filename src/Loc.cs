using System;
using System.Globalization;

namespace ValeraScreenshot
{
    internal enum UiLang { Uk, En }

    // Мова інтерфейсу (STD-LOC-01/02). Самодостатня (без залежності від Config), щоб нею міг
    // користуватися і апдейтер, і безголові шляхи, і інсталятор. Кожен рядок UI пишеться інлайн
    // як L.S("<українською>", "<English>").
    //
    // ★ ДО 2026-07-29 ЦЕ БУВ ШИМ. Main жорстко викликав L.Init("uk"), а ~220 видимих рядків були
    //   зашиті українською повз L.S — тобто друга мова існувала в API і не існувала в продукті.
    //   Тепер мова читається з конфіга, а гейт має твердження LOC: жоден кириличний строковий
    //   літерал у src\ і setup\ не має права стояти поза L.S (винятки — заморожена крона,
    //   метадані збірки й айдентика, кожен із названою причиною).
    internal static class L
    {
        public static UiLang Cur = UiLang.Uk;

        // Спрацьовує, коли мова СПРАВДІ змінилась у рантаймі. Довгоживучі поверхні (меню трея)
        // будуються один раз, тож без цієї події вони лишалися б у старій мові до перезапуску —
        // рівно та вада, яку тема вже пройшла (Theme.Changed без жодного підписника).
        public static event Action Changed;

        // setting: "uk" | "en" | будь-що інше ("auto") = мова системи: українська -> Uk, інакше En.
        public static void Init(string setting)
        {
            UiLang was = Cur;
            bool first = !_initialised;
            _initialised = true;
            if (setting == "en") Cur = UiLang.En;
            else if (setting == "uk") Cur = UiLang.Uk;
            else Cur = SystemIsUkrainian() ? UiLang.Uk : UiLang.En;
            if (!first && Cur != was && Changed != null) Changed();
        }

        private static bool _initialised;

        // Чисте рішення, окремо від стану — щоб його можна було перевірити тестом і зламати
        // мутацією (та сама причина, що й у Theme.Decide та Diag.ConsentVerdict).
        public static UiLang Decide(string setting, bool systemIsUkrainian)
        {
            if (setting == "en") return UiLang.En;
            if (setting == "uk") return UiLang.Uk;
            return systemIsUkrainian ? UiLang.Uk : UiLang.En;
        }

        public static bool SystemIsUkrainian()
        {
            try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uk"; }
            catch { return false; }
        }

        // Вибрати рядок для поточної мови.
        public static string S(string uk, string en) { return Cur == UiLang.En ? en : uk; }

        // Назва мови ЗАВЖДИ пишеться самою цією мовою — так її показують і Windows, і Office:
        // англомовний користувач має впізнати «Українська» в списку, а не прочитати «Ukrainian».
        // Тому це не рядок інтерфейсу й не місце для L.S; беремо назву в самої системи.
        public static string NativeName(string code)
        {
            try
            {
                string n = CultureInfo.GetCultureInfo(code).NativeName;
                if (n.Length == 0) return code;
                return char.ToUpper(n[0], CultureInfo.InvariantCulture) + n.Substring(1);
            }
            catch { return code; }
        }

        // ВИДИМА назва продукту. Це НЕ Ident.AppId: технічна айдентика — це "ValeraScreenshot"
        // (ім'я exe, mutex, ключі реєстру, id ресурсу), а людина читає «ВАЛЄРА Скріншот».
        // Розведення цих двох речей і є причина, чому Ident.cs існує окремо від L: у назві файла
        // не буває пробілу й кирилиці, а в назві продукту вони саме й потрібні.
        // Виклик навмисно з префіксом L., хоч ми й усередині класу L: твердження LOC у гейті
        // шукає саме "L.S(" і має рацію, коли не впізнає голе S(). Правило не послаблюємо —
        // приводимо виклик до тієї єдиної форми, яку правило й описує.
        public static string Name { get { return L.S("ВАЛЄРА Скріншот", "VALERA Screenshot"); } }

        // Назва з підзаголовком — підказка іконки трея, заголовок меню, опис ярлика.
        // ОДНЕ місце: до 2026-07-29 цей рядок був написаний РУКАМИ чотири рази (App.cs тричі,
        // setup\Setup.cs один) плюс окремо жив в Ident.DisplayName. Інваріант 1 забороняє
        // переписувати айдентику руками саме через це, і сьогоднішнє перейменування показало
        // ціну: досить проґавити одну копію, і продукт зве себе по-різному в різних вікнах.
        // Ident.DisplayName лишається як був — це технічна айдентика для реєстру (КОРОНА,
        // §18.1 п.3), і вона не локалізується; цей рядок — для ока, і він двомовний.
        public static string NameFull
        {
            get { return L.S("ВАЛЄРА Скріншот — знімки екрана", "VALERA Screenshot — screen captures"); }
        }
    }
}
