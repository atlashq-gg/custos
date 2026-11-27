# Custos

Contract version `v0`. License: MIT ([LICENSE](LICENSE)), except the icons. The icons come from the Foxhole War API of Siege Camp and are under CC BY-NC 4.0 ([icons/LICENSE.md](crates/custos-app/icons/LICENSE.md)). Thus the AppImage must not be sold or used for a commercial purpose.

Custos is a tray application for Linux and Windows. It watches the map data file of Foxhole and sends it to a service that conforms to the [Custos specification](../spec/README.md). It shows the results and the events of the service in an overlay above the game, or as desktop notifications.

## Behavior

- Custos is idle while Foxhole does not run. It does not watch the file, and it does not send data.
- While Foxhole runs, Custos watches the save folder. The map data file is old when the game starts, so Custos ignores its changes for `upload_start_delay_seconds`. When the game starts, the overlay shows `Not watching the map yet (60 s)`. When the time is over, `Watching the map for changes` replaces it.
- When the map data file changes after that time, Custos waits until the file did not change for `upload_delay_seconds`. Each change starts this time again, so that a set of map refreshes goes in one upload. Then Custos sends the full file at the next interval.
- Custos does not send a file that the service already accepted, or a file that the service refused.
- The interval is the larger of `upload_interval_seconds` and the minimum of the service. A `429` response makes Custos wait longer.
- Custos only reads the save files. It sends no file name and no other data. The file name holds the Steam ID of the player.
- If the service has a stream, Custos keeps it open while Foxhole runs, the user is signed in, and Custos is not paused. The stream gives the events of the service, for example a change of a stockpile. Custos shows each event in the overlay.
- Custos keeps the position of the stream in memory. When it connects again, the service sends the events that Custos did not get. After a restart of Custos, the stream starts from now.
- Custos gets a new access token before the old one expires. A network error does not sign the user out.
- If the service has a query, the user pushes a key and types a question in plain language at the prompt of the overlay. The overlay shows the progress and the answer. Before the service changes data, the user must accept the change.

## Requirements

- Linux with a desktop that shows tray icons through StatusNotifierItem. KDE Plasma does. GNOME needs an extension, for example AppIndicator and KStatusNotifierItem Support.
- A Secret Service for the tokens, for example GNOME Keyring or KWallet.
- Foxhole in Steam with Proton.
- For the overlay: a Wayland desktop with the layer-shell protocol, for example KDE Plasma 6 or Sway, or GNOME through XWayland.
- For the key of the query: a desktop with the `GlobalShortcuts` portal, for example KDE Plasma 6 or GNOME 48 or later. Sway does not have it. Without it, use **Ask a question** in the tray menu.

On Windows:

- Windows 10 or 11, with the WebView2 runtime. Windows 11 holds it.
- Foxhole in Steam.
- For the overlay: Foxhole in the mode "Windowed Fullscreen" or in a windowed mode.

The Windows version is experimental. CI builds and tests it, but nobody tested it with the game. The Windows installer is not signed (see [Install](#install)). The Tauri documentation says that desktop notifications on Windows work only for an installed application. Thus a build from the source can show no notifications.

## Install

Each [release](https://github.com/atlashq-gg/custos/releases/latest) has the AppImage, the installer script `install.sh`, and the Windows installer. CI attests each file. To make sure that the CI of this repository built a file, run `gh attestation verify <file> --repo atlashq-gg/custos`. The command needs a GitHub login.

### Linux

1. Download `install.sh` from the release.
2. Run `sh install.sh`. The script downloads the AppImage of the same release, and makes sure that its SHA-256 digest is correct. It installs the AppImage as `~/.local/bin/custos`, and adds Custos to the launcher. It needs no root rights.
3. Start Custos from the launcher. At the first start, Custos writes the configuration file `~/.config/custos/config.toml` with the default values and the paths of the user.
4. Use **Sign in** in the tray menu.

To update Custos, run the `install.sh` of the new release. If Custos runs, quit it and start it again. To remove Custos, run `sh install.sh --uninstall`. The configuration and the tokens stay. To install an AppImage that you downloaded, run `sh install.sh <AppImage>`.

The AppImage also runs without the script: make it executable with `chmod +x`, and start it. Custos then has no launcher entry.

### Windows

1. Download `Custos_<version>_x64-setup.exe` from the release.
2. Start the installer. It is not signed, so Windows shows "Windows protected your PC". Select **More info**, then **Run anyway**. If Smart App Control is on, Windows blocks the installer.
3. The installer installs Custos for the current user, with no administrator rights. If the computer has no WebView2 runtime, the installer downloads it.
4. Start Custos from the Start menu. At the first start, Custos writes the configuration file `%APPDATA%\custos\config.toml` with the default values and the paths of the user.
5. Use **Sign in** in the tray menu.

## Configuration

The file is `~/.config/custos/config.toml` on Linux, and `%APPDATA%\custos\config.toml` on Windows. Custos applies each change of the file at once, without a restart. If the file is not valid, Custos shows the error and keeps the last valid configuration. A new `service_url` starts the engine again, because the tokens belong to the service. Each key is optional. On Linux:

```toml
# The URL of the service. Custos gets all other settings from its discovery document.
# Default: "https://optio.atlashq.gg".
service_url = "https://optio.atlashq.gg"

# The shortest time between two uploads, in seconds. Default: 300.
upload_interval_seconds = 300

# The time with no change of the map data file before an upload, in seconds. Default: 10.
upload_delay_seconds = 10

# The time after the start of Foxhole when Custos ignores the changes of the map data file, in
# seconds. Default: 60.
upload_start_delay_seconds = 60

# Optional. The folder that holds the save files of Foxhole.
# save_dir = "/home/user/.local/share/Steam/steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/Foxhole/Saved/SaveGames"

# Optional. The Steam folder.
# steam_root = "/home/user/.local/share/Steam"

# Optional. The overlay above the game.
[overlay]
# Default: true. With false, Custos shows desktop notifications.
enabled = true
# The corner of the screen: "top-left", "top-right", "bottom-left", or "bottom-right". Default: "top-left".
anchor = "top-left"
# The distance from the top or bottom edge, in percent of the monitor height, from 0 to 90. Default: 28.
offset = 28
# The most messages that the overlay shows, from 1 to 30. Default: 6.
messages = 6
# The most lines of text that the overlay shows, from 1 to 30. Default: 12.
lines = 12

# Optional. The query, if the service has one.
[query]
# The key that starts a question at the prompt of the overlay. It needs Ctrl, Alt, or Super. Default: "Ctrl+Alt+K".
key = "Ctrl+Alt+K"
# The time that the answer stays in the overlay, in seconds, from 1 to 3600. Default: 180.
answer_seconds = 180
# Show the key and /help in the overlay, in amber. Default: true.
hint = true
```

The file that Custos writes at the first start has each value in a comment. Its example paths are the paths of the user: on Linux below `$HOME`, and on Windows below `%LOCALAPPDATA%`. On Windows, the file has no `steam_root`, and the default `save_dir` is `%LOCALAPPDATA%\Foxhole\Saved\SaveGames`. Write a Windows path in single quotes, so that TOML reads the backslashes as text:

```toml
save_dir = 'D:\Games\Foxhole\SaveGames'
```

The service URL must use HTTPS. Custos accepts HTTP only for `localhost`.

On Linux without `save_dir`, Custos searches `steam_root`, or else these Steam folders, and each library in their `libraryfolders.vdf`:

- `~/.steam/steam`
- `~/.local/share/Steam`
- `~/.var/app/com.valvesoftware.Steam/.local/share/Steam` (Flatpak)

In each library, the save folder is `steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/Foxhole/Saved/SaveGames`. Custos reads `UserData.sav` in that folder to find the name of the map data file.

## Login

**Sign in** opens the login page of the service in the browser. After the login, the browser opens `custos://oauth2callback`, and the desktop gives the link to Custos. The browser can ask before it opens Custos.

Custos keeps the tokens in the Secret Service on Linux, and in the Credential Manager on Windows. It keeps one entry for each service URL. **Sign out** deletes them. An entry holds at most 2,560 bytes. If the tokens are larger, Custos keeps only the refresh token, and gets a new access token before the next upload.

## Overlay

While Foxhole runs, Custos shows its messages as lines in a corner of the monitor that shows the game. The newest line is at the bottom. A line wraps when it is wider than 15 % of the monitor width. In the top left corner, the default `offset` of 28 % puts the lines below the notifications of the game. The distance from the side of the monitor is 8 pixels. The overlay looks like an old terminal: green text with a glow, and no background. Each new line types out after the line above it, as on a ticker tape. While no line types, a prompt shows at the bottom, and its cursor fades in and out. An error line is red. The font is the first of these that the computer has: JetBrains Mono, Cascadia Mono, Fira Code, Source Code Pro, Hack, and Noto Sans Mono. Without them, it is the default monospace font, for example Consolas on Windows 10.

A message can have more than one line. A line break in the text starts a new line, and each other control character shows as a space. The first line of a message has the prompt. Each next line starts with two spaces, under the text of the first line. The lines of a message type one after the other.

The overlay shows at most `messages` messages and `lines` lines of text, from `[overlay]` in the configuration. The stricter limit applies, and a line that wraps counts as one. Each new line pushes the older lines up. A line above the limits is hidden, but it stays. When a newer message goes, a hidden line that is still in its lifetime comes back. A message goes after 20 seconds, and an error message after 60 seconds. A new message with the reference of a kept message replaces it in place: the new text types again, and its 20 seconds start again. A message of the service with a reference and no text removes the message with that reference. A completed login removes the line that asks for the login. An accepted upload removes the error lines of the upload and of the service. When the game starts, the overlay shows the problems that are still current. Examples are a service that is not available, and a necessary sign-in. Such an error can show as a desktop notification before.

On Linux, Custos finds the monitor of the game through the window list of XWayland. If it cannot find the game window, the desktop selects the monitor. On Windows, the overlay shows on the primary monitor.

The overlay takes no pointer input, and no keyboard input except while the user types a question ([Query](#query)). Thus the game keeps them. When Foxhole stops, the overlay closes, and Custos shows desktop notifications.

The overlay needs the layer-shell protocol of Wayland. GNOME does not have it. Thus on GNOME on Wayland, Custos starts again with the X11 backend of GTK (`GDK_BACKEND=x11`). The overlay is then an X11 window of XWayland above the other windows. Custos does this only with `enabled = true` in `[overlay]`, and only when `GDK_BACKEND` is not set. Custos decides this when it starts. Thus on GNOME, a change of `enabled` from `false` to `true` needs a restart of Custos. The tray, the notifications, and the login link then also use X11. On another desktop without the layer-shell protocol, Custos writes `cannot open the overlay` to the log and shows desktop notifications.

On Windows, the overlay is a transparent webview window above the other windows, with the page in `crates/custos-app/dist/overlay.html`. In Foxhole, select the mode "Windowed Fullscreen" or a windowed mode. A window cannot show above a game in exclusive fullscreen. Nobody tested the overlay on Windows with the game.

## Query

If the discovery document of the service lists the feature `query` in `features_supported`, the user can ask the service a question in plain language ([spec/06-query.md](../spec/06-query.md)). The service answers on its stream. Thus the query needs the stream: Custos shows the answer only while Foxhole runs, the user is signed in, and Custos is not paused.

The overlay shows the key in amber, for example `> [Ctrl+Alt+K] for input. See /help.`, while the service has a query. On Linux, it shows the key that the desktop bound. Set `hint = false` in `[query]` to remove the line.

1. Push the key, `Ctrl+Alt+K` by default. The overlay takes the keyboard, and you type at its prompt. While you type, the overlay holds the new messages, so that no line moves.
2. Type the question, and push Enter. The game gets the keyboard back, and the held messages show.
3. Read the progress and the answer in the overlay.

While you type:

| Input | Result |
|---|---|
| Enter | Sends the question. A new question replaces the open question. |
| `/new <question>` | Sends the question with no context from the earlier questions. |
| `/stop` | Stops the open question. |
| Backspace | Removes the last character. |
| `Y`, with an empty line | Accepts the change that the service asks about. |
| `N`, with an empty line | Refuses the change. |
| `/help` | Shows the commands. |
| Escape | Cancels. The game gets the keyboard back. |

While you type, the area of the query shows the last question with its full answer, and the amber line `> Awaiting input...`. The cursor at the prompt does not fade.

The overlay shows the question and its state in its own area, at the edge of the anchor. The lines of the events do not move it. The states are:

- `Sent`: Custos sent the question, and waits for the response.
- `Working`: the service works on the question. The line names the current step.
- A question of the service with `[Y/N]` and the remaining time, for example `Set Label? "Main Depot" [Y/N] (42 s)`. Push the key, then `Y` or `N`. With no decision, the question expires.
- `Applying the change`: the service changes the data. The user cannot stop it.
- The answer, or the cause of the end. An end with an error is red.

Each state has a deadline. If the service sends nothing before it, the overlay shows `No contact with the service`.

The answer stays in the overlay until the next question, for `answer_seconds` at most. The overlay shows at most `lines` lines of it. Its last line then tells the user that the key shows the full text. While the user types, the overlay shows the last answer in full. Without the overlay, a question of the service and the end of a query show as desktop notifications.

On Linux, Custos asks the desktop for the key through the `GlobalShortcuts` portal when it starts. The desktop can ask the user to confirm the key. The portal knows Custos by its launcher entry `gg.atlashq.custos.desktop`, which `install.sh` installs. Without the entry, for example with a development build, the key does nothing, and the log shows `cannot register the application ID with the portal`. The desktop keeps the key, so a later change of `key` has no effect: change the key in the settings of the desktop. On KDE Plasma, it is in **System Settings**, **Keyboard**, **Shortcuts**. On Windows, Custos uses `key` as a hot key of Windows, and applies a change at once. If another application has the key, Custos writes a warning to the log.

The query needs the overlay. With `enabled = false` in `[overlay]`, or while Foxhole does not run, the key does nothing, and **Ask a question** in the tray menu is not available. On KDE Plasma and Sway, the overlay takes the keyboard with the mode Exclusive of the layer-shell protocol. This mode can make the game leave fullscreen while you type. On GNOME, the X11 window of the overlay takes the focus. Nobody tested the input with the game yet.

## Notifications

Custos shows these messages in the overlay while it is open, and as desktop notifications at other times.

| Notification | Meaning |
|---|---|
| Uploaded 120kB | The service accepted the file. The text shows its size in kilobytes (1,000 bytes). |
| Signed in | The login is complete. |
| Sign in to send stockpile data | Custos has no valid token. Use **Sign in**. |
| The service at … is not available | Custos cannot reach the service URL, or the service gave an error. The text gives the cause, for example `Connection refused`. Custos tries again each minute. When the service is available again, the message goes. |
| An error from the service | The service refused the file, or it failed. |
| An event of the service | A message on the stream of the service, for example a change of a stockpile. The title is the name of the service. A message of the kind `error` shows as an error. |
| The events of … are not available | Custos cannot open the stream two times in a row. The text gives the cause. Custos tries again after a wait of 1 second, doubled after each failure, to 60 seconds at most. When the stream opens, the message goes. |
| … sends no events to this user | The service refused the stream for this user. Custos tries again after the next sign-in. |
| A question or an answer | A question of the service that waits for a decision, or the end of a query. Only while the overlay is not open. The title is the question of the user. |

Custos shows the same error at most one time in 15 minutes.

## Build

Requirements: Rust 1.88 or later, and the system packages for your distribution.

Ubuntu or Debian:

```sh
sudo apt install libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev libgtk-layer-shell-dev build-essential
```

Fedora:

```sh
sudo dnf install webkit2gtk4.1-devel libappindicator-gtk3-devel librsvg2-devel openssl-devel libxdo-devel gtk-layer-shell-devel
sudo dnf group install c-development
```

Windows: the Visual Studio Build Tools with the workload "Desktop development with C++". This gives the MSVC toolchain, which is the default of `rustup` on Windows. The build links the Visual C++ runtime statically (`.cargo/config.toml`), so the user does not need the Visual C++ Redistributable.

Then install the Tauri command line tool:

```sh
cargo install tauri-cli --version "^2" --locked
```

```sh
cargo test -p custos-core                  # The engine. Needs no GUI libraries.
cargo run -p custos-app                    # The tray application.
cargo tauri build --bundles appimage       # Linux. Run in crates/custos-app.
cargo tauri build --bundles nsis           # Windows. Run in crates/custos-app.
```

Set `RUST_LOG=debug` for more log output.

## Development

Run Custos against a local service. [Optio](../optio/README.md) is the service of Custos. Its AppHost also starts the server, the login, and the event broker.

1. Install the packages in [Build](#build). The local service also needs the .NET SDK 10 and Docker.
2. Start the service from the root folder of the repository: `dotnet run --project optio/Optio.AppHost`. It needs the parameters `discord-token`, `optio-discord-token`, and `discord-user-id`, in the user secrets of the AppHost or in the environment. It starts these resources:

   | Resource | URL |
   |---|---|
   | Optio, the service of Custos | `http://localhost:5090` |
   | Server | `http://localhost:5080` |
   | Keycloak | `http://localhost:8180` |
   | Aspire dashboard | `http://localhost:15180` |

   For the query, also set the parameter `claude-api-key` to a key of the Claude API. Without it, the service has no query.

3. Set the service URL in `~/.config/custos/config.toml`: `service_url = "http://localhost:5090"`. Custos accepts HTTP only for `localhost`.
4. Start Custos from the `custos/` folder: `RUST_LOG=debug cargo run -p custos-app`.
5. Use **Sign in** in the tray menu. The user is `dev` and the password is `dev`. Keycloak shows a consent page at the first login.

Notes:

- A development build has no installed desktop file. At startup, Custos registers the `custos://` scheme itself. If the browser does not return to Custos after the login, search the log for `cannot register the custos:// scheme`.
- Custos sends data and opens the stream only while Foxhole runs. Without the game, set `save_dir` in the configuration file to a test folder.
- A guild with no read roles in its settings gives events only to its administrators. To get events as a plain member, give the guild a read role, for example `@everyone`.
- The service imports the Keycloak realm at each start. A change in the Keycloak console lasts for one run.

## Layout

| Folder | Content |
|---|---|
| [crates/custos-core](crates/custos-core) | The engine: configuration, discovery, login, save-file location, process detection, upload, the interval rule, the stream of the service, the query, the key of the query, and the lines of the overlay. |
| [crates/custos-app](crates/custos-app) | The Tauri tray application: menu, overlay window, the input of a question, notifications, and the deep link. |
| [packaging](packaging) | The installer script for Linux, and its test. |
