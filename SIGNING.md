# SIGNING.md — підпис і канал оновлень ValeraScreenshot

STANDARD_VERSION: 1

## Сертифікат
- **Видавець:** Павло Ісаєв (особистий бренд; жодної організації в продукті немає).
- **Суб'єкт серта:** `CN=Pavlo Isaiev, O=Pavlo Isaiev` — ВЛАСНИЙ серт ValeraScreenshot, змінчений
  власником 2026-07-21 (`tools\studio_percert.ps1 -Apply` → `tools\newcert.ps1 -Apply`).
  Ідентичність видавця відтоді — bind-поле нормалізатора студії (`CertSubject`/`CertOrg`/
  `Thumbprint`), тож власний серт НЕ форкає спільне ядро. Старий спільний серт лишився у
  сховищі — ним підписується опублікований застосунок студії; його клієнти не зачеплені.
- **Пін-відбиток:** `A30B626AF77DD5FC2FD04C11DE1D5ADAA56E8FBE`.
- Цей відбиток МУСИТЬ збігатися в трьох місцях (STD-UPD-02): `sign.ps1`, `src\Updater.cs`
  (`TrustedThumbprint`), `standard.bind.json` (`Identity.Thumbprint`).
- Публічний `.cer` для роздачі/довіри: `ValeraScreenshotCodeSign.cer` (+ `deploy\trust_cert.cmd` — у пакувальному транші).

## Правила підпису (STD-SIGN)
- `sign.ps1` обирає серт **лише за відбитком** (не за Subject/FriendlyName) і **fail-closed**:
  якщо пін-серта немає — падає з помилкою; self-signed мінтиться лише з явним `-AllowSelfSigned`
  (dev-only). Підпис чужим сертом = тиха поломка оновлень для всієї встановленої бази.
- Мітка часу — `http://timestamp.digicert.com` (підпис лишається валідним після спливу серта).

## Канал оновлень
- Апдейтер тягне маніфест `latest.txt` з GitHub Releases:
  `https://github.com/nsrosbr/valera-screenshot/releases/latest/download/latest.txt`
  (переоприділюється через `%APPDATA%\ValeraScreenshot\update_url.txt`).
- Приймається лише `.exe`, підписаний пін-сертом (`WinVerifyTrust` + звірка відбитка), новіший за
  поточну версію (антивідкат), із валідним `MZ`-заголовком. Застосування — TOCTOU-безпечне
  (rename-in-place другим, за потреби піднятим, екземпляром).

## Формат `latest.txt`
```
version = 2.5.0
url     = https://github.com/nsrosbr/valera-screenshot/releases/download/v2.5.0/ValeraScreenshot-Setup-2.5.0.exe
sha256  = <hex>
notes   = Що нового\nдругий рядок
```

## Довіреність підпису
Поки сертифікат не в «Довірених видавцях», Windows показує підпис як недовірений (SmartScreen).
Це не виявлення шкідливого — код відкритий. Довірити централізовано: `ValeraScreenshotCodeSign.cer` →
Trusted Publisher (GPO). Змінювати серт/пін — рішення власника (CROWN): зміна відбитка вб'є канал
оновлень для всіх, хто вже його запінив.
