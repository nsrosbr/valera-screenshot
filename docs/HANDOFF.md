# HANDOFF.md — ValeraScreenshot

STANDARD_VERSION: 1

Швидкий вступ для сесії/інженера, що приймає ValeraScreenshot.

## Що це
Локальний застосунок знімків екрана (аналог Lightshot): захват області/екрана в НАТИВНІЙ
роздільності (PerMonitorV2 + BitBlt), редактор (13 інструментів, у т.ч. «Маршрут»), «Поділитися»,
трей. Нуль мережі поза апдейтером. C#/WinForms, збірка вбудованим `csc` (.NET Framework 4.x).

## Дерево (ключове)
- `src\` — код. Точки входу знань: `Ident.cs` (айдентика), `Ver.cs` (версія), `Updater.cs`
  (заморожена крона оновлень), `Diag.cs` (діагностика), `Ui.cs` (тема + Ui.Msg), `App.cs` (трей,
  Main, хоткеї), `OverlayForm.cs` (захват+редактор), `Config.cs`, `Native.cs`, `ScreenCap.cs`.
- `setup\` — `Setup.cs`/`Uninstall.cs` (+`.exe`), `setup.manifest`.
- `tools\` — `ProofGate.cs` (візуальний гейт: знімає кожну поверхню з ЕКРАНА в обох темах і
  МІРЯЄ її — контраст WCAG, належність тла до палітри, заголовок, light≠dark), `Drive.cs`
  (живий прогін справжнім вводом), `FieldProbe.cs` (симетрія встановлення/видалення),
  `mutate.ps1`, `MakeIcon.cs`.
- `tests\TestMain.cs` — тести ядра (`build.ps1 -All` → `tests\Test.exe`, зараз 17/17).
- `data\author.png` + `data\author_dark.png` (портрет автора для «Про програму»), `app.ico`,
  `app.manifest` (PerMonitorV2).
- `.standard\` — вендорований стандарт; `standard.bind.json` — айдентика+відхилення.
- `dist\` — dist-пакети + `VERSIONS.md`; `release\` — версійовані артефакти (поза git).

## Робочий цикл
```
powershell -ExecutionPolicy Bypass -File .\test.ps1   # ПОВНИЙ гейт: збірка + ядро + пруфи +
                                                      # живий прогін + польова проба
.\.standard\conform.ps1 -Root .                       # число конформності
.\tools\mutate.ps1                                    # мутація: гейт мусить ЧЕРВОНІТИ
.\sign.ps1                                            # підпис (після кожної збірки)
```

## Що НЕ робити без команди власника (CROWN)
Міняти пін-серт/URL оновлень, публікувати реліз, видаляти релізи, чіпати мережу/телеметрію,
самовільно бампати версію. Деталі — `CLAUDE.md`, `SIGNING.md`, `STRUCTURE.md`.
