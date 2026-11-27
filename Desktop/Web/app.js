"use strict";

const byId = id => document.getElementById(id);
const host = window.chrome?.webview;
const partKeys = ["Процессор", "Видеокарта", "Память", "Накопитель"];
const partIcons = ["cpu", "gpu", "ram", "drive"];
const navOrder = [0, 1, 2, 3, 4, 8, 9, 5, 6, 7];
const navIcons = ["overview", "hardware", "memory", "clean", "tune", "telemetry", "focus", "compare", "packages", "personalize"];
const fields = new Map();
const chartCache = new WeakMap();
let state = {};
let page = -1;
let selection = 0;
let navigationSignature = "";
let settingsSignature = "";
let display3D;
let partButtons = [];
let board;

function send(op, value, id) {
  host?.postMessage({ op, id, value });
}

function text(el, value) {
  const next = String(value ?? "");
  if (el.textContent !== next) el.textContent = next;
}

function named(el, label) {
  if (label) el.setAttribute("aria-label", label);
  else el.removeAttribute("aria-label");
}

function term(key) {
  return state.labels?.[key] || key;
}

function make(tag, css, title) {
  const el = document.createElement(tag);
  if (css) el.className = css;
  if (title != null) text(el, title);
  if (tag === "button") el.type = "button";
  return el;
}

function child(el, tag, css, title) {
  const node = make(tag, css, title);
  el.append(node);
  return node;
}

function setChrome() {
  text(byId("search-label"), term("Поиск разделов и действий"));
  for (const [id, name] of Object.entries({
    "search-button": "Поиск разделов и действий", "settings-button": "Настройки", "assistant-button": "Шифтер", minimize: "Свернуть",
    maximize: "Развернуть", close: "Закрыть", "settings-close": "Закрыть", "search-close": "Закрыть"
  })) {
    const title = term(name);
    named(byId(id), title);
    byId(id).title = title;
  }
  text(byId("search-title"), term("Поиск разделов и действий"));
  text(byId("settings-title"), term("Настройки"));
  text(byId("assistant-label"), term("Шифтер"));
  byId("search-query").placeholder = term("Поисковый запрос");
  named(byId("search-query"), term("Поисковый запрос"));
  named(byId("board-model"), term("Схема компьютера"));
}

function drawNav() {
  const signature = JSON.stringify(state.navigation || []);
  if (signature === navigationSignature) return;
  navigationSignature = signature;
  const nav = byId("navigation");
  nav.replaceChildren();
  let group;
  for (const index of navOrder) {
    const item = state.navigation?.[index];
    if (!item) continue;
    if (item.group !== group) {
      group = item.group;
      child(nav, "div", "nav-label", group);
    }
    const button = child(nav, "button", "nav-button");
    const icon = child(button, "span", "nav-icon");
    icon.dataset.icon = navIcons[index];
    icon.setAttribute("aria-hidden", "true");
    child(button, "span", null, item.title);
    button.dataset.pageIndex = index;
    button.title = item.title;
    button.onclick = () => send("navigate", index);
  }
}

function markCurrentPage() {
  const nav = byId("navigation");
  nav.querySelector('[aria-current=page]')?.removeAttribute("aria-current");
  nav.querySelector(`[data-page-index="${page}"]`)?.setAttribute("aria-current", "page");
}

function drawSettings() {
  const dialog = byId("settings");
  if (!state.settings) {
    if (dialog.open) dialog.close();
    if (settingsSignature) byId("settings-body").replaceChildren();
    settingsSignature = "";
    return;
  }
  if (!dialog.open && !byId("search").open) dialog.showModal();
  const { sections, index = 0, body } = state.settings;
  const signature = JSON.stringify([sections, index]);
  if (settingsSignature !== signature) {
    settingsSignature = signature;
    const nav = byId("settings-tabs");
    nav.replaceChildren();
    for (const [i, name] of sections.entries()) {
      const button = child(nav, "button", "nav-button", name);
      if (i === index) button.setAttribute("aria-current", "page");
      button.onclick = () => send("settingsSection", i);
    }
    byId("settings-body").scrollTop = 0;
  }
  reconcile(byId("settings-body"), body);
}

function drawConfirmation() {
  const dialog = byId("confirmation");
  const confirmation = state.confirmation;
  if (!confirmation) {
    if (dialog.open) dialog.close();
    return;
  }
  for (const [key, value] of Object.entries({title: confirmation.title, message: confirmation.message,
    cancel: confirmation.cancel, accept: confirmation.action})) text(byId(`confirmation-${key}`), value);
  if (!dialog.open) dialog.showModal();
}

function answerConfirmation(accepted) {
  if (state.confirmation) send("confirm", accepted, String(state.confirmation.id));
}

function renderOverview(data) {
  if (!data) return;
  const staticLabels = {
    "overview-title": "Обзор системы", "cpu-label": "Загрузка процессора", "cpu-detail": "Последняя минута",
    "ram-label": "Оперативная память", "disk-label": "Свободно на диске Windows",
    "part-details": "Подробнее", "board-title": "Схема компьютера", "board-reset": "Сбросить вид",
    "readout-title": "Нагрузка и ресурсы", "refresh-label": "Обновить", "snapshots-label": "До и после"
  };
  for (const [id, key] of Object.entries(staticLabels)) text(byId(id), term(key));
  for (const [id, value] of Object.entries({
    host: data.host, windows: data.windows, "refresh-status": data.refresh,
    "cpu-value": data.cpu, "ram-value": data.ram, "ram-detail": data.ramDetail,
    "disk-value": data.disk, "disk-detail": data.diskDetail, "part-label": data.partTitle,
    "part-name": data.partName, "part-value": data.partValue, "part-detail": data.partDetail, "focus-label": data.focus
  })) text(byId(id), value || "—");
  byId("refresh-status").dataset.warning = String(Boolean(data.warning));
  byId("disk-meter").value = data.diskFill || 0;
  const sampleCount = Math.floor(60 / (data.interval || 1));
  history(byId("cpu-chart"), data.cpuHistory, state.palette?.accent, sampleCount);
  history(byId("ram-chart"), data.ramHistory, state.palette?.violet, sampleCount);
  if (display3D === undefined) display3D = Boolean(state.initial3D);
  const plan = byId("board-plan");
  if (!partButtons.length) {
    partButtons = partKeys.map((key, i) => {
      const button = child(plan, "button", "part-button");
      const icon = child(button, "span", "part-icon");
      icon.dataset.part = partIcons[i];
      child(icon, "i");
      if (i === 1) child(icon, "i");
      icon.setAttribute("aria-hidden", "true");
      const caption = child(button, "div", "part-caption");
      child(caption, "span");
      child(caption, "small");
      button.onclick = () => send("part", i);
      return button;
    });
  }
  for (let i = 0; i < partButtons.length; i++) {
    const caption = partButtons[i].lastElementChild;
    text(caption.firstElementChild, term(partKeys[i]));
    const detail = data.components?.[i];
    caption.lastElementChild.hidden = !detail || detail === "—";
    text(caption.lastElementChild, detail);
    partButtons[i].firstElementChild.hidden = !state.artwork;
    partButtons[i].setAttribute("aria-pressed", i === (data.part || 0));
  }
  const showBoard = display3D && state.artwork;
  const drives = data.drives || [];
  const list = byId("board-drives");
  const driveSignature = JSON.stringify([data.driveIndex, drives]);
  list.hidden = !(data.part === 3 && drives.length > 0);
  if (!list.hidden && list.dataset.signature !== driveSignature) {
    list.dataset.signature = driveSignature;
    list.replaceChildren();
    drives.forEach((drive, index) => {
      const button = child(list, "button", "quiet", drive.volumes || drive.name);
      button.title = drive.name;
      button.setAttribute("aria-pressed", index === (data.driveIndex || 0));
      button.onclick = () => send("boardDrive", index);
    });
  }
  plan.hidden = showBoard;
  byId("board-model").hidden = !showBoard;
  byId("board-reset").hidden = !showBoard;
  byId("board-mode").hidden = !state.artwork;
  byId("board-mode").setAttribute("aria-pressed", Boolean(showBoard));
  text(byId("board-mode"), term(showBoard ? "Вернуться к 2D" : "Открыть 3D"));
  text(byId("board-hint"), term(showBoard ? "Перетащите для поворота, прокрутите для масштаба." : "Выберите компонент, чтобы увидеть показания."));
  if (showBoard) {
    board ??= new BoardScene(byId("board-model"), part => {
      if (part >= 3 && state.overview?.drives?.length) send("boardDrive", part - 3);
      else send("part", part);
    });
    const selected = data.part === 3 ? 3 + (data.driveIndex || 0) : data.part || 0;
    board.present(selected, state.palette.accent, Boolean(state.hover), drives.length);
  } else board?.pause();
  reconcile(byId("overview-options"), state.body);
}

function history(canvas, input, color, sampleCount) {
  const points = input || [];
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (!width || !height) return;
  const signature = JSON.stringify([width, height, color, sampleCount, points]);
  if (chartCache.get(canvas) === signature) return;
  chartCache.set(canvas, signature);
  const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = width * pixelRatio;
  canvas.height = height * pixelRatio;
  const ctx = canvas.getContext("2d");
  ctx.scale(pixelRatio, pixelRatio);
  if (!points.length) return;
  ctx.strokeStyle = color || "#9bd5c8";
  ctx.lineWidth = 1.7;
  const path = new Path2D();
  const startX = width * (1 - (points.length - 1) / (sampleCount - 1));
  points.forEach((value, i) => {
    const x = startX + i * width / (sampleCount - 1);
    const y = 3 + (1 - Math.max(0, Math.min(value, 100)) / 100) * (height - 6);
    if (i) path.lineTo(x, y);
    else path.moveTo(x, y);
    if (i === points.length - 1) {
      ctx.fillStyle = color;
      ctx.fillRect(x - 3, y - 1.5, 3, 3);
    }
  });
  const fill = new Path2D(path);
  fill.lineTo(width, height);
  fill.lineTo(startX, height);
  fill.closePath();
  const gradient = ctx.createLinearGradient(0, 0, 0, height);
  gradient.addColorStop(0, color + "42");
  gradient.addColorStop(1, color + "00");
  ctx.fillStyle = gradient;
  ctx.fill(fill);
  ctx.stroke(path);
}

function reconcile(parent, nodes = []) {
  const before = new Map(Array.from(parent.children, item => [item.dataset.key, item]));
  for (let index = 0; index < nodes.length; index++) {
    const node = nodes[index];
    let element = before.get(node.id);
    if (element && element.dataset.kind !== node.kind) {
      element.remove();
      element = null;
    }
    if (!element) element = createNode(node);
    patchNode(element, node);
    if (parent.children[index] !== element) parent.insertBefore(element, parent.children[index] ?? null);
    before.delete(node.id);
  }
  for (const el of before.values()) el.remove();
}

function createNode(node) {
  const tags = { button: "button", input: "input", label: "div", select: "select", output: "pre",
    switch: "label", radio: "label", chart: "canvas", progress: "progress" };
  const heading = node.kind === "text" && { title: "h1", heading: "h2" }[node.style];
  const hasSamples = node.kind === "select" && ["ThemePicker", "PalettePicker"].includes(node.name);
  const element = make(hasSamples ? "div" : tags[node.kind] || heading || "div", hasSamples ? "sample-picker" : "node-" + node.kind);
  element.dataset.key = node.id;
  element.dataset.kind = node.kind;
  switch (node.kind) {
    case "button":
      element.onclick = () => send("control", null, node.id);
      break;
    case "input":
      element.type = "text";
      element.autocomplete = "off";
      element.oninput = () => {
        clearTimeout(fields.get(element));
        fields.set(element, setTimeout(() => {
          fields.delete(element);
          if (element.isConnected) send("control", element.value, node.id);
        }, 85));
      };
      element.onblur = () => {
        if (!fields.has(element)) return;
        clearTimeout(fields.get(element));
        fields.delete(element);
        if (element.isConnected) send("control", element.value, node.id);
      };
      break;
    case "select":
      if (hasSamples) element.setAttribute("role", "group");
      else element.onchange = () => send("control", element.selectedIndex, node.id);
      break;
    case "switch":
    case "radio": {
      const toggle = child(element, "input");
      toggle.type = node.kind === "radio" ? "radio" : "checkbox";
      toggle.onchange = () => send("control", toggle.checked, node.id);
      child(element, "span");
      break;
    }
    case "expander":
      child(element, "button").onclick = () => send("control", null, node.id);
      child(element, "div", "node-stack");
      break;
    case "tabs":
      child(element, "div", "tabbar").setAttribute("role", "tablist");
      child(element, "div", "node-stack");
      break;
    case "choice":
      element.tabIndex = 0;
      element.setAttribute("role", "option");
      element.onclick = event => {
        if (!event.target.closest("button,input,select,a") && element.getAttribute("aria-disabled") !== "true")
          send("control", null, node.id);
      };
      element.onkeydown = event => {
        if (event.target === element && (event.key === " " || event.key === "Enter")) {
          event.preventDefault();
          element.click();
        }
      };
      break;
  }
  return element;
}

function patchNode(element, node) {
  if (node.name) element.dataset.control = node.name;
  if (node.style) element.dataset.visual = node.style;
  else delete element.dataset.visual;
  if (element.parentElement?.classList.contains("node-grid")) {
    element.style.gridColumn = `${(node.column || 0) + 1} / span ${node.columnSpan || 1}`;
    element.style.gridRow = String((node.row || 0) + 1);
  } else if (!element.isConnected) {
    element.dataset.cell = `${(node.column || 0) + 1} / span ${node.columnSpan || 1}`;
  }
  if (node.style === "primary") element.classList.add("primary");
  if (node.label) named(element, node.label);
  if (node.height && ["items", "output", "scroll"].includes(node.kind)) {
    element.classList.add("bounded");
    element.style.maxHeight = `${Math.min(node.height, 430)}px`;
  }
  switch (node.kind) {
    case "button":
      text(element, node.text);
      element.disabled = Boolean(node.disabled);
      return;
    case "input": {
      const incoming = node.text || "";
      if (document.activeElement !== element && element.value !== incoming) element.value = incoming;
      if (node.maximum > 0) element.maxLength = node.maximum;
      element.disabled = Boolean(node.disabled);
      if (node.label) element.placeholder = node.label;
      return;
    }
    case "select": {
      const options = node.options || [];
      const disabled = (node.children || []).map(option => Boolean(option.disabled));
      const signature = JSON.stringify([options, disabled]);
      if (element.classList.contains("sample-picker")) {
        if (element.dataset.options !== signature) {
          element.dataset.options = signature;
          element.replaceChildren();
          options.forEach((title, index) => {
            const button = child(element, "button", "sample-option");
            button.dataset.option = index;
            child(button, "i").setAttribute("aria-hidden", "true");
            child(button, "span", null, title);
            button.onclick = () => send("control", index, node.id);
          });
        }
        for (const [index, button] of Array.from(element.children).entries()) {
          button.disabled = Boolean(node.disabled || disabled[index]);
          button.setAttribute("aria-pressed", (node.index ?? 0) === index);
        }
        return;
      }
      if (element.dataset.options !== signature) {
        element.dataset.options = signature;
        element.replaceChildren(...options.map((title, index) => {
          const option = make("option", null, title);
          option.disabled = disabled[index];
          return option;
        }));
      }
      element.selectedIndex = node.index ?? 0;
      element.disabled = Boolean(node.disabled);
      return;
    }
    case "switch":
    case "radio": {
      const toggle = element.firstElementChild;
      toggle.checked = Boolean(node.checked);
      toggle.disabled = Boolean(node.disabled);
      element.dataset.disabled = String(Boolean(node.disabled));
      text(element.lastElementChild, node.text || node.label);
      named(toggle, node.label || node.text);
      return;
    }
    case "text":
    case "label":
    case "output":
      text(element, node.text);
      element.hidden = !node.text;
      return;
    case "expander": {
      const button = element.firstElementChild;
      text(button, node.text);
      button.disabled = Boolean(node.disabled);
      button.setAttribute("aria-expanded", Boolean(node.checked));
      reconcile(element.lastElementChild, node.children);
      return;
    }
    case "tabs": {
      const headers = element.firstElementChild;
      const active = node.index ?? 0;
      const signature = JSON.stringify([node.options, active, node.disabled]);
      if (headers.dataset.state !== signature) {
        headers.dataset.state = signature;
        headers.replaceChildren();
        (node.options || []).forEach((title, index) => {
          const button = child(headers, "button", null, title);
          button.setAttribute("role", "tab");
          button.setAttribute("aria-selected", index === active);
          button.disabled = Boolean(node.disabled);
          button.onclick = () => send("control", index, node.id);
        });
      }
      reconcile(element.lastElementChild, node.children);
      return;
    }
    case "progress":
      element.max = node.maximum || 100;
      if (node.style === "pending") element.removeAttribute("value");
      else element.value = node.value || 0;
      return;
    case "chart":
      paintPath(element, node);
      return;
    case "grid":
      element.style.gridTemplateColumns = node.widths;
      break;
    case "tiles":
      if (node.columns) element.style.gridTemplateColumns = `repeat(${node.columns}, minmax(0,1fr))`;
      break;
    case "choice":
      element.setAttribute("aria-selected", Boolean(node.selected));
      element.setAttribute("aria-disabled", Boolean(node.disabled));
      break;
  }
  reconcile(element, node.children);
  if (node.kind === "grid") {
    (node.children || []).forEach((childNode, index) => {
      const child = element.children[index];
      child.style.gridColumn = `${(childNode.column || 0) + 1} / span ${childNode.columnSpan || 1}`;
      child.style.gridRow = String((childNode.row || 0) + 1);
    });
  }
}

function paintPath(canvas, node) {
  if (!node.width || !node.height) return;
  const signature = JSON.stringify([node.width, node.height, node.children]);
  if (chartCache.get(canvas) === signature) return;
  chartCache.set(canvas, signature);
  canvas.width = node.width;
  canvas.height = node.height;
  const context = canvas.getContext("2d");
  for (const line of node.children || []) {
    if (!line.text) continue;
    context.strokeStyle = line.style?.length === 9 ? "#" + line.style.slice(3) + line.style.slice(1, 3) :
      state.palette.accent;
    context.lineWidth = line.value || 1.5;
    context.stroke(new Path2D(line.text.replace(/^F[01]\s*/, "")));
  }
}

function openSearch() {
  if (byId("settings").open) return;
  const dialog = byId("search");
  if (!dialog.open) dialog.showModal();
  byId("search-query").value = "";
  selection = 0;
  drawSearch();
  byId("search-query").focus();
}

function drawSearch() {
  const query = byId("search-query").value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  const entries = (state.search || []).map((action, i) => ({ ...action, i })).filter(action =>
    query.every(word => action.keywords.toLocaleLowerCase().includes(word)));
  const results = byId("search-results");
  results.replaceChildren();
  selection = Math.max(0, Math.min(selection, entries.length - 1));
  for (const [i, entry] of entries.entries()) {
    const button = child(results, "button", "search-result");
    button.setAttribute("aria-selected", i === selection);
    child(button, "strong", null, entry.title);
    child(button, "small", null, entry.description);
    child(button, "span", "result-category", entry.category);
    button.onclick = () => { byId("search").close(); send("search", entry.i); };
  }
  if (!entries.length) child(results, "p", "hint", term("Совпадений нет"));
}

function receive({ data }) {
  if (!data || typeof data !== "object") return;
  const nextPage = data.page || 0;
  const changedPage = page !== nextPage;
  const languageChanged = Boolean(state.english) !== Boolean(data.english);
  const first = page < 0;
  if (display3D !== undefined && Boolean(state.initial3D) !== Boolean(data.initial3D)) display3D = Boolean(data.initial3D);
  page = nextPage;
  state = data;
  const root = document.documentElement;
  root.lang = state.english ? "en" : "ru";
  root.dataset.light = String(Boolean(state.light));
  root.dataset.density = String(state.density || 0);
  root.dataset.hover = String(Boolean(state.hover));
  root.dataset.motion = String(Boolean(state.animation));
  root.dataset.glass = String(Boolean(state.glass));
  root.dataset.background = String(state.background || 0);
  root.dataset.page = page;
  for (const [key, value] of Object.entries(state.palette || {})) root.style.setProperty("--" + key, value);
  if (first || languageChanged) setChrome();
  drawNav();
  if (changedPage || languageChanged) markCurrentPage();
  reconcile(byId("update"), state.banner);
  byId("overview").hidden = page !== 0;
  byId("native-data").hidden = page === 0;
  if (page === 0) {
    if (changedPage) byId("native-data").replaceChildren();
    renderOverview(state.overview);
  }
  else {
    board?.pause();
    reconcile(byId("native-data"), state.body);
  }
  if (changedPage) {
    byId("workspace").scrollTop = 0;
    const current = page === 0 ? byId("overview") : byId("native-data");
    if (state.animation) {
      current.classList.remove("enter");
      requestAnimationFrame(() => current.classList.add("enter"));
    }
  }
  if (state.scrollTarget) {
    const control = byId("workspace").querySelector(`[data-control="${CSS.escape(state.scrollTarget)}"]`);
    control?.scrollIntoView({ block: "start" });
    if (control?.matches("input")) control.focus({ preventScroll: true });
  }
  drawSettings();
  drawConfirmation();
}

byId("search-button").onclick = openSearch;
byId("search-close").onclick = () => byId("search").close();
byId("search-query").oninput = () => { selection = 0; drawSearch(); };
byId("settings-button").onclick = () => { if (!byId("search").open) send("settings"); };
byId("assistant-button").onclick = () => send("assistant");
byId("settings-close").onclick = () => { byId("settings").close(); send("settingsClose"); };
byId("settings").oncancel = () => send("settingsClose");
byId("confirmation-cancel").onclick = () => answerConfirmation(false);
byId("confirmation-accept").onclick = () => answerConfirmation(true);
byId("confirmation").oncancel = event => { event.preventDefault(); answerConfirmation(false); };
byId("refresh").onclick = () => send("refresh");
byId("snapshots").onclick = () => send("navigate", 7);
byId("focus").onclick = () => send("navigate", 6);
byId("part-details").onclick = () => send("partDetails");
byId("board-mode").onclick = () => { display3D = !display3D; renderOverview(state.overview); };
byId("board-reset").onclick = () => board?.reset();
for (const name of ["minimize", "maximize", "close"]) byId(name).onclick = () => send(name);
for (const region of document.querySelectorAll("[data-drag]")) {
  region.onpointerdown = event => { if (event.button === 0 && !event.target.closest("button")) send("drag"); };
  region.ondblclick = event => { if (!event.target.closest("button")) send("maximize"); };
}
let hoverFrame = 0;
let pointer;
document.addEventListener("pointermove", event => {
  if (!state.hover || event.pointerType !== "mouse") return;
  pointer = event;
  if (hoverFrame) return;
  hoverFrame = requestAnimationFrame(() => {
    hoverFrame = 0;
    const card = pointer.target.closest(".interactive, .node-card");
    if (!card) return;
    const bounds = card.getBoundingClientRect();
    card.style.setProperty("--px", `${pointer.clientX - bounds.left}px`);
    card.style.setProperty("--py", `${pointer.clientY - bounds.top}px`);
  });
});
document.addEventListener("keydown", event => {
  if (byId("confirmation").open) return;
  if (event.ctrlKey && event.key.toLowerCase() === "k") {
    event.preventDefault();
    openSearch();
  } else if (event.ctrlKey && event.key === ",") {
    event.preventDefault();
    byId("search").close();
    send("settings");
  } else if (byId("search").open && ["ArrowDown", "ArrowUp", "Enter"].includes(event.key)) {
    event.preventDefault();
    if (event.key === "Enter") byId("search-results").querySelector('[aria-selected="true"]')?.click();
    else { selection += event.key === "ArrowDown" ? 1 : -1; drawSearch(); }
  }
});
window.addEventListener("resize", () => { if (page === 0) renderOverview(state.overview); });
host?.addEventListener("message", receive);
send("ready");
