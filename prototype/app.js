"use strict";

const script = {
  segments: [
    ["嗯，我想把声跃做成真正的桌面输入工具，", "不要像一个网页"],
    ["嗯，我想把声跃做成真正的桌面输入工具，不要像一个网页，", "按住右 Ctrl 的时候"],
    ["嗯，我想把声跃做成真正的桌面输入工具，不要像一个网页，按住右 Ctrl 的时候，", "从屏幕下面浮出一条声纹"],
    ["嗯，我想把声跃做成真正的桌面输入工具，不要像一个网页，按住右 Ctrl 的时候，从屏幕下面浮出一条声纹，", "松开以后先让我看一遍"],
    ["嗯，我想把声跃做成真正的桌面输入工具，不要像一个网页，按住右 Ctrl 的时候，从屏幕下面浮出一条声纹，松开以后先让我看一遍，", "再写回原来的输入框。"]
  ],
  original: "嗯，我想把声跃做成真正的桌面输入工具，不要像一个网页，按住右 Ctrl 的时候，从屏幕下面浮出一条声纹，松开以后先让我看一遍，再写回原来的输入框。",
  smooth: "我想把声跃做成真正的桌面输入工具，而不是网页。按住右 Ctrl 时，从屏幕底部浮出一条声纹；松开后先让我审阅，再写回原来的输入框。",
  outline: "声跃桌面输入形态：\n\n- 它是桌面常驻工具，而不是网页；\n- 按住右 Ctrl，从屏幕底部浮出声纹；\n- 松开后先审阅，再写回原输入框。"
};

const elements = {
  body: document.body,
  demoGuide: document.querySelector("#demoGuide"),
  mouseHold: document.querySelector("#mouseHold"),
  voiceLayer: document.querySelector("#voiceLayer"),
  cancelRecording: document.querySelector("#cancelRecording"),
  transcriptState: document.querySelector("#transcriptState"),
  liveCaption: document.querySelector("#liveCaption"),
  elapsedTime: document.querySelector("#elapsedTime"),
  releaseLabel: document.querySelector("#releaseLabel"),
  waveCanvas: document.querySelector("#waveCanvas"),
  reviewDock: document.querySelector("#reviewDock"),
  closeReview: document.querySelector("#closeReview"),
  reviewEditor: document.querySelector("#reviewEditor"),
  polishState: document.querySelector("#polishState"),
  polishLabel: document.querySelector("#polishLabel"),
  compareButton: document.querySelector("#compareButton"),
  comparison: document.querySelector("#comparison"),
  originalText: document.querySelector("#originalText"),
  currentText: document.querySelector("#currentText"),
  reviewCount: document.querySelector("#reviewCount"),
  modeNote: document.querySelector("#modeNote"),
  modeNoteText: document.querySelector("#modeNoteText"),
  rerecordButton: document.querySelector("#rerecordButton"),
  copyButton: document.querySelector("#copyButton"),
  insertButton: document.querySelector("#insertButton"),
  targetInput: document.querySelector("#targetInput"),
  clearTarget: document.querySelector("#clearTarget"),
  characterCount: document.querySelector("#characterCount"),
  motionToggle: document.querySelector("#motionToggle"),
  toast: document.querySelector("#toast"),
  toastText: document.querySelector("#toastText")
};

const state = {
  phase: "idle",
  mode: "original",
  startedAt: 0,
  segmentIndex: 0,
  segmentTimer: null,
  elapsedTimer: null,
  finishTimer: null,
  organizeTimer: null,
  toastTimer: null,
  pointerHolding: false,
  keyboardHolding: false,
  organizeRequest: 0,
  drafts: Object.create(null),
  reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches,
  waveEnergy: 0,
  waveTarget: 0,
  waveTime: 0,
  animationFrame: 0
};

const waveContext = elements.waveCanvas.getContext("2d");

function countCharacters(text) {
  return Array.from(text.trim()).length;
}

function formatElapsed(milliseconds) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  return `00:${String(seconds % 60).padStart(2, "0")}`;
}

function resizeCanvas() {
  const bounds = elements.waveCanvas.getBoundingClientRect();
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  elements.waveCanvas.width = Math.max(1, Math.round(bounds.width * ratio));
  elements.waveCanvas.height = Math.max(1, Math.round(bounds.height * ratio));
  waveContext.setTransform(ratio, 0, 0, ratio, 0, 0);
}

function smoothPath(points, shouldMove = true) {
  if (points.length < 2) return;
  if (shouldMove) waveContext.moveTo(points[0][0], points[0][1]);
  else waveContext.lineTo(points[0][0], points[0][1]);
  for (let index = 1; index < points.length - 1; index += 1) {
    const current = points[index];
    const next = points[index + 1];
    const middleX = (current[0] + next[0]) / 2;
    const middleY = (current[1] + next[1]) / 2;
    waveContext.quadraticCurveTo(current[0], current[1], middleX, middleY);
  }
  const last = points[points.length - 1];
  waveContext.lineTo(last[0], last[1]);
}

function drawWave(timestamp) {
  const width = elements.waveCanvas.clientWidth;
  const height = elements.waveCanvas.clientHeight;
  const centre = height / 2;

  state.waveTarget = state.phase === "listening" ? 1 : state.phase === "finalizing" ? 0.14 : 0;
  state.waveEnergy += (state.waveTarget - state.waveEnergy) * (state.reducedMotion ? 1 : 0.075);
  state.waveTime = timestamp * 0.001;

  waveContext.clearRect(0, 0, width, height);

  const baseline = waveContext.createLinearGradient(0, 0, width, 0);
  baseline.addColorStop(0, "rgba(32,119,133,0)");
  baseline.addColorStop(0.18, "rgba(32,119,133,.14)");
  baseline.addColorStop(0.82, "rgba(32,119,133,.14)");
  baseline.addColorStop(1, "rgba(32,119,133,0)");
  waveContext.beginPath();
  waveContext.moveTo(0, centre);
  waveContext.lineTo(width, centre);
  waveContext.strokeStyle = baseline;
  waveContext.lineWidth = 1;
  waveContext.stroke();

  const pointCount = 54;
  const upper = [];
  const lower = [];
  for (let index = 0; index < pointCount; index += 1) {
    const progress = index / (pointCount - 1);
    const envelope = Math.pow(Math.sin(progress * Math.PI), 1.55);
    const living = state.reducedMotion ? 0.42 :
      Math.sin(progress * 30 + state.waveTime * 7.8) * 0.48 +
      Math.sin(progress * 13 - state.waveTime * 4.1) * 0.32 +
      Math.sin(progress * 53 + state.waveTime * 2.4) * 0.2;
    const amplitude = (2 + envelope * 15 * state.waveEnergy) * living;
    const x = progress * width;
    upper.push([x, centre - amplitude]);
    lower.push([x, centre + amplitude * 0.72]);
  }

  const fill = waveContext.createLinearGradient(0, 0, width, 0);
  fill.addColorStop(0, "rgba(54,152,162,0)");
  fill.addColorStop(0.18, "rgba(67,167,176,.18)");
  fill.addColorStop(0.52, "rgba(27,112,126,.28)");
  fill.addColorStop(0.76, "rgba(226,120,105,.16)");
  fill.addColorStop(1, "rgba(226,120,105,0)");
  waveContext.beginPath();
  smoothPath(upper);
  const reversedLower = [...lower].reverse();
  smoothPath(reversedLower, false);
  waveContext.closePath();
  waveContext.fillStyle = fill;
  waveContext.fill();

  const stroke = waveContext.createLinearGradient(0, 0, width, 0);
  stroke.addColorStop(0, "rgba(24,104,118,0)");
  stroke.addColorStop(0.18, "rgba(24,104,118,.5)");
  stroke.addColorStop(0.54, "rgba(21,85,99,.86)");
  stroke.addColorStop(0.8, "rgba(224,116,100,.52)");
  stroke.addColorStop(1, "rgba(224,116,100,0)");
  waveContext.beginPath();
  smoothPath(upper);
  waveContext.strokeStyle = stroke;
  waveContext.lineWidth = 1.35;
  waveContext.lineCap = "round";
  waveContext.stroke();

  state.animationFrame = window.requestAnimationFrame(drawWave);
}

function setCaption(confirmed, partial = "", uncertain = false) {
  elements.liveCaption.replaceChildren();
  const confirmedNode = document.createElement("span");
  confirmedNode.className = "caption-confirmed";
  confirmedNode.textContent = confirmed;
  elements.liveCaption.append(confirmedNode);

  if (partial) {
    const partialNode = document.createElement("span");
    partialNode.className = uncertain ? "caption-partial caption-uncertain" : "caption-partial";
    partialNode.textContent = partial;
    elements.liveCaption.append(partialNode);
  }
}

function showNextSegment() {
  const index = Math.min(state.segmentIndex, script.segments.length - 1);
  const [confirmed, partial] = script.segments[index];
  setCaption(confirmed, partial, /Ctrl/.test(partial));
  state.segmentIndex = (state.segmentIndex + 1) % script.segments.length;
}

function clearListeningTimers() {
  window.clearInterval(state.segmentTimer);
  window.clearInterval(state.elapsedTimer);
  state.segmentTimer = null;
  state.elapsedTimer = null;
}

function startListening() {
  if (state.phase === "listening" || state.phase === "finalizing") return;

  closeReview(false);
  window.clearTimeout(state.finishTimer);
  window.clearTimeout(state.organizeTimer);
  state.organizeRequest += 1;
  state.phase = "listening";
  state.mode = "original";
  state.drafts = Object.create(null);
  state.segmentIndex = 0;
  state.startedAt = performance.now();
  state.waveTarget = 1;

  elements.body.classList.add("is-listening");
  elements.voiceLayer.dataset.state = "listening";
  elements.voiceLayer.classList.add("visible");
  elements.voiceLayer.setAttribute("aria-hidden", "false");
  elements.voiceLayer.inert = false;
  elements.demoGuide.classList.add("quiet");
  elements.mouseHold.classList.add("holding");
  elements.transcriptState.textContent = "正在听 · 模拟声纹";
  elements.releaseLabel.textContent = "松开完成";
  elements.elapsedTime.textContent = "00:00";
  showNextSegment();

  state.segmentTimer = window.setInterval(showNextSegment, 760);
  state.elapsedTimer = window.setInterval(() => {
    elements.elapsedTime.textContent = formatElapsed(performance.now() - state.startedAt);
  }, 200);
}

function stopListening() {
  if (state.phase !== "listening") return;

  clearListeningTimers();
  state.phase = "finalizing";
  state.waveTarget = 0.14;
  elements.voiceLayer.dataset.state = "finalizing";
  elements.transcriptState.textContent = "正在确认最后一句";
  elements.releaseLabel.textContent = "确认中";
  setCaption(script.original);

  state.finishTimer = window.setTimeout(openReview, 720);
}

function hideVoiceLayer() {
  elements.body.classList.remove("is-listening");
  elements.voiceLayer.classList.remove("visible");
  elements.voiceLayer.setAttribute("aria-hidden", "true");
  elements.voiceLayer.inert = true;
  elements.mouseHold.classList.remove("holding");
  state.pointerHolding = false;
  state.keyboardHolding = false;
}

function cancelSession(showMessage = true) {
  clearListeningTimers();
  window.clearTimeout(state.finishTimer);
  window.clearTimeout(state.organizeTimer);
  state.organizeRequest += 1;
  state.phase = "idle";
  state.waveTarget = 0;
  hideVoiceLayer();
  closeReview(false);
  elements.demoGuide.classList.remove("quiet");
  if (showMessage) showToast("已取消，本次模拟没有保存");
}

function openReview() {
  clearListeningTimers();
  hideVoiceLayer();
  state.phase = "review";
  state.mode = "original";
  state.drafts.original = script.original;

  elements.reviewEditor.value = script.original;
  elements.originalText.textContent = script.original;
  elements.currentText.textContent = script.original;
  elements.comparison.hidden = true;
  elements.compareButton.setAttribute("aria-expanded", "false");
  setActiveMode("original");
  updateModeNote("original");
  updateReviewCount();
  elements.reviewDock.classList.add("open");
  elements.reviewDock.setAttribute("aria-hidden", "false");
  elements.reviewDock.inert = false;
  elements.demoGuide.classList.add("quiet");
  window.setTimeout(() => elements.reviewEditor.focus(), 220);
}

function closeReview(resetPhase = true) {
  elements.reviewDock.classList.remove("open", "processing");
  elements.reviewDock.setAttribute("aria-hidden", "true");
  elements.reviewDock.inert = true;
  elements.comparison.hidden = true;
  elements.compareButton.setAttribute("aria-expanded", "false");
  if (resetPhase && state.phase === "review") state.phase = "idle";
}

function setActiveMode(mode) {
  document.querySelectorAll(".tone").forEach((button) => {
    const active = button.dataset.mode === mode;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", String(active));
  });
}

function updateModeNote(mode) {
  const notes = {
    original: "保持口述原样，不使用生成式文字整理",
    smooth: "只去掉口头语、顺一顺句子，不补充新信息",
    outline: "整理成简短提纲，口述原文始终保留"
  };
  elements.modeNoteText.textContent = notes[mode];
  elements.modeNote.classList.toggle("organized", mode !== "original");
}

function saveDraft() {
  if (state.phase === "review") state.drafts[state.mode] = elements.reviewEditor.value;
}

function selectMode(mode) {
  if (!script[mode] || state.phase !== "review" || mode === state.mode && !elements.reviewDock.classList.contains("processing")) {
    return;
  }

  saveDraft();
  state.mode = mode;
  setActiveMode(mode);
  updateModeNote(mode);

  if (mode === "original") {
    window.clearTimeout(state.organizeTimer);
    state.organizeRequest += 1;
    elements.reviewDock.classList.remove("processing");
    elements.reviewEditor.value = state.drafts.original ?? script.original;
    updateReviewCount();
    elements.reviewEditor.focus();
    return;
  }

  const request = ++state.organizeRequest;
  elements.polishLabel.textContent = mode === "smooth" ? "正在轻度润色…" : "正在整理提纲…";
  elements.reviewDock.classList.add("processing");
  window.clearTimeout(state.organizeTimer);
  state.organizeTimer = window.setTimeout(() => {
    if (request !== state.organizeRequest || state.mode !== mode) return;
    elements.reviewDock.classList.remove("processing");
    elements.reviewEditor.value = state.drafts[mode] ?? script[mode];
    updateReviewCount();
    elements.reviewEditor.focus();
  }, 620);
}

function updateReviewCount() {
  elements.reviewCount.textContent = `${countCharacters(elements.reviewEditor.value)} 字`;
  elements.currentText.textContent = elements.reviewEditor.value;
}

function updateTargetCount() {
  elements.characterCount.textContent = `${countCharacters(elements.targetInput.value)} 字`;
}

function toggleComparison() {
  const open = elements.comparison.hidden;
  elements.comparison.hidden = !open;
  elements.compareButton.setAttribute("aria-expanded", String(open));
  if (open) {
    elements.originalText.textContent = script.original;
    elements.currentText.textContent = elements.reviewEditor.value;
  }
}

async function copyText() {
  const text = elements.reviewEditor.value;
  if (!text.trim()) {
    showToast("当前没有可以复制的文字");
    return;
  }

  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const helper = document.createElement("textarea");
    helper.value = text;
    helper.style.position = "fixed";
    helper.style.opacity = "0";
    document.body.append(helper);
    helper.select();
    document.execCommand("copy");
    helper.remove();
  }
  showToast("已经复制");
}

function insertText() {
  const text = elements.reviewEditor.value.trim();
  if (!text) {
    showToast("当前没有可以写入的文字");
    return;
  }

  const target = elements.targetInput;
  const start = Number.isInteger(target.selectionStart) ? target.selectionStart : target.value.length;
  const end = Number.isInteger(target.selectionEnd) ? target.selectionEnd : start;
  const prefix = start > 0 && !target.value.slice(0, start).endsWith("\n") ? "\n" : "";
  target.setRangeText(`${prefix}${text}`, start, end, "end");
  updateTargetCount();
  closeReview(false);
  state.phase = "idle";
  elements.demoGuide.classList.remove("quiet");
  target.focus();
  showToast("已写入光标处，没有自动发送");
}

function showToast(message) {
  window.clearTimeout(state.toastTimer);
  elements.toastText.textContent = message;
  elements.toast.classList.add("show");
  state.toastTimer = window.setTimeout(() => elements.toast.classList.remove("show"), 2100);
}

function beginPointerHold(event) {
  if (event.button !== undefined && event.button !== 0) return;
  if (state.phase !== "idle") return;
  event.preventDefault();
  state.pointerHolding = true;
  elements.mouseHold.setPointerCapture?.(event.pointerId);
  startListening();
}

function endPointerHold(event) {
  if (!state.pointerHolding) return;
  state.pointerHolding = false;
  if (elements.mouseHold.hasPointerCapture?.(event.pointerId)) {
    elements.mouseHold.releasePointerCapture(event.pointerId);
  }
  stopListening();
}

elements.mouseHold.addEventListener("pointerdown", beginPointerHold);
elements.mouseHold.addEventListener("pointerup", endPointerHold);
elements.mouseHold.addEventListener("pointercancel", endPointerHold);
elements.mouseHold.addEventListener("contextmenu", (event) => event.preventDefault());
elements.cancelRecording.addEventListener("click", () => cancelSession(true));
elements.closeReview.addEventListener("click", () => cancelSession(true));
elements.rerecordButton.addEventListener("click", () => {
  closeReview(false);
  state.phase = "idle";
  startListening();
});
elements.copyButton.addEventListener("click", copyText);
elements.insertButton.addEventListener("click", insertText);
elements.compareButton.addEventListener("click", toggleComparison);

document.querySelectorAll(".tone").forEach((button) => {
  button.addEventListener("click", () => selectMode(button.dataset.mode));
});

elements.reviewEditor.addEventListener("input", () => {
  state.drafts[state.mode] = elements.reviewEditor.value;
  updateReviewCount();
});

elements.targetInput.addEventListener("input", updateTargetCount);
elements.clearTarget.addEventListener("click", () => {
  elements.targetInput.value = "";
  updateTargetCount();
  elements.targetInput.focus();
});

elements.motionToggle.addEventListener("click", () => {
  state.reducedMotion = !state.reducedMotion;
  elements.body.classList.toggle("reduced-motion", state.reducedMotion);
  elements.motionToggle.setAttribute("aria-pressed", String(state.reducedMotion));
  showToast(state.reducedMotion ? "已减少动态效果" : "已恢复完整动态效果");
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && state.phase !== "idle") {
    event.preventDefault();
    cancelSession(true);
    return;
  }

  if (state.phase === "review") {
    if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
      event.preventDefault();
      insertText();
    }
    return;
  }

  if (event.code === "ControlRight" && !event.repeat && state.phase === "idle") {
    event.preventDefault();
    state.keyboardHolding = true;
    startListening();
  }
});

document.addEventListener("keyup", (event) => {
  if (event.code === "ControlRight" && state.keyboardHolding) {
    event.preventDefault();
    state.keyboardHolding = false;
    stopListening();
  }
});

window.addEventListener("blur", () => {
  if (state.pointerHolding || state.keyboardHolding) stopListening();
  state.pointerHolding = false;
  state.keyboardHolding = false;
});

window.addEventListener("resize", resizeCanvas);

elements.body.classList.toggle("reduced-motion", state.reducedMotion);
elements.motionToggle.setAttribute("aria-pressed", String(state.reducedMotion));
resizeCanvas();
updateTargetCount();
state.animationFrame = window.requestAnimationFrame(drawWave);

const previewState = new URLSearchParams(window.location.search).get("preview");
if (previewState === "listening") window.setTimeout(startListening, 120);
if (previewState === "review") window.setTimeout(openReview, 120);
