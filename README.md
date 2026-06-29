# LedImageUpdaterService

Полная документация по сервису генерации и публикации изображения для LED-экрана.

## Windows migration pack

For Windows onboarding and deployment handover, see:

- migration-pack/windows/README.md
- migration-pack/windows/verify-windows-env.ps1
- migration-pack/windows/publish-win-package.ps1

Сервис написан на .NET 8 и работает как фоновый Worker.
Он может:
1. Рендерить финальную картинку курса валют из шаблона и данных.
2. Обновлять курсы из API (quiq.kz) по расписанию.
3. Публиковать итоговый файл на LED-контроллер (по FTP) или складывать payload в relay-папку.

## 1. Что делает сервис (от А до Я)

В проекте работают два фоновых процесса:

1. `RatesFetcherService`
- Каждые `RatesFetchIntervalMinutes` минут делает `GET` в API.
- Ищет отделение по `depcode` (например, `MEGA PARK`).
- Берет активные валюты и записывает их в `rates.json`.
- Поля API: `buy` и `sale` (в `rates.json` сохраняется как `buy` и `sell`).

2. `Worker`
- В режиме `RenderOnly` следит за изменениями входных файлов (`compose.json`, папка source image, `rates.json`).
- Если вход изменился, запускает `DotnetComposer` и собирает `final.jpg`.
- В режиме `Uploader` берет самый новый файл из `WatchFolder` и публикует на контроллер.

## 2. Архитектура компонентов

- `Program.cs`: регистрация сервисов, DI, запуск хоста.
- `Models/ServiceOptions.cs`: все настройки секции `LedUpdater`.
- `Services/RatesFetcherService.cs`: загрузка курсов из API и обновление `rates.json`.
- `Services/DotnetComposer.cs`: нативный .NET рендер 580x80 JPEG (ImageSharp).
- `Services/RenderOnlyRunner.cs`: проверка путей и запуск композитора.
- `Services/ScreenModelReader.cs`: чтение параметров экрана из `screen.xml`.
- `Services/LedPayloadBuilder.cs`: генерация XML payload (program/list) и имен файлов.
- `Services/FtpPublisher.cs`: прямая отправка payload на LED-контроллер по FTP.
- `Services/RelayPublisher.cs`: локальная сборка payload в relay-директорию.

## 3. Режимы работы

### 3.1 RunMode = RenderOnly

Назначение: только генерация финального изображения.

Что происходит:
1. `RatesFetcherService` обновляет `rates.json` из API.
2. `Worker` видит изменение входных данных.
3. `DotnetComposer` собирает `content/points/.../output/final.jpg`.

Публикации на FTP в этом режиме нет.

### 3.2 RunMode = Uploader

Назначение: отправка изображения на экран.

Что происходит:
1. `Worker` берет самый новый файл из `WatchFolder` (`jpg/jpeg/png/bmp`).
2. Читает параметры контроллера из `screen.xml`.
3. Генерирует XML (`list`, `program`) и имена файлов.
4. В зависимости от `PublishMode`:
- `WifiFtp`: загружает напрямую на контроллер.
- `WifiRelay`: пишет такую же структуру локально в `RelayOutRoot`.

## 4. Требования для старта

## 4.1 ПО

- .NET SDK 8.0+
- Доступ к интернету (для API курсов)
- Доступ к сети контроллера (для режима `WifiFtp`)

Дополнительно (опционально, только если используете SVG-логотип):
- `inkscape` или `rsvg-convert` или `sips` в PATH

## 4.2 NuGet зависимости

Подтягиваются автоматически из `.csproj`:
- `FluentFTP`
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Options.DataAnnotations`
- `SixLabors.ImageSharp`
- `SixLabors.ImageSharp.Drawing`

## 4.3 Структура папок (минимум)

Пример для точки `megapark`:

```text
content/points/megapark/
	images/
		logo.svg
	flags/
		usd.png eur.png ...
	output/
	rates.json

layout/points/
	megapark.compose.json
```

## 5. Главный конфиг: appsettings.json

Все параметры находятся в секции `LedUpdater`.

```json
{
	"LedUpdater": {
		"RunMode": "RenderOnly",
		"PublishMode": "WifiFtp",
		"WatchFolder": "...",
		"ScreenXmlPath": "...",
		"PollSeconds": 10,
		"FtpUser": "guest",
		"FtpPassword": "guest",
		"UseTls": false,
		"FtpPort": 21,
		"SkipIfUnchanged": true,
		"ForceRemoteRoot": "NET_00000199",
		"EnforceWifiOnly": true,
		"RequirePrivateAddress": true,
		"RelayOutRoot": ".../LedYQ/ftp_temp",
		"ComposeConfigPath": "layout/points/megapark.compose.json",
		"RatesJsonPath": "content/points/megapark/rates.json",
		"RatesApiUrl": "https://api.quiq.kz/Department/getDepsLandingInfo",
		"RatesDepCode": "MEGA PARK",
		"RatesFetchIntervalMinutes": 10
	}
}
```

Описание ключей:

- `RunMode`: `RenderOnly` или `Uploader`.
- `PublishMode`: `WifiFtp` или `WifiRelay`.
- `WatchFolder`: входная папка с картинками для `Uploader`.
- `ScreenXmlPath`: путь к `screen.xml`.
- `PollSeconds`: интервал цикла `Worker` (5..3600).
- `FtpUser`/`FtpPassword`/`FtpPort`/`UseTls`: FTP-настройки.
- `SkipIfUnchanged`: пропускать цикл, если вход не менялся.
- `ForceRemoteRoot`: принудительный корень на контроллере.
- `EnforceWifiOnly`: публиковать только при Wi-Fi маршруте.
- `RequirePrivateAddress`: блокировать публикацию на публичный IP.
- `RelayOutRoot`: корень для relay-режима.
- `ComposeConfigPath`: путь к compose-конфигу.
- `RatesJsonPath`: путь к json курсов.
- `RatesApiUrl`: URL API курсов.
- `RatesDepCode`: код/имя точки в API (`depcode`).
- `RatesFetchIntervalMinutes`: период обновления курсов.

## 6. Входные данные (обязательно)

## 6.1 Compose config (`layout/points/<point>.compose.json`)

Пример рабочего файла:

```json
{
	"pointId": "megapark",
	"canvas": { "width": 580, "height": 80 },
	"gridLayout": {
		"oversample": 4,
		"flagsDir": "../flags",
		"logoFile": "logo.svg",
		"left":  ["USD", "EUR", "RUB", "CNY"],
		"right": ["GBP", "CHF", "AED", "TRY"],
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
	"sourceDir": "content/points/megapark/images",
	"outputFile": "content/points/megapark/output/final.jpg"
}
```

Важно:
- `left` и `right` используются для вывода на табло.
- Даже если API вернул больше валют, отрисуются только те, что указаны в `left/right`.
- `outputFile` должен указывать на существующую или создаваемую папку.

## 6.2 Rates JSON (`content/points/<point>/rates.json`)

Файл обновляется сервисом автоматически, но структура должна быть такой:

```json
{
	"labels": {
		"buy": ["Сатып аламыз", "Покупаем", "We buy"],
		"sell": ["Сатамыз", "Продаем", "We sell"]
	},
	"currencies": {
		"USD": { "buy": 463.1, "sell": 467.1 },
		"EUR": { "buy": 537.0, "sell": 547.0 }
	}
}
```

Важно:
- `labels` сервис старается сохранить при обновлении.
- `currencies` перезаписывается на основе API.

## 6.3 Screen XML (для Uploader)

Из `screen.xml` читаются обязательные атрибуты:
- `<screen>`: `unique_identifier`, `w`, `h`, `ftp_ip`, `ftp_port`, `remote_ftp_dir`
- `<program>` внутри `<screen>`: `unique_identifier`, `path`

При отсутствии любого обязательного атрибута сервис падает с явной ошибкой.

## 7. Как запустить (пошагово)

Важно: запускайте из папки проекта `LedImageUpdaterService`.

```bash
cd /Users/mr.kadyroff/Documents/led/LedshowYQ/LedImageUpdaterService
dotnet restore
dotnet build
dotnet run
```

Альтернатива из корня репозитория:

```bash
cd /Users/mr.kadyroff/Documents/led/LedshowYQ
dotnet run --project LedImageUpdaterService/LedImageUpdaterService.csproj
```

Если запускать из корня без `--project`, команда завершится ошибкой, потому что там нет `.csproj` по умолчанию.

## 8. Логика обновления курсов

Алгоритм `RatesFetcherService`:

1. Делает запрос в `RatesApiUrl`.
2. Десериализует массив отделений.
3. Ищет отделение: `depcode == RatesDepCode` (без учета регистра).
4. Берет только активные валюты (`isActive=true`).
5. Для каждой валюты берет первую запись из `rateList`.
6. Поля `buy/sale` записывает как `buy/sell` в `rates.json`.
7. Пишет файл атомарно: сначала `rates.json.tmp`, потом rename с overwrite.

Периодичность:
- Первый fetch сразу при старте.
- Далее каждые `RatesFetchIntervalMinutes` минут.

## 9. Логика рендера

Алгоритм `RenderOnly`:

1. Сравнивает время изменения входов (`compose.json`, `sourceDir`, `rates.json`).
2. Если `SkipIfUnchanged=true` и вход не изменился, пропускает цикл.
3. Иначе собирает `final.jpg`.

Параметры текущего шаблона (megapark):
- Финал: `580x80`
- Oversample: `4x`
- Фон: черный
- Текст: белый
- Две секции курсов по 4 строки + логотип по центру

## 10. Логика публикации (Uploader)

В `WifiFtp` отправляются:

- `/{RemoteRoot}/share/{image}`
- `/{RemoteRoot}/programs/program_0/{programXml}`
- `/{RemoteRoot}/lists/{listXml}`

В `WifiRelay` создается локальная структура:
- `{RelayOutRoot}/{ScreenId}/share/...`
- `{RelayOutRoot}/{ScreenId}/programs/program_0/...`
- `{RelayOutRoot}/{ScreenId}/lists/...`
- `{RelayOutRoot}/{ProgramId}/...` (копия изображения)

## 11. Проверка, что все работает

Минимальный чек-лист:

1. В логах есть строка `Fetching rates from ...`.
2. В логах есть строка `rates.json updated`.
3. В `content/points/megapark/rates.json` меняются значения.
4. В логах есть `Grid board composed`.
5. Файл `content/points/megapark/output/final.jpg` обновляется по времени.

## 12. Типовые проблемы и решения

1. Курсы не обновляются
- Проверьте `RatesApiUrl` и `RatesDepCode`.
- Проверьте, что приложение реально запущено (`dotnet run`).
- Ищите в логах `Rates fetch failed`.

2. `dotnet run --no-build` из корня не стартует
- Запускайте из папки `LedImageUpdaterService` или указывайте `--project`.

3. Рендер не запускается
- Проверьте существование `ComposeConfigPath` и `RatesJsonPath`.
- Проверьте `sourceDir` в compose-конфиге.

4. Не грузится SVG логотип
- Установите `inkscape` или `rsvg-convert`.
- Либо используйте PNG вместо SVG.

5. FTP публикация не проходит
- Проверьте `screen.xml` (`ftp_ip`, `ftp_port`, `remote_ftp_dir`).
- Проверьте `FtpUser`/`FtpPassword`.
- При необходимости отключите TLS (`UseTls=false`, если контроллер без TLS).

## 13. Быстрые рабочие профили

### Профиль A: только рендер + API

- `RunMode = RenderOnly`
- `RatesFetchIntervalMinutes = 10`
- Результат: обновляется `rates.json` и `final.jpg`.

### Профиль B: загрузка на контроллер

- `RunMode = Uploader`
- `PublishMode = WifiFtp`
- `WatchFolder` указывает на папку с готовыми изображениями.

### Профиль C: relay режим

- `RunMode = Uploader`
- `PublishMode = WifiRelay`
- `RelayOutRoot` обязателен.

## 13.1 Боевой профиль (aport2, "запустил и работает")

Минимум для прод-старта:

1. В `appsettings.json`:
- `ActivePointId = "aport2"`
- `LedUpdater.RunMode = "RenderOnly"`
- `LedUpdater.LayoutTestMode = false`
- `OnbonLed.AutoSend = true`
- `OnbonLed.PollSeconds = 10`
- `LedUpdater.WatchFolder = "content/points/aport2/output"`

2. В `config/points/aport2.json`:
- `OnbonLed.ControllerIp = "192.168.22.2"`
- `OnbonLed.ControllerPort = 80`
- `OnbonLed.ScreenWidth = 128`
- `OnbonLed.ScreenHeight = 256`
- `LedUpdater.ComposeConfigPath = "layout/points/aport2.compose.json"`
- `LedUpdater.RatesJsonPath = "content/points/aport2/rates.json"`

3. Проверить файлы:
- `layout/points/aport2.compose.json`
- `layout/points/index.json` (должен быть `depCode: "АПОРТ2"`)
- `content/common/flags/*` (все нужные флаги)

4. Запуск:
- `dotnet build -c Release`
- `dotnet run`

5. Проверка после старта:
- в логе есть `rates.json обновлён`
- в логе есть `composed` для `final.jpg`
- в логе есть `Image sent successfully`
- API диагностики: `/api/led/connection`, `/api/led/timer-status`, `/api/led/diagnostics`

## 14. Команды эксплуатации

Сборка:

```bash
dotnet build -c Release
```

Запуск:

```bash
dotnet run
```

Публикация бинаря:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

---

Если нужна отдельная версия документации под Windows-эксплуатацию (служба, автозапуск, ротация логов, watchdog), добавьте второй файл, например `README-WINDOWS.md`, на базе этого документа.
