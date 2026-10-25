The third preview of Wicked IDE: the assistant works in your project from the first question, and chat models are kept
apart from code completion models.

## Downloads

- **Linux:** `WickedIDE-0.0.6-linux-x64.tar.gz` (unpack anywhere, run `./wicked`), `wicked-ide_0.0.6_amd64.deb`
  (Debian 12+, Ubuntu 22.04+), or `wicked-ide-bin` from the AUR.
- **Windows:** `WickedIDE-0.0.6-win-x64.zip` (Windows 10 1809 or newer / Windows 11, 64-bit): unzip anywhere and run
  `wicked.exe`. Nothing is installed.

Everything is self-contained: .NET ships inside.

## What's new

- **The assistant is an agent as soon as a project is open.** Ask it to build something and it reads, creates and
  edits the files itself, opening each change in the editor and asking before it changes anything. Turn the Agent
  chip off for a plain chat; in that mode the assistant says when a request needs Agent instead of handing you steps
  to copy.
- **Chat models and code completion models are kept apart.** Small completion models fill in code as you type but
  cannot hold a conversation; they are no longer offered to the chat. The Models panel recommends chat models that fit
  this PC, with the completion model on a card of its own marked CODE COMPLETION ONLY. If no chat model fits, the panel
  says so.
- **Browse lists what this PC can run.** Chat models, Completion only and Hugging Face are separate tabs; models and
  quants too large to run well here are left out, and a note counts them. Hugging Face repos say whether they chat,
  complete code, or do not say.

## Fixes

- Models failed to load, or crashed on their first answer, on a graphics card already busy with other programs: the
  engine now sizes the model to the memory that is actually free.
- New Project's Browse… button could stay greyed out when the desktop's folder picker did not answer.
- The Coding recommendation no longer installs a completion model along with the chat model.

## Known limits

- This is still an early preview: expect rough edges, and please report them on the issue tracker.
- Agent mode needs a model that can call tools; the recommended chat models can. A model from Hugging Face that cannot
  will answer in text without touching your files.
- Switching Agent off lasts until Wicked restarts.
- Hugging Face often does not say whether a model chats or only completes code; Wicked then says "Hugging Face does not
  say".
- `when` clauses in keybindings.json are shown but not yet acted on.
- Completion inside settings.json and keybindings.json needs the JSON language server (one-click install).
