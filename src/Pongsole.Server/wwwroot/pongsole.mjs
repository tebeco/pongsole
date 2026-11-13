import { drawArena } from "./arena.mjs";
import { bindControls } from "./controls.mjs";
import { initialInviteCode, inviteAccess } from "./invite.mjs";
import { pickNickname } from "./nicknames.mjs";
import { Movement } from "./movement.mjs";
import { renderRosters, RosterActivity } from "./roster.mjs";
import { clock, GameSession } from "./session.mjs";
import { browserSession, gameSocket } from "./transport.mjs";

const byId = id => document.getElementById(id);
const arena = byId("arena");
const connection = byId("connection");
const playAgain = byId("play-again");
const retry = byId("retry");
const leave = byId("leave");
const message = byId("message");
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
const labels = {
  checking: "Checking access", ready: "Ready", access: "Access required", unavailable: "Access unavailable",
  connecting: "Connecting", reconnecting: "Reconnecting", playing: "Connected", terminal: "Connection ended",
};
const modeButtons = [...document.querySelectorAll("[data-mode]")];
const modeLabels = { allTogether: "All together", individualPaddles: "Individual paddles" };
const controls = [...document.querySelectorAll("[data-direction]")];
let lastAnnouncement = "";
let previousState;
let frameId;
let rosterTimer;
let rosterPlayers = [];
const rosterActivity = new RosterActivity();
const rosterLists = { left: byId("roster-left"), right: byId("roster-right") };

function showRosters() {
  clearTimeout(rosterTimer);
  const now = performance.now();
  renderRosters(rosterLists, rosterPlayers, rosterActivity, now);
  const deadline = rosterActivity.nextDeadline(now);
  if (Number.isFinite(deadline)) rosterTimer = setTimeout(showRosters, deadline - now);
}

const session = new GameSession({
  access: inviteAccess(browserSession, location, window.history),
  socket: gameSocket,
  changed: show,
  clearMovement: disconnected => movement.clear(disconnected),
});

const movement = new Movement({
  time: clock,
  enabled: () => session.canMove && !document.hidden,
  send: direction => session.send(direction),
  changed: () => {
    for (const button of controls) {
      button.dataset.held = movement.isHeld(button.dataset.direction);
    }
  },
});
bindControls(movement, controls, window, document);

function show() {
  const { state, detail, history, player } = session;
  movement.setConnected(state === "playing");
  connection.textContent = labels[state];
  connection.dataset.state = state;
  message.textContent = detail;
  playAgain.hidden = state !== "ready";
  retry.hidden = state !== "unavailable";
  leave.hidden = !["connecting", "reconnecting", "playing", "terminal"].includes(state);
  byId("play-controls").hidden = !player;
  controls.forEach(button => { button.disabled = !session.canMove; });
  const snapshot = history.newest;
  const mode = snapshot?.arena.mode;
  const selecting = mode === "awaitingSelection";
  const selector = snapshot?.players.find(candidate => candidate.playerId === snapshot.modeSelectorPlayerId);
  byId("game-mode").hidden = !mode || state !== "playing";
  byId("mode-status").textContent = selecting ? session.canSelectMode ? "Choose how everyone plays" :
    selector ? `Waiting for ${selector.displayName} to choose` : "Waiting for a player to choose" : modeLabels[mode] ?? "";
  const choices = byId("mode-choices");
  const gainedChoice = choices.hidden && session.canSelectMode;
  choices.hidden = !session.canSelectMode;
  for (const button of modeButtons) button.disabled = session.choosingMode;
  const modeError = byId("mode-error");
  modeError.textContent = session.modeError;
  modeError.hidden = !session.modeError;
  rosterPlayers = rosterActivity.update(snapshot, state === "playing", performance.now());
  showRosters();
  const left = snapshot?.arena.leftScore ?? 0;
  const right = snapshot?.arena.rightScore ?? 0;
  const phase = snapshot?.arena.phase;
  const players = snapshot?.players.length ?? 0;
  byId("left-score").textContent = left;
  byId("right-score").textContent = right;
  byId("players").textContent = `${players} ${players === 1 ? "player" : "players"}`;
  byId("phase").textContent = selecting ? "Choosing mode" : phase === "waiting" ? "Waiting for teams" :
    phase === "serving" ? `Serve in ${Math.max(0, snapshot.arena.serveTicksRemaining / 60).toFixed(1)}s` :
    phase === "playing" ? "Playing" : "Ready";
  byId("team-label").hidden = !player;
  byId("team-label").dataset.team = player?.team ?? "";
  byId("team-label").textContent = player ? `${session.name} · Team ${player.team === "left" ? "Left" : "Right"}` : "";
  const announcement = [labels[state], phase === "waiting" ? "Waiting for both teams" :
    phase === "serving" ? "Serving" : phase === "playing" ? "Playing" : "",
    snapshot ? `Left ${left}, Right ${right}. ${players} ${players === 1 ? "player" : "players"}.` : "",
    player ? `Team ${player.team}.` : ""].filter(Boolean).join(". ");
  if (lastAnnouncement !== announcement) {
    byId("announcer").textContent = announcement;
    lastAnnouncement = announcement;
  }

  if (previousState !== state) {
    if (state === "ready") playAgain.focus();
    if (state === "playing") leave.focus();
    if (state === "terminal") leave.focus();
    previousState = state;
  }

  if (gainedChoice) modeButtons[0].focus();
  queueFrame();
}

function queueFrame() {
  if (frameId !== undefined) return;
  frameId = requestAnimationFrame(now => {
    frameId = undefined;
    const interpolated = session.state === "playing" && !reduceMotion.matches;
    drawArena(arena, session.history.frame(now, !interpolated), session.player);
    if (interpolated) {
      const playing = session.history.newest?.arena.phase === "playing";
      if (now - session.history.receivedAt < (playing ? 250 : 50)) queueFrame();
    }
  });
}

for (const button of modeButtons) {
  button.addEventListener("click", () => { void session.selectMode(button.dataset.mode); });
}

playAgain.addEventListener("click", () => { void session.join(pickNickname()); });
retry.addEventListener("click", () => { void session.checkAccess(); });
leave.addEventListener("click", () => { void session.leave(); });

const resize = new ResizeObserver(queueFrame);
resize.observe(arena);
reduceMotion.addEventListener("change", queueFrame);
void session.join(pickNickname(), initialInviteCode(location));
