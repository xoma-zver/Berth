# Оформление и стилизация

Как настроить внешний вид Berth под приложение. Документ описывает **контракт стилизации** —
публичную поверхность, на которую приложение вправе опираться: токены оформления
(`BerthThemeKeys`), PART-имена и псевдоклассы. Всё остальное во внутреннем визуальном дереве
контролов — детали реализации, меняющиеся без предупреждения.

Границы контракта заданы спекой: лист-хром (кнопки полос, заголовки вкладок, меню, сплиттеры)
пересоздаётся при любой смене состояния (TW-9.13, DA-9.6) — стилизация обязана быть
декларативной (токены, селекторы по псевдоклассам), а не удержанием ссылок на конкретные
экземпляры. Персистентные узлы (хосты панелей и вкладок) сознательно не шаблонизируются:
их однократное создание — основа гарантий сохранения фокуса и view-state.

## Токены оформления

Каждая кисть, которой рисуется встроенное оформление, резолвится через публичный ключ ресурса
(`BerthThemeKeys`, пространство имён `Berth.Controls`) по штатному поиску ресурсов Avalonia:
от контрола вверх по логическому дереву до ресурсов приложения, включая словари вариантов темы.
Переопределение — обычный ресурс под этим ключом на любом уровне; подключать тему-пакет не
нужно: ключ без ресурса получает встроенный дефолт. Значения живые (семантика
`DynamicResource`): подмена ресурса в рантайме и переключение варианта темы подхватываются
без пересборки.

| Константа | Ключ ресурса | Роль | Дефолт |
|---|---|---|---|
| `Pane` | `BerthPaneBrush` | фон хрома: полосы иконок, заголовки панелей, полосы вкладок | `#14808080` |
| `Separator` | `BerthSeparatorBrush` | разделители, сплиттеры, рамки | `#50808080` |
| `OpenIcon` | `BerthOpenIconBrush` | подсветка открытой иконки на полосе и активной вкладки группы | `#40808080` |
| `ActiveHeader` | `BerthActiveHeaderBrush` | акцент заголовка активной панели и вкладки текущего документа | `#30808080` |
| `DropMarker` | `BerthDropMarkerBrush` | маркер вставки целей перетаскивания | `#B0808080` |
| `DropAreaPreview` | `BerthDropAreaPreviewBrush` | полупрозрачное превью зоны вставки и заглушка перестановки вкладок | `#38808080` |
| `OverlaySurface` | `BerthOverlaySurfaceBrush` | непрозрачная подложка оверлеев: Undock-оверлей, псевдоокна, плашки жестов | светлая `#F7F8FA` / тёмная `#1E1F22` |

`OverlaySurface` обязан оставаться непрозрачным: панели под оверлеем не должны просвечивать
(TW-3.3, TW-7.7). Его дефолт зависит от варианта темы; переопределение обычно задаётся
по-вариантно через `ThemeDictionaries`.

Пример — переопределение на уровне приложения:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
      <ResourceDictionary x:Key="Light">
        <SolidColorBrush x:Key="BerthPaneBrush" Color="#F2F3F5"/>
        <SolidColorBrush x:Key="BerthOverlaySurfaceBrush" Color="#FFFFFF"/>
      </ResourceDictionary>
      <ResourceDictionary x:Key="Dark">
        <SolidColorBrush x:Key="BerthPaneBrush" Color="#26282B"/>
        <SolidColorBrush x:Key="BerthOverlaySurfaceBrush" Color="#1B1D21"/>
      </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
    <!-- невариантные токены — обычными ресурсами -->
    <SolidColorBrush x:Key="BerthDropMarkerBrush" Color="#806C5CE7"/>
  </ResourceDictionary>
</Application.Resources>
```

Из кода ключи доступны константами: `Resources[BerthThemeKeys.Pane] = new SolidColorBrush(…)`.

## Токены размеров

Размеры хрома настраиваются той же техникой; значение ресурса — число с плавающей точкой
(`x:Double` в AXAML, `double` из кода — целое значение не подойдёт):

| Константа | Ключ ресурса | Роль | Дефолт |
|---|---|---|---|
| `StripeWidth` | `BerthStripeWidth` | ширина полосы иконок | 36 |
| `StripeButtonSize` | `BerthStripeButtonSize` | сторона квадратной иконки полосы; ею же меряется маркер вставки в полосу | 28 |
| `HeaderHeight` | `BerthHeaderHeight` | высота заголовка панели и титула псевдоокна документов | 28 |
| `TabStripHeight` | `BerthTabStripHeight` | высота полосы вкладок группы | 28 |
| `SplitterThickness` | `BerthSplitterThickness` | толщина сплиттеров | 4 |

```xml
<Application.Resources>
  <x:Double x:Key="BerthHeaderHeight">32</x:Double>
  <x:Double x:Key="BerthStripeWidth">44</x:Double>
</Application.Resources>
```

Не токенизированы сознательно: минимальные размеры зон (`MinPaneSize` — рендерный клемп
TW-2.8) и поведенческие константы жестов (порог начала перетаскивания, доля клина, предел
миниатюры) — это поведение, закреплённое спекой, а не оформление.

## PART-имена

Внутренние контролы носят стабильные имена с префиксом `PART_` — опоры селекторов стилей,
тестов и инструментов. Закреплённый набор (каждое имя покрыто headless-тестами):

**Каркас рабочей области**
`PART_LeftStripe`, `PART_RightStripe` — полосы иконок; `PART_StripeSeparator` — разделитель
сегментов; `PART_QuickAccess` — кнопка «⋯»; `PART_LeftPane`, `PART_RightPane`,
`PART_BottomPane` — доки сторон; `PART_DockArea` — документная зона;
`PART_LeftSideSplitter`, `PART_RightSideSplitter`, `PART_BottomSplitter` — сплиттеры сторон;
`PART_PairSplitter` — сплиттер пары группы; `PART_UndockOverlay`, `PART_OverlayBackdrop` —
слой и подложка Undock-оверлеев.

**Панель (декоратор)**
`PART_Header` — полоса заголовка; `PART_HeaderTabs` — полоса вкладок корневой группы в
заголовке; `PART_MenuButton` («⋮»), `PART_HideButton` («—»); `PART_Content` — зона контента.

**Деревья вкладок**
`PART_TabStrip` — полоса вкладок группы; `PART_TabHeader` — заголовок вкладки (имя общее,
конкретная вкладка различается по `Tag` = id вкладки); `PART_TabClose` — «×» вкладки;
`PART_GroupContent` — контент группы; `PART_DockSplitter` — сплиттер дерева;
`PART_DockTree`, `PART_DocumentTree` — корни деревьев.

**Плавающий слой и жесты**
`PART_PseudoWindowLayer`, `PART_PseudoWindow`, `PART_PseudoWindowTitle`,
`PART_PseudoWindowClose`; `PART_DragLayer`, `PART_DragGhost`, `PART_DropMarker`,
`PART_DropZonePreview`, `PART_DropHint`, `PART_StripPlaceholder`, `PART_WindowDropMarker`.

## Псевдоклассы

| Псевдокласс | Носитель | Смысл |
|---|---|---|
| `:open` | `StripeButton` | панель открыта — подсветка иконки (TW-6.4) |
| `:active` | `ToolWindowDecorator` | активная панель (TW-6.4) |
| `:active` | заголовок вкладки (`PART_TabHeader`) | активная вкладка своей группы |
| `:current` | заголовок вкладки (`PART_TabHeader`) | вкладка — текущий документ активного хоста при неактивных панелях (DA-6.2) |

**Чем перекрасить саму подсветку.** Цвета подсветки — это токены (`OpenIcon`,
`ActiveHeader`), а не свойства, доступные через селектор по псевдоклассу: библиотека ставит
эти фоны напрямую, приоритетом `LocalValue`, который перекрывает сеттеры стилей; к тому же
`:open` живёт на `StripeButton`, а сам фон рисует его дочерний узел иконки. Поэтому
`StripeButton:open { Background: … }` ничего не даст — **перекрашивайте подсветку через токен**
(`BerthOpenIconBrush`, `BerthActiveHeaderBrush`). Псевдоклассы предназначены навешивать стиль
по состоянию на свойства, которые библиотека сама не красит (акцентная рамка, начертание
заголовка, foreground вкладки). Полная стилизация подсветки селектором придёт с
`ControlTheme` для лист-хрома (см. бэклог).

## Что дальше

Кандидаты следующих задач (см. [BACKLOG.md](BACKLOG.md)): `ControlTheme` для лист-хрома —
структурная кастомизация, полная замена шаблона кнопки полосы и заголовка вкладки (снимет и
ограничение на стилизацию подсветки селектором); опциональный пакет готового оформления в
стиле IDEA.
