# mutate.ps1 - MUTATION TESTING for the gate itself. ASCII-only (STD-ENC-01).
#
# WHY THIS EXISTS. A green gate proves the tests RAN, not that they GUARD anything. This project has
# a concrete example: an offline probe fed a Microsoft-signed binary to the update gate and the run
# was reported as proof that OUR pinned certificate is enforced. It was not. System binaries are
# CATALOGUE-signed, so WinVerifyTrust rejects them BEFORE the thumbprint line is ever reached - the
# probe proved "something was rejected", not "the pin works". STD-GATE-09 names this exact trap.
# Mutation is the answer: break the guarantee in the SHIPPING code and require the gate to go red.
#
# WHAT IT DOES. For each mutation: back up the file, patch one crown guarantee to the WRONG
# behaviour, run test.ps1, restore, and record the verdict.
#   CAUGHT    - the gate failed => that guarantee is genuinely guarded.
#   SURVIVED  - the gate stayed green with a broken guarantee => the claim is UNPROVEN. That is the
#               finding. A survivor is not a test bug to paper over; it is an untested promise.
#
# The catalogue targets guarantees the product PROMISES the user (privacy, signed updates, no
# silent downgrade) - not implementation details. Mutating a detail proves nothing.
#
# Usage:  .\tools\mutate.ps1              run all
#         .\tools\mutate.ps1 -Only UPD    run one group
#         .\tools\mutate.ps1 -List        just list the catalogue
param(
    [string]$Only = "",
    [switch]$List,
    [switch]$WithDrive
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# Each mutation: Id, Group, Claim (the promise it breaks), File, Find, Replace.
# Find must match EXACTLY ONCE - a mutation that does not apply is reported as BROKEN, never skipped
# silently: a stale anchor would otherwise look like a passing run.
$mutations = @(
    @{ Id = "DIAG-1"; Group = "DIAG"; File = "src\Diag.cs"
       Claim = "log stops the moment the user withdraws consent (debug.on removed)"
       Find  = "            if (!_on) return;`n            if (!StillEnabled()) return;"
       Repl  = "            if (!_on) return;   // MUTANT: consent never re-checked" },

    @{ Id = "DIAG-2"; Group = "DIAG"; File = "src\Diag.cs"
       Claim = "a consent check that FAILS must not keep logging (fail-closed)"
       Find  = "            if (checkFailed) return false;   // FAIL-CLOSED"
       Repl  = "            if (false) return false;   // MUTANT: fail-open" },

    @{ Id = "DIAG-3"; Group = "DIAG"; File = "src\Diag.cs"
       Claim = "a missing consent marker silences the log"
       Find  = "            return markerExists;"
       Repl  = "            return true;   // MUTANT: marker ignored" },

    @{ Id = "DIAG-4"; Group = "DIAG"; File = "src\Diag.cs"
       Claim = "only truthy env values arm the sensitive flag (0/false must NOT)"
       Find  = "                case `"1`": case `"true`": case `"yes`": case `"on`": case `"y`": return true;"
       Repl  = "                case `"1`": case `"true`": case `"yes`": case `"on`": case `"y`": return true;`n                case `"0`": case `"false`": return true;   // MUTANT: falsy arms the flag" },

    @{ Id = "UPD-1"; Group = "UPD"; File = "src\Updater.cs"
       Claim = "an update is applied only if signed by OUR pinned certificate"
       Find  = "            return string.Equals(certHashHex, TrustedThumbprint, StringComparison.OrdinalIgnoreCase);"
       Repl  = "            return true;   // MUTANT: pin ignored" },

    @{ Id = "UPD-2"; Group = "UPD"; File = "src\Updater.cs"
       Claim = "anti-rollback: never accept a build that is not strictly newer"
       Find  = "            if (current != null && Norm3(bin) <= Norm3(current)) return Verdict.NotNewer;"
       Repl  = "            if (false) return Verdict.NotNewer;   // MUTANT: rollback allowed" },

    @{ Id = "UPD-3"; Group = "UPD"; File = "src\Updater.cs"
       Claim = "the manifest version must match the downloaded binary"
       Find  = "            if (bin == null || manifest == null || Norm3(bin) != Norm3(manifest)) return Verdict.VersionMismatch;"
       Repl  = "            if (false) return Verdict.VersionMismatch;   // MUTANT: version binding dropped" },

    @{ Id = "LIFE-1"; Group = "LIFE"; File = "src\Installer.cs"
       Claim = "a portable copy never writes an autostart entry into the host machine's registry"
       Find  = "            if (IsPortable()) return;   // portable: never write autostart (STD-LIFE-03)"
       Repl  = "            if (false) return;   // MUTANT: portable marker ignored" },

    @{ Id = "LIFE-2"; Group = "LIFE"; File = "src\Arp.cs"
       Claim = "updating must not rewrite the registered uninstall command"
       Find  = "            string keepUninstall = k.GetValue(`"UninstallString`") as string;"
       Repl  = "            string keepUninstall = null;   // MUTANT: uninstall command overwritten" },

    # LIFE-3..5 lock the worst defect the product ever had: uninstall recursively wiped the folder
    # the exe was run from. A portable copy in Documents turned /uninstall into "delete Documents",
    # while the dialog promised "your screenshots are never deleted".
    # Single-quoted anchors on purpose: these C# lines carry both \ and " , and double-quoted
    # PowerShell strings turned them into a parser error. STD-ENC-01's cousin - the same file
    # already broke once on a non-ASCII anchor.
    @{ Id = "LIFE-3"; Group = "LIFE"; File = "src\Installer.cs"
       Claim = "uninstall never removes a folder recursively"
       Find  = '            sb.Append(" & rmdir \"").Append(installDir).Append("\" 2>nul");'
       Repl  = '            sb.Append(" & rmdir /s /q \"").Append(installDir).Append("\"");   // MUTANT: recursive wipe' },

    @{ Id = "LIFE-4"; Group = "LIFE"; File = "src\Installer.cs"
       Claim = "uninstall never wipes the folder the exe happened to be run from"
       Find  = '                sb.Append(" & rmdir \"").Append(liveDir).Append("\" 2>nul");'
       Repl  = '                sb.Append(" & rmdir /s /q \"").Append(liveDir).Append("\"");   // MUTANT: wipes live dir' },

    # Mutates the DELETE TARGET, not the OwnedFiles list. Widening the list would also widen the
    # test's own reference data - the mutant would grade itself and survive for the wrong reason.
    @{ Id = "LIFE-5"; Group = "LIFE"; File = "src\Installer.cs"
       Claim = "uninstall deletes only files the installer itself placed"
       Find  = '                sb.Append(" & del /f /q \"").Append(Path.Combine(installDir, name)).Append("\"");'
       Repl  = '                sb.Append(" & del /f /q \"").Append(Path.Combine(installDir, "*.*")).Append("\"");   // MUTANT: wildcard' },

    # The silent switch used to be two separate string literals in two compilation units; the ARP
    # card registered "/silent" while the uninstaller parsed only "/S", so every unattended removal
    # stopped on a modal dialog. Re-introducing the literal must turn the gate red.
    @{ Id = "LIFE-7"; Group = "LIFE"; File = "src\Arp.cs"
       Claim = "the registered quiet-uninstall switch is the one constant the app parses"
       Find  = '                      ? "\"" + exePath + "\" /uninstall " + Ident.SilentSwitch : keepQuiet);'
       Repl  = '                      ? "\"" + exePath + "\" /uninstall /S" : keepQuiet);   // MUTANT: literal drift' },

    # A repeating exception in OnPaint does not stop the message loop: Windows sends WM_PAINT again
    # and the same stack was written hundreds of times a second, synchronously, from the UI thread.
    @{ Id = "DIAG-5"; Group = "DIAG"; File = "src\Diag.cs"
       Claim = "a repeating crash is logged once, not on every repaint"
       Find  = '                if (key == _lastCrashKey) return;'
       Repl  = '                if (false) return;   // MUTANT: crash storm' },

    # The mosaic annotation owns a Bitmap nobody released - hundreds of leaked GDI objects per session.
    @{ Id = "LIFE-8"; Group = "LIFE"; File = "src\Annotate.cs"
       Claim = "a pixelate annotation releases the tile bitmap it owns"
       Find  = '            if (Tile != null) { Tile.Dispose(); Tile = null; }'
       Repl  = '            if (false) { Tile.Dispose(); Tile = null; }   // MUTANT: tile leaks' },

    @{ Id = "LIFE-6"; Group = "LIFE"; File = "src\App.cs"
       Claim = "/install refuses to install while portable.txt is present"
       Find  = "                if (Installer.IsPortable()) return 2;   // portable.txt -> refuse, install.ps1 explains"
       Repl  = "                if (false) return 2;   // MUTANT: portable guard bypassed" },

    # UI-* lock the VISUAL promises - the half of the product no unit test can reach. Every one of
    # them is a defect that really shipped: the owner's report was literally "dark fields in the
    # light theme", and the proofs on disk were byte-identical between light and dark because the
    # tools that made them were built by build.ps1 and run by nothing.
    @{ Id = "UI-1"; Group = "UI"; File = "src\Ui.cs"
       Claim = "the light theme paints light surfaces (no dark card in the light theme)"
       Find  = "            CardBg = Color.White;"
       Repl  = "            CardBg = Color.FromArgb(0x2B, 0x2B, 0x2B);   // MUTANT: dark card in light theme" },

    @{ Id = "UI-2"; Group = "UI"; File = "src\Ui.cs"
       Claim = "an input field has a boundary a person can see (WCAG 1.4.11, 3:1)"
       Find  = "            ControlBorder = Color.FromArgb(0x8A, 0x8A, 0x8A);"
       Repl  = "            ControlBorder = Color.FromArgb(0xD1, 0xD1, 0xD1);   // MUTANT: 1.53:1 border" },

    @{ Id = "UI-3"; Group = "UI"; File = "src\Ui.cs"
       Claim = "the combo box face is painted by US, not by the system (no white face in dark theme)"
       Find  = "            if (m.Msg == WM_PAINT)`n            {`n                var ps = new PAINTSTRUCT();"
       Repl  = "            if (false)`n            {`n                var ps = new PAINTSTRUCT();   // MUTANT: system paints the combo" },

    # The scroll bar is drawn by Windows, not by us. This anchor is the ONE call that darkens it -
    # proven by experiment: two undocumented uxtheme ordinals were tried here first and removing
    # them changed no pixel, while removing this line brings the white 17x576 bar straight back.
    @{ Id = "UI-4"; Group = "UI"; File = "src\Ui.cs"
       Claim = "system-drawn parts (scroll bars) follow the app theme"
       Find  = '            try { SetWindowTheme(c.Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null); }'
       Repl  = '            try { SetWindowTheme(c.Handle, "Explorer", null); }   // MUTANT: always light scroll bar' },

    # ACC-* lock the half of the interface a screenshot cannot show. Before them the tree had ZERO
    # AccessibleName/AccessibleRole: half the controls paint themselves, and a self-painted control
    # is a nameless rectangle to a screen reader. Perfect contrast on a control that does not exist
    # for a blind user is not accessibility.
    @{ Id = "ACC-1"; Group = "ACC"; File = "src\SettingsForm.cs"
       Claim = "every switch announces WHICH setting it is (a switch has no text of its own)"
       Find  = 'var tog = new ToggleSwitch { BackColor = Theme.CardBg, AccessibleName = title };'
       Repl  = 'var tog = new ToggleSwitch { BackColor = Theme.CardBg };   // MUTANT: nameless switch' },

    @{ Id = "ACC-2"; Group = "ACC"; File = "src\Ui.cs"
       Claim = "Windows High Contrast overrides our palette instead of being overridden by it"
       Find  = "            if (highContrast) return Palette.HighContrast;"
       Repl  = "            if (false) return Palette.HighContrast;   // MUTANT: high contrast ignored" },

    @{ Id = "ACC-3"; Group = "ACC"; File = "src\Ui.cs"
       Claim = "in High Contrast the palette comes from the SYSTEM, not from our constants"
       Find  = "            TextPrimary = SystemColors.WindowText;"
       Repl  = "            TextPrimary = Color.FromArgb(0x24, 0x24, 0x24);   // MUTANT: our colour in high contrast" },

    # Caught by PIXELS, not by a field comparison: the high-contrast proof scans the rendered
    # window for our brand blue. A control that paints itself past the palette shows up there
    # and nowhere else.
    @{ Id = "ACC-4"; Group = "ACC"; File = "src\Ui.cs"
       Claim = "no control paints our brand colour once the user asked for High Contrast"
       Find  = "            Accent = SystemColors.Highlight;"
       Repl  = "            Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);   // MUTANT: brand blue in high contrast" },

    # LOC-* lock the second language. The L.S shim existed from day one, which is exactly why
    # bilingualism LOOKED present: Main hardcoded L.Init("uk") and 219 visible strings sat past
    # L.S entirely. A half-translated UI is worse than a single-language one, because it is silent.
    @{ Id = "LOC-1"; Group = "LOC"; File = "src\App.cs"
       Claim = "the interface language comes from the user's setting, not from a hardcoded one"
       Find  = "            L.Init(Config.Load().UiLang);"
       Repl  = '            L.Init("uk");   // MUTANT: language hardcoded again' },

    @{ Id = "LOC-2"; Group = "LOC"; File = "src\Loc.cs"
       Claim = "L.S actually switches language instead of always returning Ukrainian"
       Find  = "        public static string S(string uk, string en) { return Cur == UiLang.En ? en : uk; }"
       Repl  = "        public static string S(string uk, string en) { return uk; }   // MUTANT: English never shown" },

    # ANCHOR IN A FILE, NOT INLINE. This mutation must contain Cyrillic - it re-creates exactly the
    # defect the LOC assertion guards - but PowerShell 5.1 reads .ps1 as ANSI, so a single Cyrillic
    # byte here is a PARSER ERROR, not a runtime one. This project has now hit STD-ENC-01 three
    # times (a Cyrillic anchor in this very file, Ukrainian comments in build.ps1, and this).
    # Payloads that cannot be ASCII live in tools\mutants\*.txt and are read as UTF-8.
    @{ Id = "LOC-3"; Group = "LOC"; File = "src\SettingsForm.cs"
       Claim = "a visible string added past L.S turns the gate red"
       FindFile = "LOC-3.find.txt"; ReplFile = "LOC-3.repl.txt" },

    # LOC-4 locks a defect the LOC matrix could not see BY CONSTRUCTION. The literal sat inside
    # L.S - the rule was obeyed - but it sat in a FIELD INITIALISER, so it ran while Config was
    # being built, which App.Main does BEFORE it knows the user's language (it reads the language
    # from that very config). An English user got an English UI and files called
    # "Znimok_2026-08-05_...", and Save() runs after every screenshot, so it stuck. Only Find is
    # inline here: the replacement has to carry the Ukrainian default, and one Cyrillic byte in
    # this ASCII-only script is a parser error (STD-ENC-01).
    @{ Id = "LOC-4"; Group = "LOC"; File = "src\Config.cs"
       Claim = "a default built before the language is known still follows the language"
       Find  = '        private string _template = "";'
       ReplFile = "LOC-4.repl.txt" },

    # LOC-5: the product name, hand-copied a second time. It had FOUR hand-written copies until
    # 2026-07-29, and today's rename showed what that costs - miss one and the product calls
    # itself different things in different windows, discovered by the user rather than the gate.
    # Only Find is inline: the replacement carries the Ukrainian name, and one Cyrillic byte in
    # this ASCII-only script is a parser error (STD-ENC-01).
    @{ Id = "LOC-5"; Group = "LOC"; File = "src\App.cs"
       Claim = "the visible product name is declared once, not copied by hand into each window"
       # Two lines on purpose: the 12-space form is a SUBSTRING of the 16-space one in
       # OnLangChanged, so the short anchor matched twice and the catalogue reported BROKEN -
       # which is the anchor check doing its job, not a nuisance.
       Find  = "            _tray.Text = L.NameFull;" + "`n" + "            _tray.Visible = true;"
       ReplFile = "LOC-5.repl.txt" },

    # HON-* lock the promises that SILENCE breaks. The most expensive defects this product ever
    # shipped were not crashes but quiet ones: the action failed and the app either reported
    # success or said nothing at all.
    @{ Id = "HON-1"; Group = "HON"; File = "src\Diag.cs"
       Claim = "a crash reaches the PERSON, not only the log file (STD-DIAG-03)"
       Find  = "            if (!firstTimeForThisCause) return false;`n            return alreadyShown < MaxCrashDialogs;"
       Repl  = "            return false;   // MUTANT: crashes stay silent again" },

    @{ Id = "HON-2"; Group = "HON"; File = "src\Diag.cs"
       Claim = "a repaint crash storm cannot open a window per frame"
       Find  = "            if (!firstTimeForThisCause) return false;"
       Repl  = "            if (false) return false;   // MUTANT: a dialog per repeat" },

    @{ Id = "HON-3"; Group = "HON"; File = "src\Config.cs"
       Claim = "settings that could not be READ are never overwritten"
       Find  = "                if (LoadFailed && File.Exists(IniPath))"
       Repl  = "                if (false && File.Exists(IniPath))   // MUTANT: overwrite the unreadable file" },

    @{ Id = "HON-4"; Group = "HON"; File = "src\Config.cs"
       Claim = "an unreadable config is reported instead of silently becoming defaults"
       Find  = "                c.LoadFailed = true;"
       Repl  = "                c.LoadFailed = false;   // MUTANT: failure hidden" },

    # PKG-* guard the SHIPPING ARTEFACT, not the app inside it. The whole group exists because on
    # 2026-07-29 both package binaries were found not to compile, and had not compiled for a while:
    # nothing but a release ever invoked build_setup.ps1, so the break was invisible until the one
    # moment it costs most. Single quotes below are load-bearing - $root must reach the file as
    # literal text, not as this script's variable.
    @{ Id = "PKG-1"; Group = "PKG"; File = "build_setup.ps1"
       Claim = "the installer still compiles (the package is a GATE layer, not a release step)"
       Find  = '    (Join-Path $root "src\Arp.cs"),' + "`n" + '    (Join-Path $root "src\Seed.cs")'
       Repl  = '    (Join-Path $root "src\Arp.cs")   # MUTANT: Seed.cs dropped from the installer' },

    # The installer is a SEPARATE binary with its own Main, and for a long time nothing measured
    # it at all. ACC-5 is the defect that was actually shipping; ACC-6 is the one that would have
    # remained even after fixing the palette, because a bare Form never darkens its title bar.
    @{ Id = "ACC-5"; Group = "ACC"; File = "setup\Setup.cs"
       Claim = "the installer switches on the theme engine (else it is light in dark mode AND in high contrast)"
       Find  = '        Theme.Init("auto");'
       Repl  = '        // MUTANT: Theme.Init removed - the installer is light forever again' },

    @{ Id = "ACC-6"; Group = "ACC"; File = "setup\Setup.cs"
       Claim = "the installer window follows the theme, title bar included"
       Find  = "internal class SetupForm : ThemedForm"
       Repl  = "internal class SetupForm : Form   // MUTANT: bare Form, white title bar in dark mode" },

    # WHY THERE IS NO SHARE-* GROUP, stated instead of quietly omitted. C6/C7/C8 in the gate do
    # assert ShareUtil, but every target it returns is conditional on software being INSTALLED
    # (Telegram, Viber, Signal, a MAPI mail client). A mutation there would be caught on this
    # machine and reported SURVIVED on a clean CI box - a verdict that depends on the host is
    # worse than no verdict, because it teaches people to ignore the report. The honest state:
    # ShareUtil's promises are asserted but NOT mutation-proven, and C6 now prints "VACUOUS"
    # when it examined zero targets rather than passing green on an empty list.
    #
    # LIFE-11: the ARP card's install date. Write() sets it to today, which is right for an
    # INSTALL - but SelfHeal calls Refresh after every version change, so without the guard
    # Windows would report "Installed: today" after each update and the true date would be gone
    # for good. Same doctrine as the uninstall command right above it in Arp.Refresh: what the
    # installer recorded is not an updater's to rewrite.
    @{ Id = "LIFE-11"; Group = "LIFE"; File = "src\Arp.cs"
       Claim = "an update keeps the ORIGINAL install date instead of stamping today's"
       Find  = '            string keepInstalled = k.GetValue("InstallDate") as string;'
       Repl  = '            string keepInstalled = null;   // MUTANT: every update restamps the install date' },

    # LIFE-12: re-baking a pixelate tile must free the one it replaces. Not a live leak today -
    # Bake is called once per finished drag - but the method is public and used to overwrite the
    # field unguarded, and THIS FILE already shipped exactly that leak once (nobody freed the tile
    # at all; an hour of mosaic work ate hundreds of GDI handles, and Windows gives a process
    # 10 000). A guard that costs one line beats rediscovering the class a second time.
    @{ Id = "LIFE-12"; Group = "LIFE"; File = "src\Annotate.cs"
       Claim = "re-baking a pixelate annotation frees the tile it replaces"
       Find  = "            if (Tile != null) { try { Tile.Dispose(); } catch { } }"
       Repl  = "            // MUTANT: the previous tile is abandoned, not freed" },

    # LIFE-13: a static event holds a STRONG reference to its subscriber. A form that subscribes
    # and never detaches stays alive after it is closed, together with its bitmaps, its handles and
    # the frozen screenshot behind it. This product shipped that leak TWICE (Theme.Changed and
    # SystemEvents.DisplaySettingsChanged), so the class is proven, not theoretical.
    @{ Id = "LIFE-13"; Group = "LIFE"; File = "src\OverlayForm.cs"
       Claim = "a closed overlay lets go of the system event it subscribed to"
       Find  = "                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }"
       Repl  = "                // MUTANT: the overlay stays alive on a static event forever" },

    # SEED-* are the last shipping file with real logic and no mutation. Its assertions (L8, L14,
    # L16) were green - but OVL-6 had just proved, the same day, that a green assertion can be a
    # tautology guarding nothing. Green is a claim; a caught mutant is the evidence.
    # This file exists because ONE question got two different answers: the installer wrote the
    # autostart intent into %APPDATA% while the installed copy read settings.ini next to itself,
    # saw the default False, and deleted the Run key it had just been given. That is how the
    # "Start with Windows" checkbox used to vanish.
    @{ Id = "SEED-1"; Group = "SEED"; File = "src\Seed.cs"
       Claim = "seeding patches ONE line and leaves the rest of the user's settings alone"
       Find  = "                        else lines.Add(ln);"
       Repl  = "                        else { }   // MUTANT: every other setting is dropped" },

    @{ Id = "SEED-2"; Group = "SEED"; File = "src\Seed.cs"
       Claim = "the config is read STRICTLY as UTF-8, so Cyrillic paths survive a re-write"
       Find  = "                    foreach (string ln in File.ReadAllLines(iniPath, Encoding.UTF8))"
       Repl  = "                    foreach (string ln in File.ReadAllLines(iniPath, Encoding.Default))   // MUTANT: encoding as it comes" },

    @{ Id = "SEED-3"; Group = "SEED"; File = "src\Seed.cs"
       Claim = "a writable install folder keeps its config next to the exe, not in the profile"
       Find  = '                    return exeDir.TrimEnd(''\\'');'
       Repl  = '                    // MUTANT: a writable folder is ignored, config always in the profile' },

    # CAP-* guard the CAPTURE PATH and the share panel - neither had a mutation or an assertion
    # before 2026-07-29. Capture.cs is where the fourth "lie about success" lived: the BitBlt
    # return value was discarded, a protected window produced a BLACK frame, it was saved quietly
    # and the tray announced "Saved 1920 x 1080".
    @{ Id = "CAP-1"; Group = "CAP"; File = "src\Capture.cs"
       Claim = "the chosen file format is honoured, not overridden by whatever is convenient"
       Find  = "                SaveJpeg(b, path, cfg.JpegQuality);"
       Repl  = "                SavePng(b, path);   // MUTANT: a .jpg silently contains a PNG" },

    @{ Id = "CAP-2"; Group = "CAP"; File = "src\Capture.cs"
       Claim = "the JPEG quality setting reaches the encoder instead of decorating the config"
       Find  = "                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);"
       Repl  = "                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)50);   // MUTANT: setting ignored" },

    @{ Id = "CAP-3"; Group = "CAP"; File = "src\Capture.cs"
       Claim = "an UPPERCASE extension is routed like any other"
       Find  = '            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||' + "`n" + '                path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))'
       Repl  = '            if (path.EndsWith(".jpg", StringComparison.Ordinal) ||' + "`n" + '                path.EndsWith(".jpeg", StringComparison.Ordinal))   // MUTANT: .JPG misses' },

    @{ Id = "CAP-5"; Group = "CAP"; File = "src\Config.cs"
       Claim = "an unusable save folder fails loudly instead of handing back an unwritable path"
       Find  = "            Directory.CreateDirectory(dir);"
       Repl  = "            try { Directory.CreateDirectory(dir); } catch { }   // MUTANT: broken folder passes silently" },

    # Anchor in a FILE: the real code carries the Ukrainian failure message, and a single Cyrillic
    # byte in this ASCII-only script is a PARSER error (STD-ENC-01, four times in this project).
    @{ Id = "CAP-4"; Group = "CAP"; File = "src\Capture.cs"
       Claim = "a failed BitBlt is an ERROR, not a black frame reported as a screenshot"
       FindFile = "CAP-4.find.txt"; ReplFile = "CAP-4.repl.txt" },

    # OVL-* guard the EDITOR - the surface the user actually works in. Until 2026-07-29
    # src\OverlayForm.cs (1248 lines, the largest file in the product) had zero mutations and zero
    # assertions: the most-used part of the app was the least protected, which is what "bugs on top
    # of bugs" actually felt like from the owner's chair. Each mutant below is a defect a person
    # would notice with their hands, not in a log.
    @{ Id = "OVL-1"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "undo removes ONE annotation, not the whole drawing"
       Find  = "            _anns.RemoveAt(_anns.Count - 1);"
       Repl  = "            _anns.Clear();   // MUTANT: one undo wipes the lot" },

    @{ Id = "OVL-2"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "a new action discards the redo branch, so an undone annotation cannot resurrect"
       Find  = "            foreach (var r in _redo) { try { r.Dispose(); } catch { } }" + "`n" + "            _redo.Clear();"
       Repl  = "            // MUTANT: the redo branch survives a new action" },

    @{ Id = "OVL-3"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "the discarded redo branch frees the bitmaps it owned"
       Find  = "            foreach (var r in _redo) { try { r.Dispose(); } catch { } }"
       Repl  = "            // MUTANT: branch dropped without freeing what it owned" },

    @{ Id = "OVL-4"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "a selection dragged past the edge is clamped back into the frame"
       Find  = "            return Rectangle.Intersect(_sel, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));"
       Repl  = "            return _sel;   // MUTANT: selection may sit outside the captured frame" },

    @{ Id = "OVL-5"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "opposite corners get MIRRORED resize cursors"
       Find  = "                case 0: case 4: return Cursors.SizeNWSE;"
       Repl  = "                case 0: case 4: return Cursors.SizeNESW;   // MUTANT: both diagonals identical" },

    @{ Id = "OVL-6"; Group = "OVL"; File = "src\OverlayForm.cs"
       Claim = "every handle is drawn where it can actually be grabbed"
       Find  = "                case 3: return new Point(s.Right, s.Top + s.Height / 2);"
       Repl  = "                case 3: return new Point(s.Right + 40, s.Top + s.Height / 2);   // MUTANT: drawn off its hit box" },

    # LIFE-9/LIFE-10 lock the install<->uninstall symmetry that a COMMENT used to guarantee. Until
    # 2026-07-29 setup\Uninstall.cs claimed "test LU3 cross-checks both lists" and no such test
    # existed anywhere in the tree - one grep hit, the comment itself. A file placed by the
    # installer but unknown to the uninstaller does not merely linger: it blocks the deliberately
    # NON-recursive rmdir, so the install folder survives the uninstall.
    @{ Id = "LIFE-9"; Group = "LIFE"; File = "build_setup.ps1"
       Claim = "nothing is PLACED by the installer that the uninstaller is not allowed to remove"
       Find  = '$payloadDocs = @("MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md")'
       Repl  = '$payloadDocs = @("MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md", "NOTICE.txt")   # MUTANT: placed, never removed' },

    @{ Id = "LIFE-10"; Group = "LIFE"; File = "setup\Uninstall.cs"
       Claim = "the uninstaller's file list mirrors the installer's instead of drifting from it"
       Find  = '        Ident.Exe, Ident.CerFile,' + "`n" + '        "MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md"'
       Repl  = '        Ident.Exe, Ident.CerFile,' + "`n" + '        "MANUAL.txt", "README.md"   // MUTANT: the two English docs drift out' },

    # Anchors here are deliberately ASCII-only even though the lines around them are Ukrainian:
    # PS 5.1 reads this file as ANSI, and a single Cyrillic byte is a PARSER error (STD-ENC-01,
    # which this project has now tripped over three times). Payloads that cannot be ASCII live
    # in tools\mutants\*.txt instead.
    @{ Id = "ACC-7"; Group = "ACC"; File = "setup\Setup.cs"
       Claim = "the installer routes dialogs through Ui.Msg, like every other window in the product"
       Find  = "            var r = Ui.Msg(this,"
       Repl  = "            var r = MessageBox.Show(this,   // MUTANT: dialog goes around Ui.Msg" },

    @{ Id = "ACC-8"; Group = "ACC"; File = "setup\Uninstall.cs"
       Claim = "the package shows the PRODUCT NAME to a person, never the technical identity"
       Find  = "                L.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);"
       Repl  = "                Ident.AppId, MessageBoxButtons.OK, MessageBoxIcon.Information);   // MUTANT: raw id in the caption" },

    @{ Id = "PKG-2"; Group = "PKG"; File = "build_setup.ps1"
       Claim = "the uninstaller still compiles"
       Find  = '    (Join-Path $root "setup\Uninstall.cs"),' + "`n" + '    (Join-Path $root "src\Loc.cs"),' + "`n" + '    (Join-Path $root "src\Ident.cs")'
       Repl  = '    (Join-Path $root "setup\Uninstall.cs")   # MUTANT: exactly the break that shipped' }
)

if ($Only) { $mutations = @($mutations | Where-Object { $_.Group -eq $Only -or $_.Id -eq $Only }) }

if ($List) {
    $mutations | ForEach-Object { "{0,-8} {1,-16} {2}" -f $_.Id, $_.File, $_.Claim }
    exit 0
}

# Drive.exe (the real app under real input) costs minutes per run, so it is skipped by default.
# This is the SAFE direction: without Drive a mutation can only be reported SURVIVED when it was
# really caught (a false ALARM), never CAUGHT when it was not. -WithDrive runs the full gate.
#
# ProofGate is NOT skipped. It costs seconds, and the UI-* mutations exist precisely because the
# visual promises have no other guard - riding them on the Drive switch would report every theme
# mutation as SURVIVED for a reason that has nothing to do with the theme.
function Run-Gate {
    # Drive is OPT-IN in test.ps1 since 2026-07-29 (taking the owner's desktop must never be the
    # default). Clearing the old skip variable is therefore no longer enough to run it - the run
    # variable has to be SET. Left as it was, -WithDrive would have quietly stopped meaning
    # anything: the catalogue would report the same verdicts while silently covering less.
    if ($WithDrive) { $env:VALERASCREENSHOT_RUN_DRIVE = "1" } else { $env:VALERASCREENSHOT_RUN_DRIVE = $null }
    $env:VALERASCREENSHOT_SKIP_PROOF = $null
    # $ErrorActionPreference MUST drop to Continue around this call. In PS 5.1, `2>&1` on a NATIVE
    # exe wraps every stderr line in an ErrorRecord; with the script-level "Stop" that is a
    # TERMINATING error. A mutated gate is SUPPOSED to be red and may print to stderr, so the very
    # thing we are measuring aborted the whole catalogue: a full run died at mutation 17 of 21 and
    # silently lost the remaining four verdicts. An expected failure must never kill the measurer.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "test.ps1") 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    $env:VALERASCREENSHOT_RUN_DRIVE = $null
    # EXIT CODE first, banner second. Matching a substring alone is fragile: any tool the gate
    # runs could print those words and quietly turn every verdict green. The gate's contract is
    # its exit code (STD-GATE-01) - the banner is only a human-readable confirmation of it.
    return (($code -eq 0) -and ($out -match "GATE: ALL TESTS PASSED"))
}

Write-Host "=== BASELINE: gate must be GREEN before mutating ==="
if (-not (Run-Gate)) { throw "gate is RED before mutation - fix that first" }
Write-Host "baseline OK"
Write-Host ""

# Read a payload that cannot live inside this ASCII-only script (STD-ENC-01). Trailing newlines
# are stripped: an editor adding one would silently break the anchor and report BROKEN.
function Read-Payload($name) {
    $p = Join-Path (Join-Path $root "tools\mutants") $name
    if (-not (Test-Path $p)) { throw "mutation payload missing: $p" }
    return ([System.IO.File]::ReadAllText($p, (New-Object System.Text.UTF8Encoding($false)))).TrimEnd("`r", "`n")
}

$results = @()
foreach ($m in $mutations) {
    $path = Join-Path $root $m.File
    $orig = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    if ($m.ContainsKey("FindFile")) { $m.Find = Read-Payload $m.FindFile }
    if ($m.ContainsKey("ReplFile")) { $m.Repl = Read-Payload $m.ReplFile }
    $find = $m.Find -replace "`r`n", "`n"

    $count = ([regex]::Matches($orig, [regex]::Escape($find))).Count
    if ($count -ne 1) {
        Write-Host ("{0,-8} BROKEN   anchor matched {1}x (expected 1) in {2}" -f $m.Id, $count, $m.File)
        $results += @{ Id = $m.Id; Status = "BROKEN"; Claim = $m.Claim }
        continue
    }

    [System.IO.File]::WriteAllText($path, $orig.Replace($find, ($m.Repl -replace "`r`n", "`n")), (New-Object System.Text.UTF8Encoding($false)))
    try {
        $green = Run-Gate
        $status = if ($green) { "SURVIVED" } else { "CAUGHT" }
    }
    finally {
        [System.IO.File]::WriteAllText($path, $orig, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host ("{0,-8} {1,-9} {2}" -f $m.Id, $status, $m.Claim)
    $results += @{ Id = $m.Id; Status = $status; Claim = $m.Claim }
}

Write-Host ""
$caught = @($results | Where-Object { $_.Status -eq "CAUGHT" }).Count
$surv = @($results | Where-Object { $_.Status -eq "SURVIVED" })
$broken = @($results | Where-Object { $_.Status -eq "BROKEN" })
Write-Host ("MUTATION SCORE: {0}/{1} caught" -f $caught, $results.Count)
if ($surv.Count) {
    Write-Host ""
    Write-Host "SURVIVORS - crown claims the gate does NOT guard:"
    $surv | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Id, $_.Claim) }
}
if ($broken.Count) { Write-Host ""; Write-Host "BROKEN ANCHORS (fix the catalogue):"; $broken | ForEach-Object { Write-Host ("  " + $_.Id) } }

# Restore verification: no mutant may survive on disk.
$leak = @()
foreach ($f in ($mutations | ForEach-Object { $_.File } | Select-Object -Unique)) {
    $t = [System.IO.File]::ReadAllText((Join-Path $root $f), [System.Text.Encoding]::UTF8)
    if ($t -match "MUTANT") { $leak += $f }
}
if ($leak.Count) { throw ("MUTANT LEFT ON DISK in: " + ($leak -join ", ")) }
Write-Host ""
Write-Host "restore verified: no MUTANT marker left in any source file"
# Explicit exit codes. Without them the script inherited $LASTEXITCODE from the LAST gate run -
# and that run is the mutated one, which is SUPPOSED to be red. A perfect 100%-caught pass
# therefore reported failure to its caller.
if ($surv.Count -gt 0 -or $broken.Count -gt 0) { exit 1 }
exit 0
