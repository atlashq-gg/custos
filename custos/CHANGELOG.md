# Changelog

## Unreleased

- Overlay on Linux: the messages show as lines above the game while Foxhole runs, on the monitor of the game. A native GTK window on the overlay layer of the layer-shell protocol, for example on KDE Plasma 6 and Sway. Desktop notifications when the overlay is not open. New configuration table `[overlay]` with `enabled`, `anchor`, and `offset`.
- Overlay style of an old terminal: green text that types out line by line, and a prompt with a cursor that fades in and out.
- Overlay on GNOME: Custos starts again with the X11 backend of GTK, and the overlay is an X11 window above the game.
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
