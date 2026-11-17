import { CREW, crewMember, dispatcherInstructions, dispatcherSchema, workerInstructions, workerSchema, context, clean } from "./crew.js";

const DISPATCH_MODEL = "gpt-5.4-mini";   // quick: who takes which job
const WORK_MODEL = "gpt-5.4";            // the jobs themselves (Scout also searches the web)
const CHAIN = 2;                         // follow-up jobs the team may start on its own per message

const $ = id => document.getElementById(id);
let state;
let dispatching = Promise.resolve();
let jobCount = 0;

function fresh() {
  state = {
    id: "work_" + new Date().toISOString().replace(/[:.]/g, "-"),
    feed: [], cards: [], jobs: [], chainBudget: CHAIN,
    said: {}, rating: {}, note: ""
  };
  renderTeam();
  renderBoard();
  renderTalk();
  $("input").focus();
}

// ------------------------------------------------------------ the person

function say(text) {
  text = (text || "").trim();
  if (!text) return;
  $("input").value = "";
  state.feed.push({ who: "me", text });
  state.chainBudget = CHAIN;
  renderTalk();
  queueDispatch({ type: "message", text });
}

function react(cardId, index, kind) {
  const card = state.cards.find(c => c.id === cardId);
  const item = card && card.items[index];
  if (!item) return;
  item.reaction = item.reaction === kind ? "" : kind;
  renderBoard();
  if (!item.reaction) return;
  const words = { like: "좋아", drop: "버려", more: "이거 더" }[kind];
  state.feed.push({ who: "me", text: `${words}: ${item.name}` });
  renderTalk();
  // Dropping only steers later work; liking or asking for more gives the team a job.
  if (kind === "drop") { autosave(); return; }
  state.chainBudget = CHAIN;
  queueDispatch({ type: "reaction", reaction: kind, card: card.title, item: item.name, detail: item.detail, by: card.agent });
}

// ------------------------------------------------------------ dispatcher

function queueDispatch(event) {
  dispatching = dispatching.then(() => dispatch(event)).catch(() => {});
}

async function dispatch(event) {
  try {
    const out = await ai(DISPATCH_MODEL, dispatcherInstructions(), context(state, { event, chainBudget: state.chainBudget }), dispatcherSchema(), false);
    const lines = event.type === "card_landed" ? out.lines.slice(0, 1) : out.lines.slice(0, 2);
    for (const l of lines) speak(l.agent, clean(l.line));
    let jobs = out.jobs.filter(j => !duplicate(j));
    if (event.type === "card_landed") {
      // A follow-up goes to someone else than the one who just delivered.
      jobs = state.chainBudget > 0 ? jobs.filter(j => j.agent !== event.agent).slice(0, 1) : [];
      state.chainBudget -= jobs.length;
    }
    for (const j of jobs.slice(0, 3)) if (!duplicate(j)) startJob(j);
  } catch (error) {
    note("팀 배정 오류: " + error.message);
  }
}

// ------------------------------------------------------------ jobs

// No double work: one waiting job per teammate, and never a job like one already given to them.
function duplicate(j) {
  const mine = state.jobs.filter(o => o.agent === j.agent && o.status !== "failed");
  if (mine.filter(o => o.status === "queued").length >= 1) return true;
  const words = t => new Set(String(t).toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(w => w.length > 1));
  const a = words(j.task + " " + j.claim);
  return mine.some(o => {
    if (o.status === "done" && state.cards.some(c => c.job === o.id && c.items.some(i => i.reaction))) return false; // reacted-to work may be built on
    const b = words(o.task + " " + o.claim);
    let same = 0;
    for (const w of a) if (b.has(w)) same++;
    return same / Math.max(1, Math.min(a.size, b.size)) > .55;
  });
}

function startJob(j) {
  const job = { id: "j" + ++jobCount, agent: j.agent, task: j.task, claim: clean(j.claim), assumption: clean(j.assumption), status: "queued" };
  state.jobs.push(job);
  if (!state.jobs.some(o => o.agent === job.agent && o.status === "running")) run(job);
  else renderTeam();
}

async function run(job) {
  job.status = "running";
  speak(job.agent, job.claim, true);
  renderTeam();
  try {
    const r = await ai(WORK_MODEL, workerInstructions(job.agent), context(state, { job: { task: job.task, assumption: job.assumption } }), workerSchema(), job.agent === "scout");
    const card = {
      id: "c" + (state.cards.length + 1), agent: job.agent, job: job.id,
      title: clean(r.title), assumption: clean(r.assumption) || job.assumption,
      items: r.items.map(i => ({ name: clean(i.name), detail: clean(i.detail), reaction: "" })),
      takeaway: clean(r.takeaway), question: clean(r.question)
    };
    job.status = "done";
    state.cards.push(card);
    renderBoard();
    speak(job.agent, clean(r.say));
    queueDispatch({ type: "card_landed", agent: job.agent, card: card.title, items: card.items.map(i => i.name) });
  } catch (error) {
    job.status = "failed";
    note(crewMember(job.agent).name + " 작업 실패: " + error.message);
  }
  const next = state.jobs.find(o => o.agent === job.agent && o.status === "queued");
  if (next) run(next);
  renderTeam();
  autosave();
}

async function ai(model, instructions, input, schema, web) {
  const r = await fetch("/api/respond", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ model, instructions, input: JSON.stringify(input), schema, web }) });
  const data = await r.json();
  if (!r.ok) throw new Error(data.error || r.status);
  return data.result;
}

// ------------------------------------------------------------ talk

function speak(agent, text, claim = false) {
  if (!text) return;
  state.feed.push({ who: agent, text });
  state.said[agent] = { text, at: Date.now(), claim };
  renderTalk();
  renderTeam();
  const el = document.querySelector(`.member[data-id="${agent}"]`);
  if (el && !claim) { el.classList.add("talking"); setTimeout(() => el.classList.remove("talking"), 2200); }
}

function note(text) {
  state.feed.push({ who: "err", text });
  renderTalk();
}

// ------------------------------------------------------------ rendering

function renderTeam() {
  $("team").innerHTML = "";
  for (const c of CREW) {
    const running = state.jobs.find(j => j.agent === c.id && j.status === "running");
    const queued = state.jobs.filter(j => j.agent === c.id && j.status === "queued").length;
    const last = state.said[c.id];
    const div = document.createElement("div");
    div.className = "member" + (running ? " working" : "");
    div.dataset.id = c.id;
    const status = running ? `<div class="status working">${esc(running.claim || "작업 중")}</div>`
      : last ? `<div class="status said">${esc(last.text)}</div>` : `<div class="status" style="color:var(--soft)">대기 중</div>`;
    div.innerHTML = `<div class="top"><div class="blob" style="background:${c.color}"></div><div><div class="name">${c.name}</div><div class="job">${c.job}</div></div></div>${status}${queued ? `<div class="queued">다음 할 일 ${queued}개</div>` : ""}`;
    $("team").appendChild(div);
  }
}

function renderBoard() {
  const board = $("board");
  if (!state.cards.length) {
    board.innerHTML = `<div class="empty"><b>아이디어나 고민을 그냥 말해 보세요.</b>팀이 알아서 일을 나눠 맡고, 찾고 만든 걸 여기에 카드로 가져와요.<br>카드의 항목에 “좋아 / 버려 / 이거 더”로 반응하면 다음 일이 돼요.</div>`;
    return;
  }
  board.innerHTML = "";
  for (const card of [...state.cards].reverse()) {
    const c = crewMember(card.agent);
    const div = document.createElement("div");
    div.className = "card";
    div.innerHTML = `<div class="head" style="background:${c.color}"><span>${c.name} · ${c.job}</span><span>${card.id}</span></div>
      <div class="title">${esc(card.title)}</div>${card.assumption ? `<div class="assume">가정: ${esc(card.assumption)}</div>` : ""}
      ${card.items.map((it, i) => `<div class="item ${it.reaction}"><div class="n">${esc(it.name)}</div><div class="d">${esc(it.detail)}</div>
        <div class="acts"><button class="like" data-c="${card.id}" data-i="${i}" data-k="like">좋아</button><button class="drop" data-c="${card.id}" data-i="${i}" data-k="drop">버려</button><button class="more" data-c="${card.id}" data-i="${i}" data-k="more">이거 더</button></div></div>`).join("")}
      ${card.takeaway ? `<div class="take">→ ${esc(card.takeaway)}</div>` : ""}${card.question ? `<div class="ask">질문: ${esc(card.question)}</div>` : ""}`;
    board.appendChild(div);
  }
  board.querySelectorAll("[data-k]").forEach(b => b.onclick = () => react(b.dataset.c, +b.dataset.i, b.dataset.k));
}

function renderTalk() {
  const talk = $("talk");
  if (!state.feed.length) { talk.innerHTML = `<div style="color:var(--soft)">대화가 여기에 짧게 남아요.</div>`; return; }
  talk.innerHTML = state.feed.slice(-30).map(f => {
    if (f.who === "me") return `<div class="me">나: ${esc(f.text)}</div>`;
    if (f.who === "err") return `<div class="err">${esc(f.text)}</div>`;
    const c = crewMember(f.who);
    return `<div><b style="color:${c.color}">${c.name}</b> ${esc(f.text)}</div>`;
  }).join("");
  talk.scrollTop = talk.scrollHeight;
}

const esc = s => String(s ?? "").replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));

async function autosave() {
  if (!state.feed.length) return;
  try {
    await fetch("/api/session", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...state, version: "working-team", savedAt: new Date().toISOString() }) });
  } catch { /* the last saved copy stays */ }
}

// ------------------------------------------------------------ rating

function openRating() {
  for (const box of document.querySelectorAll("#rate .opts")) {
    const k = box.dataset.k;
    if (k !== "found") box.innerHTML = [1, 2, 3, 4, 5].map(n => `<button data-v="${n}">${n}</button>`).join("");
    box.querySelectorAll("button").forEach(b => {
      b.classList.toggle("on", String(state.rating[k]) === b.dataset.v);
      b.onclick = () => { state.rating[k] = b.dataset.v; box.querySelectorAll("button").forEach(x => x.classList.toggle("on", x === b)); };
    });
  }
  $("rateNote").value = state.note || "";
  $("rateMsg").textContent = "";
  $("rate").showModal();
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
        const r = await fetch("/api/stt?lang=ko", { method: "POST", headers: { "Content-Type": blob.type }, body: blob });
        const data = await r.json();
        if (!r.ok) throw new Error(data.error);
        say(data.text);
      } catch (error) { note("음성 인식 오류: " + error.message); }
      $("mic").textContent = "🎤";
    };
    recorder.start();
    $("mic").classList.add("rec");
  } catch (error) {
    note("마이크를 쓸 수 없어요 (브라우저에서 마이크를 허용해 주세요): " + error.message);
  }
}

// ------------------------------------------------------------ wiring

$("send").onclick = () => say($("input").value);
$("input").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); say($("input").value); } });
$("mic").onclick = toggleMic;
$("newBtn").onclick = () => { autosave(); fresh(); };
$("rateBtn").onclick = openRating;
$("rateClose").onclick = () => $("rate").close();
$("rateSave").onclick = async () => { state.note = $("rateNote").value; await autosave(); $("rateMsg").textContent = "저장했어요"; };

window.windupsWork = { get state() { return state; }, say, react };
fresh();
