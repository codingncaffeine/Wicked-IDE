Wicked IDE — a code editor that runs AI models on your own machine.

Run it
  Unzip the folder anywhere you like and run wicked.exe. Nothing is installed; delete the folder to remove it
  (settings, models and logs live in %APPDATA%\Wicked and %LOCALAPPDATA%\Wicked).

What it needs
  Windows 10 or 11, 64-bit. .NET is inside the folder. Models and the llama.cpp engine that runs them are downloaded
  when you choose to install one (Models on the left rail); the engine is picked for your GPU (CUDA or Vulkan) or
  runs on the CPU. The terminal uses PowerShell (or cmd) and needs Windows 10 version 1809 or newer.

Your code never leaves your machine. The app only talks to huggingface.co (models), github.com (the engine, the
model catalog and app releases) and nothing else; set WICKED_OFFLINE=1 to turn even that off.

Options
  wicked.exe --version            print the version and licence
  set WICKED_HOME=DIR             keep every setting, model and log under DIR (a portable copy on a USB stick)
  wicked-cli.exe status           the command-line tools; wickedd.exe is the background service they talk to

Wicked IDE is free software under the GNU General Public License, version 3 or later (LICENSE.txt).
https://github.com/codingncaffeine/Wicked-IDE
