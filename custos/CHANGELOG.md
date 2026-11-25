# Changelog

## 0.3.0-alpha.1

The stream of the service, and messages with more than one line. A pre-release.

- Custos reads `stream_endpoint` of the discovery document, and keeps the stream of `spec/05-stream.md` open while Foxhole runs and the user is signed in. Each message shows in the overlay, or as a desktop notification when the overlay is not open.
- When Custos connects again, it sends its position. It keeps the position in memory only. After a failure, it waits 1 second, doubled after each failure, to 60 seconds at most, with a random part.
- Custos gets a new access token before the old one expires. A network error or an error of the token endpoint no longer signs the user out. Only a refused refresh token does.
- A message with the reference of a kept message replaces it in place, and its text types again.
- A line break in a message starts a new line. Each next line of a message starts with two spaces.
- New keys `messages` (default 6) and `lines` (default 12) in `[overlay]`. A line above the limits is hidden, and it comes back when a newer message goes.

## 0.2.0

The overlay above the game, and the first Windows version.

- Installer script for Linux, `install.sh`. It installs the AppImage as `~/.local/bin/custos` with a launcher entry and icons, and replaces an installed version. `--uninstall` removes Custos and keeps the configuration and the tokens.
- Windows installer (NSIS) for the current user, not signed. It downloads the WebView2 runtime when the computer does not have it.
- Build provenance attestation for the AppImage, the installer script, and the Windows installer.
- Custos applies each change of the configuration file at once, without a restart. If the file is not valid, Custos shows the error and keeps the last valid configuration.
- A clear message when the service is not available, with the cause, for example `Connection refused`. The message shows within 10 seconds when the host does not answer. Before, it showed after 60 seconds.
- Overlay on Linux: the messages show as lines above the game while Foxhole runs, on the monitor of the game. A native GTK window on the overlay layer of the layer-shell protocol, for example on KDE Plasma 6 and Sway. Desktop notifications when the overlay is not open. New configuration table `[overlay]` with `enabled`, `anchor`, and `offset`.
- Overlay style of an old terminal: green text that types out line by line, and a prompt with a cursor that fades in and out.
- Overlay on GNOME: Custos starts again with the X11 backend of GTK, and the overlay is an X11 window above the game.
- Overlay on Windows, experimental: a transparent webview window above the game, on the primary monitor, with the same look as on Linux. Foxhole must use the mode "Windowed Fullscreen" or a windowed mode.
- Windows build, experimental: process detection, the save folder in `%LOCALAPPDATA%`, the Credential Manager for the tokens, a configuration template with Windows paths, and an ICO icon. A release build opens no console window.
- If the tokens are larger than 2,560 bytes, the credential store keeps only the refresh token.
- Custos now uses `steam_root` of the configuration. Before, it ignored the value.

## 0.1.0

First version. Implements contract version `v0`. Linux only.

- Tray application with the menu items Pause, Sign in, Sign out, Open configuration, and Quit.
- Desktop notifications for upload results and errors.
- Configuration in `~/.config/custos/config.toml`.
- Discovery of the service from one URL.
- Login with Authorization Code, PKCE, and the deep link `custos://oauth2callback`. Tokens in the Secret Service.
- Location of the save folder in the Proton prefix of each Steam library (`AppData/Local/Foxhole/Saved/SaveGames`).
- Foxhole process detection. Custos is idle while the game does not run.
- Upload of the full map data file, with the interval rule of the specification.
- AppImage package.
- The scrap (Salvage) icon of the Foxhole War API as the application icon, under CC BY-NC 4.0.
