# Custos

Version 0.1.0. Contract version `v0`. License: MIT ([LICENSE](LICENSE)), except the icons. The icons come from the Foxhole War API of Siege Camp and are under CC BY-NC 4.0 ([icons/LICENSE.md](crates/custos-app/icons/LICENSE.md)). Thus the AppImage must not be sold or used for a commercial purpose.

Custos is a tray application for Linux and Windows. It watches the map data file of Foxhole and sends it to a service that conforms to the [Custos specification](../spec/README.md). It shows the results in an overlay above the game, or as desktop notifications.

## Behavior

- Custos is idle while Foxhole does not run. It does not watch the file, and it does not send data.
- While Foxhole runs, Custos watches the save folder. When the map data file changes, Custos sends the full file at the next interval.
- Custos does not send a file that the service already accepted, or a file that the service refused.
- The interval is the larger of `upload_interval_seconds` and the minimum of the service. A `429` response makes Custos wait longer.
- Custos only reads the save files. It sends no file name and no other data. The file name holds the Steam ID of the player.

## Requirements

- Linux with a desktop that shows tray icons through StatusNotifierItem. KDE Plasma does. GNOME needs an extension, for example AppIndicator and KStatusNotifierItem Support.
- A Secret Service for the tokens, for example GNOME Keyring or KWallet.
- Foxhole in Steam with Proton.
- For the overlay: a Wayland desktop with the layer-shell protocol, for example KDE Plasma 6 or Sway. On GNOME, Custos shows desktop notifications.

On Windows:

- Windows 10 or 11, with the WebView2 runtime. Windows 11 holds it.
- Foxhole in Steam.

The Windows version is experimental. CI builds and tests it, but nobody tested it with the game. This version has no Windows installer: build Custos from the source (see [Build](#build)). The Tauri documentation says that desktop notifications on Windows work only for an installed application. Thus a build from the source can show no notifications.

## Install

1. Download the AppImage from the release.
2. Make it executable: `chmod +x Custos_0.1.0_amd64.AppImage`.
3. Start it one time. Custos writes a template to `~/.config/custos/config.toml` and shows a notification.
4. Set `service_url` in the file.
5. Start Custos again. Use **Sign in** in the tray menu.

## Configuration

The file is `~/.config/custos/config.toml` on Linux, and `%APPDATA%\custos\config.toml` on Windows. On Linux:

```toml
# The URL of the service. Custos gets all other settings from its discovery document.
service_url = "https://example.com"

# The shortest time between two uploads, in seconds. Default: 300.
upload_interval_seconds = 300

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
```

On Windows, the template has no `steam_root`, and the default `save_dir` is `%LOCALAPPDATA%\Foxhole\Saved\SaveGames`. Write a Windows path in single quotes, so that TOML reads the backslashes as text:

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

While Foxhole runs, Custos shows its messages as lines in a corner of the monitor that shows the game. The newest line is at the bottom. A line wraps when it is wider than 15 % of the monitor width. In the top left corner, the default `offset` of 28 % puts the lines below the notifications of the game. The overlay looks like an old terminal: green text with a glow, and no background. Each new line types out after the line above it, as on a ticker tape. While no line types, a prompt shows at the bottom, and its cursor fades in and out. An error line is red. The font is the first of these that the computer has: JetBrains Mono, Cascadia Mono, Fira Code, Source Code Pro, Hack, and Noto Sans Mono. Without them, it is the default monospace font. The overlay shows at most 6 lines. A line goes after 20 seconds, and an error line after 60 seconds. A new message of the same kind replaces the old line. A completed login removes the line that asks for the login. An accepted upload removes the error lines of the upload and of the service.

Custos finds the monitor of the game through the window list of XWayland. If it cannot find the game window, the desktop selects the monitor.

The overlay takes no pointer input and no keyboard input, so the game keeps them. When Foxhole stops, the overlay closes, and Custos shows desktop notifications.

The overlay needs the layer-shell protocol of Wayland. If the desktop does not have it, Custos writes `cannot open the overlay` to the log and shows desktop notifications. Windows has no overlay in this version.

## Notifications

Custos shows these messages in the overlay while it is open, and as desktop notifications at other times.

| Notification | Meaning |
|---|---|
| Stockpile data sent | The service accepted the file. The text shows the size and the war. |
| Signed in | The login is complete. |
| Sign in to send stockpile data | Custos has no valid token. Use **Sign in**. |
| An error from the service | The service refused the file, or it failed. Custos shows the same error at most one time in 15 minutes. |

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
cargo tauri build --bundles appimage       # Run in crates/custos-app.
```

Set `RUST_LOG=debug` for more log output.

## Development

Run Custos against a local service. The [server](../server/README.md) supplies the service and a login.

1. Install the packages in [Build](#build). The local service also needs the .NET SDK 10 and Docker.
2. Start the service from the `server/` folder: `dotnet run --project Custos.Server.AppHost`. It starts these resources:

   | Resource | URL |
   |---|---|
   | Service | `http://localhost:5080` |
   | Keycloak | `http://localhost:8180` |
   | Aspire dashboard | `http://localhost:15180` |

3. Set the service URL in `~/.config/custos/config.toml`: `service_url = "http://localhost:5080"`. Custos accepts HTTP only for `localhost`.
4. Start Custos from the `custos/` folder: `RUST_LOG=debug cargo run -p custos-app`.
5. Use **Sign in** in the tray menu. The user is `dev` and the password is `dev`. Keycloak shows a consent page at the first login.

Notes:

- A development build has no installed desktop file. At startup, Custos registers the `custos://` scheme itself. If the browser does not return to Custos after the login, search the log for `cannot register the custos:// scheme`.
- Custos sends data only while Foxhole runs. Without the game, set `save_dir` in the configuration file to a test folder.
- The service imports the Keycloak realm at each start. A change in the Keycloak console lasts for one run.

## Layout

| Crate | Content |
|---|---|
| [crates/custos-core](crates/custos-core) | The engine: configuration, discovery, login, save-file location, process detection, upload, the interval rule, and the message stream of the overlay. |
| [crates/custos-app](crates/custos-app) | The Tauri tray application: menu, overlay window, notifications, and the deep link. |
