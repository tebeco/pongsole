import { drawArena } from "./arena.mjs";
import { GameSession } from "./session.mjs";
import { browserSession, gameSocket } from "./transport.mjs";

const byId = id => document.getElementById(id);
const arena = byId("arena");
const connection = byId("connection");
const joinForm = byId("join-form");
const accessForm = byId("access-form");
const retry = byId("retry");
const leave = byId("leave");
const message = byId("message");
const code = byId("shared-code");
const name = byId("display-name");
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
const labels = {
  checking: "Checking access", ready: "Ready", access: "Private game", unavailable: "Access unavailable",
  connecting: "Connecting", reconnecting: "Reconnecting", playing: "Connected", terminal: "Connection ended",
};
const controls = [...document.querySelectorAll("[data-direction]")];
let lastAnnouncement = "";
let previousState;
let frameId;

const session = new GameSession({
  access: async (signal, secret) => {
    const result = await browserSession(signal, secret);
    if (secret !== undefined && !signal.aborted) code.value = "";
    return result;
  },
  socket: gameSocket,
  changed: show,
  clearMovement: () => {},
});

function show() {
  const { state, detail, history, player } = session;
  connection.textContent = labels[state];
  connection.dataset.state = state;
  message.textContent = detail;
  joinForm.hidden = state !== "ready";
  accessForm.hidden = state !== "access";
  retry.hidden = state !== "unavailable";
  leave.hidden = !session.name && state !== "terminal";
  byId("play-controls").hidden = !player;
  controls.forEach(button => { button.disabled = !session.canMove; });
  const snapshot = history.newest;
  const left = snapshot?.arena.leftScore ?? 0;
  const right = snapshot?.arena.rightScore ?? 0;
  const phase = snapshot?.arena.phase;
  const players = snapshot?.players.length ?? 0;
  byId("left-score").textContent = left;
  byId("right-score").textContent = right;
  byId("players").textContent = `${players} ${players === 1 ? "player" : "players"}`;
  byId("phase").textContent = phase === "waiting" ? "Waiting for teams" :
    phase === "serving" ? `Serve in ${Math.max(0, snapshot.arena.serveTicksRemaining / 60).toFixed(1)}s` :
    phase === "playing" ? "Playing" : "Ready";
  byId("team-label").dataset.team = player?.team ?? "";
  byId("team-label").textContent = player ? `${session.name} · Team ${player.team === "left" ? "Left" : "Right"}` : "Join to play";
  const announcement = [labels[state], phase === "waiting" ? "Waiting for both teams" :
    phase === "serving" ? "Serving" : phase === "playing" ? "Playing" : "",
    snapshot ? `Left ${left}, Right ${right}. ${players} ${players === 1 ? "player" : "players"}.` : "",
    player ? `Team ${player.team}.` : ""].filter(Boolean).join(". ");
  if (lastAnnouncement !== announcement) {
    byId("announcer").textContent = announcement;
    lastAnnouncement = announcement;
  }

  if (previousState !== state) {
    if (state === "access") code.focus();
    if (state === "ready" && previousState !== undefined) name.focus();
    if (state === "playing") leave.focus();
    if (state === "terminal") leave.focus();
    previousState = state;
  }

  queueFrame();
}

function queueFrame() {
  if (frameId !== undefined) return;
  frameId = requestAnimationFrame(now => {
    frameId = undefined;
    const interpolated = session.state === "playing" && !reduceMotion.matches;
    drawArena(arena, session.history.frame(now, !interpolated));
    if (interpolated && now - session.history.receivedAt < 50) queueFrame();
  });
}

joinForm.addEventListener("submit", event => {
  event.preventDefault();
  void session.join(name.value);
});
accessForm.addEventListener("submit", event => {
  event.preventDefault();
  void session.checkAccess(code.value);
});
retry.addEventListener("click", () => { void session.checkAccess(); });
leave.addEventListener("click", () => {
  code.value = "";
  void session.leave();
});

const resize = new ResizeObserver(queueFrame);
resize.observe(arena);
reduceMotion.addEventListener("change", queueFrame);
void session.checkAccess();
