# Трассировка спеки tool-windows к эталону (IntelliJ IDEA)

Отчёт: как **каждый** нормативный пункт `docs/spec/tool-windows.md` реализован в открытых
исходниках IntelliJ Platform (`intellij-community-master`, Apache 2.0). Цель — понять устройство
«образца», а не оценить наш код.

## Как читать

- **Вердикт**: `=` поведение совпадает с IDEA; `≈` наше упрощение/обобщение при том же наблюдаемом
  эффекте; `≠` **расхождение** (IDEA делает иначе — важно для ревью спеки); `—` вне модели IDEA
  (наша конвенция, эталона нет).
- Ссылки вида `Файл.kt:NNN` — на исходники под
  `platform/platform-impl/src/com/intellij/` (пакеты `openapi/wm/impl` и `toolWindow`), если не
  указано иное.
- Пути к файлам в этом отчёте относительны корня `intellij-community-master`.

## Ключевые файлы эталона

| Файл | Роль |
|---|---|
| `openapi/wm/impl/ToolWindowManagerImpl.kt` (~1978 стр.) | оркестрация: open/close/activate, вытеснение, смена режима/якоря, Apply (`setLayout`) |
| `openapi/wm/impl/WindowInfoImpl.kt` | модель состояния одной панели (раздел 3 спеки) |
| `openapi/wm/impl/DesktopLayout.kt` | раскладка: `Order`, нормализация, сериализация |
| `openapi/wm/impl/UnifiedToolWindowWeights.kt` | вес на **сторону** (new UI) |
| `openapi/wm/impl/ToolWindowManagerState.kt` | персистентность (`PersistentStateComponent`) |
| `openapi/wm/impl/ToolWindowManagerDecorators.kt` | Float/Window-декораторы, ресайз → веса, валидация bounds |
| `openapi/wm/impl/ToolWindowManagerLifecycle.kt` | автоскрытие по фокусу, hold-state стрипов |
| `openapi/wm/impl/AbstractDroppableStripe.kt` | раскладка стрипа: порядок, разделитель, drop-индекс |
| `toolWindow/ToolWindowPane.kt` | геометрия: два `ThreeComponentsSplitter`, split пары |
| `toolWindow/InternalDecoratorImpl.kt` | контент панели: режимы split/cell, `ContentManager` |
| `toolWindow/ToolWindow{Left,Right}Toolbar.kt`, `MoreSquareStripeButton.kt` | стрипы и кнопка «⋯» |
| `ide/actions/ActivateToolWindowAction.kt`, `HideAllToolWindowsAction.kt`, `ToolWindowsGroup.java` | команды-действия |

---

## Модель соответствия (самое важное)

Прежде чем идти по пунктам — как термины спеки ложатся на модель IDEA. Именно здесь сосредоточены
все расхождения; дальше по пунктам они лишь конкретизируются.

| Термин спеки | В IDEA | Замечание |
|---|---|---|
| **Слот** (Side, Group) | пара `anchor ∈ {LEFT,RIGHT,BOTTOM,TOP}` + `isSplit: Boolean` (`WindowInfoImpl`) | `Primary`=`isSplit=false`, `Secondary`=`isSplit=true`. IDEA имеет ещё `TOP` (спека — нет). |
| **Прописка** (`Side`,`Group`,`Order`) | `anchor` + `isSplit` + `order: Int` | `order = -1` = «не задан, в конец» (`WindowInfoImpl.kt:100`) |
| **Режим** (`Mode`, 5 шт.) | `type: ToolWindowType {DOCKED,SLIDING,FLOATING,WINDOWED}` × `isAutoHide: Boolean` | `DockPinned`=DOCKED+autoHide=false; `DockUnpinned`=DOCKED+autoHide=true; `Undock`=SLIDING; `Float`=FLOATING; `Window`=WINDOWED |
| **Слой** (докированный / оверлейный) | в `doShowWindow` вытеснение по равенству `type` | DOCKED (обе пин-версии) и SLIDING — разные `type`, потому сосуществуют (см. INV-2) |
| **`Weight` стороны** | `UnifiedToolWindowWeights[anchor]` (new UI) | одно число на сторону; дефолт `0.33` (`WindowInfoImpl.DEFAULT_WEIGHT`) |
| **`CurrentRatio` + `PairRatio`** | **одно** поле `WindowInfoImpl.sideWeight` (дефолт `0.5`) на панель; сплиттер следует за показанной | у IDEA нет отдельного «CurrentRatio стороны» — спека расщепляет одно поле на два (≈) |
| **`UndockWeight`** | у SLIDING-окна тот же `weight` | отдельного поля нет; спека выделяет его явно (≈) |
| **`IsOpen`** | `isVisible` | |
| **`IsIconVisible`** | `isShowStripeButton` | |
| **`FloatingBounds`** | `floatingBounds: Rectangle?` | |
| **`ActiveToolWindowId`** | **вычисляется** из фокуса (`activeToolWindowId` getter, `ToolWindowManagerImpl.kt:699`), не хранится | + persist-флаг `isActiveOnStart` и стек `recentToolWindows`. Спека хранит поле (≈) |
| **`QuickAccessSide`** | `ToolWindowManagerState.moreButton: ToolWindowAnchor` (дефолт LEFT) | |
| **`ContentTree`** | `ContentManager` + `InternalDecoratorImpl.Mode` (SINGLE/VERTICAL_SPLIT/HORIZONTAL_SPLIT/CELL) | у IDEA бинарный split, и модель контента панели **≠** модель редакторов (см. раздел 9) |

**Топ расхождений**, которые всплывут ниже (сведены здесь, чтобы не потерялись):

1. **`Order` нормализуется по `anchor`, а не по слоту** (`DesktopLayout.normalizeOrder`, `:198`). Primary и
   Secondary одной стороны делят **одну** нумерацию 0..n−1; порядок внутри сегмента задаётся отдельным
   компаратором стрипа. Спека (INV-3) объявляет плотность 0..n−1 **на слот**.
2. **SideStack** (`SideStack.java`): IDEA умеет **восстановить** ранее вытесненную докированную панель при
   закрытии заменившей. Спека (R3, E3) этого не делает. В new UI выключено реестром
   `ide.enable.toolwindow.stack` (`ToolWindowManagerSupport.kt:88`).
3. **HideAll** охватывает только `type.isInternal` (docked+sliding); Float/Window остаются открытыми
   (`HideToolWindowAction.kt:18`). Спека TW-5.12 — «все режимы».
4. **Сортировка списка «⋯»** — по мнемонике, затем по id алфавитно (`ToolWindowsGroup.java:78`), не по
   прописке. Закрывает открытый вопрос TW-8.2.
5. **Дефолт `Weight` = 0.33** у IDEA против рекомендованных спекой 0.25/0.30.
6. **Контент панели**: IDEA использует `ContentManager` (иерархия split/cell), **не** ту же модель, что
   редакторы. Спека унифицирует контент панели и док-зоны в одно дерево — это наш дизайн.

---

## Раздел 1. Термины и слоты

**TW-1.1 — шесть слотов = anchor × isSplit.** `=`
Слот в IDEA — пара `anchor` + `isSplit` (`WindowInfoImpl.anchor:44`, `isSplit:92`, атрибут `side_tool`).
Комбинации LEFT/RIGHT/BOTTOM × {false,true} дают ровно шесть, плюс исторический TOP, который new UI не
использует. Соответствие «anchor=left,split=false ↔ Left.Primary» из таблицы спеки буквально совпадает.
Вытеснение и геометрия всюду ключуются по `(paneId, anchor, isSplit)` — напр. `getDockedInfoAt`
(`ToolWindowManagerImpl.kt:744`).

**TW-1.2 — порядок сегментов стрипа.** `=`
Каждая сторона-тулбар (`ToolWindowToolbar`) держит `topStripe` + `bottomStripe` + `moreButton`
(`ToolWindowToolbar.kt:40-43`). Раскладка:
- **Левый** (`ToolWindowLeftToolbar.kt:11-13`): `topStripe`=anchor `LEFT`, `bottomStripe`=anchor `BOTTOM`,
  `moreButton(side=LEFT, moveTo=RIGHT)`.
- **Правый** (`ToolWindowRightToolbar.kt:11-13`): `topStripe`=`RIGHT`, `bottomStripe`=`BOTTOM, split=true`,
  `moreButton(side=RIGHT, moveTo=LEFT)`.

То есть Left.Primary и Left.Secondary живут в **одном** `topStripe` (anchor=LEFT), разделённые
разделителем (TW-1.3); Bottom.Primary — это `bottomStripe` левого тулбара, Bottom.Secondary —
`bottomStripe` правого. «⋯» добавляется в центр тулбара `initMoreButton` (`ToolWindowToolbar.kt:93`), между
верхним и нижним сегментами — как в спеке. Зеркальность левый/правый — прямо в конструкторах тулбаров.

**TW-1.3 — разделитель только при обоих непустых сегментах.** `=`
`AbstractDroppableStripe.getButtonsToLayOut` (`:500-554`, ветка new UI для LEFT/RIGHT): разделитель
(`separatorStripe`) вставляется перед **первой** кнопкой с `isSplit=true` (`:521-527`). Если split-кнопок
нет — разделителя нет (Secondary пуст). Если разделитель оказался в позиции 0 (нет non-split, Primary
пуст) — он скрывается (`:531-540`, кроме DnD-подсказки). Итог ровно как в спеке: виден только когда оба
сегмента непусты.

**TW-1.4 — порядок иконок по `Order`; переупорядочивание сводится к команде.** `=`
Компаратор стрипа `createButtonLayoutComparator` (`:55-70`): сначала non-split, затем split; внутри — по
`order` (`-1` → `Int.MAX_VALUE`, в конец, `:72-75`). Для `BOTTOM` в new UI порядок **инвертирован**
(`:61-65` — «пользователь читает снизу вверх»; спекой не оговорено, деталь new UI). DnD-иконки в
`finishDrop` сводятся к команде ядра `setSideToolAndAnchor(id, paneId, anchor, order, isSplit)`
(`:210-226`) — то есть жест = вызов той же команды, что и меню.

**TW-1.5 — вставка после ближайшего видимого предшественника.** `≈`
`getButtonsToLayOut` раскладывает **только видимые** кнопки (`filterTo { isVisible }`, `:515/549`), а
`recomputeBounds` вычисляет `dragInsertPosition = insertOrder` относительно этих видимых соседей
(`:342-356`). То есть drop действительно считается по видимым позициям. IDEA ведёт себя здесь **детерминированно, но
непреднамеренно** и **иначе, чем наш E22**: `dragInsertPosition` берётся из `order` видимого **преемника**
(перед чьей кнопкой сброс), затем `DesktopLayout.setAnchor` сдвигает всех с `order ≥` вставляемого. Прогон
E22 (`[1,2,3,4,5]`, 2–4 скрыты, X между видимыми 1 и 5): сброс ближе к 5 → `order=4` → `[1,2,3,4,X,5]` (X в
конец скрытого прогона); сброс ближе к 1 → `order=0` → `[X,1,2,3,4,5]` (X в начало слота). Нашего
`[1,X,2,3,4,5]` IDEA не даёт — она не считает «предшественник + 1». Наша конвенция (вставка сразу после
видимого предшественника, скрытый прогон остаётся целым за X) предсказуемее в этом краевом случае — потому
пункт помечен `≈` (конвенция наша).

---

## Раздел 2. Геометрия рабочей области

**TW-2.1 — компоновка; нижняя панель на всю ширину.** `=`
`ToolWindowPane` (`:145-186`): два `ThreeComponentsSplitter` — `verticalSplitter(true)` и
`horizontalSplitter(false)`. В обычном режиме `verticalSplitter.innerComponent = horizontalSplitter`
(`:181`), корень — `verticalSplitter` (`:186`). Значит вертикальный сплиттер держит BOTTOM (`lastComponent`)
на всю ширину, а горизонтальный (LEFT/RIGHT + редактор) вложен выше него. Ровно «нижняя панель между
стрипами, боковые над ней».

**TW-2.2 — widescreen не реализуем в v1.** `=` (как факт наличия у IDEA)
У IDEA это глобальная app-настройка `UISettings.wideScreenSupport` (одна на все фреймы, не пер-окно). Весь
механизм — **~20 строк в `ToolWindowPane`** и это чистое **переподвешивание** двух сплиттеров: инициализация
(`:177-186`) и живое переключение (`:482-496`, редактор сохраняется). При включении
`horizontalSplitter.innerComponent = verticalSplitter` (`:178`), корень — `horizontalSplitter` (`:186`):
боковые на всю высоту, нижняя между ними. Дёшево, потому что **оба** сплиттера существуют всегда, а
присвоение «сторона → сплиттер» (`setComponent`/`setWeight`/`addDecorator`) от widescreen не зависит —
меняется только вложенность, а не раскладка зон.

Следствие для нас: **Core не затронут** (пиксель-free, веса на сторону — уже widescreen-ready by
construction); вся стоимость — в слое материализации Avalonia + персист UI-настройки (не в layout-состоянии
тул-панелей). Решение отложить в v2 — осознанное; чтобы v2 остался ~полдня, достаточно **параметризовать
вложенность** двух сплиттеров, а не хардкодить её. Рядом у IDEA есть независимые тумблеры
`leftHorizontalSplit`/`rightHorizontalSplit` (ориентация сплита пары; влияют на семантику весов через
`isUltrawideLayout`, `ToolWindowManagerImpl:1367`) — отдельная, чуть более хитрая опция, не часть чистого
widescreen.

**TW-2.3 / TW-2.4 — деление стороны на пару.** `=`
`addDecorator` → `addAndSplitDockedComponentCmd` при занятой соседней группе (`ToolWindowPane.kt:230-244`)
кладёт пару в `Splitter`. Ориентация — от `mode`/якоря: для боковых стек вертикальный (Primary сверху),
для нижней горизонтальный (Primary слева). Одиночная группа занимает всю сторону (`setComponent`,
`:301-310`). При закрытии одной из пары оставшаяся снова занимает сторону целиком (`removeDecorator`,
`:254-291`, ветка со `Splitter`).

**TW-2.5 — доли, геометрия на сторонах.** `=` по весу стороны, `≈` по расщеплению ratio
- `Weight` стороны: `UnifiedToolWindowWeights` — по одному `Float` на `top/left/bottom/right`
  (`UnifiedToolWindowWeights.kt:20-23`), дефолт `0.33` (спека рекомендует 0.25/0.30 — расхождение дефолтов).
  Хранится в `DesktopLayout.unifiedWeights`, применяется `ToolWindowPane.setWeight` (`:315-355`).
- `PairRatio`/`CurrentRatio`: у IDEA это **одно** поле `WindowInfoImpl.sideWeight` (дефолт `0.5`, `:89`).
  Сплиттер пары ставится из `sideWeight` **показанной** панели (`setSideWeight`, `ToolWindowPane.kt:357-376`:
  для Primary `proportion=sideWeight`, для Secondary `1−sideWeight`). Спека вводит два поля (side-level
  `CurrentRatio` + per-window `PairRatio`), IDEA обходится одним + правилом «следуй за показанной». Наблюдаемо
  эквивалентно; расщепление — наша модель.

**TW-2.6 — open/close/switch не меняют `Weight` стороны.** `=`
`showToolWindowImpl` (`:900-922`) и `hideToolWindow` (`:803-838`) не трогают `unifiedWeights`. При активации
панель даже **берёт** текущий вес стороны себе: `activateToolWindow` пишет
`info.weight = getUnifiedAnchorWeight(info.anchor)` (`:590-592`) — то есть панель показывается в
унаследованной ширине стороны. `Weight` пишется только ресайзом кромки (`movedOrResized` →
`setUnifiedAnchorWeight`, `ToolWindowManagerDecorators.kt:185`).

**TW-2.7 — четыре правила пары (R1–R4).** `≈` (эквивалентно, но одно поле вместо двух)
- **R1** (открытие со своим предпочтением): панель показывается со своим `sideWeight`, из которого
  `setSideWeight` вычисляет позицию сплиттера — Primary даёт `sideWeight`, Secondary `1−sideWeight`
  (`ToolWindowPane.kt:365-370`). Это ровно «панель приходит со своим предпочтением».
- **R2** (ресайз пары): `movedOrResized` пишет `info.sideWeight` показанной панели через `getAdjustedRatio`
  (`ToolWindowManagerDecorators.kt:171-179`). IDEA учит **ту** панель, что двигали; «учим обе» с суммой 1 —
  наша нормализация поверх одного поля.
- **R3** (закрытие из пары): `removeDecorator` отдаёт сторону оставшейся, `sideWeight` не пересчитывает —
  совпадает.
- **R4** (одиночное открытие): сплиттера нет, `setSideWeight` — no-op (`:362-364`) — совпадает.

**TW-2.8 — минимумы клемпируются на рендере.** `=`
Минимумы сплиттеров — `Registry "ide.mainSplitter.min.size"`, `verticalSplitter.minSize/…`
(`ToolWindowPane.kt:165,213-214`); веса — доли, клемп в `[0,1]` в сеттерах `WindowInfoImpl.weight`/
`sideWeight` (`:87,89`). Ограничение размера — на уровне UI-сплиттеров, состояние-доли не портится.

---

## Раздел 3. Модель состояния

**TW-3.1 — поля состояния панели.** `=` по сути, `≈` по расфасовке
`WindowInfoImpl` — построчно: `id:64`, `anchor:44`+`isSplit:92` (=Side/Group), `order:100`, `type:74`+
`isAutoHide:47` (=Mode), `isVisible:77` (=IsOpen), `isShowStripeButton:80` (=IsIconVisible), `sideWeight:89`
(=PairRatio), `weight:87` (у SLIDING = толщина оверлея = UndockWeight), `floatingBounds:53`. На уровне
раскладки: `DesktopLayout.unifiedWeights` (Weight сторон), `ToolWindowManagerState.moreButton`
(QuickAccessSide, `:60`), `recentToolWindows` (стек), корень док-зоны — отдельно. `ActiveToolWindowId` —
вычисляемый, не хранимый (см. модель соответствия).

**TW-3.2 — таблица режимов.** `=`
Соответствие столбцов таблицы спеки:
- «Занимает место»: `type.isInternal` (DOCKED/SLIDING внутренние; FLOATING/WINDOWED — нет).
- «Автоскрытие»: `isAutoHide || type==SLIDING` (`ToolWindowManagerLifecycle.kt:116`) — то есть DockUnpinned
  и Undock; DockPinned/Float/Window — нет.
- «Отдельное окно»: FLOATING → `FloatingDecorator` (owned), WINDOWED → `WindowedDecorator` (independent)
  (`ToolWindowManagerDecorators.kt:78,90`).

**TW-3.3 — Undock-оверлей на полную протяжённость.** `=` (по эффекту)
SLIDING рисуется как sliding-компонент поверх (`ToolWindowPane.addSlidingComponent`, `:246-247`), не входит
в сплиттеры сторон (в `movedOrResized` для SLIDING нет `ThreeComponentsSplitter`,
`ToolWindowManagerDecorators.kt:270-276` — размер «можно доверять»), потому размеры докированных соседей не
меняет и в правила пары R1–R2 не входит. Толщина — `weight` окна. «Полная протяжённость поверх нижней
панели» — деталь позиционирования sliding-слоя.

---

## Раздел 4. Инварианты

**INV-1 — одно состояние на панель, прописка валидна.** `=`
`idToEntry: ConcurrentHashMap<String, ToolWindowEntry>` + `DesktopLayout.idToInfo` — по одному `WindowInfoImpl`
на id; `addInfo` ассертит отсутствие дубля (`DesktopLayout.kt:60-63`). `anchor` дефолтится LEFT при кривом
вводе (`ToolWindowAnchorConverter`, `WindowInfoImpl.kt:149-163`).

**INV-2 — два слоя на слот; ≤1 докированная и ≤1 undock.** `=` (спека цитирует точно)
`doShowWindow` (`ToolWindowManagerImpl.kt:940-964`) вытесняет только окна с **совпадающим**
`type` + `isSplit` + `paneId` + `anchor`. DOCKED (обе пин-версии) — один `type`, вытесняют друг друга; SLIDING
(Undock) — другой `type`, сосуществует. Float/Window уходят в отдельные ветки (`:926-931`) и никого не
вытесняют — «не ограничены».

**INV-3 — плотный `Order` 0..n−1.** `≠` (гранулярность)
`DesktopLayout.normalizeOrder` (`:198-214`) уплотняет `order` **по `anchor`**, обнуляя счётчик при смене
якоря — **не** по `(anchor,isSplit)`. То есть Primary и Secondary одной стороны нумеруются сквозной
последовательностью; разделение на сегменты делает компаратор стрипа (TW-1.4), а не `Order`. Спека объявляет
плотность на слот — при переносе инварианта в наш код это осознанное ужесточение, о нём стоит помнить в
property-тестах.

**INV-4 — веса и ratio в (0..1); пара согласована.** `=` по клемпу, `≈` по «сумме 1»
Клемп в сеттерах `weight`/`sideWeight` (`WindowInfoImpl.kt:87,89`) и `unifiedWeights`. «Пара даёт в сумме 1»
у IDEA обеспечивается тем, что позиция сплиттера одна (`proportion` и `1−proportion`), а не двумя
согласуемыми полями — наша инвариант-формулировка поверх нашей же двухполевой модели.

**INV-5 — `ActiveToolWindowId` → открытая панель либо null.** `=` (структурно иначе)
У IDEA нет хранимого поля; `activeToolWindowId` (`ToolWindowManagerImpl.kt:699-714`) вычисляется от
фокус-владельца и по определению указывает на реально показанное окно либо null. Инвариант выполняется
конструктивно.

**INV-6 — открытая панель имеет иконку.** `=`
`showToolWindowImpl` всегда ставит `isShowStripeButton = true` при показе (`:912`); `SetIconVisible(false)`
через `hideToolWindow(removeFromStripe=true)` сперва закрывает (`:819-828`). Открытой-без-иконки состояния не
возникает.

---

## Раздел 5. Операции

Все операции спеки — команды ядра. У IDEA публичный API `ToolWindowManager`/`ToolWindowManagerImpl`
плюс `AnAction`-обёртки; DnD в новых стрипах тоже сводится к тем же командам (`finishDrop` →
`setSideToolAndAnchor`, `AbstractDroppableStripe.kt:210-226`), что подтверждает конвенцию ADR-0004.

**TW-5.1 — `Open` с вытеснением своего слоя.** `=`
`showToolWindowImpl` (`ToolWindowManagerImpl.kt:900-922`) ставит `isVisible`/`isShowStripeButton`, назначает
`order` если был `-1`, вызывает `doShowWindow`. Вытеснение — цикл `doShowWindow:940-964` по совпадающему
`type+isSplit+paneId+anchor`: старая панель получает `setHiddenState` (`isVisible=false`, поля Mode/веса не
трогаются, `:682-688`) и снимается её декоратор. Undock-соседка другого `type` не затрагивается (INV-2).
Активная назначается через фокус (`activateToolWindow`, `:582-629`).

**TW-5.2 — повторный `Open`.** `=`
`showToolWindow` при уже видимой панели — ранний возврат (`:787-790`). `activateToolWindow` при видимой и
`autoFocusContents` просто запрашивает фокус (`:613-623`); без активации — по сути no-op.

**TW-5.3 — `Close` сохраняет режим/веса/прописку.** `=`
`hideToolWindow` → `executeHide` → `deactivateToolWindow`/`setHiddenState` (`:840-895,666-688`): ставится
только `isVisible=false` (+ снятие из activeStack); `type`, `weight`, `sideWeight`, `anchor`, `order`
остаются. Если панель была активной — `activeToolWindowId` естественно становится null (вычисляемый).

**TW-5.4 — клик по иконке = toggle open/close.** `=`
Клик по `SquareStripeButton` → `toolWindow.hide()`/`activate()`; для скрытой — `Open+activate`, для
показанной — `Close`, независимо от активности. Ср. также `HideToolWindowAction`. (Обёртка над теми же
`hideToolWindow`/`activateToolWindow`.)

**TW-5.5 — шорткат активации (трёхпозиционный).** `=`
`ActivateToolWindowAction.actionPerformed` (`ide/actions/ActivateToolWindowAction.kt:144-178`):
```
if (isEditorComponentActive || id != activeToolWindowId) { activate(id) }  // закрыта или не активна → открыть/активировать
else { hideToolWindow(id) }                                                // открыта и активна → закрыть
```
Ровно три случая спеки.

**TW-5.6 — `SetMode` в любом состоянии, с переходами.** `=`
`setToolWindowType` (`:1724-1762`): у закрытой просто меняет `type` (проявится при `Open`, `:1737-1741`); у
открытой снимает декоратор, меняет `type`, заново `doShowWindow` — с вытеснением слоя-приёмника. Переход в
Float/Window использует сохранённые `floatingBounds`, иначе текущие экранные (`setExternalDecoratorBounds`,
`ToolWindowManagerDecorators.kt:212-252`). Смена `internalType` сохраняется (`:1746-1748`) — «память» о
докируемом типе. Отдельно `setToolWindowAutoHide` (`:1707-1722`) переключает DockPinned↔DockUnpinned не меняя
видимости.

**TW-5.7 — `Move(id, side, group, index)`.** `=`
`setSideToolAndAnchor` (`:1641-1663`) → `hideIfNeededAndShowAfterTask` + `doSetAnchor`. `doSetAnchor` зовёт
`DesktopLayout.setAnchor` (`DesktopLayout.kt:75-101`): при заданном `index` сдвигает `order` соседей вправо,
ставит новые `anchor/order/paneId`; при `index=-1` — `getNextOrder` (в конец). Открытая докируемая панель
переоткрывается в новом слоте и вытесняет тамошнюю своего слоя («движущаяся побеждает» — через
`hideIfNeededAndShowAfterTask`, `:1665-1691`). Для Float/Window меняется только прописка
(`setToolWindowAnchorImpl:1530-1541` — ветка без пере-дока).

**TW-5.8 — `Move` не трогает геометрию сторон; `PairRatio` сохраняется.** `=`
`setAnchor` пишет только `order/anchor/paneId`; `unifiedWeights` и `sideWeight` не участвуют. `sideWeight`
как атрибут окна переживает смену стороны (переносится вместе с `WindowInfoImpl`).

**TW-5.9 — ресайзы.** `=`
- `SetSideSize`: `setUnifiedAnchorWeight` из `movedOrResized` (`ToolWindowManagerDecorators.kt:185`) /
  `ToolWindowPane.setWeight`.
- `SetSideRatio`(R2): `info.sideWeight` из `movedOrResized` (`:171-179`).
- `SetUndockWeight`: для SLIDING тот же `info.weight`, геометрию сторон не трогает.
- `SetFloatingBounds`: `movedOrResized` для FLOATING/WINDOWED пишет `floatingBounds` (`:147-162`); UI зовёт
  при перемещении окна.

**TW-5.10 — `SetIconVisible(false)` закрывает и снимает иконку, `Order` жив.** `=`
`hideToolWindow(removeFromStripe=true)` (`:819-832`): `isShowStripeButton=false`, `removeStripeButton()`, но
`WindowInfoImpl` остаётся в раскладке с прежним `order`. Для Float/Window — тот же путь (сначала закрытие
окна). `paneId` сбрасывается к дефолтному (`:831`), сам порядок слота сохранён.

**TW-5.11 — `SetIconVisible(true)` возвращает на прежнее место.** `=`
IDEA не «запоминает и восстанавливает» позицию — она её **не теряет**. Механизм в трёх фактах:
1. в `DesktopLayout` нет метода удаления `info` — при снятии иконки `WindowInfoImpl` из раскладки не выходит,
   `order` цел;
2. пока панель скрыта, её `order` продолжает участвовать в нумерации: `DesktopLayout.setAnchor` (`:75-101`)
   при переупорядочивании соседей сдвигает и её — относительный слот держится сам;
3. при показе `showToolWindowImpl` переназначает `order` **только если он `-1`** (`:913-915`); здесь `order`
   не `-1`, потому берётся старый, а компаратор стрипа расставляет кнопку по нему среди текущих видимых
   (`setShowStripeButton`/`toolWindowAvailable`, `:1916-1947,1826-1844`).

Гарантируется **относительный** порядок (сортировка по стабильному `order`), а не тот же пиксельный индекс:
при дырах в нумерации место зависит от того, какие соседи сейчас видимы — отсюда «= IDEA в простом случае».

**TW-5.12 — `HideAll`.** `≠` (объём)
`HideAllToolWindowsAction` (`ide/actions/HideAllToolWindowsAction.kt`): собирает id через
`shouldBeHiddenByShortCut = isVisible && type.isInternal` (`HideToolWindowAction.kt:18-19`) — то есть только
**docked+sliding**; Float/Window **не** закрываются. Плюс сохраняет `layoutToRestoreLater` для обратного
«Restore Windows» (toggle). Спека TW-5.12 требует «все режимы» — расхождение, которое стоит учесть: либо
принять IDEA-объём, либо сознательно расширить.

**TW-5.13 — открытие панели с `IsIconVisible=false` включает иконку.** `=`
`showToolWindowImpl:912` безусловно ставит `isShowStripeButton=true`; выбор из «⋯» делает это явно
(TW-8.3). Открытой-без-иконки не бывает (INV-6).

**TW-5.14 — `Snapshot`/`Apply`/`ResetToDefaults`.** `=` c оговоркой по «отчёту»
- `Snapshot` = `DesktopLayout.copy()` (`:37-40`) — глубокая копия.
- `Apply` = `setLayout(newLayout)` (`:1217-1406`) — атомарная замена в 3 прохода: (1) show/hide/dock/
  rearrange, (2) веса, (3) revalidate. Примирение с дескрипторами и «making invisible» для отсутствующих
  (`:1233-1264`).
- `ResetToDefaults` = `ToolWindowDefaultLayoutManager.getLayoutCopy()` / `loadDefault` (`:544-552`).
- **Отчёт о нормализации** (наш пункт TW-10.4) у IDEA заменён логированием (`LOG.debug`, `checkInvariants`
  `:1949-1977`) — нет структурированного возврата исправлений. Наш `Apply`-report — надстройка.

**TW-5.15 — `SetQuickAccessSide`.** `=`
`setMoreButtonSide(side)` (`:1410-1423`) пишет `state.moreButton` и обновляет `MoreSquareStripeButton`
(видимость по `getMoreButtonSide()==side`, `MoreSquareStripeButton.kt:76-78`). Само меню «⋯» предлагает
«Move to Left/Right» (`:47-58`).

---

## Раздел 6. Автоскрытие и фокус

Механизм целиком в `ToolWindowManagerLifecycle.kt` (глобальный `AWTEventListener` на FOCUS/WINDOW-события,
`:146-168`).

**TW-6.1 — закрытие DockUnpinned/Undock при уходе фокуса, с исключениями.** `=`
`handleFocusEvent` (FOCUS_LOST, `:55-83`) → `hideIfAutoHideToolWindowLostFocus` (`:108-138`):
- условие типа: `isAutoHide || type==SLIDING` (`:116`) — ровно DockUnpinned + Undock;
- исключение «фокус ушёл в никуда / временно / компонент не показан»: ранний возврат (`:62-64`) — покрывает
  **деактивацию приложения** (переключение в другое приложение не закрывает панель);
- «панель сама запросила фокус» (`isAboutToReceiveFocus`, `:124-126`) — при переключении между sliding;
- попап/балун: `getParentBalloonFor != null` → не закрывать (`:131,133`);
- диалог: `getWindow(focusedComponent) is Dialog` → не закрывать (`:132-133`).
Все четыре исключения спеки присутствуют. Перетаскивание из панели покрывается тем, что DnD не переносит
фокус на «чужой» показанный компонент.

**TW-6.2 — клик/действие вне панели = потеря фокуса.** `=` (двумя путями)
Клик, уводящий фокус, идёт через тот же FOCUS_LOST. Дополнительно — реестр
`auto.hide.all.tool.windows.on.any.action` (`:216-227`): перед любым action прячет все unfocused autohide-
панели (кроме той, откуда action и кроме попапов). То есть закрытие «затем обрабатывается сам клик».

**TW-6.3 — `Esc` → фокус в док-зону, влечёт закрытие.** `=`
`Esc` возвращает фокус в редактор (`activateEditorComponent`/`focusDefaultComponentInSplitters`), что для
unpinned/undock через FOCUS_LOST даёт закрытие (TW-6.1). Отдельного «Esc-хендлера закрытия» нет — это
следствие фокус-модели.

**TW-6.4 — индикация открытых иконок; заголовок не регламентируется.** `=`
Открытость подсвечивает `SquareStripeButton` (селект/hover). Активную панель ядро дополнительно не метит в
заголовке — активность вычисляется от фокуса, заголовок при активации не меняется (подтверждает уточнение
владельца).

**TW-6.5 — активация панели ↔ активация документа.** `=`
`activateToolWindow` (`:582-629`) двигает `recentToolWindows`/`activeStack`; при получении фокуса редактором
`activeStack.clear()` (FOCUS_GAINED, `:84-92`), и `activeToolWindowId` становится null (getter видит
editor-фокус). Взаимный сброс налицо.

---

## Раздел 7. Float и Window

**TW-7.1 — `Float` = owned-окно поверх главного.** `=`
`FloatingDecorator(frame, decorator)` создаётся с владельцем-`frame` главного окна
(`ToolWindowManagerDecorators.kt:78-88`) — поверх главного, сворачивается с ним, вне таскбара.

**TW-7.2 — `Window` = независимое окно.** `=`
`WindowedDecorator(project, title, component)` — самостоятельный `JFrame` (`:90-135`), собственный z-порядок
и таскбар; заголовок «`<Title> - <Project>`».

**TW-7.3 — закрытие системной кнопкой = `Close`, режим жив.** `=`
`Disposer.register(windowedDecorator){ hideToolWindow(id,false) }` (`:106-110`) — закрытие окна вызывает
`hideToolWindow`, `type` остаётся WINDOWED, следующий `Open` снова откроет окно. Для Float аналогично.

**TW-7.4 — валидация `FloatingBounds` при восстановлении.** `=`
`setExternalDecoratorBounds` + `isValidBounds` (`:212-268`): если сохранённые bounds невалидны (заголовок вне
экранов / малое пересечение), окно центрируется относительно главного (`setLocationRelativeTo`, `:248-250`) с
дефолтным размером. Порог видимости — `ScreenUtil.isVisible` углов + «в основном видимо». Состояние bounds
при этом обновляется.

**TW-7.5 — закрытие главного окна: снапшот + закрытие плавающих.** `=`
`ProjectCloseListener.projectClosingBeforeSave` сохраняет floating/windowed-состояние
(`ToolWindowManagerLifecycle.kt:187-193`); `projectClosed` → `projectClosed()` снимает внешние декораторы
(`ToolWindowManagerImpl.kt:522-542`). Панели остаются `isVisible=true` в снапшоте и откроются при следующем
запуске.

**TW-7.6 — деградация `Window`→`Float`→`Undock` по возможностям платформы.** `=` (концептуально)
У IDEA доступность режимов идёт через `ProjectFrameCapabilitiesService`/`ToolWindowType` и скрытие пунктов
меню; в вебе (RemDev) WINDOWED недоступен и эффективный режим деградирует, при этом **хранимый** `type` не
меняется (`WindowInfoImpl` сериализует исходный). Наши `CanFloat`/`CanUseWindowed` — явная формализация того
же принципа.

**TW-7.7 — в браузере `Float` = псевдоокно в оверлей-слое.** `≈`
IDEA-фронтенд (RemDev) реализует «оконные» режимы в клиентском слое; десктопная модель того же
`WindowInfoImpl` переносится без изменения `type`. Точная реализация псевдоокна — на стороне клиента, деталь
не в этом дереве исходников.

**TW-7.8 — вытаскивание за заголовок = `SetMode(Float)`.** `=`
DnD за заголовок панели создаёт FLOATING с bounds у точки отпускания (drag-helper'ы `ToolWindowDragHelper`),
что эквивалентно `setToolWindowType(id, FLOATING)` + `floatingBounds`. До DnD — через меню «Float».

---

## Раздел 8. Быстрый доступ «⋯»

**TW-8.1 — «⋯» в конце Secondary, перемещается настройкой.** `=`
`MoreSquareStripeButton` живёт в центре тулбара (`ToolWindowToolbar.initMoreButton:93-98`), виден на стороне
`getMoreButtonSide()` (дефолт LEFT). Меню кнопки — «Move to Left/Right» → `setMoreButtonSide`
(`MoreSquareStripeButton.kt:47-58`).

**TW-8.2 — список = панели без иконки, сортировка.** `≠` критерий сортировки (закрывает открытый вопрос)
`ToolWindowsGroup.getToolWindowActions(project, shouldSkipShown=true)` (`ide/actions/ToolWindowsGroup.java`):
берёт панели, у которых **нет** видимой кнопки стрипа (`skip if isShowStripeButton && isAvailable &&
isStripeButtonShow`, `:51-53`) — т.е. `IsIconVisible=false`. **Сортировка**: `comparingMnemonic()` затем по
`toolWindowId` (`CASE_INSENSITIVE_ORDER`) (`:78-88`) — по мнемонике, затем по id алфавитно. Это НЕ сортировка
по прописке (side/group/order). Открытый вопрос TW-8.2 разрешён: критерий IDEA — мнемоника+алфавит; наша
сортировка по прописке — сознательное отклонение.

**TW-8.3 — выбор элемента = вернуть иконку + открыть.** `=`
Элемент списка — `ActivateToolWindowAction`; `actionPerformed` активирует панель (`Open+activate`), а
показ выставляет `isShowStripeButton=true` (`showToolWindowImpl:912`). Иконка возвращается на стрип, панель
открывается.

**TW-8.4 — «⋯» скрывается при пустом списке.** `=`
`AbstractMoreSquareStripeButton.updateState` (`MoreSquareStripeButton.kt:132-140`): `isVisible =
getToolWindowActions(...).isNotEmpty()`. Пустой список — кнопка скрыта.

---

## Раздел 9. Жизненный цикл контента

**TW-9.1 — дескриптор регистрации.** `=`
`RegisterToolWindowTaskData`/`ToolWindowEP`: `id`, `stripeTitle`, `icon`, `anchor`, `sideTool` (Group),
фабрика `contentFactory`, `shouldBeAvailable`, `canCloseContent` и пр. (`ToolWindowManagerImpl.kt:475-485`).
Дефолтный `Order` — `getNextOrder` при регистрации (`:1050`).

**TW-9.2 — политики создания/удержания.** `=` (эквиваленты)
- Создание: `OnFirstOpen` (дефолт, `contentFactory.init` при первом показе) vs `Eager` — у IDEA лениво по
  умолчанию; `factory.manage` стартует корутиной (`:1097-1100`). `scheduleContentInitializationIfNeeded`
  (`doShowWindow:988`).
- Удержание: `KeepWhileRegistered` vs `DisposeOnClose` — у IDEA близко `hideOnEmptyContent`/пересоздание
  контента фабрикой. Прямой оси «Dispose on close» как отдельного флага в этом файле нет — наша ось шире.

**TW-9.3 — состояние не зависит от материализации контента.** `=`
Раскладка (`WindowInfoImpl`) отделена от `ToolWindowImpl.contentManager`; `isVisible` может быть true до
создания контента (`registerToolWindow:1061-1064` гасит visible, если фабрики нет). Создание — обязанность
слоя UI (`scheduleContentInitializationIfNeeded`).

**TW-9.4 — `Unregister`: закрыть, освободить контент, «спящее» состояние.** `=`
`doUnregisterToolWindow` (`:1168-1197`): снимает декоратор/кнопку, `Disposer.dispose(entry.disposable)`, но
`WindowInfoImpl` в `DesktopLayout` **сохраняется** — повторная регистрация того же `id` подхватит его
(`registerToolWindow` берёт `existingInfo`, `:1043`). Это и есть «спящее» состояние.

**TW-9.5 — контент панели = дерево таб-групп.** `≈` (важное различие модели)
`InternalDecoratorImpl` умеет режимы `SINGLE / VERTICAL_SPLIT / HORIZONTAL_SPLIT / CELL` (`:264-270`) с
`OnePixelSplitter` и **отдельным `ContentManager` на ячейку** (`splitWithContent:376-386`,
`doUpdateMode:344-371`). Вырожденный SINGLE — обычная панель без полосы разбиения. То есть IDEA действительно
даёт «дерево» разбиений внутри панели. **Но**: (1) split бинарный (first/second), а не произвольная n-арная
таб-группа; (2) модель контента панели — `ContentManager`, а модель редакторов — `EditorsSplitters` — они
**разные**. Спека унифицирует контент панели и док-зоны в одно дерево той же модели — это наш дизайн, не
буквальная IDEA. Помечено `= IDEA` в спеке со ссылкой на terminal Reworked — по духу верно, по структуре —
обобщение.

**TW-9.6 — слой слотов оперирует панелью как атомом.** `=`
Все слот-операции (`Move`, `SetMode`, вытеснение) работают с `ToolWindowEntry`/`WindowInfoImpl` целиком;
внутрь `ContentManager`/`InternalDecoratorImpl` не заглядывают. Перенос панели тащит её декоратор со всем
содержимым.

**TW-9.7 — `canHost` по владельцу вкладки.** `—` (нет прямого эталона)
У IDEA контент панели держит `ContentManager` этой панели; «документ в панель» структурно невозможно, т.к.
редакторы — иной слой (`FileEditorManager`). Явного реестра `canHost` с проверкой владельца по id нет — это
наша модель для унифицированного дерева. Ограничение «вкладка чужой панели невыразима» у IDEA обеспечено
раздельностью `ContentManager`'ов, а не проверкой.

**TW-9.8 / TW-9.9 / TW-9.10 — перемещения вкладок, персист структуры, независимость перенесённых вкладок.**
`—`/`≈`
Перетаскивание контента между ячейками — `InternalDecoratorImpl.splitWithContent`/`unsplit`
(`:376`, `:112-127` перенос `Content` между менеджерами). Персист структуры разбиения панели — часть layout,
контент восстанавливают фабрики (ADR-0003 у нас; у IDEA — `ToolWindowFactory` + сериализация `Content`).
Сценарий «вкладка панели живёт в док-зоне независимо от панели-владельца» — это уже наша унифицированная
модель дерева; в IDEA tool-window content и editor tabs не смешиваются, поэтому прямого аналога нет.
Разбор этих пунктов — в спеке document-area, здесь только отмечаем, что эталон их не диктует.

---

## Раздел 10. Персистентность

**TW-10.1 — сериализуется полное состояние + `SchemaVersion`.** `=` (формат иной)
`ToolWindowManagerStateImpl : PersistentStateComponent<Element>` (`ToolWindowManagerState.kt:38-90`) — формат
**XML/JDOM** (не JSON), хранилище `PRODUCT_WORKSPACE_FILE`. Пишет `layout`/`layoutV2`, `layout-to-restore`,
`recentWindows`, `moreButton`. Версионирование — через теги `layout` (old UI) vs `layoutV2` (new UI),
`:100-122`; отдельного числового `SchemaVersion` нет — версия закодирована именем тега. Наш JSON+int
`SchemaVersion` — эквивалент по назначению.

**TW-10.2 — «спящие» состояния переживают round-trip.** `=`
`DesktopLayout.readExternal` (`:103-132`) читает все `window_info` в `idToInfo` независимо от регистрации;
`writeExternal` (`:147-167`) пишет их все обратно. Незарегистрированный `id` сохраняется — регистрация
плагином может прийти позже.

**TW-10.3 — примирение сохранёнка ⊃ дескриптор.** `=`
`registerToolWindow` берёт `existingInfo` если есть (сохранённое побеждает, `:1043-1053`); иначе
`layout.create(task)` с дефолтами и `order = getNextOrder` (после существующих в слоте, `:1050`). Ровно
правило спеки.

**TW-10.4 — нормализация при `Apply`.** `=` по правилам, `≠` по «отчёту»
- Дубликаты `id`: `Map` по ключу — остаётся один (последний при чтении/первый по смыслу).
- Несколько открытых докируемых в слоте: `doShowWindow`-вытеснение оставит одну; при чтении
  `WindowInfoImpl.normalizeAfterRead` гасит невалидную видимость (`:116-122`).
- Уплотнение `Order`: `normalizeOrder` (`DesktopLayout.kt:198-214`) — с сохранением относительного порядка
  (стабильная сортировка компаратором).
- Клемп весов: сеттеры `WindowInfoImpl`.
- Невалидный active: getter вернёт null.
**Но**: спека требует структурированный **отчёт** об исправлениях (round-trip property-инвариант). У IDEA —
только `LOG` + `checkInvariants` (EAP/internal, `:1949-1977`). Отчёт — наша надстройка.

**TW-10.5 — версия новее/битый документ → ошибка загрузки.** `≈`
У IDEA несовместимый layout деградирует к дефолтам мягко (нераспознанные теги игнорируются,
`normalizeAfterRead`), явной «ошибки загрузки с решением вызывающей стороны» нет — платформа старается
никогда не падать на workspace. Наше «явная ошибка → приложение решает (обычно ResetToDefaults)» — более
строгий контракт.

**TW-10.6 — области `Full` / `Arrangement`.** `≈`
`Full` ≈ обычный `setLayout` (весь `DesktopLayout`: слоты, веса, но контент-деревья в IDEA живут в
`ContentManager`, а не в layout). `Arrangement` (размещение без контента) у IDEA — это именно как работает
`setLayout`: он переставляет/показывает панели, а `Content` не пересоздаёт (материализация ленивая,
`:988`). Явного разделения на два scope в API IDEA нет — `setLayout` по природе «arrangement», а полный
контент восстанавливается отдельными механизмами. Наши два scope формализуют это различие.

**TW-10.7 — слияние `Arrangement` с текущим (частичный макет).** `≈`
`setLayout` (`:1217-1406`) частично реализует ту же логику: для панелей, которых нет в новом layout,
берётся дефолт или гасится видимость/иконка (`:1233-1264`) — это «неизвестное макету». Конфликт открытости
в слое чинит `doShowWindow`-вытеснение (наш E24). Однако IDEA `setLayout` **полон** (перечисляет все
известные окна), тогда как наш `Arrangement` явно **частичен** (затрагивает только упомянутые id, чужие
атрибуты не переписывает, а коллективные свойства — порядок/единственность — пересчитывает). Тонкая
семантика частичного слияния (собственные vs коллективные свойства) — наша; у IDEA нет отдельного
«частичного apply».

---

## Раздел 11. Каталог краевых случаев (E1–E26)

Кратко — на какой механизм эталона ложится каждый случай (детали см. в соответствующих пунктах выше).

| # | Механизм в IDEA | Вердикт |
|---|---|---|
| E1 | `doShowWindow`-вытеснение по `type+split+anchor`; поля A нетронуты (`setHiddenState`) | `=` |
| E2 | вытеснение только при совпадении `isSplit` → B (Secondary) не тронута | `=` |
| E3 | `removeDecorator` освобождает сторону; `unifiedWeights` сохранён. **Но** SideStack мог бы восстановить ранее вытеснённую (если включён реестром) | `=` / см. расхождение SideStack |
| E4 | `setSideToolAndAnchor` + `hideIfNeededAndShowAfterTask` — «движущаяся побеждает» | `=` |
| E5 | Float: `setToolWindowAnchorImpl` ветка без пере-дока — меняется только прописка | `=` |
| E6 | `setToolWindowType(DOCKED)` у видимой → `doShowWindow` вытесняет B | `=` |
| E7 | `hideToolWindow(removeFromStripe=true)` → закрыта, иконки нет, в «⋯» | `=` |
| E8 | тот же путь для WINDOWED: окно закрыто, иконки нет, в «⋯» | `=` |
| E9 | FOCUS_LOST → `hideIfAutoHideToolWindowLostFocus` (`type==SLIDING`/autoHide) | `=` |
| E10 | исключение `getParentBalloonFor != null` — комбобокс/попап не закрывает | `=` |
| E11 | `normalizeAfterRead` + вытеснение → одна открытая (меньший `order` через нормализацию) | `=` |
| E12 | «спящий» id переживает round-trip (`readExternal`/`writeExternal`) | `=` |
| E13 | деградация WINDOWED→Float в вебе, хранимый `type` не меняется | `=` |
| E14 | `isValidBounds` false → центрирование у главного окна, bounds обновлены | `=` |
| E15 | регистрация в непустой слот → `getNextOrder` (в конец), закрыта по дескриптору | `=` |
| E16 | INV-2: SLIDING (другой `type`) сосуществует с DOCKED | `=` |
| E17 | FOCUS_LOST закрывает undock (SLIDING), pinned остаётся | `=` |
| E18 | `activateToolWindow` берёт `unifiedAnchorWeight` → унаследованная ширина | `=` |
| E19 | R1: показ X ставит сплиттер из `X.sideWeight` (`setSideWeight`) | `=` (одно поле) |
| E20 | `setLayout` (Arrangement) переставляет панели, `ContentManager`/документы живы | `≈` |
| E21 | `order` сохранён при скрытии → иконка встаёт по нему (компаратор стрипа) | `=` |
| E22 | drop по видимым кнопкам: IDEA даёт `[1,2,3,4,X,5]` или `[X,1,2,3,4,5]` (order преемника), не наш `[1,X,2,3,4,5]` — см. TW-1.5 | `≈` (наша конвенция предсказуемее) |
| E23 | `setLayout`: панель не в макете → дефолт/гашение; для «зарегистрирована после» — не тронута | `≈` |
| E24 | конфликт открытости в слое → `doShowWindow`-вытеснение (Y вытеснена) | `=` |
| E25 | порядок слота: упомянутые по макету, прочие в конец — принцип `getNextOrder`/`normalizeOrder` | `≈` (частичный merge — наш) |
| E26 | «вкладка панели в док-зоне переживает закрытие панели» — вне модели IDEA (раздельные слои) | `—` |

---

## Итог: что перенять и на что смотреть

**Совпадает с IDEA (можно опираться уверенно)**: слот = `anchor`+`isSplit`; вытеснение по равенству
`type+split+anchor` (INV-2, TW-5.1) — ядро всей модели; автоскрытие по фокусу с исключениями (раздел 6);
Float=owned / Window=independent (раздел 7); валидация bounds (TW-7.4); toggle-логика клика и шортката
(TW-5.4/5.5); вес на сторону через `UnifiedToolWindowWeights` (TW-2.5/2.6); порядок стрипа и разделитель
(TW-1.2/1.3/1.4); «спящие» состояния round-trip (TW-10.2/10.3).

**Расхождения — держать в поле зрения при реализации/ревью спеки**:
1. `Order` в IDEA плотен **по `anchor`**, не по слоту (INV-3) — наш инвариант строже.
2. **SideStack** восстанавливает вытеснённую панель при закрытии заменившей (R3/E3) — у нас нет; в new UI
   IDEA выключен реестром.
3. **HideAll** у IDEA не трогает Float/Window (TW-5.12) — решить, расширяем ли.
4. Список **«⋯»** сортируется по мнемонике+алфавиту, не по прописке (TW-8.2) — открытый вопрос закрыт.
5. Дефолт **`Weight` = 0.33** (мы рекомендуем 0.25/0.30).
6. **`sideWeight`** — одно поле у IDEA; наши `CurrentRatio`+`PairRatio` — расщепление (TW-2.5/2.7).
7. **`ActiveToolWindowId`** у IDEA вычисляемый, не хранимый (INV-5, TW-6.5).
8. **Контент панели** — `ContentManager` с бинарным split, модель ≠ редакторам; наша унификация
   панель+док-зона в одно дерево — дизайн-решение вне буквальной IDEA (раздел 9, TW-9.5/9.7/9.10).
9. **Отчёт нормализации** `Apply` и **явная ошибка версии** — наши контракты; IDEA логирует и мягко
   деградирует (TW-10.4/10.5).
10. **Частичный `Arrangement`-merge** (собственные vs коллективные свойства, TW-10.7) — наша семантика;
    у IDEA `setLayout` полон.
