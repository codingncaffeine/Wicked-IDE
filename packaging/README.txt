Wicked IDE — a code editor that runs AI models on your own machine.

Run it
  From the tarball: ./wicked
  From the .deb or the AUR package: wicked, or Wicked IDE in your applications menu.

What it needs
  Nothing to start. Models and the llama.cpp engine that runs them are downloaded when you choose to install one
  (Models on the left rail); the engine is picked for your GPU (CUDA, ROCm or Vulkan) or runs on the CPU.
  Vulkan engines need a Vulkan loader (libvulkan1 on Debian and Ubuntu, vulkan-icd-loader on Arch).

Your code never leaves your machine. The app only talks to huggingface.co (models), github.com (the engine, the
model catalog and app releases) and nothing else; WICKED_OFFLINE=1 turns even that off.

Options
  wicked --version          print the version and licence
  WICKED_HOME=DIR wicked    keep every setting, model and log under DIR
  wicked-cli status         the command-line tools; wickedd is the background service they talk to

Wicked IDE is free software under the GNU General Public License, version 3 or later (LICENSE).
https://github.com/codingncaffeine/Wicked-IDE
