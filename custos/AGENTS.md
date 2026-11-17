# Agents: custos

- Custos conforms to [spec/](../spec/README.md). Where the code and spec/ disagree, change the code, or change spec/ in the same pull request.
- Put logic in `crates/custos-core`. Keep `crates/custos-app` to the tray, the notifications, and the deep link. The core crate must not depend on Tauri, so that its tests run without GUI libraries.
- Custos only reads the save files of the game. It does not write to them, inject code, or read process memory.
- Do not send the file name or the Steam ID of the player to a service.
- Before a commit, run `cargo fmt`, `cargo clippy --workspace --all-targets -- -D warnings`, and `cargo test --workspace`. The application crate needs the GUI libraries in [README.md](README.md#build).
- The content of this folder is under MIT.
