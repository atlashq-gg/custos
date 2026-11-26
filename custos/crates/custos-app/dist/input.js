// The input window of the query on Windows (input.rs). Enter sends the line, and Escape closes the
// window. While a question waits and the entry is empty, Y accepts and N refuses.
"use strict";

const { invoke } = window.__TAURI__.core;
const history = document.getElementById("history");
const hint = document.getElementById("hint");
const entry = document.getElementById("entry");
let help = "";

// Shows the last query and the help line. Custos calls it (input.rs).
function show(page) {
    history.replaceChildren(
        ...page.lines.map((line) => {
            const label = document.createElement("label");
            if (line.error) {
                label.className = "custos-error";
            }
            label.textContent = line.text;
            return label;
        }),
    );
    help = page.hint;
    if (!hint.classList.contains("custos-error")) {
        hint.textContent = help;
    }
    entry.focus();
}

// A change of the entry removes an error.
entry.addEventListener("input", () => {
    hint.classList.remove("custos-error");
    hint.textContent = help;
});

document.addEventListener("keydown", async (event) => {
    if (event.key === "Escape") {
        event.preventDefault();
        await invoke("query_close");
    } else if (event.key === "Enter") {
        event.preventDefault();
        try {
            await invoke("query_submit", { line: entry.value });
        } catch (error) {
            hint.classList.add("custos-error");
            hint.textContent = error;
        }
    } else if (entry.value === "" && /^[yn]$/i.test(event.key)) {
        // The key types into the entry when no question waits.
        event.preventDefault();
        if (!(await invoke("query_decide", { key: event.key }))) {
            entry.value = event.key;
        }
    }
});
