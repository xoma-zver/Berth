# Начало работы

Прикладной гайд: как подключить Berth к Avalonia-приложению и собрать раскладку. Нормативное
поведение — в [spec/](spec/); здесь только точка входа. Код, идентификаторы и XML-doc —
английские; этот документ, как и прочие в `docs/`, — русский.

## Установка

Два пакета:

- **`Berth.Core`** — модель раскладки и операции; без зависимости на UI-фреймворк
  (ADR-0002), пространство имён `Berth`.
- **`Berth.Avalonia`** — контролы, материализующие модель; зависит от `Berth.Core`,
  пространство имён `Berth.Controls`.

Ставится один пакет контролов — ядро приходит транзитивно:

```sh
dotnet add package Berth.Avalonia
```

## Композиция раскладки

Приложение поставляет три вещи: **реестр** дескрипторов (`ToolWindowRegistry`),
**координатор** жизненного цикла контента (`ContentLifecycle`) и начальное **состояние**
(`LayoutState`). Собрать их вручную можно, но проще fluent-builder'ом
`LayoutCompositionBuilder` — он регистрирует дескрипторы, выставляет дефолтную расстановку по
правилу `ResetToDefaults` и применяет начальную открытость командами (TW-5.14, TW-9.2):

```csharp
var composition = new LayoutCompositionBuilder()
    .AddToolWindow("project", "Project", w => w
        .Slot(ToolWindowSide.Left, ToolWindowGroup.Primary)
        .Eager()                              // создать контент при регистрации (TW-9.2)
        .Content(_ => new ProjectViewModel())) // MVVM-путь: вид строят DataTemplates приложения
    .AddToolWindow("terminal", "Terminal", w => w
        .Slot(ToolWindowSide.Bottom, ToolWindowGroup.Primary)
        .DisposeOnClose()                     // освобождать контент при закрытии (TW-9.2)
        .Content(_ => new TerminalControl())  // Control хостится напрямую
        .Tabs(                                // вкладки панели по заявке-префиксу (TW-9.11)
            id => id.StartsWith("term:", StringComparison.Ordinal),
            id => new TerminalControl()))
    .AddDockContent(                          // документы док-зоны по заявке-префиксу
        id => id.StartsWith("doc:", StringComparison.Ordinal),
        id => new DocumentControl(id))
    // Начальная открытость — команды, не поля дескриптора (E15):
    .Open("project")
    .Open("terminal")
    .OpenDocument("doc:README.md")
    .OpenPanelTab("term:Local")
    .Build();
```

`composition.State` иммутабельно и потому годится как снимок дефолтов приложения (TW-5.14):
сохраните его, чтобы позже сбросить раскладку без потери документов —
`current.Apply(composition.State, ApplyScope.Arrangement, composition.Registry)` восстановит
прописку и дефолтную открытость, не трогая док-зону (TW-10.6).

## Проводка контрола

```csharp
var workspace = new BerthWorkspace
{
    Registry = composition.Registry,
    Lifecycle = composition.Lifecycle,
    State = composition.State,
};
```

`State` — двусторонний: каждый завершённый жест присваивает результат команды обратно
(ADR-0004), приложение наблюдает изменения через `GetObservable(BerthWorkspace.StateProperty)`
или биндинг. Необязательно: `TabTitleProvider` (id → заголовок вкладки, DA-9.6),
`ShortcutHintProvider` (id → подсказка шортката в тултипе иконки, TW-5.5); кеймап активации
приложение вешает на публичную команду `BerthWorkspace.ActivateToolWindow(id)`.

## Разметочный вариант (AXAML)

Ту же композицию можно объявить декларативно и отдать `BerthWorkspace.Definition` — workspace
самособерётся при первом аттаче (явные `Registry`/`Lifecycle` побеждают):

```xml
<berth:BerthWorkspace xmlns:berth="using:Berth.Controls">
  <berth:BerthWorkspace.Definition>
    <berth:BerthLayoutDefinition>
      <berth:ToolWindowDefinition Id="project" Title="Project"
                                  Side="Left" Group="Primary" IsOpen="True">
        <views:ProjectView/> <!-- прямой Content-синглтон, легален при Keep -->
      </berth:ToolWindowDefinition>
      <berth:DockContentDefinition TabIdPrefix="doc:">
        <berth:DockContentDefinition.ContentTemplate>
          <DataTemplate><views:DocumentView/></DataTemplate>
        </berth:DockContentDefinition.ContentTemplate>
      </berth:DockContentDefinition>
      <berth:DocumentDefinition Id="doc:README.md"/>
    </berth:BerthLayoutDefinition>
  </berth:BerthWorkspace.Definition>
</berth:BerthWorkspace>
```

Прямой `Content` — синглтон разметки, легален только при `KeepWhileRegistered`; для полного
жизненного цикла (`DisposeOnClose`) используйте `ContentTemplate`/`TabTemplate` —
декларативную фабрику, строящую вид на каждое создание (TW-9.2).

## Персистентность

Формат и версия документа — забота ядра; где хранить и когда писать — политика приложения
(ADR-0003). Кирпичики публичны:

- `LayoutPersistence.Serialize`/`Deserialize` — JSON со `SchemaVersion` (несовместимая версия
  или битый документ → `LayoutFormatException`, TW-10.5);
- `LayoutApply.Apply(snapshot, scope, registry, validateBounds)` — атомарное применение с
  отчётом об исправлениях (`ApplyResult.Fixes`, TW-10.4);
- `LayoutApply.ResetToDefaults(registry)` — дефолтная раскладка;
- `FloatingBoundsValidation.CreateValidator(window)` / `CreateOverlayValidator(workspace)` —
  валидация сохранённых экранных границ (TW-7.4).

Полный образец прикладного слоя — «мини-IDE» демо в
[`samples/Berth.Demo`](../samples/Berth.Demo): шов `ILayoutStore`, файловое хранилище на
десктопе и localStorage в браузере, восстановление до показа окна, автосейв с дебаунсом.

## Дальше

- Поведение: [spec/tool-windows.md](spec/tool-windows.md),
  [spec/document-area.md](spec/document-area.md).
- Решения: [adr/](adr/).
- Демо: [`samples/Berth.Demo`](../samples/Berth.Demo) и десктоп-хост
  `samples/Berth.Demo.Desktop`.
