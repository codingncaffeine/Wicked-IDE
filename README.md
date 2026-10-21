# Wicked IDE

A code editor built around local AI models. Wicked finds the models that fit your PC, downloads them, keeps them up to
date, and puts them to work in a full editor: completions as you type, a chat that knows your project, and edits you
review before they land. Everything runs on your machine.

> **Status: early preview.** Expect rough edges, and please report them on the issue tracker.

## Download

From the [releases page](https://github.com/codingncaffeine/Wicked-IDE/releases/latest):

- **Linux:** the `.tar.gz` (unpack anywhere, run `./wicked`), the `.deb` for Debian and Ubuntu, or `wicked-ide-bin`
  from the AUR.
- **Windows 10/11 (64-bit):** the `.zip`; unzip anywhere and run `wicked.exe`.

.NET ships inside every download. Models and the engine that runs them are downloaded when you choose to install one.

## What it does

- **Models that fit, kept current.** Two or three recommendations for your hardware, one-click installs, verified
  downloads that resume, and a notice in the footer when a newer upload or a better model for your PC is out.
- **A real editor.** Tabs and splits, a command palette, multi-cursor editing, language servers, Git, a terminal,
  debugging and tasks.
- **An assistant that uses your models.** Completions as you type, chat with your project as context, and changes
  shown as diffs you accept or reject.
- **A local API.** Other tools (VS Code extensions, scripts) can use the models Wicked manages.
- **Yours to restyle.** Built-in themes, and for those who want it, themes and styles written as code.

## Building (Linux first; Windows works too)

Requires the .NET 10 SDK.

```sh
dotnet build Wicked.slnx -c Release
dotnet test tests/Wicked.Tests -c Release
dotnet run --project src/Wicked.App -c Release      # the window
dotnet run --project src/Wicked.Daemon -c Release   # the daemon, wickedd
```

`WICKED_HOME=/some/folder` keeps every setting, model and log under that folder instead of your usual profile.

## Licence

GPL-3.0-or-later; see `LICENSE`. Third-party works are listed in `THIRD-PARTY-NOTICES.md`.
