# Добавление новой точки / нового экрана

Это руководство объясняет, как подключить **новый LED-экран** (с любым разрешением и набором валют) не трогая существующие точки.

---

## 1. Выбрать идентификатор точки

Придумайте короткое имя без пробелов, например `downtown`, `almaty2`, `airport`.
Далее везде в документе используется имя **`mypoint`**. Замените его на своё.

---

## 2. Создать папку контента

```
content/points/mypoint/
    images/          ← логотип (logo.svg или logo.png)
    flags/           ← флаги валют (usd.png, eur.png, ...)
    output/          ← сюда пишется final.jpg (создаётся автоматически)
```

Флаги можно скопировать из `content/points/megapark/flags/` — они общие.  
Если у вас другой логотип, положите его в `images/`.

> При желании вынести флаги в общее место укажите в compose-конфиге `"flagsDir": "../../common/flags"`.

---

## 3. Создать compose-конфиг

Скопируйте `layout/points/megapark.compose.json` → `layout/points/mypoint.compose.json`
и отредактируйте под свой экран:

```jsonc
{
    "pointId": "mypoint",

    // Размер выходного изображения в пикселях:
    "canvas": { "width": 960, "height": 80 },

    "gridLayout": {
        // Множитель оверсемплинга (2 или 4 — выше = лучше + медленнее):
        "oversample": 4,

        // Путь к папке с флагами (относительно корня проекта):
        "flagsDir": "content/points/mypoint/flags",

        // Имя файла логотипа (ищется в sourceDir):
        "logoFile": "logo.svg",

        // Четыре валюты в левой секции (до логотипа):
        "left":  ["USD", "EUR", "RUB", "CNY"],

        // Четыре валюты в правой секции (после логотипа):
        "right": ["GBP", "CHF", "AED", "TRY"],

        // Соответствие кодов валют → имена файлов флагов:
        "flagFiles": {
            "USD": "usd.png",
            "EUR": "eur.png",
            "RUB": "rub.png",
            "CNY": "cny.png",
            "GBP": "gbp.png",
            "CHF": "chf.png",
            "AED": "aed.png",
            "TRY": "try.png"
        }
    },

    // Папка с логотипом и остальными исходниками:
    "sourceDir": "content/points/mypoint/images",

    // Куда сохранять результат:
    "outputFile": "content/points/mypoint/output/final.jpg"
}
```

**Доступные коды валют** (зависит от того, что вернёт API для вашего `depcode`):
`USD EUR RUB CNY GBP CHF AED TRY KGS UZS JPY KRW INR THB VND GOLD1 GOLD5`

Если нужна другая комбинация — просто смените коды в `left` / `right`.

---

## 4. Настроить screen.xml

Если экран новый (другой контроллер), отредактируйте `config/screen.xml`:

```xml
<screen
    unique_identifier="СГЕНЕРИРУЙТЕ_UUID"
    w="960"
    h="80"
    ftp_ip="192.168.ваш.ip"
    ftp_port="21"
    remote_ftp_dir="NET_XXXXXXXXXX">
  <program
      unique_identifier="СГЕНЕРИРУЙТЕ_UUID"
      path="\program_list\program.xml" />
</screen>
```

- `w` / `h` — должны совпадать с `canvas.width` / `canvas.height` из compose-конфига.
- `ftp_ip` — IP, который показывает контроллер в своём Wi-Fi.
- `remote_ftp_dir` — `network_id` контроллера (берётся из его настроек, формат `NET_ХХХХ`).
- UUID можно сгенерировать командой `uuidgen` в терминале.

> Если нужно управлять **несколькими экранами** одновременно, на данный момент запускайте
> отдельный экземпляр сервиса для каждой точки с собственным `appsettings.json`.

---

## 5. Настроить appsettings.json

Скопируйте `appsettings.json` в рабочую директорию новой точки или создайте
`appsettings.Production.json` для переопределения нужных ключей:

```jsonc
{
  "LedUpdater": {
    // Режим работы: Full = рендер + публикация на FTP
    "RunMode": "Full",
    "PublishMode": "WifiFtp",

    // Папка с готовым изображением (откуда Uploader берёт файл):
    "WatchFolder": "content/points/mypoint/output",

    // Конфиг экрана:
    "ScreenXmlPath": "config/screen.xml",

    // Compose-конфиг новой точки:
    "ComposeConfigPath": "layout/points/mypoint.compose.json",

    // Путь к rates.json новой точки:
    "RatesJsonPath": "content/points/mypoint/rates.json",

    // API курсов:
    "RatesApiUrl": "https://api.quiq.kz/Department/getDepsLandingInfo",

    // Название отделения в API (точное совпадение, без учёта регистра):
    "RatesDepCode": "МОЯ ТОЧКА",

    // Интервал обновления курсов (минуты):
    "RatesFetchIntervalMinutes": 10,

    // Интервал цикла Worker (секунды):
    "PollSeconds": 10,

    // FTP-параметры (берутся из screen.xml, но пароль только здесь):
    "FtpUser": "guest",
    "FtpPassword": "guest",
    "FtpPort": 21,
    "UseTls": false,

    "SkipIfUnchanged": true,
    "EnforceWifiOnly": false,
    "RequirePrivateAddress": true
  }
}
```

---

## 6. Запустить

```bash
cd /путь/к/TabloPosterService
dotnet run
```

Или только рендер (без публикации на FTP):

```bash
dotnet run -- --LedUpdater:RunMode=RenderOnly
```

---

## 7. Проверка

Чек-лист после запуска:

| # | Что проверить | Где смотреть |
|---|---------------|--------------|
| 1 | Курсы загружаются | Лог: `rates.json updated` |
| 2 | Изображение собирается | Лог: `Grid board composed` |
| 3 | Файл создан | `content/points/mypoint/output/final.jpg` |
| 4 | Публикация прошла | Лог: `Publish OK` / `FTP upload complete` |
| 5 | Экран показывает картинку | Визуально |

---

## 8. Типовые ошибки

### Курсы не подтягиваются
- Убедитесь, что `RatesDepCode` совпадает с полем `depcode` в ответе API
  (можно проверить: `curl https://api.quiq.kz/Department/getDepsLandingInfo | python3 -m json.tool | grep depcode`).
- Проверьте `RatesApiUrl` на доступность из машины.

### `gridLayout` рисует не те валюты
- Убедитесь, что указанные коды в `left`/`right` есть в `rates.json` после первого фетча.
- Валюты, которых нет в `rates.json`, молча пропускаются.

### Флаг не отображается / пустой квадрат
- Проверьте, что файл `flags/xxx.png` существует и совпадает с ключом в `flagFiles`.

### FTP не подключается
- Проверьте `ftp_ip` в `screen.xml` — должен быть IP контроллера, к которому подключён ПК.
- Убедитесь, что `EnforceWifiOnly: false` если используете проводное соединение.

### Логотип не отображается / чёрный квадрат
- Если логотип в формате SVG, убедитесь, что установлен `inkscape` или `rsvg-convert`:
  ```bash
  brew install inkscape        # macOS
  apt install inkscape         # Ubuntu/Debian
  ```
- Либо конвертируйте логотип в PNG и укажите `"logoFile": "logo.png"`.

---

## 9. Структура файлов для новой точки (итого)

```
TabloPosterService/
├── appsettings.json                         ← обновить ключи LedUpdater
├── config/
│   └── screen.xml                           ← настроить под новый контроллер
├── layout/points/
│   └── mypoint.compose.json                 ← СОЗДАТЬ
└── content/points/
    └── mypoint/
        ├── images/
        │   └── logo.svg                     ← ДОБАВИТЬ логотип
        ├── flags/
        │   ├── usd.png                      ← ДОБАВИТЬ флаги
        │   └── ...
        └── output/                          ← создаётся автоматически
```
