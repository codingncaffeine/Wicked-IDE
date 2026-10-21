# Extending Wicked

Wicked can be extended four ways, from the lightest to the heaviest. None is required: every setting, theme and layout
choice is also reachable from Settings and the built-in themes.

1. **`settings.json`, `keybindings.json`, snippets, themes**: files in the config folder, in VS Code's formats.
2. **`init.csx`**: one C# script that runs when Wicked starts, with the extension API as a global called `wicked`.
3. **Declarative extensions**: a folder with a `wicked-extension.json` that adds themes, TextMate grammars, snippets,
   language servers, key bindings and setting defaults, with no code.
4. **Code extensions**: a .NET assembly with a class that implements `IWickedExtension`, plus anything a declarative
   extension has.

All four use the same API, **version 1.0**. A minor version only ever adds members; a major version may remove them. An
extension says which versions it was written for (`"engines": { "wicked": "^1.0" }`) and Wicked refuses to load one
outside that range, naming the range in the message.

## The rules every extension lives by

* **Nothing you do blocks the interface.** Your code runs on worker threads: `ActivateAsync`, command handlers and event
  handlers are all called on the thread pool, never on the interface thread. Every API call that touches the window is
  asynchronous and does its window work itself. Do not call `.Result` or `.Wait()` on them.
* **Everything you register is taken back** when the extension is disabled or uninstalled (or `init.csx` reloaded):
  commands, keys, panels, marks in editors, notices, event handlers, language servers, snippets, themes and grammars. You
  do not have to unregister anything yourself.
* **An error in your code never takes the window down.** An exception in a command or an event handler is written to
  the Output panel (channel "Extensions") with a notice, and the extension keeps running. An exception while activating
  switches the extension off with a notice. (A thread you start yourself that throws without catching is the one thing
  the host cannot catch; wrap your own threads.)
* **Offsets are UTF-16 code units** into the document's text, in which every line break is a single `\n` (the editor
  keeps the file's real line endings aside and writes them on save).

## Your first command

`init.csx` (Command palette: **Preferences: Open init.csx** writes a commented starter and opens it):

```csharp
wicked.Commands.Register(
    id: "init.hello",
    title: "Say Hello",
    run: () => wicked.Notices.Post("Hello from init.csx", NoticeLevel.Success),
    keys: "Ctrl+Alt+H",
    category: "My Commands");
```

Save the file, then run **Preferences: Reload init.csx**. The command is in the palette as "My Commands: Say Hello" and
Ctrl+Alt+H runs it. Keys are written the way the palette shows them (`Ctrl+K Ctrl+H` is a chord); VS Code's lower-case
spelling (`ctrl+alt+h`) is accepted too. The user's `keybindings.json` always wins over an extension's keys.

As a code extension, the same command:

```csharp
using Wicked.Core.Extensions;

[assembly: WickedExtension(typeof(Hello.HelloExtension))]

namespace Hello;

public sealed class HelloExtension : IWickedExtension
{
    public Task ActivateAsync(IWickedApi api, CancellationToken ct)
    {
        api.Commands.Register("hello.say", "Say Hello",
            () => api.Notices.Post("Hello from the extension", NoticeLevel.Success), "Ctrl+Alt+H", "Hello");
        return Task.CompletedTask;
    }
}
```

`[assembly: WickedExtension(typeof(...))]` names the main type; without it, the assembly's single `IWickedExtension`
implementation is used. If the type also implements `IDisposable` or `IAsyncDisposable` it is disposed when the
extension is switched off, after everything it registered has been taken back.

## Reading and changing text

```csharp
wicked.Commands.Register("init.upper", "Upper-case the Selection", async ct =>
{
    var doc = await wicked.Editors.GetActiveAsync();        // an immutable snapshot: id, path, language, version, text, selections
    if (doc is null) return;
    foreach (var range in doc.Selections.Where(r => !r.IsEmpty).Reverse())   // last first, so earlier offsets stay valid
    {
        var text = doc.Text[range.Start..range.End].ToUpperInvariant();
        // expectedVersion: refused (false) if the document changed while you worked, so you never edit text that moved
        if (!await wicked.Editors.ReplaceAsync(doc.Id, range, text, doc.Version)) break;
    }
}, null, "My Commands");
```

* `GetActiveAsync`, `GetOpenAsync`, `GetAsync(id)`: snapshots. A document's **id** is its path, or `untitled:<name>`.
* `ReplaceAsync(id, range, text, expectedVersion?)`: one undo step; false when the document is read-only, closed or newer.
* `InsertAtCaretsAsync(text)`: at every caret of the active editor (a selection is replaced), one undo step.
* `SetSelectionsAsync(id, ranges)`.

## Marks in the editor: decorations

An extension colours text by putting **marks** in a named **layer** of a document. Setting a layer replaces only your
layer of that document; marks follow their text through edits until you set the layer again; other extensions' layers and
the language servers' diagnostics are never touched.

```csharp
async Task Mark(EditorSnapshot doc)
{
    var ranges = new List<RangeMark>();
    for (var i = doc.Text.IndexOf("TODO", StringComparison.Ordinal); i >= 0; i = doc.Text.IndexOf("TODO", i + 4, StringComparison.Ordinal))
        ranges.Add(new RangeMark(i, i + 4, MarkStyle.Warning));

    await wicked.Editors.SetDecorationsAsync(doc.Id, "todos",
        new Decorations(Ranges: ranges, Gutter: new[] { new GutterMark(0, GutterMarkKind.Modified) }),
        forVersion: doc.Version);     // worked out for this text; carried forward over edits made since
}

wicked.Events.DocumentOpened += e => Mark(e.Document);
wicked.Events.DocumentSaved += e => Mark(e.Document);
```

| Kind | Type | Styles |
| --- | --- | --- |
| Ranges | `RangeMark(start, end, style)` | `Error`, `Warning`, `Info`, `Hint`, `Unnecessary`, `Deprecated`, `Highlight`, `Inserted`, `Removed`, `Link` |
| Whole lines | `LineMark(line, style)` | `Inserted`, `Removed`, `Highlight` |
| Gutter | `GutterMark(line, kind)` | `Added`, `Modified`, `Deleted`, `Bookmark` |
| Inline text | `InlineMark(offset, text, style)` | `Hint` (a pill), `Ghost` (faded text) |

Lines are zero-based and always mean the current text's lines; use ranges when a result is for an older version.
`ClearDecorationsAsync(id, layer?)` takes your marks off.

## Panels

```csharp
using Avalonia.Controls;

await wicked.Views.AddBottomTabAsync("todos", "Todos", () => new ListBox { ItemsSource = new[] { "one", "two" } });
await wicked.Views.AddSidePanelAsync("todos-side", "Todos", icon: "list-todo", () => new TextBlock { Text = "..." });
await wicked.Views.ShowAsync("todos");
```

`IViewHost` is the whole of what an extension may do to the window's layout. The factory returns an Avalonia
`Control`; it is called on the interface thread once, the first time the panel is shown, so it should only build the
control. Do slow work on a worker and post results back with `Dispatcher.UIThread.Post(...)`. The icon is the name of
one of the built-in icons (`puzzle`, `boxes`, `git-branch`, ...); an unknown name gets the puzzle piece.

Code extensions that build controls reference Avalonia the way Wicked does (see Packaging); `init.csx` already
references it and imports `Avalonia.Controls`.

## Everything else in `IWickedApi`

| Member | What it does |
| --- | --- |
| `Commands` | `Register`, `BindKeys` (add a key for any command), `ExecuteAsync(id)`, `ListAsync()` |
| `Notices` | `Post(text, level, key?, route?)` to the footer; a click goes to `route` (`panel/extensions`, `output/<channel>`) |
| `Output` | `Channel(name).WriteLine(...)`, `Clear()`, `ShowAsync()`: a channel of the Output panel |
| `Settings` | `GetAsync<T>(key, fallback)` (the user's value, else your manifest default, else the fallback; Wicked's own settings too), `SetAsync(key, value)` (writes `settings.json`), `Changed` (the keys that changed) |
| `Workspace` | `GetRootAsync()`, `OpenFileAsync(path, line?, column?)` |
| `Events` | `DocumentOpened`, `DocumentSaved`, `DocumentClosed`, `ActiveEditorChanged`, each with a snapshot. Handlers run one at a time, in order, on worker threads |
| `Contributions` | `AddLanguageServer(spec)`, `AddSnippetFile(path)`, `AddThemeFile(path)`, `AddGrammar(spec)`: the same things a manifest can declare, from code |
| `ExtensionId`, `ExtensionFolder`, `ApiVersion` | who you are, where your files are, which API this is |

## The manifest: `wicked-extension.json`

```json
{
  "id": "hello",
  "name": "Hello",
  "version": "1.0.0",
  "publisher": "acme",
  "description": "Says hello.",
  "homepage": "https://example.com/hello",
  "engines": { "wicked": "^1.0" },
  "main": "Hello.dll",
  "activationEvents": [ "onLanguage:foolang" ],
  "contributes": {
    "themes":    [ { "label": "Dusk", "path": "themes/dusk.json" } ],
    "grammars":  [ { "language": "foolang", "scopeName": "source.foo", "path": "foo.tmLanguage.json",
                     "extensions": [".foo"], "fileNames": ["Foofile"], "configuration": "language-configuration.json" } ],
    "snippets":  [ { "language": "foolang", "path": "snippets/foo.json" } ],
    "languageServers": [ { "id": "foo-ls", "name": "Foo", "languages": ["foolang"],
                           "programs": ["foo-ls", "./bin/foo-ls"], "arguments": ["--stdio"],
                           "install": "npm", "package": "foo-language-server", "rootMarkers": ["foo.toml"] } ],
    "keybindings": [ { "command": "hello.say", "key": "ctrl+alt+h" } ],
    "settings":  { "hello.greeting": "Hello" }
  }
}
```

| Field | Meaning |
| --- | --- |
| `id`, `publisher` | Lower-case letters, digits, `-` and `_`. The extension's identity is `publisher.id`, and its folder is `publisher.id-version`. |
| `name`, `version`, `description`, `homepage` | Shown in the Extensions panel. `version` is `major.minor.patch`. |
| `engines.wicked` | The API range you need: `^1.0` (1.0 up to 2.0), `~1.2`, `>=1.1`, `1.0`, or a list separated by spaces. Required. |
| `main` | The assembly, relative to the extension's folder. Leave it out for a declarative extension. |
| `activationEvents` | `onStartup` (the default) or `onLanguage:<id>`: the assembly is only loaded when a document of that language is first opened. |
| `contributes.themes` | VS Code theme JSON files, offered by the theme picker. |
| `contributes.grammars` | TextMate grammars (`.tmLanguage.json`) with the file extensions and names they claim; `configuration` is a VS Code language configuration (comments, bracket pairs, folding markers). |
| `contributes.snippets` | Snippet files in VS Code's format; `language` scopes a file whose snippets name no language. |
| `contributes.languageServers` | A server the IDE can start and install. `programs` are names to look for (a name starting with `./` is a file inside the extension); `install` is `system`, `github`, `npm`, `dotnet` or `go`, as for the built-in servers. The server of an installed extension is tried before the built-in one for the same language. |
| `contributes.keybindings` | Keys for any command, in VS Code's spelling. |
| `contributes.settings` | Default values for the extension's own setting keys (what `Settings.GetAsync` returns until the user sets one). |

Paths in a manifest are relative to the extension's folder and may not leave it (`..` and absolute paths are refused).

## `init.csx` in detail

* It lives in the config folder (`~/.config/wicked/init.csx` on Linux). **Preferences: Open init.csx** creates it with a
  commented starter; **Preferences: Reload init.csx** runs it again, first taking back everything the last run registered.
  It also reloads when you save it in Wicked.
* It runs once at start on a worker thread, after installed extensions have loaded, so it can use their commands.
* Top-level `await`, `using` lines and `#load`-free single files work as in any C# script. `System`, `System.IO`,
  `System.Linq`, `System.Collections.Generic`, `System.Threading(.Tasks)`, `System.Text`, `Avalonia`, `Avalonia.Controls`,
  `Avalonia.Threading` and `Wicked.Core.Extensions` are imported; Wicked.Core, Avalonia and the framework are referenced.
* **It is compiled once and kept by hash** in the cache folder, so a start with an unchanged script loads a finished
  assembly and never starts the compiler. Editing the script (or updating Wicked) compiles it again.
* **Errors**: a compile error is written to the Output channel **init.csx** as `init.csx(12,5): error CS1002: ; expected`
  and a notice appears in the footer (a click opens the channel). A runtime exception is written with its stack trace,
  which names the script's line (`init.csx:line 7`). The rest of the IDE is unaffected either way.

More examples:

```csharp
// Format on save for one language, using a command another extension (or Wicked) provides.
wicked.Events.DocumentSaved += async e =>
{
    if (e.Document.LanguageId == "json") await wicked.Commands.ExecuteAsync("editor.action.formatDocument");
};

// A setting of your own, with a default, read when needed.
var greeting = await wicked.Settings.GetAsync("init.greeting", "Hello");
wicked.Commands.Register("init.greet", "Greet", () => wicked.Notices.Post(greeting!), null, "My Commands");

// Add a key to a built-in command.
wicked.Commands.BindKeys("workbench.action.files.saveAll", "Ctrl+Alt+S");

// Log to your own Output channel.
var log = wicked.Output.Channel("My Script");
wicked.Events.ActiveEditorChanged += e => log.WriteLine($"now editing {e?.Document.Path ?? "nothing"}");
```

## Packaging and installing

A package is a folder with `wicked-extension.json` at its top, your `main` assembly and anything it needs, and the
files the manifest names. A zip of that folder named `<name>.wicked.zip` is how it is shared.

A code extension's project (`dotnet build -c Release`, then zip the output):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Your own dependencies are copied next to the assembly; Wicked's and Avalonia's are not. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Wicked.Core">
      <HintPath>/path/to/wicked/Wicked.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- Only if you build panels: the same Avalonia version as the Wicked you target. -->
    <PackageReference Include="Avalonia" Version="12.1.4" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <None Include="wicked-extension.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Each extension loads into its own collectible load context. Wicked's own assemblies (`Wicked.*`, Avalonia, the
framework) always come from the host, so types mean the same thing on both sides; everything else, including your
dependencies and native libraries listed in your `.deps.json`, is looked up in your folder first. Assemblies are read
into memory, so uninstalling can delete the folder while Wicked runs.

**Installing**, from the Extensions panel (Ctrl+Shift+X), or from the palette:

* **Install from Folder...**: a folder with a manifest.
* **Install from a .zip...**: a package. Its files may be at the top or inside one folder.
* **Install from a GitHub Release...**: `owner/name`. The newest release's asset named `*.wicked.zip` is downloaded and
  checked against the sha256 digest GitHub publishes for the asset (or a `<asset>.sha256` file beside it). A download
  that does not match is refused and nothing is installed. A release that publishes neither is installed only if you
  accept a question that says it could not be verified.

Every install first shows what is about to be installed and, for an extension with code, says plainly that
**it runs code on your computer** with your permissions; nothing is copied into place until you agree. Installing a
newer version of an installed extension replaces it. Extensions live in `<data folder>/extensions/<publisher>.<id>-<version>/`;
turning one off keeps its files and remembers the choice.

## What the host does and does not protect

Extensions are ordinary .NET code in Wicked's process. The host catches their exceptions and takes back their
registrations, but it does not sandbox them: an extension can read your files and use the network like any program you
run. Install extensions you trust, as you would any program. Running them in separate processes or in WebAssembly was
weighed and not chosen: the measured cost of a loaded extension is a fraction of a millisecond,
and a process boundary would make editor APIs asynchronous round trips for little gain while the extensions are trusted
code the user installs by hand.
