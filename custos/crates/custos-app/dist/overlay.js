// The overlay page on Windows. Custos keeps the lines (stream.rs in custos-core) and calls render() at
// each change. The page types the lines at the rate of the stream, one after the other.
"use strict";

const CURSOR = "█";
const lines = document.getElementById("lines");
const prompt = document.getElementById("prompt");
// For each line: its characters, the number of typed characters at the last render, and its elements.
let shown = [];
let rendered = 0;
let rate = 60;
let frame = 0;

// Shows the lines. Custos calls it (overlay.rs).
function render(page) {
    document.body.classList.toggle("bottom", !page.top);
    document.body.classList.toggle("right", !page.left);
    document.body.style.setProperty("--cursor-period", `${page.period}s`);
    rate = page.rate;
    shown = page.lines.map((line) => {
        const label = document.createElement("label");
        if (line.error) {
            label.className = "custos-error";
        }
        const typed = document.createTextNode("");
        const cursor = document.createElement("span");
        cursor.textContent = CURSOR;
        const rest = document.createElement("span");
        rest.className = "rest";
        label.append(line.prompt, typed, cursor, rest);
        return { text: Array.from(line.text), typed: line.typed, count: -1, label, typedNode: typed, cursor, rest };
    });
    lines.replaceChildren(...shown.map((line) => line.label));
    cancelAnimationFrame(frame);
    rendered = performance.now();
    type(rendered);
}

// Shows the typed part of each line. A line types when the line above it is complete. While no line
// types, shows the prompt.
function type(now) {
    let budget = Math.floor(((now - rendered) * rate) / 1000);
    let typing = false;
    for (const line of shown) {
        const count = Math.min(line.text.length, line.typed + budget);
        budget -= count - line.typed;
        typing ||= count < line.text.length;
        if (count !== line.count) {
            line.count = count;
            line.typedNode.data = line.text.slice(0, count).join("");
            line.rest.textContent = line.text.slice(count).join("");
            line.cursor.hidden = count === line.text.length;
        }
    }
    prompt.hidden = typing;
    frame = typing ? requestAnimationFrame(type) : 0;
}
