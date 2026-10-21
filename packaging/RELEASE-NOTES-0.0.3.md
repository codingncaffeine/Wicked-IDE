The first public preview of Wicked IDE: a code editor built around AI models that run on your own machine.

## Downloads

- **Linux:** `WickedIDE-0.0.3-linux-x64.tar.gz` (unpack anywhere, run `./wicked`), `wicked-ide_0.0.3_amd64.deb`
  (Debian 12+, Ubuntu 22.04+), or `wicked-ide-bin` from the AUR.
- **Windows:** `WickedIDE-0.0.3-win-x64.zip` (Windows 10 1809 or newer / Windows 11, 64-bit): unzip anywhere and run
  `wicked.exe`. Nothing is installed.

Everything is self-contained: .NET ships inside. Models and the llama.cpp engine are downloaded only when you choose
to install one.

## What's in it

- **Models that fit your PC.** Two or three recommendations for your hardware, the full catalog and Hugging Face
  search behind "Browse all", a licence step, verified downloads that pause and resume, and daily update checks with
  pin / notify / auto-update per model.
- **The engine, handled.** Detects your GPU, installs the matching llama.cpp build (CUDA, Vulkan or CPU), runs it
  supervised, updates it and rolls back if an update misbehaves.
- **Chat and assistance.** Streaming chat with reasoning, pictures and saved conversations; ghost-text completions;
  Ctrl+I edits reviewed change by change; agent mode that asks before it acts; MCP tools; @-mentions; commit messages.
- **A full editor.** Tabs and splits, command palette, multi-cursor, find and replace, folding, minimap, snippets,
  EditorConfig, and language-server features (problems, completion, hover, go to, rename, format, quick
  fixes, inlay hints) from 20 language servers, with one-click installs.
- **Git, terminal, debugging, tasks.** Source Control with staging by hunk and line, branches, stash, graph and blame;
  a built-in terminal; a debugger (netcoredbg, CodeLLDB, js-debug) driven by launch.json; tasks.json with problem
  matchers and a test explorer.
- **A local API.** OpenAI- and Anthropic-compatible endpoints on `127.0.0.1:1337`, with keys and a request log, so
  other tools can use the models Wicked manages; `wicked-cli` to serve, pull and run from a shell.
- **Yours to restyle.** Six built-in themes, VS Code theme import, hot-reloaded style packs, `init.csx` and C#
  extensions.

Your code never leaves your machine. Wicked talks only to huggingface.co (models) and github.com (the engine, the
model catalog and app releases); `WICKED_OFFLINE=1` turns even that off.

## Known limits

- This is an early preview: expect rough edges, and please report them on the issue tracker.
- The Windows build passes the full test suite on Windows but is newer than the Linux one.
- The first code block in a chat reply can still cost a frame or two while the window warms up.
