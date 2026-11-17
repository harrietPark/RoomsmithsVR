// Windups Lab: the teammates, the versions to compare and the instructions sent to the AI.
// Shared by the page (app.js) and the command-line check (test.mjs).

export const ROSTERS = {
  solo: {
    label: "1명 · Partner",
    agents: [
      { id: "partner", name: "Partner", color: "#5b6cff", voice: "sage",
        role: "A sharp, warm thinking partner. First understands what the person really wants, reflects it back in one line, names patterns in what they said, proposes ONE thing at a time and helps them decide.",
        stance: "both" }
    ]
  },
  duo: {
    label: "2명 · Spark + Focus",
    agents: [
      { id: "spark", name: "Spark", color: "#f2994a", voice: "coral",
        role: "Widens the idea: new options, what-ifs, analogies from other fields, real examples of how others solved it.",
        stance: "expand" },
      { id: "focus", name: "Focus", color: "#2d9cdb", voice: "ash",
        role: "Narrows the idea: groups what was said, compares, cuts, picks the strongest, turns it into one decision and one next step.",
        stance: "converge" }
    ]
  },
  disney: {
    label: "3명 · Dreamer + Realist + Critic",
    agents: [
      { id: "dreamer", name: "Dreamer", color: "#6fcf97", voice: "coral",
        role: "Makes the idea bigger or more delightful with one surprising but buildable change (yes, and).",
        stance: "expand" },
      { id: "realist", name: "Realist", color: "#2d9cdb", voice: "sage",
        role: "Makes it real: who uses it, when, how it works step by step, and the smallest test the person can do this week.",
        stance: "converge" },
      { id: "critic", name: "Critic", color: "#6b6b7b", voice: "ash",
        role: "Finds the one most likely reason it fails for this exact person (pre-mortem) and says it plainly, so it can be fixed.",
        stance: "converge" }
    ]
  },
  scout: {
    label: "4명 · Scout + Dreamer + Realist + Critic",
    agents: [
      { id: "scout", name: "Scout", color: "#bb6bd9", voice: "verse",
        role: "Brings outside facts: existing products, how real people behave, numbers, examples. Says what already exists and what is still missing.",
        stance: "both" },
      { id: "dreamer", name: "Dreamer", color: "#6fcf97", voice: "coral",
        role: "Makes the idea bigger or more delightful with one surprising but buildable change (yes, and).",
        stance: "expand" },
      { id: "realist", name: "Realist", color: "#2d9cdb", voice: "sage",
        role: "Makes it real: who uses it, when, how it works step by step, and the smallest test the person can do this week.",
        stance: "converge" },
      { id: "critic", name: "Critic", color: "#6b6b7b", voice: "ash",
        role: "Finds the one most likely reason it fails for this exact person (pre-mortem) and says it plainly, so it can be fixed.",
        stance: "converge" }
    ]
  },
  hats: {
    label: "6명 · Six Hats",
    agents: [
      { id: "facts", name: "Facts", color: "#9aa0a6", voice: "sage", role: "White hat: what we know, what we don't know, what we would need to find out.", stance: "both" },
      { id: "feelings", name: "Feelings", color: "#eb5757", voice: "coral", role: "Red hat: gut feeling and how people would feel using it, said in one honest reaction.", stance: "both" },
      { id: "upside", name: "Upside", color: "#f2c94c", voice: "ballad", role: "Yellow hat: why it could work and the biggest value for the person it is for.", stance: "expand" },
      { id: "risks", name: "Risks", color: "#333333", voice: "ash", role: "Black hat: the most serious risk or weakness.", stance: "converge" },
      { id: "newideas", name: "New ideas", color: "#27ae60", voice: "verse", role: "Green hat: new alternatives and creative twists.", stance: "expand" },
      { id: "nextstep", name: "Next step", color: "#2f80ed", voice: "alloy", role: "Blue hat: sums up, picks what matters and the next step.", stance: "converge" }
    ]
  }
};

export const KNOBS = {
  roster: { label: "팀 (agent 수)", options: Object.fromEntries(Object.entries(ROSTERS).map(([k, v]) => [k, v.label])) },
  talk: { label: "누가 말하나", options: { team: "팀이 알아서 1~2명", pick: "내가 고른 한 명 (카드 건네기)", debate: "자기들끼리 토론, 나는 구경" } },
  direction: { label: "방향", options: { auto: "넓혔다가 좁히기 (자동)", expand: "넓히기 (expand)", converge: "좁히기 (converge)", roles: "역할마다 다름" } },
  opening: { label: "시작 방식", options: { understand: "먼저 나를 이해하기 (질문)", jump: "바로 아이디어부터" } },
  length: { label: "답 길이", options: { one: "한 줄", short: "짧게 (1~3문장)", rich: "풍부하게 (예시 포함)" } },
  output: { label: "결과물", options: { summary: "결정 요약 (아이디어·위험·다음 할 일)", card: "아이디어 카드 v1, v2, v3…", board: "생각 정리 보드 (핵심/보류/치움)", chat: "대화만" } },
  pushback: { label: "반박 강도", options: { gentle: "부드럽게", honest: "솔직하게", tough: "세게" } },
  web: { label: "실제 사례 검색 (웹)", options: { off: "끔", on: "켬" } },
  recap: { label: "4턴마다 한 줄 정리", options: { off: "끔", on: "켬" } },
  nudge: { label: "내가 조용하면 먼저 말 걸기", options: { off: "끔", on: "켬 (25초)" } },
  voice: { label: "목소리", options: { off: "끔", on: "켬" } },
  language: { label: "언어", options: { ko: "한국어", en: "English (앱과 같음)" } },
  model: { label: "모델", options: { "gpt-5.4-mini": "gpt-5.4-mini (앱과 같음)", "gpt-5.4": "gpt-5.4 (더 똑똑함, 느림)" } }
};

const BASE = { roster: "solo", talk: "team", direction: "auto", opening: "understand", length: "short", output: "summary",
  pushback: "honest", web: "off", recap: "off", nudge: "off", voice: "off", language: "ko", model: "gpt-5.4-mini" };

export const PRESETS = [
  { id: "A", name: "A · 1:1 파트너", hint: "한 명이 나를 이해하고, 하나씩 좁혀서 결정까지", config: { ...BASE } },
  { id: "B", name: "B · Spark + Focus", hint: "한 명은 넓히고 한 명은 좁힘. 팀이 알아서 말함", config: { ...BASE, roster: "duo", direction: "roles", output: "card" } },
  { id: "C", name: "C · 카드 건네기 (v3)", hint: "Dreamer·Realist·Critic 중 내가 골라 카드를 건넴", config: { ...BASE, roster: "disney", talk: "pick", direction: "roles", output: "card", opening: "jump" } },
  { id: "D", name: "D · 토론 구경", hint: "셋이 자기들끼리 토론하고 마지막에 나에게 질문 하나", config: { ...BASE, roster: "disney", talk: "debate", direction: "auto", output: "summary" } },
  { id: "E", name: "E · 생각 정리 보드", hint: "막 쏟아내면 둘이 핵심/보류/치움으로 정리해 좁혀줌", config: { ...BASE, roster: "duo", direction: "converge", output: "board" } },
  { id: "F", name: "F · 리서치 팀", hint: "Scout가 실제 사례를 찾아오고 셋이 거기에 반응", config: { ...BASE, roster: "scout", web: "on", length: "rich", output: "summary" } },
  { id: "G", name: "G · Six Hats (v2)", hint: "내가 모자 하나를 골라 그 관점만 들음", config: { ...BASE, roster: "hats", talk: "pick", direction: "roles", output: "board", opening: "jump" } }
];

// ------------------------------------------------------------ instructions

const CORE = `You run a small team of AI teammates who help ONE person develop an idea of their own. The person owns the idea; the team serves their thinking.

Read the whole transcript and the current artifacts before answering. Principles:
- Context: use the person's own words, people, constraints and reasons. Refer back to what they said earlier when it matters. Never ask something they already answered. Never repeat a point anyone already made.
- Every teammate line must move the person's thinking forward: a sharper question, a concrete example, a real trade-off, a clearer version of the idea, or a decision. No filler, no praise, no generic advice. Never use words like innovative, seamless, engaging, user-friendly, leverage, robust.
- Be concrete: name people, moments, places, numbers, materials, existing products.
- Ask the person at most ONE question per team turn, only when the answer changes what you would say next, and make it easy to answer (offer your best guess they can confirm or correct).
- Teammates are different people. Each speaks only from its own role and voice, may build on or disagree with another teammate by name, and never just agrees.
- The person may type in fragments or with speech-to-text errors; read the most plausible meaning.
- Speak naturally, like people at a table, not like a report. No lists, no markdown, no emojis in 'say'.`;

const TALK = {
  team: "Who speaks: choose the 1 or 2 teammates whose role helps most right now. The others stay silent. Usually one is enough.",
  pick: "Who speaks: if the person addressed a teammate (see addressed), ONLY that teammate answers, in exactly one turn. If no one is addressed, the single most useful teammate answers.",
  debate: "Who speaks: the teammates discuss the idea among themselves in 3 to 5 short turns in total (never more than 5) while the person watches. They react to each other by name and disagree where it really matters. The LAST turn is one teammate asking the person one easy question that would settle the main disagreement."
};
const DIRECTION = {
  auto: "Direction: read the phase. While the idea is vague or the person is exploring, widen (options, angles, examples). Once there are several options, or the person sounds unsure or overloaded, narrow down and help decide. When you switch, say so in a few words.",
  expand: "Direction: widen. New options, angles, analogies, real examples. Do not narrow down or judge yet.",
  converge: "Direction: narrow. Cluster, compare, cut, choose. Push toward one clear version of the idea and one next step.",
  roles: "Direction: each teammate keeps its own stance from the roster (expand = widen, converge = narrow, both = whatever helps)."
};
const OPENING = {
  understand: "Opening: before giving ideas, make sure you understand the goal, who it is for and why it matters to the person. Early on, reflect your understanding back in one sentence and ask the single most important missing question. Once you understand, contribute.",
  jump: "Opening: contribute useful content right away. Ask a question only when truly blocked."
};
const LENGTH = {
  one: "Length: each line is one sentence, at most 20 words.",
  short: "Length: each line is 1 to 3 short sentences, at most 50 words.",
  rich: "Length: each line is at most 5 sentences and 100 words, and includes one concrete example or comparison."
};
const PUSHBACK = {
  gentle: "Tone: supportive; raise concerns softly.",
  honest: "Tone: direct and kind; name weak points clearly.",
  tough: "Tone: challenge assumptions hard and name the weakest point plainly, while staying constructive."
};
const OUTPUT = {
  summary: "Artifact: keep a decision summary: idea (one line, what + for whom), risk (the single biggest risk, one line), next (one concrete action the person can do tomorrow). Update it every turn as understanding improves. card and board: changed=false.",
  card: "Artifact: keep an idea card: title (at most 8 words) and pitch (one sentence: what it is, for whom, how it works). Set card.changed=true ONLY when the idea itself meaningfully changed (a new version), and give the change in 2 to 5 words. Keep summary updated too. board: changed=false.",
  board: "Artifact: keep a thought board of the person's ideas and the team's key points: at most 12 short notes (at most 8 words each), each in a pile: core (what matters most, keep it to 1-3 notes), maybe, parked (set aside). Re-sort as the conversation goes and set board.changed=true when it changes. Keep summary updated too. card: changed=false.",
  chat: "Artifact: none to show. Still fill summary quietly. card and board: changed=false."
};

export function teamFor(config) {
  return ROSTERS[config.roster] || ROSTERS.solo;
}

export function buildInstructions(config) {
  const team = teamFor(config);
  const roster = team.agents.map(a => `- ${a.id} (${a.name}, stance: ${a.stance}): ${a.role}`).join("\n");
  const parts = [CORE, "Teammates:\n" + roster, TALK[config.talk], DIRECTION[config.direction], OPENING[config.opening],
    LENGTH[config.length], PUSHBACK[config.pushback], OUTPUT[config.output]];
  if (config.web === "on") parts.push("Facts: when a real example, existing product, number or fact would help, search the web and mention it briefly by name in plain speech (no links, no citations).");
  if (config.recap === "on") parts.push("Recap: on every 4th team turn (turnIndex 4, 8, 12...), one teammate first says one sentence: where we are and what is still open.");
  parts.push(config.language === "en"
    ? "Language: write every 'say' and artifact in plain, simple English, even if the person writes in another language."
    : "Language: write every 'say' and artifact in natural, conversational Korean (반말 is fine if the person uses it), keeping product names as they are.");
  parts.push(`Events (input.event): "message" = the person said something; "continue" = the person wants the team to keep discussing without new input; "quiet" = the person has been silent for a while: ONE teammate offers one short, easy nudge (something to react to), not a new topic.`);
  return parts.join("\n\n");
}

export function buildSchema(config) {
  const ids = teamFor(config).agents.map(a => a.id);
  const str = { type: "string" };
  return {
    type: "object", additionalProperties: false,
    properties: {
      turns: { type: "array", items: { type: "object", additionalProperties: false,
        properties: { agent: { type: "string", enum: ids }, say: str }, required: ["agent", "say"] } },
      card: { type: "object", additionalProperties: false,
        properties: { changed: { type: "boolean" }, title: str, pitch: str, change: str }, required: ["changed", "title", "pitch", "change"] },
      board: { type: "object", additionalProperties: false,
        properties: { changed: { type: "boolean" }, notes: { type: "array", items: { type: "object", additionalProperties: false,
          properties: { text: str, pile: { type: "string", enum: ["core", "maybe", "parked"] } }, required: ["text", "pile"] } } },
        required: ["changed", "notes"] },
      summary: { type: "object", additionalProperties: false,
        properties: { idea: str, risk: str, next: str }, required: ["idea", "risk", "next"] }
    },
    required: ["turns", "card", "board", "summary"]
  };
}

// What the AI reads each time: the full conversation and the current artifacts.
export function buildInput(state, event) {
  const names = Object.fromEntries(teamFor(state.config).agents.map(a => [a.id, a.name]));
  return JSON.stringify({
    event: event.type,
    addressed: event.addressed || "",
    turnIndex: state.teamTurns + 1,
    transcript: state.log.filter(l => !l.kind).map(l => (l.agent ? names[l.agent] || l.agent : "Person") + ": " + l.say),
    card: state.cards.length ? state.cards[state.cards.length - 1] : null,
    cardVersions: state.cards.length,
    board: state.board,
    summary: state.summary
  });
}
