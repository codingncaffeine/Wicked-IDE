The second preview of Wicked IDE: the everyday basics every IDE user expects, from the menu bar to a Run button.

## Downloads

- **Linux:** `WickedIDE-0.0.5-linux-x64.tar.gz` (unpack anywhere, run `./wicked`), `wicked-ide_0.0.5_amd64.deb`
  (Debian 12+, Ubuntu 22.04+), or `wicked-ide-bin` from the AUR.
- **Windows:** `WickedIDE-0.0.5-win-x64.zip` (Windows 10 1809 or newer / Windows 11, 64-bit): unzip anywhere and run
  `wicked.exe`. Nothing is installed.

Everything is self-contained: .NET ships inside.

## What's new

- **Run, build and debug from the title bar.** A Run / Debug / Stop / Build control with one picker for your
  launch.json configurations, the current file and the programs your folder builds (dotnet run, cargo run, make run,
  npm start). **Run Current File** works with no setup for Python, JavaScript and TypeScript, shell, PowerShell, Ruby,
  Perl, PHP, Lua, Go, Dart, Swift, Java, C# (a project or a single .cs file), Rust (Cargo) and C/C++. Python and
  JavaScript files can also be debugged with no launch.json. Rebuild and Clean, and the last build's result stays in
  the status bar.
- **Menus everywhere.** A menu bar (File, Edit, Selection, View, Go, Run, Terminal, Help), right-click menus for the
  editor, tabs and Explorer, and clickable status bar items for the line, indentation, encoding, line endings and
  language.
- **Settings you can search.** Every setting, searchable, with what it does, a mark on the ones you changed and Reset;
  `@modified` lists your changes.
- **Keyboard Shortcuts editor** (Ctrl+K Ctrl+S): every command and its keys; record new keys, see which commands
  already use them, remove or reset a key, search by pressing keys. Changes go to `keybindings.json` in VS Code's
  format. Prefer JetBrains keys? Pick the JetBrains keymap in Settings or on the Get Started page.
- **Files and windows.** New Window, `wicked .` / `wicked FILE:LINE` from a terminal, unsaved files kept when you close,
  Reopen Closed Editor, Revert, Open Recent, Compare with Saved, image and Markdown previews, change language, line
  endings and encoding.
- **Editing and layout.** Back and Forward, line tools, expand selection, sticky scroll, rulers, word suggestions,
  closing HTML tags; split the editor any way, drag tabs between groups, Zen Mode, full screen, window zoom, pinned
  tabs.
- **Fits your desktop.** Follows your system's light or dark setting when asked, sharp at fractional scales on X11 and
  Wayland, the first window fits small screens, menus and settings are read correctly by screen readers, input
  methods (Japanese, Chinese, Korean) compose at the caret.
- **Help.** A Get Started page, a keyboard shortcuts reference, release notes, Report Issue and About.
- **A new logo**, in the app, the taskbar and the packages.

## Fixes

- The files open when you last closed Wicked open again at the next start.
- Status bar items took no click while their tooltip was showing.
- The Output panel showed "No output yet" over a task's output.
- Two quick changes to a setting (a toggle pressed twice) could leave the first change in force.

## Known limits

- This is still an early preview: expect rough edges, and please report them on the issue tracker.
- `when` clauses in keybindings.json are shown but not yet acted on.
- Comments in keybindings.json are not kept when the Keyboard Shortcuts editor changes the file.
- Completion inside settings.json and keybindings.json needs the JSON language server (one-click install).
- In a narrow editor, code can run past the minimap; Go to Definition is offered on plain text, where it does nothing.
