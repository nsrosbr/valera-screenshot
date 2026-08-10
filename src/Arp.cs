using System;
using Microsoft.Win32;

namespace ValeraScreenshot
{
    // ЄДИНЕ місце, де пишеться картка застосунку для «Параметри -> Програми» (ARP, Uninstall-ключ).
    // Спільне для ОБОХ інсталяційних шляхів — self-install (Installer.cs, HKCU) і Setup.exe
    // (Setup.cs, HKLM). Вони компілюються ОКРЕМО, тому дві копії цього коду неминуче б розійшлися —
    // рівно той клас дубля, проти якого зроблено Ident.cs.
    //
    // Чому окремий файл, а не метод в Ident.cs: чекер [E10] (STD-LIFE-02) вимагає, щоб файл із
    // константою Run-ключа НЕ містив SetValue — інакше не відрізнити «оголошує ключ» від «пише в
    // нього», і друге джерело правди про автозапуск проходить непоміченим. Ident.cs = самі константи.
    //
    // ★ Arp.cs МУСИТЬ входити в ОБИДВІ цілі компіляції: build.ps1 (через src\*.cs) і
    //   build_setup.ps1 (явним списком). Прибереш звідти — Setup.exe не збереться.
    internal static class Arp
    {
        // ОНОВЛЕННЯ картки після заміни exe новішою версією. Свідомо НЕ чіпає команди видалення:
        // їх записав той, хто ставив програму, і лише він знає, як її прибрати. Машинну інсталяцію
        // знімає ValeraScreenshotSetup.exe (він уміє HKLM і Program Files); перезаписавши це на
        // «ValeraScreenshot.exe /uninstall», ми залишили б користувача з кнопкою «Видалити», яка тихо
        // не робить нічого. Саме так ламалася чужа деінсталяція після авто-оновлення в сусідньому
        // застосунку — реальний польовий регрес, а не теорія.
        public static void Refresh(RegistryKey k, string exePath, string installDir)
        {
            if (k == null) return;
            string keepUninstall = k.GetValue("UninstallString") as string;
            string keepQuiet = k.GetValue("QuietUninstallString") as string;
            // ДАТА ВСТАНОВЛЕННЯ НАЛЕЖИТЬ ВСТАНОВЛЕННЮ, А НЕ ОНОВЛЕННЮ — за тим самим принципом,
            // що й команда видалення двома рядками вище: Refresh не переписує те, що записав той,
            // хто СТАВИВ програму. Write() ставить InstallDate = сьогодні, і це правильно для
            // інсталяції; але SelfHeal кличе Refresh після КОЖНОГО оновлення версії, тож без цих
            // рядків Windows у «Параметри -> Програми» показувала б «Встановлено: сьогодні» після
            // кожного апдейту. Справжня дата втрачалася б назавжди, а сортування за датою
            // встановлення переставало б щось означати.
            string keepInstalled = k.GetValue("InstallDate") as string;
            Write(k, exePath, installDir,
                  string.IsNullOrEmpty(keepUninstall) ? "\"" + exePath + "\" /uninstall" : keepUninstall,
                  string.IsNullOrEmpty(keepQuiet)
                      ? "\"" + exePath + "\" /uninstall " + Ident.SilentSwitch : keepQuiet);
            if (!string.IsNullOrEmpty(keepInstalled))
                try { k.SetValue("InstallDate", keepInstalled); } catch { }
        }

        public static void Write(RegistryKey k, string exePath, string installDir,
                                 string uninstallString, string quietUninstallString)
        {
            if (k == null) return;
            k.SetValue("DisplayName", Ident.DisplayName);
            k.SetValue("DisplayVersion", Ver.Number);
            k.SetValue("Publisher", Ident.Publisher);
            k.SetValue("DisplayIcon", exePath);
            k.SetValue("InstallLocation", installDir);
            k.SetValue("UninstallString", uninstallString);
            k.SetValue("QuietUninstallString", quietUninstallString);
            k.SetValue("HelpLink", Ident.HelpUrl);
            k.SetValue("URLInfoAbout", Ident.SiteUrl);
            k.SetValue("URLUpdateInfo", Ident.UpdatesUrl);
            k.SetValue("Contact", Ident.Publisher);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try
            {
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                Version v;
                if (Version.TryParse(Ver.Number, out v))
                {
                    k.SetValue("VersionMajor", v.Major, RegistryValueKind.DWord);
                    k.SetValue("VersionMinor", v.Minor, RegistryValueKind.DWord);
                }
                long sizeKb = 0;
                try { sizeKb = new System.IO.FileInfo(exePath).Length / 1024; } catch { }
                if (sizeKb > 0) k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
