# Реєстрація ValeraScreenshot у студії — три коронні кроки ВЛАСНИКА

Створено 2026-07-29 після перейменування LiteScribe → ValeraScreenshot.

## Навіщо

`D:\Soft\_studio\known_identities.json` — реєстр усіх айдентик студії. `conform.ps1 [C]`
прочісує дерево кожного проєкту на токени, що належать **іншому** застосунку: саме так колись
виявили, що ValeraZSU возить у ключі деінсталяції рядок сусіднього продукту.

Зараз реєстр числить **LiteScribe** (застосунку з такою айдентикою більше не існує) і **не знає**
ValeraScreenshot. Наш `conform` це не блокує — пломба ціла, колізій немає, 51/51 — але студія
тимчасово неузгоджена, і наступний sweep у сусідніх проєктах цього не побачить.

## Чому не зробив агент

Файл під пломбою (`payload.sha256`), а `rebuild.ps1` і `seal-sign.ps1` — коронні: заголовок
`seal-sign.ps1` дослівно каже «THE AGENT NEVER RUNS THIS». Запис у `D:\Soft` агентові
блокувався класифікатором тричі. Це крок власника за побудовою, не за обережністю.

## Крок 1 — правка реєстру

У `D:\Soft\_studio\known_identities.json`, у масиві `apps`, знайти запис `"AppId": "LiteScribe"`
і **замінити його цілком** на:

```json
    {
      "AppId": "ValeraScreenshot",
      "BrandUk": "ВАЛЄРА Скріншот",
      "BrandEn": "VALERA Screenshot",
      "DisplayName": "ВАЛЄРА Скріншот — знімки екрана",
      "Namespace": "ValeraScreenshot",
      "Exe": "ValeraScreenshot.exe",
      "AppData": "%APPDATA%\\ValeraScreenshot",
      "Mutex": "ValeraScreenshot_SingleInstance_{A17D}",
      "ResourceId": "valerascreenshot_exe",
      "EnvPrefix": "VALERASCREENSHOT",
      "Repo": "nsrosbr/valera-screenshot",
      "Cer": "ValeraScreenshotCodeSign.cer",
      "ReleaseTitle": "VALERA SCREENSHOT",
      "Path": "D:\\ValeraScreenshot",
      "Retired": false
    }
```

**Чому заміна, а не додавання.** Якщо лишити обидва записи, `LiteScribe` стане «іншим
застосунком» у сенсі перевірки [C], і будь-яка згадка старої назви — навіть у нашому власному
журналі — читатиметься як чужий токен. Продукт один; імен у нього два лише в історії.

**`Path`** вказано вже перейменованим (`D:\ValeraScreenshot`). Якщо теку ще не перейменовано,
поставте поточний шлях і виправте після.

**`BrandUk`/`BrandEn` НЕ дорівнюють `AppId`** — це навмисно. Коли вони збігалися, нормалізатор
студії мапив кожне входження назви на `<BRAND_UK>` замість `<APP>` і через це `sign.ps1`,
`package.ps1` і `trust_cert.cmd` читалися як форки ядра. Цей інцидент уже описаний у журналі.

## Крок 2 — перепломбування

```powershell
cd D:\Soft\_studio
.\rebuild.ps1
```

Перевірте у виводі: **членство ядра не має зменшитись**. Якщо якийсь файл випав із погодженого
ядра — це регрес, а не наслідок перейменування; зупиніться й перевірте, чому.

## Крок 3 — підпис пломби

```powershell
cd D:\Soft\_studio
.\seal-sign.ps1
```

## Крок 4 — перевірка

```powershell
cd D:\ValeraScreenshot
.\verify.ps1
```

Очікується `CONFORMANCE: 51/51`, зокрема `G1` (пломба) і `[C]` (айдентика) — зелені.
Якщо `G1` червона з `SEAL MISMATCH` — крок 3 не виконано або виконано до кроку 2.
