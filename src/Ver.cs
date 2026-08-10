namespace ValeraScreenshot
{
    // Єдине джерело версії (STD-VER-01): AssemblyInfo, «Про програму», інсталятор, апдейтер і
    // dist-пакети читають звідси. Піднімати Number+Date на кожній передачі. Number — 3-компонентний
    // (Major.Minor.Build), щоб антивідкат апдейтера порівнював його з 4-компонентним FileVersion.
    internal static class Ver
    {
        public const string Number = "2.5.1";         // людино-читна версія (3-компонентна)
        public const string Build = "2.5.1.0";        // версія збірки (обов'язково x.x.x.x)
        public const string Date = "2026-08-08";      // дата збірки (оновлювати разом із Number)

        public static string Display { get { return Number + " (" + Date + ")"; } }
    }
}
