import { PRESETS, KNOBS, ROSTERS, teamFor, buildInstructions, buildSchema, buildInput } from "./team.js";

const $ = id => document.getElementById(id);
const RATINGS = [
  ["helped", "내 아이디어에 도움이 됐다"],
  ["understood", "내 말과 맥락을 이해했다"],
  ["team", "진짜 팀원(에이전트) 같았다"],
  ["overload", "정보가 너무 많았다 (낮을수록 좋음)"]
];

let state, busy = false, lastActivity = Date.now(), nudged = false, presetId = "A";
const speech = [];        // lines waiting to be shown/spoken
let speaking = false;

// ------------------------------------------------------------ session

function newSession(config, id) {
  presetId = id;
  state = {
    id: new Date().toISOString().replace(/[:.]/g, "-") + "_" + id,
    preset: id, config: { ...config }, log: [], cards: [], board: [], summary: { idea: "", risk: "", next: "" },
    teamTurns: 0, addressed: "", viewVersion: -1, rating: {}, note: "", timings: []
  };
  speech.length = 0;
  busy = false;
  nudged = false;
  renderPresets();
  renderDesk();
  renderLog();
  renderArtifact();
  renderRating();
  const preset = PRESETS.find(p => p.id === id);
  $("hint").textContent = (preset ? preset.hint + " · " : "직접 설정 · ") + describe(state.config);
  $("continueBtn").hidden = state.config.talk !== "debate";
  updateTo();
  $("input").focus();
}

function describe(c) {
  return [KNOBS.roster.options[c.roster], KNOBS.talk.options[c.talk], KNOBS.direction.options[c.direction], KNOBS.output.options[c.output]].join(" · ");
}

function agents() { return teamFor(state.config).agents; }
function agent(id) { return agents().find(a => a.id === id); }

// ------------------------------------------------------------ team turn

async function teamTurn(event) {
  if (busy) return;
  busy = true;
  setThinking(true);
  const started = performance.now();
  try {
    const r = await fetch("/api/respond", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        model: state.config.model, instructions: buildInstructions(state.config), input: buildInput(state, event),
        schema: buildSchema(state.config), web: state.config.web === "on"
      })
    });
    const data = await r.json();
    if (!r.ok) throw new Error(data.error || r.status);
    state.timings.push(Math.round(performance.now() - started) / 1000);
    state.teamTurns++;
    let turns = data.result.turns.filter(t => t.say && agent(t.agent));
    // "I pick": only the one I handed the card to answers.
    if (state.config.talk === "pick" && event.addressed) {
      const mine = turns.filter(t => t.agent === event.addressed);
      turns = mine.length ? [{ agent: event.addressed, say: mine.map(t => t.say).join(" ") }] : turns.slice(0, 1);
    }
    applyArtifacts(data.result);
    setThinking(false);
    for (const t of turns) speech.push(t);
    await playSpeech();
  } catch (error) {
    setThinking(false);
    addLine({ err: true, say: "오류: " + error.message });
  } finally {
    busy = false;
    updateSend();
    lastActivity = Date.now();
    autosave();
  }
}

function applyArtifacts(result) {
  if (result.card && result.card.changed && result.card.title) {
    state.cards.push({ title: result.card.title, pitch: result.card.pitch, change: result.card.change, at: state.log.length });
    state.viewVersion = -1;
  }
  if (result.board && result.board.changed) state.board = result.board.notes || [];
  if (result.summary) state.summary = result.summary;
  renderArtifact();
  renderDeskCard();
}

// Lines appear one after another like people talking (with voice when it is on).
async function playSpeech() {
  if (speaking) return;
  speaking = true;
  while (speech.length) {
    const t = speech.shift();
    state.log.push({ agent: t.agent, say: t.say });
    renderLog();
    showBubble(t.agent, t.say);
    if (state.config.voice === "on") await speak(t);
    else await wait(Math.min(4500, 900 + t.say.length * 28));
  }
  setSpeaking(null);
  speaking = false;
}

async function speak(t) {
  try {
    const a = agent(t.agent);
    const r = await fetch("/api/tts", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ voice: a.voice, text: t.say, style: "You are " + a.name + ", a small toy teammate. " + a.role + " Speak naturally, like a friend at the table." }) });
    if (!r.ok) throw new Error("voice " + r.status);
    const url = URL.createObjectURL(await r.blob());
    const audio = new Audio(url);
    await new Promise(resolve => { audio.onended = resolve; audio.onerror = resolve; audio.play().catch(resolve); });
    URL.revokeObjectURL(url);
  } catch (error) {
    await wait(1500);
  }
}

const wait = ms => new Promise(r => setTimeout(r, ms));

// ------------------------------------------------------------ input

function send(text, handedTo) {
  text = (text || "").trim();
  const addressed = handedTo || (state.config.talk === "pick" ? state.addressed : "");
  if (!text && !addressed) return;
  if (busy) return;
  if (state.config.talk === "pick" && !addressed && state.config.roster !== "solo") {
    addLine({ sys: true, say: "위 캐릭터를 눌러서 누구에게 말할지 고르거나, 카드를 그 캐릭터에게 끌어다 놓아 주세요." });
    return;
  }
  const a = addressed ? agent(addressed) : null;
  const say = text ? (a ? `(${a.name}에게) ${text}` : text) : `(카드를 ${a.name}에게 건넴)`;
  addLine({ say });
  $("input").value = "";
  lastActivity = Date.now();
  nudged = false;
  teamTurn({ type: "message", addressed });
}

// The person's lines (agent: null), or notes for the person only (kind: sys/err, never sent to the AI).
function addLine(line) {
  state.log.push(line.err || line.sys ? { kind: line.err ? "err" : "sys", say: line.say } : { agent: null, say: line.say });
  renderLog();
}

// ------------------------------------------------------------ rendering

function renderPresets() {
  $("presets").innerHTML = "";
  for (const p of PRESETS) {
    const b = document.createElement("button");
    b.className = "chip" + (p.id === presetId ? " on" : "");
    b.textContent = p.name;
    b.title = p.hint;
    b.onclick = () => newSession(p.config, p.id);
    $("presets").appendChild(b);
  }
  if (presetId === "custom") {
    const b = document.createElement("span");
    b.className = "chip on";
    b.textContent = "직접 설정";
    $("presets").appendChild(b);
  }
}

function renderDesk() {
  const desk = $("desk");
  desk.querySelectorAll(".agent,.bubble,.deskcard").forEach(n => n.remove());
  const list = agents();
  list.forEach((a, i) => {
    const t = list.length === 1 ? .5 : i / (list.length - 1);
    const angle = Math.PI * (1.12 + .76 * t); // an arc above "나"
    const x = 50 + Math.cos(angle) * 40, y = 92 + Math.sin(angle) * 70;
    const el = document.createElement("div");
    el.className = "agent";
    el.dataset.id = a.id;
    el.style.left = x + "%";
    el.style.top = y + "%";
    el.innerHTML = `<div class="body" style="background:${a.color}"></div><div class="name">${a.name}</div>`;
    el.title = a.role;
    el.onclick = () => {
      if (state.config.talk !== "pick") return;
      state.addressed = state.addressed === a.id ? "" : a.id;
      updateTo();
    };
    desk.appendChild(el);
  });
  renderDeskCard();
}

// In "I pick" with a card, the card sits in front of me and can be dragged onto a teammate.
function renderDeskCard() {
  const desk = $("desk");
  desk.querySelector(".deskcard")?.remove();
  if (state.config.talk !== "pick") return;
  const card = document.createElement("div");
  card.className = "deskcard";
  const last = state.cards[state.cards.length - 1];
  card.innerHTML = last ? `<b>${esc(last.title)}</b>v${state.cards.length} · 끌어서 건네기` : `<b>내 아이디어</b>끌어서 건네기`;
  desk.appendChild(card);
  let drag = null;
  card.onpointerdown = e => { drag = { x: e.clientX, y: e.clientY }; card.setPointerCapture(e.pointerId); card.style.cursor = "grabbing"; };
  card.onpointermove = e => {
    if (!drag) return;
    card.style.transform = `translate(calc(-50% + ${e.clientX - drag.x}px), calc(-50% + ${e.clientY - drag.y}px))`;
    const over = agentAt(e.clientX, e.clientY);
    desk.querySelectorAll(".agent").forEach(n => n.classList.toggle("drop", n === over));
  };
  card.onpointerup = e => {
    if (!drag) return;
    const over = agentAt(e.clientX, e.clientY);
    drag = null;
    card.style.transform = "";
    card.style.cursor = "";
    desk.querySelectorAll(".agent").forEach(n => n.classList.remove("drop"));
    if (over) { state.addressed = over.dataset.id; updateTo(); send($("input").value, over.dataset.id); }
  };
}

function agentAt(x, y) {
  for (const n of $("desk").querySelectorAll(".agent")) {
    const r = n.getBoundingClientRect();
    if (x > r.left - 14 && x < r.right + 14 && y > r.top - 14 && y < r.bottom + 14) return n;
  }
  return null;
}

function updateTo() {
  $("desk").querySelectorAll(".agent").forEach(n => n.classList.toggle("addressed", n.dataset.id === state.addressed));
  const a = agent(state.addressed);
  $("to").textContent = state.config.talk === "pick"
    ? (a ? `받는 사람: ${a.name} (다시 누르면 해제)` : "캐릭터를 눌러 받는 사람을 고르거나, 카드를 캐릭터에게 끌어다 놓기")
    : "";
}

function setThinking(on) {
  $("desk").querySelectorAll(".agent").forEach(n => {
    const one = state.config.talk === "pick" && state.addressed;
    n.classList.toggle("thinking", on && (!one || n.dataset.id === state.addressed));
  });
  updateSend(on ? "생각 중…" : "말하는 중…");
}

// The send button stays off until the team has finished speaking.
function updateSend(label) {
  $("send").disabled = busy;
  $("continueBtn").disabled = busy;
  $("send").textContent = busy ? label : "보내기";
}

function setSpeaking(id) {
  $("desk").querySelectorAll(".agent").forEach(n => n.classList.toggle("speaking", n.dataset.id === id));
  if (!id) $("desk").querySelectorAll(".bubble").forEach(b => { b.style.opacity = 0; setTimeout(() => b.remove(), 300); });
}

function showBubble(id, text) {
  setSpeaking(id);
  const desk = $("desk");
  desk.querySelectorAll(".bubble").forEach(b => b.remove());
  const el = desk.querySelector(`.agent[data-id="${id}"]`);
  if (!el) return;
  const b = document.createElement("div");
  b.className = "bubble";
  b.textContent = text.length > 140 ? text.slice(0, 137) + "…" : text;
  b.style.left = el.style.left;
  b.style.top = `calc(${el.style.top} - 36px)`;
  desk.appendChild(b);
}

function renderLog() {
  const log = $("log");
  log.innerHTML = "";
  if (!state.log.length) {
    log.innerHTML = `<div class="line sys"><div class="text">아이디어나 지금의 고민을 한두 문장으로 말해 보세요. 예: “Windups를 진짜 쓸모 있게 만들고 싶어.”</div></div>`;
    return;
  }
  for (const l of state.log) {
    const div = document.createElement("div");
    if (l.kind) { div.className = "line " + l.kind; div.innerHTML = `<div class="text">${esc(l.say)}</div>`; }
    else if (!l.agent) { div.className = "line you"; div.innerHTML = `<div class="text">${esc(l.say)}</div>`; }
    else {
      const a = agent(l.agent) || { name: l.agent, color: "#999" };
      div.className = "line";
      div.innerHTML = `<span class="who" style="background:${a.color}">${esc(a.name)}</span><div class="text">${esc(l.say)}</div>`;
    }
    log.appendChild(div);
  }
  log.scrollTop = log.scrollHeight;
}

function renderArtifact() {
  const box = $("artifact");
  const c = state.config.output;
  const s = state.summary;
  const summary = `<div class="sum"><div><small>아이디어</small>${esc(s.idea) || "—"}</div><div><small>가장 큰 위험</small>${esc(s.risk) || "—"}</div><div><small>다음 할 일</small>${esc(s.next) || "—"}</div></div>`;
  if (c === "summary") box.innerHTML = `<h2>결정 요약</h2>${summary}`;
  else if (c === "card") {
    if (!state.cards.length) { box.innerHTML = `<h2>아이디어 카드</h2><div class="muted">아이디어가 바뀌면 카드가 v1, v2, v3…로 쌓여요.</div>`; return; }
    const i = state.viewVersion < 0 ? state.cards.length - 1 : state.viewVersion;
    const card = state.cards[i];
    const chips = state.cards.map((_, k) => `<button class="chip${k === i ? " on" : ""}" data-v="${k}">v${k + 1}</button>`).join("");
    box.innerHTML = `<h2>아이디어 카드</h2><div class="card"><div class="ver">v${i + 1}${i < state.cards.length - 1 ? " (이전 버전)" : " (지금)"}${card.change ? " · 바뀐 점: " + esc(card.change) : ""}</div><div class="title">${esc(card.title)}</div><div>${esc(card.pitch)}</div></div><div class="versions">${chips}</div>`;
    box.querySelectorAll("[data-v]").forEach(b => b.onclick = () => { state.viewVersion = +b.dataset.v === state.cards.length - 1 ? -1 : +b.dataset.v; renderArtifact(); });
  } else if (c === "board") {
    const pile = (k, label) => `<div class="pile ${k}"><h3>${label}</h3>${state.board.filter(n => n.pile === k).map(n => `<div class="note">${esc(n.text)}</div>`).join("")}</div>`;
    box.innerHTML = `<h2>생각 정리 보드</h2><div class="board">${pile("core", "핵심")}${pile("maybe", "보류")}${pile("parked", "치움")}</div>`;
  } else box.innerHTML = `<h2>결과물 없음</h2><div class="muted">이 버전은 대화만 해요.</div>`;
}

function renderRating() {
  $("rateRows").innerHTML = "";
  for (const [key, label] of RATINGS) {
    const row = document.createElement("div");
    row.className = "row";
    row.innerHTML = `<span>${label}</span><span class="dots">${[1, 2, 3, 4, 5].map(n => `<button data-k="${key}" data-n="${n}">${n}</button>`).join("")}</span>`;
    $("rateRows").appendChild(row);
  }
  $("rateRows").querySelectorAll("button").forEach(b => b.onclick = () => {
    state.rating[b.dataset.k] = +b.dataset.n;
    $("rateRows").querySelectorAll(`[data-k="${b.dataset.k}"]`).forEach(x => x.classList.toggle("on", +x.dataset.n === +b.dataset.n));
  });
  $("rateNote").value = "";
  $("rateMsg").textContent = "";
}

const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

async function autosave() {
  if (!state.log.length) return;
  try {
    await fetch("/api/session", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...state, savedAt: new Date().toISOString() }) });
  } catch { /* the server keeps the last copy */ }
}

// ------------------------------------------------------------ settings

function openSettings() {
  $("knobs").innerHTML = "";
  for (const [key, knob] of Object.entries(KNOBS)) {
    const label = document.createElement("label");
    label.innerHTML = `<span>${knob.label}</span><select data-k="${key}">${Object.entries(knob.options).map(([v, t]) => `<option value="${v}"${state.config[key] === v ? " selected" : ""}>${t}</option>`).join("")}</select>`;
    $("knobs").appendChild(label);
  }
  $("settings").showModal();
}

function applySettings() {
  const config = { ...state.config };
  $("knobs").querySelectorAll("select").forEach(s => config[s.dataset.k] = s.value);
  $("settings").close();
  const same = PRESETS.find(p => Object.keys(KNOBS).every(k => p.config[k] === config[k]));
  newSession(config, same ? same.id : "custom");
}

// ------------------------------------------------------------ microphone (push to talk)

let recorder = null, chunks = [];
async function toggleMic() {
  if (recorder) { recorder.stop(); return; }
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    recorder = new MediaRecorder(stream);
    chunks = [];
    recorder.ondataavailable = e => chunks.push(e.data);
    recorder.onstop = async () => {
      stream.getTracks().forEach(t => t.stop());
      $("mic").classList.remove("rec");
      const blob = new Blob(chunks, { type: recorder.mimeType || "audio/webm" });
      recorder = null;
      $("mic").textContent = "…";
      try {
        const r = await fetch("/api/stt?lang=" + (state.config.language === "en" ? "en" : "ko"), { method: "POST", headers: { "Content-Type": blob.type }, body: blob });
        const data = await r.json();
        if (!r.ok) throw new Error(data.error);
        send(data.text);
      } catch (error) { addLine({ err: true, say: "음성 인식 오류: " + error.message }); }
      $("mic").textContent = "🎤";
    };
    recorder.start();
    $("mic").classList.add("rec");
  } catch (error) {
    addLine({ err: true, say: "마이크를 쓸 수 없어요: " + error.message });
  }
}

// ------------------------------------------------------------ wiring

$("send").onclick = () => send($("input").value);
$("input").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); send($("input").value); } });
$("input").addEventListener("input", () => { lastActivity = Date.now(); });
$("continueBtn").onclick = () => { if (!busy && state.log.length) { addLine({ sys: true, say: "(계속 토론)" }); teamTurn({ type: "continue" }); } };
$("mic").onclick = toggleMic;
$("newBtn").onclick = () => newSession(state.config, presetId);
$("settingsBtn").onclick = openSettings;
$("settingsClose").onclick = () => $("settings").close();
$("settingsApply").onclick = applySettings;
$("rateSave").onclick = async () => {
  state.note = $("rateNote").value;
  await autosave();
  $("rateMsg").textContent = "저장했어요 (" + state.id + ")";
};
setInterval(() => {
  if (state.config.nudge !== "on" || busy || speaking || nudged || !state.log.length) return;
  if (Date.now() - lastActivity > 25000) { nudged = true; teamTurn({ type: "quiet" }); }
}, 1000);

window.windupsLab = { get state() { return state; }, send, newSession, PRESETS };
newSession(PRESETS[0].config, "A");
