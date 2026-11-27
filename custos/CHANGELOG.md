# Changelog

## 0.3.1

The upload waits for a new map after the start of Foxhole, the prompt no longer locks, the overlay keeps to its line limit, and its colors are in the configuration.

- Custos no longer sends the map data file when Foxhole starts, because the file is old then. It ignores the changes of the file for `upload_start_delay_seconds` (default 60) after the start of Foxhole, after its own start while Foxhole runs, and after a change of the save folder. At the start, the overlay shows `Not watching the map yet (60 s)`. The seconds count down in place, with no new typing and no new desktop notification, and the message stays until `Watching the map for changes` replaces it at the end of the start delay. The log shows the end of the start delay at the level `info`, and each change of the file at the level `debug`, also in the start delay (#114).
- Custos sends a change only after the file did not change for `upload_delay_seconds` (default 10). Each change starts this time again, so that a set of map refreshes goes in one upload. A change during the interval stays pending, and goes when the interval is over (#113).
- The prompt of the overlay no longer locks when the user selects another window while typing a question. The prompt closes when another window takes the keyboard, for example on Alt+Tab, a mouse click that goes through the overlay, or a push-to-talk key, and that window keeps the keyboard. It also closes after 60 seconds with no key. In both conditions, the typed text stays, and the next prompt starts with it. While the prompt is open, the key takes the keyboard again (#111).
- The overlay counts the lines on the screen. A line that wraps counts as more lines, so the overlay is never higher than `lines` of `[overlay]`. Custos wraps each line at the width of the overlay. The question and its answer use at most half of `lines`, and all of it while the user types; the messages use the rest. A longer answer is cut, and its last line tells the user that the key shows more (#112).
- The default of `lines` in `[overlay]` is 20, because a line that wraps now counts as more lines.
- New keys `text_color`, `error_color`, and `input_color` in `[overlay]` set the colors of the overlay as `"#rrggbb"`. The defaults are the colors of before: green, red, and amber. The glow of the text has the color of the text.

## 0.3.0

The stream of the service, and the query: a question to the service in plain language.

- Custos reads `features_supported` of the discovery document, and uses the stream and the query only when the list names them. It refuses a document without `upload`, or with a listed feature without its members. Thus Custos 0.3.0 needs a service such as Optio 0.1.0 or later.
- The default `service_url` is `https://optio.atlashq.gg`. Each key of the configuration is now optional. At the first start, Custos writes the configuration file with the defaults and starts at once. Its example paths are the paths of the user: below `$HOME` on Linux, and below `%LOCALAPPDATA%` on Windows.
- Stream: Custos reads `stream_endpoint` of the discovery document, and keeps the stream of `spec/05-stream.md` open while Foxhole runs and the user is signed in. Each message shows in the overlay, or as a desktop notification when the overlay is not open.
- When Custos connects to the stream again, it sends its position. It keeps the position in memory only. After a failure, it waits 1 second, doubled after each failure, to 60 seconds at most, with a random part.
- Custos gets a new access token before the old one expires. A network error or an error of the token endpoint no longer signs the user out. Only a refused refresh token does. A token request waits 15 seconds at most.
- Overlay: a message with the reference of a kept message replaces it in place, and its text types again. A line break in a message starts a new line, and each next line of a message starts with two spaces.
- New keys `messages` (default 6) and `lines` (default 12) in `[overlay]`. A line above the limits is hidden, and it comes back when a newer message goes.
- The message of an accepted upload is short: `Uploaded 120kB`, with 1 kB = 1,000 bytes.
- Query: Custos implements the query of `spec/06-query.md`, with `query_endpoint` of the discovery document.
- A key starts a question at the prompt of the overlay: `Ctrl+Alt+K` by default, `key` in the new table `[query]`. On Linux, Custos asks the desktop for the key through the `GlobalShortcuts` portal. KDE Plasma 6 and GNOME 48 or later have the portal, and Sway does not. On Windows, the key is a hot key. The tray menu item **Ask a question** also starts a question. The key and the menu item work only while the overlay is open.
- While the user types, the overlay takes the keyboard and holds the new messages. Enter sends the question, gives the keyboard back to the game, and shows the held messages. Escape cancels. `/new <question>` starts a new conversation, `/stop` stops the open question, and `/help` shows the commands. The amber line `> Awaiting input...` shows while the overlay has the keyboard. A long question wraps, with the cursor after its last character.
- A question of the service ends with `[Y/N]` and the remaining time. While it waits and the line is empty, `Y` accepts the change and `N` refuses it.
- The overlay shows the question and its state in its own area, at the edge of the anchor. The events do not move it. After the deadline of a state, Custos shows that the service is not in contact.
- The answer stays in the overlay until the next question, for `answer_seconds` at most (default 180). While the user types, the overlay shows the last answer in full. Without the overlay, a question of the service and the end of a query show as desktop notifications.
- While the service has a query, the overlay shows the key in amber: `> [Ctrl+Alt+K] for input. See /help.` Set `hint = false` in `[query]` to remove the line.
- `install.sh` installs the launcher entry as `gg.atlashq.custos.desktop`, the application ID that the portal needs, and removes the entry `custos.desktop` of 0.2.0. Run the `install.sh` of this release to update.
- At the level `debug`, the log shows each change of the map data file, and each file that Custos does not send because the service already accepted or refused it.

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
