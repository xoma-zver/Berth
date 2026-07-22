# Эталон: визуальная дизайн-система New UI тул-панелей IntelliJ IDEA

Справочник по образцу (открытые исходники `intellij-community`, Apache 2.0): точные цвета
(hex, light/dark), отступы, размеры, радиусы и обводки подсистемы тул-окон **New UI**
(`ExperimentalUI`). Не нормативен — оформление Berth определяет `docs/styling.md`
(`BerthThemeKeys`); здесь — «как выглядит у них», сырьё для будущего пакета оформления в
стиле IDEA new UI (см. `docs/BACKLOG.md`). Ключи и hex ниже — из `expUI_light.theme.json` /
`expUI_dark.theme.json` и `JBUI.CurrentTheme.*`; построчная трассировка — в git-истории
этого файла.

Палитра-алиасы темы (Gray1–14, Blue1–13) ниже везде уже разрешены в конкретный hex —
алиасы не показаны отдельно, они различаются между light/dark.

## 1. Стрип (полоса иконок панелей)

| Параметр | Light | Dark | Источник |
|---|---|---|---|
| Фон стрипа | `#F7F8FA` (Gray13) | `#2B2D30` (Gray2) | `JBUI.java:1085-1087`; `ToolWindow.background`, тот же hex, что и фон контента (§6) — стрип не выделен отдельным тоном |
| Граница стрип↔контент/редактор | `#EBECF0` (Gray12) | `#1E1F22` (Gray1) | `JBUI.java:1089-1091`, `ToolWindowToolbar.kt:111`; резолвится через wildcard `*.borderColor`, ключ `ToolWindow.Stripe.borderColor` явно не задан |
| Граница стрип↔главный тулбар | `#EBECF0` (Gray12) | `#1E1F22` (Gray1) | `ToolWindowToolbar.kt:56`, ключ `MainToolbar.borderColor` |
| Разделитель сегментов (между группами кнопок) | `#C9CCD6` (Gray9) | `#43454A` (Gray4) | `JBUI.java:1148-1152`; `StripeButtonSeparator.kt:36` |
| Разделитель сегментов при drag-переупорядочивании | `#709CF5` (Blue7) | `#548AF7` (Blue8) | явные ключи `expUI_light.theme.json:750`, `expUI_dark.theme.json:737` |

Ширина стрипа — **не константа**: определяется шириной кнопки (40 px, §2); отдельного
«StripeWidth» в New UI нет. Ширина сама регулируется только в режиме подписей (см. §2).
Нижнего стрипа как отдельной сущности нет — кнопки BOTTOM встают в нижний сегмент
LEFT/RIGHT стрипа, причём для New UI **в обратном порядке** (снизу вверх), см. комментарий
`AbstractDroppableStripe.createButtonLayoutComparator` (`AbstractDroppableStripe.kt:61-64`).

## 2. Кнопка-иконка стрипа (`SquareStripeButton`)

### Геометрия

| Параметр | Значение | Источник |
|---|---|---|
| Кликабельный квадрат кнопки | **40×40** | `JBUI.java:1322-1324`, ключ `StripeToolbar.Button.size` |
| Размер иконки внутри | **20×20** | `JBUI.java:1334-1336`, ключ `StripeToolbar.Button.iconSize` |
| Паддинг иконки/подсветки от края кнопки | **5 px** со всех сторон | `JBUI.java:1346-1348`; видимый прямоугольник подсветки — **30×30** внутри 40×40 хит-зоны (`SquareStripeButtonLook.kt:37-40`) |
| Зазор между соседними кнопками | **0 px** (кнопки впритык, `VerticalFlowLayout(0,0)`) — визуально между подсветками 10 px за счёт паддинга каждой | `ToolWindowToolbar.kt:181` |
| Радиус скругления подсветки | **12 px** (обычный режим) / **8 px** (compact mode) | `JBUI.java:1358-1360`, ключ `Button.ToolWindow.arc` |
| Разделитель-сегмент (визуальный блок) | бокс 32×11, видимая линия 24×1 | `StripeButtonSeparator.kt` |
| Ширина стрипа в режиме подписей: дефолт / мин / мин-compact / макс | **59 / 40 / 33 / 100 px** | `ResizeStripeManager.kt:220,138,143` |

### Цвета по состояниям

Путь покраски: `SquareStripeButtonLook.getState()` → `ActionButtonLook.getStateBackground()`,
с переопределением `getBackgroundColor()` для «выбранного» (открытая+активная панель)
состояния (`SquareStripeButtonLook.kt:46-58,77-82`).

| Состояние | Условие | Фон | Обводка | Иконка/текст |
|---|---|---|---|---|
| Обычное (idle) | не открыта, без ховера | прозрачный (просвечивает фон стрипа) | нет | нативные цвета SVG-иконки; текст подписи (только в режиме имён) — `#5A5D6B` light / `#9DA0A8` dark (Gray5/Gray9) |
| Ховер (панель закрыта) | курсор над кнопкой | `#00000012` light (~7% чёрного) / `#FFFFFF16` dark (~9% белого) — `ActionButton.hoverBackground` | нет (`hoverBorderColor` = полностью прозрачный) | как обычное |
| Открыта, не активна («в сплите», не в фокусе) | `isVisible=true`, `isActive=false` | `#0000001D` light (~11%) / `#FFFFFF26` dark (~15%) — `ActionButton.pressedBackground` | нет | подпись — Gray5/Gray9 |
| Открыта + активна («выбрана», текущая панель) | `isActive=true` (по фокусу) | **`#3574F0`** — одинаковый hex в обоих темах — `ToolWindow.Button.selectedBackground` | нет | `#FFFFFF` (`selectedForeground`); иконка перерисовывается как stroke-icon, тонированная белым (`SquareStripeButtonLook.kt:105-113`) |
| Drag — плавающий призрак кнопки | во время перетаскивания самой кнопки | `#DFE1E5` light (Gray11) / Gray5 dark (`expUI_dark.theme.json:731`) | — | — |
| Drop-цель (наведение при DnD вкладки/окна) | над валидным стрипом-целью | `#D4E2FF` light (Blue11) / `#35538F99` dark (полупрозрачный) | `#709CF5` light (Blue7) / `#548AF7` dark (Blue8) | — |
| Drop-оверлей области стрипа | во время drag поверх области | `#A0BDF84D` light / `#366ACF4D` dark (полупрозрачный синий) | — | — |

Важные нюансы:
- **Фокусное кольцо клавиатуры отсутствует** для стрип-кнопок в обычном случае: перехват в
  `SquareStripeButtonLook.getState()` происходит раньше базовой логики
  `ActionButtonLook`, которая красила бы `ActionButton.focusedBorderColor`
  (`#4682FA` light / `#3574F0` dark) — Tab-фокус без ховера на закрытой панели визуально
  неотличим от Normal.
- `ToolWindow.Button.selectedBackground/selectedForeground` — особый случай: если тема их не
  задаёт явно, `UIThemeBean.kt:472-474` форсит `#3573F0`/`#FFFFFF` (на 1 бит темнее, чем
  `expUI`'шный `#3574F0`) — сам `expUI` всегда задаёт их явно, так что этот фолбэк неактивен
  для штатных тем.
- Классический (не-New-UI) `StripeButtonUi` со своими `BACKGROUND_COLOR`/`FOREGROUND_COLOR`
  (Gray12-ховер и т.п.) — мёртвый код для New UI: `ToolWindowPane.kt:115-127` создаёт его
  только когда `!ExperimentalUI.isNewUI()`.

## 3. Заголовок панели (`ToolWindowHeader`)

### Геометрия

| Параметр | Значение | Источник |
|---|---|---|
| Высота заголовка | **41 px** | `JBUI.java:1178-1180`, ключ `ToolWindow.Header.height` |
| Инсеты подписи (title) слева/справа | **12 / 16 px** | `JBUI.java:1182-1184` |
| Инсеты тулбара действий слева/справа | **12 / 8 px** | `JBUI.java:1186-1188`; в old UI — плоские `2,0` |
| Зазор подпись↔иконки действий | нет отдельной константы: сумма правого инсета подписи (16) + левого инсета тулбара (12), панели идут WEST/EAST без прослойки | `ToolWindowHeader.kt:117,202` |
| Кнопка действия в заголовке | **22×22** (общий дефолт `ActionToolbar.DEFAULT_MINIMUM_BUTTON_SIZE`, не специфично для тул-окон) | `ActionToolbar.java:79` |
| Радиус подчёркивания активной вкладки заголовка | **4 px** | `JBUI.java:1129-1131` |
| Инсеты вкладки заголовка слева/справа | **12 px** (New UI) / 8 px (old UI) | `JBUI.java:1133-1135` |
| Высота подчёркивания активной вкладки | из `DefaultTabs.UNDERLINE_HEIGHT` — числовой дефолт не прослежен в этом заходе | `JBUI.java:1190-1192` |

Заголовок→контент и рамка→контент — зазор **0 px** в обоих случаях (никакого промежуточного
паддинга, `InternalDecoratorImpl.kt:353-355`).

### Цвета

| Элемент | Light | Dark | Источник |
|---|---|---|---|
| Фон заголовка | `#F7F8FA` (Gray13) | `#2B2D30` (Gray2) | `ToolWindow.Header.inactiveBackground`; **New UI всегда использует «неактивное» значение** — `ToolWindowHeader.kt:312` вычисляет `active = !isNewUi && isActive`, для New UI это всегда `false`. Активный (синеватый) фон заголовка — мёртвый код old-UI |
| Граница (верх/низ) | `#EBECF0` (Gray12) | `#1E1F22` (Gray1) | wildcard `*.borderColor`, `UIUtil.java:1486-1491` |
| Текст заголовка (единственная/активная вкладка) | `#000000` | `#BBBBBB` | `Label.foreground` родительской темы (`intellijlaf.theme.json:385` / `darcula.theme.json:349`) |
| Кнопки действий заголовка — ховер/нажатие | `#00000012` / `#0000001D` | `#FFFFFF16` / `#FFFFFF26` | те же generic `ActionButton.hover/pressedBackground`, что и у стрипа |
| Вкладка заголовка — ховер (активная панель) | `#EBECF0` (Gray12) | не задан явно → java-дефолт `rgba(0,0,0,.35)` ≈ `#00000059` | `expUI_light.theme.json:732`; `JBUI.java:611-615` |
| Вкладка заголовка — ховер (неактивная панель) | `#EBECF0` (Gray12) | `#393B40` (Gray3) | `expUI_dark.theme.json:721` |
| Вкладка заголовка — выбрана / выбрана-неактивна | `#D0D4D8` / `#D9D9D9` | `#313B45` / `#343638` | не заданы в `expUI` — наследуются из legacy `intellijlaf`/`darcula` (`intellijlaf.theme.json:984-985`, `darcula.theme.json:711-712`) |

## 4. Плавающее (недокированное) окно (`FloatingDecorator`)

Заголовок и контент плавающего окна — те же `InternalDecoratorImpl`/`ToolWindowHeader`,
цвета совпадают с §3/§6. Специфика самого декоратора:

| Элемент | Light | Dark | Источник |
|---|---|---|---|
| Рамка недекорированного окна (Linux, `ide.linux.use.undecorated.border`) | `#5A5D6B` (Gray5), 1 px по каждой стороне | `#5A5D63` (Gray6), 1 px | `Window.undecorated.border`, `expUI_light.theme.json:779` / `expUI_dark.theme.json:760`; `JBUI.java:1958-1969` |
| Полоса ресайза (Windows-only, недекорированный режим) | `lightGray #C0C0C0` / `gray #808080` — **захардкожено**, не через тему | одинаково в обеих темах (`Gray._95 #5F5F5F` фолбэк) | `FloatingDecorator.java:470-495` — сырые `JBColor`/`Gray`, не `namedColor` |
| Ширина полосы ресайза / угловая зона ресайза | **3 px** / **10 px** | — | `FloatingDecorator.java:66,321` (`DIVIDER_WIDTH`, `RESIZER_WIDTH`) |
| Радиус скругления углов | нет пиксельного значения: делегируется ОС (`WindowRoundedCornersManager`), только Windows ≥ build 22000 (Win11), передаётся строка-ключевое слово `"full"` (`"small"` под Wayland). macOS/Linux — без явного скругления в коде | — | `WindowRoundedCornersManager.java:90-98,120-122`, `FloatingDecorator.java:159-161` |
| Тень | не определена цветом/константой — нативная тень ОС | — | нет find в `FloatingDecorator`/`ToolWindowManagerDecorators.kt` |
| Мин./дефолтный размер окна | нет фиксированной константы: наследует последний известный/текущий preferred размер декоратора | — | `ToolWindowManagerDecorators.kt:212-252` |

На macOS/Linux при нативном декорировании кастомный хром вообще не рисуется — используется
системная рамка ОС (`FloatingDecorator.java:102-115`).

## 5. Сплиттеры и границы

| Разделитель | Light | Dark | Толщина | Источник |
|---|---|---|---|---|
| Тул-окно ↔ редактор | `#EBECF0` (Gray12) | `#1E1F22` (Gray1) | **0 px** видимой линии (New UI явно `dividerWidth = 0`), хит-зона драга — **6 px** (`ide.splitter.mouseZone`) | `ToolWindowPane.kt:196-198,627-630`, `JBUI.java:1093-1095` |
| Внешняя рамка вокруг докированной панели | то же | то же | **1 px** хайрлайн (`InnerPanelBorder`) | `InternalDecoratorImpl.kt:672-825,690-725` |
| Внутренний «шов» рамки (сторона контента, маскирует 1px) | `#F7F8FA` (Gray13) | `#2B2D30` (Gray2) | — | `InternalDecoratorImpl.kt:727-745` |
| Между докированными панелями на одной стороне | `#EBECF0` / `#1E1F22` | — | та же логика `mainBorderColor` | `ToolWindowPane.kt:611-630` |
| Сплиттер стрипа в режиме подписей (ресайз ширины) | — | — | **1 px** (`OnePixelDivider`) | `ResizeStripeManager.kt:81-82` |

Отдельный класс `ThreeComponentsSplitter` по умолчанию рисует **7 px** делитель — New UI
явно затирает его до 0 именно для тул-окон.

## 6. Фон зоны контента панели

`ToolWindow.background()` = `#F7F8FA` (Gray13) light / `#2B2D30` (Gray2) dark, ставится на
корень `InternalDecoratorImpl` и рекурсивно распространяется на дочерние компоненты
(`InternalDecoratorImpl.kt:307-309`; `ToolWindowImpl.kt:230,241,779`). Это **тот же hex**,
что и фон заголовка (§3) и фон стрипа (§1) — во всей New UI-подсистеме тул-окон один общий
нейтральный фон; единственная видимая граница между зонами — hairline-рамка (§5).

Фон **редактора** — отдельная подсистема (`EditorColorsScheme`, XML-схемы, юзер-выбираемые
независимо от UI-темы), сюда не входит.

## Механизмы темизации (важно при переносе значений)

1. **Wildcard-фолбэк.** Любой `JBColor.namedColor("X.borderColor"/"X.background"/…, default)`,
   чей точный ключ отсутствует и в `expUI`-JSON, и в родительской теме, резолвится через
   верхнеуровневый блок темы `"*": {"borderColor": …, "background": …}`
   (`expUI_light.theme.json:109-135`) раньше, чем через java-дефолт. Практически все
   `ToolWindow.*borderColor` в §1–§5 идут именно этим путём, а не буквальным ключом.
2. **`ToolWindow.Button.selectedBackground/selectedForeground`** — исключение из wildcard;
   `expUI` всегда задаёт их явно, но платформенный дефолт на случай отсутствия
   форсится отдельно (см. §2).
3. **«Активный» фон заголовка** (`ToolWindow.Header.background`, синеватый) — реальный,
   но **недостижимый в New UI** код-путь (см. §3): визуальный акцент активной панели в New
   UI живёт только в кнопке стрипа (`selectedBackground` = синий), не в заголовке.

## Соответствие токенам Berth (для будущего пакета оформления IDEA-look)

Текущие дефолты `BerthThemeKeys` (`docs/styling.md`) — нейтральные полупрозрачные серые
плейсхолдеры, сознательно не привязанные к конкретной теме. Ниже — куда они лягут, если
собирать пакет оформления «как в IDEA New UI»; это ориентир, не готовое решение (нужны
варианты light/dark, а не одно значение на ключ).

| Токен Berth | Дефолт Berth | Аналог IDEA New UI | Замечание |
|---|---|---|---|
| `Pane` (`BerthPaneBrush`) | `#14808080` (полупрозрачный) | `#F7F8FA` / `#2B2D30` — сплошной, не полупрозрачный | у IDEA один и тот же сплошной фон для стрипа, заголовка и контента — Berth сейчас красит слоем поверх, не заменяя базовый фон |
| `Separator` (`BerthSeparatorBrush`) | `#50808080` | `#EBECF0` / `#1E1F22` | прямое соответствие по роли |
| `OpenIcon` (`BerthOpenIconBrush`) | `#40808080` | у IDEA нет отдельного «открыта, не активна» тона в неоновом смысле — это `ActionButton.pressedBackground` (`#0000001D`/`#FFFFFF26`), тусклее, чем «активна» | активное состояние (`#3574F0`, заливка целиком, без прозрачности) у Berth сейчас не выделено отдельным токеном — см. `ActiveHeader` |
| `ActiveHeader` (`BerthActiveHeaderBrush`) | `#30808080` | у IDEA New UI акцент активной панели **не в заголовке** (см. «Механизмы», п.3), а в самой кнопке стрипа (`#3574F0`) | при портировании стиля IDEA этот токен пришлось бы либо оставить пустым, либо сознательно расходиться с эталоном (Berth красит заголовок, IDEA — кнопку) |
| `DropMarker`/`DropAreaPreview` | `#B0808080` / `#38808080` | `#709CF5`/`#548AF7` (маркер), `#A0BDF84D`/`#366ACF4D` (область) | IDEA использует синий акцент темы, не серый |
| `OverlaySurface` | light `#F7F8FA` / dark `#1E1F22` | `#F7F8FA` / `#2B2D30` (фон контента) | light **совпадает** буквально; dark у Berth сейчас `#1E1F22` — это hex **границы** IDEA (Gray1), а не фона контента (Gray2, `#2B2D30`) — стоит проверить осознанность выбора |

| Токен размера Berth | Дефолт Berth | Аналог IDEA New UI |
|---|---|---|
| `StripeWidth` | 36 | нет константы; определяется кнопкой (40, режим подписей 59, диапазон 33–100) |
| `StripeButtonSize` | 28 | 40 (хит-зона) / 30 (видимая подсветка после паддинга 5) |
| `HeaderHeight` | 28 | 41 |
| `TabStripHeight` | 28 | высота = высоте заголовка (41), инсеты вкладки 12/12 |
| `SplitterThickness` | 4 | 0 видимых (New UI прячет делитель) + 6 px хит-зона драга; рамка панели — 1 px хайрлайн |
