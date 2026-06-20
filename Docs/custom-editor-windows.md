# Custom Editor Windows

Write your own editor windows — inspectors, tools, dashboards — **without touching ImGui**. You
subclass one public base (`EditorWindow`) and draw through one seam (`IEditorGui`); the editor handles
the window, docking, menu entry, and lifecycle. This is the same model Unity uses: editor code lives in
a separate assembly that the shipped game (player) never loads.

## TL;DR

1. Create a script under `Assets/Editor/` (the folder name matters — it's how the engine knows it's
   editor-only code).
2. Subclass `EditorWindow`, mark it `[EditorWindowMeta("My Tool")]`, fill `OnGui(IEditorGui)`.
3. Alt-tab back into the editor (or Ctrl+R). Your window appears under **Window > My Tool**.

```csharp
// Assets/Editor/MyTool.cs
using BallisticEngine.Editor;

[EditorWindowMeta("My Tool", "Window/My Tool", Width = 360, Height = 240)]
public sealed class MyTool : EditorWindow {
    int clicks;
    float amount = 0.5f;
    string note = "";

    protected override void OnGui(IEditorGui gui) {
        gui.Text("Hello from a custom editor window.");
        gui.Separator();

        if (gui.Button($"Clicked {clicks}x"))
            clicks++;

        gui.SetNextItemWidth(-1);
        gui.SliderFloat("Amount", ref amount, 0f, 1f, "%.2f");

        gui.SetNextItemWidth(-1);
        gui.InputText("Note", ref note, 128);
    }
}
```

That's the whole contract. No `using Hexa.NET.ImGui` — and you couldn't add one even if you tried: the
ImGui binding isn't referenced by the assembly your editor scripts compile into.

## Why `Assets/Editor/`

Scripts under `Assets/Editor/` compile into a **separate, editor-only assembly** (`EditorScripts.dll`)
that references the editor. Your normal game scripts (everything else under `Assets/`) compile into
`GameScripts.dll`, which the **player ships and loads**. The player has no editor assembly, so editor
code must stay out of `GameScripts.dll` — putting it under `Assets/Editor/` is what keeps that separation
automatic. (Try referencing `EditorWindow` from a normal game script and it won't compile — the game
assembly doesn't reference the editor.)

| Folder | Compiles into | Loaded by | May reference |
|---|---|---|---|
| `Assets/**` (except `Editor/`) | `GameScripts.dll` | editor **and** player | engine only |
| `Assets/Editor/**` | `EditorScripts.dll` | editor **only** | engine **+ editor** (+ your game types) |

Your editor scripts can use your own game types (the editor-script assembly references
`GameScripts.dll`), so a tool window can inspect or drive your components.

## `[EditorWindowMeta]`

```csharp
[EditorWindowMeta(
    title:    "My Tool",          // window title + default menu leaf
    menuPath: "Window/My Tool",   // optional; defaults to "Window/<title>". Use "Tools/Sub/Thing" to nest.
    order:    100)]               // sort order among siblings under the same menu (lower = higher up)
```

Named extras:

```csharp
[EditorWindowMeta("My Tool", Icon = EditorIcons.Wrench, Width = 420, Height = 540)]
```

The attribute is the single source of identity — you don't set the title/icon/size in a constructor.
(If you *do* set them in a constructor, your values win.)

## `IEditorGui` — the drawing seam

`OnGui(IEditorGui gui)` is called once per frame while the window is open. `IEditorGui` mirrors the
common immediate-mode surface: text, buttons, sliders/drag/input fields, combos, checkboxes, tree nodes,
collapsing headers, child regions, tables, popups, menus, tooltips. For custom drawing there's
`gui.WindowDrawList` (lines/rects/circles/text/bezier) and `gui.Input` (mouse/keyboard polling).

A few conventions:

- `gui.Scale` is the editor UI scale (DPI × user setting) — multiply pixel sizes by it.
- Width `-1` means "fill the remaining row" (e.g. `gui.SetNextItemWidth(-1)`).
- Value widgets take `ref` and return `true` the frame they change, e.g. `if (gui.SliderFloat(...)) { ... }`.
- Format strings use the printf convention ImGui expects: `"%.2f"`, `"%d"`.

Because you draw through the seam (not ImGui directly), the same window code keeps working if the editor
swaps its rendering backend, and it can be exercised by a recording fake in tests.

## Lifecycle

- `OnEnable()` / `OnDisable()` — override to allocate/release resources when the window opens/closes.
- The window's open state, docking position, and maximize are owned by the editor shell — you never
  call `Begin`/`End` or manage visibility yourself.

## Hot reload

Edit the script and alt-tab back into the editor (or press **Ctrl+R**). The editor recompiles
`EditorScripts.dll`, reloads it, and re-discovers your windows — add, remove, or rename windows live.
Compile errors appear in the **Console** as `Assets/Editor/...(line,col): error CSxxxx` and leave the
previously-loaded windows running.

## Built-in vs. user windows

Engine panels that need constructor arguments (Inspector, Build, …) are registered explicitly by the
editor and don't carry `[EditorWindowMeta]`. Your windows are discovered by the attribute and must have
a **public parameterless constructor** (the editor instantiates one of each). That's the only
requirement beyond subclassing `EditorWindow`.
