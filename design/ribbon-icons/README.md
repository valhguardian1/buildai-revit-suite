# BuildAI — иконки ленты Revit (2026-09-25)

Два варианта одного набора. Нужно выбрать один:
- `dark/`: белый глиф, акцент #3186F6, обводка 1 px тёмно-серая #2B2F36
- `blue/`: белый глиф, акцент #3186F6, обводка 1 px фирменная синяя #3186F6

В каждом варианте:
- `png16/`, `png32/`: стандартные размеры для ленты Revit (Image / LargeImage), PNG с прозрачностью, 96 dpi.
- `png64-hidpi/`: 64×64 с метаданными 192 dpi. WPF покажет такую картинку в 32 логических px, но с чётким рендером на экранах 150–200%. Можно подставить вместо LargeImage.
- `svg/`: исходники. `*16.svg` нарисованы на 16-пиксельной сетке, `*32.svg` на 32-пиксельной.
- `drop-in/<Plugin>/`: готовые к замене файлы в структуре проекта.

## ВАЖНО: как применить
`RibbonIconLoader.Load()` сначала берёт иконку из `Revit/EmbeddedIconData.cs` (base64) и только потом из `Resources/icons/*.png`.
**Если заменить только PNG, в Revit ничего не изменится.** Нужно заменить оба файла:
- `src/<Plugin>/Resources/icons/*.png`
- `src/<Plugin>/Revit/EmbeddedIconData.cs` (base64 пересобран, ключи прежние, `buildai32` (логотип) не тронут)

Plugin1.TimeModelAnalytics не затронут: у него нет кнопок на ленте.

## Новые иконки
Ключи `support` и `syncback` уже добавлены в EmbeddedIconData всех четырёх плагинов. Кнопки нужно добавить в `Application.cs`, например:
```csharp
RibbonIconLoader.AddButton(panel, new PushButtonData("BuildAI_Support", Loc.T("Btn_Support"), asm,
    "Plugin5.ClashFormaIntegration.Revit.OpenSupportChatCommand"), "support");
RibbonIconLoader.AddButton(panel, new PushButtonData("BuildAI_SyncBack", Loc.T("Btn_SyncBack"), asm,
    "Plugin5.ClashFormaIntegration.Revit.SyncBackCommand"), "syncback");
```

## Редактирование
Всё генерируется из `gen.py` (геометрия каждой иконки для сеток 16 и 32). `build.py` собирает PNG, SVG и drop-in.
Зависимости: `pip install cairosvg pillow`.
