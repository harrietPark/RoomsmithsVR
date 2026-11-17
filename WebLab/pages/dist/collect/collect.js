const demoCode = "WAVE2026";
const sampleCreations = [
  {
    id: "soft-wave",
    name: "Soft wave",
    description: "Desk lamp shade · about 102 mm wide",
    createdOn: "2026-11-13",
    preview: "soft-wave.glb",
    formats: [
      ["GLB", "soft-wave.glb"], ["FBX", "soft-wave.fbx"], ["OBJ", "soft-wave-obj.zip"],
      ["3MF", "soft-wave.3mf"], ["STL", "soft-wave.stl"],
    ],
  },
  {
    id: "sage-hex",
    name: "Sage hex",
    description: "Hexagonal coaster · 100 × 111 × 7 mm",
    createdOn: "2026-11-09",
    preview: "sage-hex-preview.glb",
    formats: [["STL", "sage-hex.stl"]],
  },
];
let creations = sampleCreations;
let lookupRequest;
const openCollectionKey = "roomsmiths.openCollection";
const form = document.querySelector("#code-form");
const code = document.querySelector("#code");
const feedback = document.querySelector("#code-feedback");
const lookup = document.querySelector("#lookup");
const collection = document.querySelector("#collection");
const result = document.querySelector("#result");
const model = document.querySelector("#model");
const state = document.querySelector("#model-state");
const rotateHint = document.querySelector("#rotate-hint");
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const promptedModels = new Set();
let rotationPromptTimer;

function stopRotationPrompt() {
  clearTimeout(rotationPromptTimer);
  model.setAttribute("interaction-prompt", "none");
}

function showRotationPrompt() {
  const src = model.getAttribute("src");
  if (result.hidden || !model.loaded || reducedMotion.matches || promptedModels.has(src)) return;
  promptedModels.add(src);
  model.interactionPrompt = "auto";
  model.resetInteractionPrompt();
  rotationPromptTimer = setTimeout(stopRotationPrompt, 4400);
}

function setError(message) {
  feedback.textContent = message;
  code.setAttribute("aria-invalid", String(Boolean(message)));
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (className) node.className = className;
  return node;
}

const dateFormat = new Intl.DateTimeFormat("en-GB", {
  day: "numeric", month: "short", year: "numeric", timeZone: "UTC",
});

function showCreationDate(node, work) {
  if (!work.createdOn) { node.hidden = true; return node; }
  node.hidden = false;
  node.dateTime = work.createdOn;
  node.textContent = "Made " + dateFormat.format(new Date(work.createdOn));
  return node;
}

function fillCollection() {
  const list = document.querySelector("#works");
  list.replaceChildren();
  for (const work of creations) {
  const card = element("li", "", "panel");
  const link = element("a", "", "work-link");
  link.href = "#creations/" + work.id;
  const image = element(work.preview ? "model-viewer" : "img", "", "work-image");
  if (work.preview) {
    image.setAttribute("src", work.remote ? work.preview : "sample/" + work.preview);
    image.setAttribute("camera-orbit", "35deg 55deg 112%");
    image.setAttribute("shadow-intensity", "0.65");
    image.setAttribute("exposure", "1.05");
    image.setAttribute("interaction-prompt", "none");
    image.setAttribute("loading", "lazy");
    image.setAttribute("aria-hidden", "true");
    image.style.pointerEvents = "none";
  }
  else if (work.photo) image.src = work.photo;
  else if (!work.remote) image.src = "sample/" + work.id + ".png";
  else image.hidden = true;
  image.alt = "";
  const caption = element("div", "", "work-caption");
  const action = element("span", "↗", "work-action");
  action.setAttribute("aria-hidden", "true");
  caption.append(
    element("h2", work.name),
    element("p", work.description),
    showCreationDate(element("time", "", "creation-date"), work),
    action,
  );
  link.append(image, caption);
  card.append(link);
  list.append(card);
  }
}

function downloadLink([label, filename]) {
  const link = element("a", label + " ↓", "button secondary");
  link.href = filename.startsWith("/") ? filename : "sample/" + filename;
  link.download = filename.startsWith("/") ? "" : filename;
  link.setAttribute("aria-label", "Download " + (label === "OBJ" ? "OBJ ZIP" : label));
  return link;
}

function openWork(work) {
  document.querySelector("#work-title").textContent = work.name;
  document.querySelector("#work-description").textContent = work.description;
  showCreationDate(document.querySelector("#work-date"), work);
  const printing = work.formats.filter(([label]) => label === "3MF" || label === "STL");
  const editing = work.formats.filter((format) => !printing.includes(format));
  document.querySelector("#model-formats").hidden = editing.length === 0;
  document.querySelector("#model-downloads").replaceChildren(...editing.map(downloadLink));
  document.querySelector("#print-downloads").replaceChildren(...printing.map(downloadLink));
  document.querySelector("#print-description").textContent = printing.length > 1
    ? "Open in your slicer. 3MF has colour data. STL is shape only."
    : "Open the STL in your slicer.";
  const src = !work.preview ? null : work.remote ? work.preview : "sample/" + work.preview;
  model.hidden = !src;
  if (!src) {
    stopRotationPrompt();
    model.removeAttribute("src");
    state.hidden = false;
    state.textContent = "Download the files to view this creation.";
    rotateHint.hidden = true;
  }
  else if (model.getAttribute("src") !== src) {
    stopRotationPrompt();
    state.hidden = false;
    rotateHint.hidden = true;
    state.textContent = "Loading 3D preview…";
    model.setAttribute("alt", work.name + " 3D preview");
    model.setAttribute("src", src);
  }
  document.querySelector("#work-title").focus({ preventScroll: true });
}

function render() {
  const route = location.hash.slice(1);
  const work = creations.find((item) => route === "creations/" + item.id);
  const inCollection = route === "creations";
  const wasLookup = !lookup.hidden;
  lookup.hidden = Boolean(work) || inCollection;
  collection.hidden = !inCollection;
  result.hidden = !work;
  if (work) openWork(work);
  else stopRotationPrompt();
  if (inCollection) document.querySelector("#collection-title").focus({ preventScroll: true });
  else if (!work && !wasLookup) code.focus({ preventScroll: true });
  window.scrollTo(0, 0);
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const value = code.value.replace(/[\s-]/g, "").toUpperCase();
  if (!/^[A-Z0-9]{8}$/.test(value)) {
    setError("Enter the eight characters in your code.");
    code.focus();
    return;
  }
  if (value === demoCode) { openSample(); return; }
  await openCollection(value);
});

async function openCollection(value, keepRoute = false) {
  lookupRequest?.abort();
  const request = new AbortController();
  lookupRequest = request;
  const submit = form.querySelector('button[type="submit"]');
  submit.disabled = true;
  setError("");
  try {
    const response = await fetch("/api/collect/" + value, { signal: request.signal });
    if (response.status === 404) {
      if (keepRoute) { sessionStorage.removeItem(openCollectionKey); location.hash = ""; }
      setError("Code not found or expired."); code.focus(); return;
    }
    if (!response.ok) throw new Error("Collection unavailable");
    const { works } = await response.json();
    const accepted = { glb: "GLB", fbx: "FBX", obj: "OBJ", stl: "STL", "3mf": "3MF" };
    creations = works.map(work => {
      const base = "/api/collect/" + value + "/" + encodeURIComponent(work.id) + "/";
      return { ...work, remote: true, photo: work.formats.includes("jpg") ? base + "jpg" : null,
        preview: work.formats.includes("glb") ? base + "glb" : null,
        formats: Object.keys(accepted).filter(format => work.formats.includes(format)).map(format => [accepted[format], base + format]) };
    });
    sessionStorage.setItem(openCollectionKey, value);
    fillCollection();
    if (keepRoute) render();
    else location.hash = "creations";
  } catch (error) {
    if (error.name !== "AbortError") {
      if (keepRoute) location.hash = "";
      setError("Could not open the collection. Try again.");
    }
  } finally { if (lookupRequest === request) submit.disabled = false; }
}
code.addEventListener("input", () => setError(""));
function openSample() {
  lookupRequest?.abort();
  form.querySelector('button[type="submit"]').disabled = false;
  sessionStorage.removeItem(openCollectionKey);
  creations = sampleCreations;
  fillCollection();
  location.hash = "creations";
}
document.querySelector("#sample").addEventListener("click", openSample);
document.querySelector("#change-code").addEventListener("click", () => {
  lookupRequest?.abort();
  sessionStorage.removeItem(openCollectionKey);
  form.reset();
  setError("");
  location.hash = "";
});
model.addEventListener("pointerdown", stopRotationPrompt);
model.addEventListener("keydown", stopRotationPrompt);
model.addEventListener("wheel", stopRotationPrompt, { passive: true });
reducedMotion.addEventListener("change", stopRotationPrompt);
model.addEventListener("load", () => {
  state.hidden = true;
  rotateHint.hidden = false;
  showRotationPrompt();
});
model.addEventListener("error", () => {
  state.hidden = false;
  state.textContent = "The 3D preview couldn’t load. You can still download the files.";
});
window.addEventListener("hashchange", render);
const openCode = sessionStorage.getItem(openCollectionKey);
if (openCode && location.hash.startsWith("#creations")) openCollection(openCode, true);
else { fillCollection(); render(); }
