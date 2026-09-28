# Задача для Codex: применить новые иконки BuildAI (лента Revit, логотип, установщик)

> Этот файл — инструкция для агента (Codex) и для разработчика. Выполняй шаги по порядку.
> Не рисуй и не генерируй иконки сам: все готовые файлы лежат в пакете `BuildAI-ribbon-icons/`.
> Не изменяй логику плагинов сверх описанного ниже.

## 0. Входные данные

- Репозиторий: корень сборки плагина (там, где лежат `src/`, `installer/`, `tools/`), например `BuildAI-7.x.x/`.
- Пакет иконок: папка `BuildAI-ribbon-icons/`. Скопируй её в репозиторий как `design/ribbon-icons/`. Дальше в тексте она называется `$PKG`.
- **Вариант обводки: `VARIANT`**, либо `dark` (тёмно-серая #2B2F36), либо `blue` (синяя #3186F6).
  Если вариант не указан в задаче, **остановись и спроси**. Не выбирай сам.

Структура пакета (только то, что нужно):

```
$PKG/
  <VARIANT>/drop-in/
    Plugin1.TimeModelAnalytics/Resources/icons/*.png
    Plugin2.VolumeEstimator/{Resources/icons/*.png, Revit/EmbeddedIconData.cs}
    Plugin3.LinkChangeMonitor/{Resources/icons/*.png, Revit/EmbeddedIconData.cs}
    Plugin4.LinkComparatorAI/{Resources/icons/*.png, Revit/EmbeddedIconData.cs}
    Plugin5.ClashFormaIntegration/{Resources/icons/*.png, Revit/EmbeddedIconData.cs}
  <VARIANT>/png16, png32, png64-hidpi, svg   — полный набор (справочно)
  logo/buildai16.png, buildai32.png, buildai64.png, buildai512.png
  installer/BuildAI.ico, BuildAI_Setup.ico, BuildAI.png, BuildAI_Setup.png
  gen.py, logo.py, build.py, build2.py       — генераторы (в сборку НЕ включать)
```

## 1. Ключевой факт о загрузке иконок

`src/Plugin{2..5}/Revit/RibbonIconLoader.Load(name, size)` ищет иконку в таком порядке:
1. `EmbeddedIconData.TryGet(name + size)`: base64 в `Revit/EmbeddedIconData.cs`. **Это главный источник.**
2. Встроенный ресурс `Resources.icons.{name}{size}.png`.
3. Файл `Resources/icons/{name}{size}.png` рядом с DLL.

Поэтому **обязательно заменить оба файла**: `EmbeddedIconData.cs` и PNG. Если заменить только PNG, в Revit ничего не изменится.

## 2. Ribbon-иконки Plugin2–Plugin5

Для каждого `P` из `Plugin2.VolumeEstimator`, `Plugin3.LinkChangeMonitor`, `Plugin4.LinkComparatorAI`, `Plugin5.ClashFormaIntegration`:

```bash
cp -f  "$PKG/$VARIANT/drop-in/$P/Revit/EmbeddedIconData.cs"   "src/$P/Revit/EmbeddedIconData.cs"
cp -f  "$PKG/$VARIANT/drop-in/$P/Resources/icons/"*.png      "src/$P/Resources/icons/"
```

Что содержат новые `EmbeddedIconData.cs`:
- namespace и API (`TryGet`) **не менялись**, прежние ключи сохранены, изменились только base64-строки;
- добавлены ключи: `support16/32`, `syncback16/32`, `buildai16`;
- `buildai32` (логотип в шапке `ResultsWindow`) пересобран с прозрачным фоном. Старый был сведён на чёрный.

Проверка:
- `git diff --stat src/*/Revit/EmbeddedIconData.cs`: меняются только строки словаря `Data`;
- в каждом `EmbeddedIconData.cs` namespace совпадает с именем проекта (`namespace Plugin4.LinkComparatorAI.Revit` и т. п.).

Соответствие кнопок и ключей (менять не нужно, справочно):

| Плагин | Кнопка | Ключ |
|---|---|---|
| P2 Volumes | Recalculate / Open in BuildAI / Connect | `recalculate` / `open` / `connect` |
| P3 Link Monitor | Check links / Changes | `checklinks` / `changes` |
| P4 Comparator | Compare / Rooms / Results / Settings | `compare` / `rooms` / `results` / `settings` |
| P5 Clash | Find clashes / Results / Create views / Settings | `clash` / `results` / `createview` / `settings` |
| все | иконка плагина | `ribbonicon` (P2 = results, P3 = checklinks, P4 = compare, P5 = clash) |

## 3. Plugin1.TimeModelAnalytics

У Plugin1 нет `RibbonIconLoader` и `EmbeddedIconData`, а `BuildRibbon()` пустой: кнопок на ленте нет. Поэтому только заменяем заглушки 1×1 и кладём набор иконок про запас:

```bash
cp -f "$PKG/$VARIANT/drop-in/Plugin1.TimeModelAnalytics/Resources/icons/"*.png \
      "src/Plugin1.TimeModelAnalytics/Resources/icons/"
```

Файлы: `ribbonicon16/32` (секундомер), `analytics`, `session`, `pause`, `resume`, `connect` в размерах 16 и 32.
**Код Plugin1 не менять.** Если позже появятся кнопки (строки `Btn_Pause`, `Btn_Resume`, `Btn_Status`, `Btn_Connect` уже есть в `Strings.resx`), скопировать `RibbonIconLoader.cs` из Plugin2 и заменить namespace. Отсутствие `EmbeddedIconData` не мешает: сработает загрузка из файла.

## 4. Новые иконки `support` и `syncback`: только ассеты

Ключи уже есть в `EmbeddedIconData.cs` всех плагинов. **Кнопки на ленту в рамках этой задачи не добавлять**: нужны команды и URL, которых пока нет.
Когда команды появятся, подключить так (пример для P5):

```csharp
RibbonIconLoader.AddButton(panel, new PushButtonData(
    "BuildAI_Support", Loc.T("Btn_Support"), asm,
    "Plugin5.ClashFormaIntegration.Revit.OpenSupportChatCommand"), "support");
RibbonIconLoader.AddButton(panel, new PushButtonData(
    "BuildAI_SyncBack", Loc.T("Btn_SyncBack"), asm,
    "Plugin5.ClashFormaIntegration.Revit.SyncBackCommand"), "syncback");
```

Не забыть строки `Btn_Support` и `Btn_SyncBack` в `Strings.resx` и `Strings.he.resx`.

## 5. Установщик Windows

```bash
cp -f "$PKG/installer/BuildAI.ico"        "installer/assets/BuildAI.ico"
cp -f "$PKG/installer/BuildAI_Setup.ico"  "installer/assets/BuildAI_Setup.ico"
cp -f "$PKG/installer/BuildAI.png"        "installer/assets/BuildAI.png"
```

В `installer/BuildAI.iss`, секция `[Setup]`, заменить одну строку:

```diff
-SetupIconFile=assets\BuildAI.ico
+SetupIconFile=assets\BuildAI_Setup.ico
```

Остальное не трогать: `UninstallDisplayIcon={app}\BuildAI.ico`, `[Files]` и `[Icons]` продолжают использовать `BuildAI.ico` (он уже заменён новым).
`installer/BuildAI.RevitSuite.wxs` (`<Icon ... SourceFile="assets\BuildAI.ico">`) и `tools/Build-StandaloneUninstaller.ps1` ссылаются на `assets\BuildAI.ico`, и их менять не нужно.

Оба ICO содержат размеры 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 (PNG внутри ICO, 32-bit RGBA). Их поддерживают Inno Setup 6 и WiX.

`wizard.bmp` и `wizard-small.bmp` в этой задаче не меняются.

## 6. Проверка csproj

Для каждого плагина убедиться, что новые PNG попадают в сборку так же, как старые:
- открыть `src/<P>/<P>.csproj` и найти, как подключены `Resources/icons/*.png` (`EmbeddedResource`, `Content`/`None` с `CopyToOutputDirectory`, или wildcard);
- если PNG перечислены поимённо, добавить новые файлы по тому же образцу: `support16.png`, `support32.png`, `syncback16.png`, `syncback32.png`, `buildai16.png`, `buildai32.png`, а для Plugin1 ещё `analytics*`, `session*`, `pause*`, `resume*`, `connect*`;
- если используется wildcard, ничего не менять.

Папку `design/ribbon-icons/` в сборку и инсталлятор **не включать**.

## 7. Сборка и приёмка

1. Собрать решение для всех целевых версий Revit (2023–2026) обычным скриптом (`build.ps1`).
2. Проверить в Revit на светлой и тёмной теме (Revit 2024+):
   - все кнопки вкладки BuildAI показывают новые иконки, 32 px на больших кнопках и 16 px на малых;
   - в окне результатов Link Comparator логотип в шапке **без чёрного квадрата**.
3. Собрать установщик (`ISCC.exe installer\BuildAI.iss`) и проверить:
   - иконку `BuildAI_RevitPlugins_Setup.exe` в Проводнике (плитка со значком загрузки);
   - иконку программы в «Параметры → Приложения» (плитка без значка).
4. Поднять версию (например, 7.x.y → 7.x.y+1) в `BuildAI.iss` (`#define AppVersion`) и там, где проект хранит версию сборок, по принятой в репозитории схеме.

## 8. Коммит

Один коммит или PR, например:
`chore(icons): new ribbon icons (<VARIANT>), transparent brand logo, Windows installer icons`

В описании PR перечислить: выбранный `VARIANT`; заменённые `EmbeddedIconData.cs` (4 шт.); добавленные ключи `support`, `syncback`, `buildai16`; изменение `SetupIconFile`.

## 9. Если что-то пошло не так

- Иконки не поменялись: проверь, что заменён именно `Revit/EmbeddedIconData.cs` нужного плагина и что пересобрана именно эта DLL.
- Иконка размыта или крупная: PNG сохранены с 96 dpi, это важно для WPF. Не пересохраняй их графическими редакторами.
- Нужно перерисовать иконку: правка в `$PKG/gen.py` (геометрия), затем `python build.py && python build2.py` (нужны `cairosvg`, `pillow`, `opencv-python`), затем повторить шаги 2–5. Перед запуском в `build.py` заменить константу `SRC` на путь к `src/` репозитория (оттуда берутся исходные `EmbeddedIconData.cs`), а в `logo.py` заменить `SRC` на путь к `B_Logo_Icon.png`.
