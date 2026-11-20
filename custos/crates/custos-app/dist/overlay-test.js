// The defect test of the overlay. The first line stays. The second line appears and disappears every
// 3 seconds. Its length changes each time, so a stale pixel of a longer line stays visible.
"use strict";

const lines = document.getElementById("lines");
const started = new Date().toLocaleTimeString();
let count = 0;

function line(text, isError) {
  const item = document.createElement("li");
  item.textContent = text;
  item.classList.toggle("error", isError);
  return item;
}

lines.append(line(`Custos overlay test, started at ${started}`, false));

setInterval(() => {
  const second = lines.children[1];
  if (second) {
    second.remove();
    return;
  }
  count += 1;
  const long = count % 2 === 1;
  const text = long
    ? `Line ${count}: a long line that the next short line does not cover`
    : `Line ${count}: short`;
  lines.append(line(text, long));
}, 3000);
