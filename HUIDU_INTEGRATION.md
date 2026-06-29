# Huidu (HDPlayer) контроллер — BX A3L

Сервис поддерживает два семейства LED‑контроллеров. Выбор — параметром `Led:Family`:

| Family  | Транспорт                          | Конфиг        | Модели                |
|---------|------------------------------------|---------------|-----------------------|
| `Onbon` | нативный `YQNetCom.dll` (P/Invoke) | `OnbonLed`    | BX‑Y04…BX‑C08 (как было) |
| `Huidu` | Huidu SDK (XML поверх TCP)         | `HuiduLed`    | **BX A3L** (HDPlayer)  |

Onbon‑путь **не изменён**: при `Led:Family = "Onbon"` всё работает ровно как раньше,
класс `HuiduLedController` и его TCP‑сервер вообще не создаются.

Оба семейства получают **один и тот же полноэкранный рендер** точки (`final.jpg`);
отличается только способ доставки на табло. Логика курсов/рендера общая.

## Архитектура

- `Services/ILedController.cs` — общий интерфейс (send image, clear, status, power…).
- `Services/OnbonLedController.cs` — реализация Onbon (без изменений логики, добавлен `: ILedController`).
- `Services/HuiduLedController.cs` — реализация Huidu.
- `Services/HuiduSdkClient.cs` — чистый порт официального протокола Huidu
  (`Huidu_CSharp_SDK/`, репозиторий github.com/huidutech/sdk): handshake, чтение GUID,
  загрузка файла, XML‑команды, плюс TCP‑сервер `HuiduSdkServer`.
- `Models/LedControllerOptions.cs` — переключатель семейства (`Led:Family`).
- `Models/HuiduOptions.cs` — настройки Huidu (`HuiduLed`).
- `config/huidu/program-template.xml` — шаблон программы (метод `SetPrograms`).

Каталог `Huidu_CSharp_SDK/` лежит рядом как референс и **исключён из компиляции**
(как `BX_Y_CSharp_SDK/`); протокол переписан в `Services/HuiduSdkClient.cs`.

## Модель подключения (важно)

> **Обновлено:** реальный протокол HDPlayer определён по дампу Wireshark (`wirreshark.txt`)
> и реализован в `Services/HuiduHdPlayerClient.cs`. Старый `HuiduSdkClient.cs`
> (XML, режим сервера) больше **не** используется для отправки — оставлен только как
> референс для диагностики.

Карта BX A3L (прошивка HDPlayer) — это **TCP‑сервер**, а ПК — **клиент**. То есть
сервис сам **подключается к карте** на `CardIp:CardPort` (порт `10001`). В режиме
Wi‑Fi точки доступа карта является шлюзом (например `192.168.43.1`), и ПК должен быть
подключён к её Wi‑Fi (SSID вида `A3L-25-…`). Поэтому:

1. Подключите ПК к Wi‑Fi карты (или к одной сети с ней).
2. В `HuiduLed.CardIp` укажите адрес карты (в AP‑режиме обычно `192.168.43.1`).
   Можно оставить пустым — тогда адрес ищется UDP‑broadcast’ом по порту **9527**.
3. `HuiduLed.CardPort` = `10001` (по умолчанию).
4. Проверка связи — `GET /api/led/connection`: сервис открывает TCP к карте и проходит
   version‑handshake.

## Как отправляется картинка

Протокол — JSON поверх TCP (кадр: `[len u16][type u8][0x21][jsonLen u32][reserved u32][json]`):

1. Сервис подключается к карте и шлёт version‑handshake.
2. Отправляет команду `PlayTask` (JSON) — одна программа / одна зона на весь экран,
   ссылающаяся на изображение по `md5`/`size`/`name` и описывающая `screenSize`.
3. Карта отвечает `kTaskAccepted`, после чего идёт под‑протокол синхронизации файлов
   (`0x80fc` begin → `0x8001` «есть такой md5?» → если нет, стрим `0x8003` → `0x8005` end →
   `0x80fe` finish). Карта подгружает только недостающие байты (по md5 — повторные
   отправки того же файла не перезаливаются).
4. Карта отвечает `kSuccess` — картинка на табло.

Схема `PlayTask` зашита в `HuiduHdPlayerClient.SendFullScreenImage` (значения эффекта,
`screenSize`, `frame` зоны и т. п. сняты один в один с дампа). Размер экрана берётся из
`HuiduLed.ScreenWidth/Height` (по дампу — **1216×192**); под него же должен рендериться
`final.jpg`.

## Настройка `HuiduLed`

```jsonc
"Led":      { "Family": "Huidu" },          // переключение на Huidu
"HuiduLed": {
  "Enabled": true,
  "CardIp": "",                              // адрес карты (AP‑режим: 192.168.43.1); пусто = автопоиск (UDP 9527)
  "CardPort": 10001,                         // TCP‑порт карты (HDPlayer)
  "DeviceId": "",                            // фильтр по id карты при автопоиске (пусто = любая)
  "ScreenWidth": 1216, "ScreenHeight": 192,  // размер экрана (по дампу Wireshark)
  "IoTimeoutMs": 10000,
  "SkipDuplicateUploads": true,
  "RejectSizeMismatchBeforePublish": false
}
```

Можно переопределить семейство на точку: положите `"Led": { "Family": "Huidu" }`
в `config/points/{point}.json`.

В трее (Настройки → вкладка «Подключение») добавлен выбор **«Семейство (подключение)»**:
`Onbon BX-Y` / `Huidu / HDPlayer (BX A3L)`.

## Что поддержано на Huidu‑транспорте

- ✅ Отправка полноэкранного изображения (основной сценарий, как у Onbon).
- ✅ Проверка соединения (`/api/led/connection`), очистка экрана (best‑effort).
- ⚠️ Яркость / питание / reboot / чтение статуса‑прошивки — не реализованы для Huidu
  (API возвращает понятный отказ). При необходимости добавляются по аналогии через
  методы Huidu SDK (`SetLuminancePloy`, `SetSwitchTime` и т.п. — см. `Huidu_CSharp_SDK/`).
