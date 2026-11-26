// The overlay page on Windows. Custos keeps the lines (lines.rs in custos-core) and calls render() at
// each change. Each line has the times when it shows and when newer lines push it out, in milliseconds
// from the render. The page shows each line in its time, and types it at the rate of Custos.
"use strict";

const CURSOR = "█";
const lines = document.getElementById("lines");
const prompt = document.getElementById("prompt");
// For each line: its characters, its times, the state at the last frame, and its elements.
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
    shown = page.rows.map((row) => {
        const label = document.createElement("label");
        if (row.error) {
            label.className = "custos-error";
        }
        label.hidden = true;
        const typed = document.createTextNode("");
        const cursor = document.createElement("span");
        cursor.textContent = CURSOR;
        const rest = document.createElement("span");
        rest.className = "rest";
        label.append(row.prompt, typed, cursor, rest);
        return { text: Array.from(row.text), show: row.show, hide: row.hide, count: -1, label, typedNode: typed, cursor, rest };
    });
    lines.replaceChildren(...shown.map((line) => line.label));
    cancelAnimationFrame(frame);
    rendered = performance.now();
    type(rendered);
}

// Shows each line in its time, and its typed part. While no line types, shows the prompt. Draws
// frames only while a line types or a time is still to come.
function type(now) {
    const elapsed = now - rendered;
    let typing = false;
    let waiting = false;
    for (const line of shown) {
        line.label.hidden = elapsed < line.show || (line.hide !== null && elapsed >= line.hide);
        waiting ||= line.hide !== null && elapsed < line.hide;
        const count = Math.max(0, Math.min(line.text.length, Math.floor(((elapsed - line.show) * rate) / 1000)));
        typing ||= count < line.text.length;
        if (count !== line.count) {
            line.count = count;
            line.typedNode.data = line.text.slice(0, count).join("");
            line.rest.textContent = line.text.slice(count).join("");
            line.cursor.hidden = count === line.text.length;
        }
    }
    prompt.hidden = typing;
    frame = typing || waiting ? requestAnimationFrame(type) : 0;
}

// Shows the lines of the query in their area. They do not type. Custos calls it (overlay.rs).
function renderQuery(rows) {
    document.getElementById("query").replaceChildren(
        ...rows.map((row) => {
            const label = document.createElement("label");
            if (row.error) {
                label.className = "custos-error";
            }
            label.textContent = row.prompt + row.text;
            return label;
        }),
    );
}
