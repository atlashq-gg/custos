# Custos

Version 0.1.0. Contract version `0.1`. License: MIT ([LICENSE](LICENSE)).

Custos is a tray application for Linux. It watches the map data file of Foxhole and sends it to a service that conforms to the [Custos specification](../spec/README.md). It shows the results as desktop notifications. It has no window.

## Behavior

- Custos is idle while Foxhole does not run. It does not watch the file, and it does not send data.
- While Foxhole runs, Custos watches the save folder. When the map data file changes, Custos sends the full file at the next interval.
- Custos does not send a file that the service already accepted, or a file that the service refused.
- The interval is the larger of `upload_interval_seconds` and the minimum of the service. A `429` response makes Custos wait longer.
- Custos only reads the save files. It sends no file name and no other data. The file name holds the Steam ID of the player.

## Requirements

- Linux with a desktop that shows tray icons through StatusNotifierItem. KDE Plasma does. GNOME needs an extension, for example AppIndicator and KStatusNotifierItem Support.
- A Secret Service for the tokens, for example GNOME Keyring or KWallet.
- Foxhole in Steam with Proton. Windows is not supported in this version.

## Install

1. Download the AppImage from the release.
2. Make it executable: `chmod +x Custos_0.1.0_amd64.AppImage`.
3. Start it one time. Custos writes a template to `~/.config/custos/config.toml` and shows a notification.
4. Set `service_url` in the file.
5. Start Custos again. Use **Sign in** in the tray menu.

## Configuration

`~/.config/custos/config.toml`:

```toml
# The URL of the service. Custos gets all other settings from its discovery document.
service_url = "https://example.com"

# The shortest time between two uploads, in seconds. Default: 300.
upload_interval_seconds = 300

# Optional. The folder that holds the save files of Foxhole.
# save_dir = "/home/user/.local/share/Steam/steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/War/Saved/SaveGames"

# Optional. The Steam folder.
# steam_root = "/home/user/.local/share/Steam"
```

The service URL must use HTTPS. Custos accepts HTTP only for `localhost`.

Without `save_dir`, Custos searches these Steam folders and each library in their `libraryfolders.vdf`:

- `~/.steam/steam`
- `~/.local/share/Steam`
- `~/.var/app/com.valvesoftware.Steam/.local/share/Steam` (Flatpak)

In each library, the save folder is `steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/War/Saved/SaveGames`. Custos reads `UserData.sav` in that folder to find the name of the map data file.

## Login

**Sign in** opens the login page of the service in the browser. After the login, the browser opens `custos://oauth2callback`, and the desktop gives the link to Custos. The browser can ask before it opens Custos.

Custos keeps the tokens in the Secret Service, one entry for each service URL. **Sign out** deletes them.

## Notifications

| Notification | Meaning |
|---|---|
| Stockpile data sent | The service accepted the file. The text shows the size and the war. |
| Signed in | The login is complete. |
| Sign in to send stockpile data | Custos has no valid token. Use **Sign in**. |
| An error from the service | The service refused the file, or it failed. Custos shows the same error at most one time in 15 minutes. |

## Build

Requirements: Rust 1.88 or later, and these packages on Ubuntu or Debian:

```sh
sudo apt install libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev build-essential
cargo install tauri-cli --version "^2" --locked
```

```sh
cargo test -p custos-core                  # The engine. Needs no GUI libraries.
cargo run -p custos-app                    # The tray application.
cargo tauri build --bundles appimage       # Run in crates/custos-app.
```

Set `RUST_LOG=debug` for more log output.

## Layout

| Crate | Content |
|---|---|
| [crates/custos-core](crates/custos-core) | The engine: configuration, discovery, login, save-file location, process detection, upload, and the interval rule. |
| [crates/custos-app](crates/custos-app) | The Tauri tray application: menu, notifications, and the deep link. |
