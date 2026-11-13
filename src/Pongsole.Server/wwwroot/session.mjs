import { nameRule, validName } from "./roster.mjs";
import { ArenaSnapshots } from "./arena.mjs";
import { AccessError } from "./transport.mjs";

const retryDelays = [0, 2_000, 5_000, 10_000];
const marker = error => String(error?.message ?? "").match(/\bPONGSOLE_[A-Z_]+\b/)?.[0];
const resumeRejected = error => ["PONGSOLE_INVALID_RESUME_TOKEN", "PONGSOLE_EXPIRED_RESUME_TOKEN"].includes(marker(error));

export const clock = {
  now: () => performance.now(),
  set: (callback, delay) => setTimeout(callback, delay),
  clear: timer => clearTimeout(timer),
};

export class GameSession {
  state = "checking";
  detail = "";
  history = new ArenaSnapshots();
  name = "";
  player = null;
  lastSequence = 0;
  #attempt;
  #generation = 0;
  #retry;
  #retryIndex = 0;
  #stopping = Promise.resolve();

  constructor({ access, socket, changed, clearMovement, time = clock }) {
    this.access = access;
    this.socket = socket;
    this.changed = changed;
    this.clearMovement = clearMovement;
    this.time = time;
  }

  get canMove() {
    return this.state === "playing" && ["serving", "playing"].includes(this.history.newest?.arena.phase);
  }

  #show(state, detail = "") {
    this.state = state;
    this.detail = detail;
    if (!this.canMove) this.clearMovement(this.state !== "playing");
    this.changed();
  }

  #replaceAttempt() {
    const previous = this.#attempt;
    const next = { generation: ++this.#generation, abort: new AbortController() };
    this.#attempt = next;
    previous?.abort.abort();
    this.time.clear(this.#retry);
    this.#retry = undefined;
    return { next, previous };
  }

  #isCurrent(attempt) {
    return this.#generation === attempt.generation;
  }

  #stop(attempt) {
    if (attempt?.connection) {
      this.#stopping = this.#stopping.then(() => attempt.connection.stop()).catch(() => {});
    }
    return this.#stopping;
  }

  async checkAccess(code) {
    const { next, previous } = this.#replaceAttempt();
    this.#show("checking", code === undefined ? "" : "Checking shared code…");
    try {
      await this.#stop(previous);
      if (!this.#isCurrent(next)) return;
      if (code !== undefined) {
        await this.access(next.abort.signal, code);
        code = undefined;
      }
      const grant = await this.access(next.abort.signal);
      if (!this.#isCurrent(next)) return;
      if (!grant.authorized) {
        this.#show("access");
      } else if (this.name) {
        await this.#connect(next);
      } else {
        this.#show("ready");
      }
    } catch (error) {
      if (!this.#isCurrent(next)) return;
      if (error instanceof AccessError && [401, 403].includes(error.status) && code !== undefined) {
        this.#show("access", "Check the shared code and try again.");
      } else if (error instanceof AccessError && error.status === 403) {
        this.#show("unavailable", "Access denied. Private games require HTTPS.");
      } else if (this.name) {
        this.#schedule(next, "Connection unavailable. Reconnecting…");
      } else {
        this.#show("unavailable", "Could not check access. Try again.");
      }
    }
  }

  join(name) {
    if (!validName(name)) {
      this.#show("ready", nameRule);
      return;
    }
    this.name = name;
    this.#retryIndex = 0;
    return this.checkAccess();
  }

  async leave() {
    if (this.clearMovement(true)) void this.send(null);
    const { next, previous } = this.#replaceAttempt();
    this.name = "";
    this.player = null;
    this.lastSequence = 0;
    this.#retryIndex = 0;
    this.history = new ArenaSnapshots();
    this.#show("checking");
    await this.#stop(previous);
    if (this.#isCurrent(next)) await this.checkAccess();
  }

  #accept(snapshot) {
    if (!this.history.accept(snapshot, this.time.now())) return;
    const player = snapshot.players.find(candidate => candidate.playerId === this.player?.playerId);
    if (player) this.lastSequence = Math.max(this.lastSequence, player.lastProcessedSequence);
    if (!this.canMove) this.clearMovement(this.state !== "playing");
    this.changed();
  }

  async #connect(attempt) {
    this.#show(this.player ? "reconnecting" : "connecting");
    let early;
    let joined = false;
    try {
      const connection = this.socket();
      attempt.connection = connection;
      connection.on("LatencyProbe", nonce => nonce);
      connection.on("Snapshot", snapshot => {
        if (!this.#isCurrent(attempt)) return;
        if (joined) this.#accept(snapshot);
        else if (!early || snapshot.arena.tick >= early.arena.tick) early = snapshot;
      });
      connection.onclose(error => {
        if (!this.#isCurrent(attempt)) return;
        this.#handleError(attempt, error, true);
      });

      await connection.start();
      if (!this.#isCurrent(attempt)) return;
      let join;
      try {
        join = await connection.invoke("Join", this.name, this.player?.resumeToken ?? null);
      } catch (error) {
        if (!this.#isCurrent(attempt)) return;
        if (!this.player || !resumeRejected(error)) throw error;
        this.player = null;
        this.lastSequence = 0;
        join = await connection.invoke("Join", this.name, null);
      }

      if (!this.#isCurrent(attempt)) return;
      if (join.playerId !== this.player?.playerId) this.lastSequence = 0;
      this.lastSequence = Math.max(this.lastSequence, join.lastProcessedSequence);
      this.player = { playerId: join.playerId, team: join.team, resumeToken: join.resumeToken };
      this.history = new ArenaSnapshots();
      joined = true;
      this.#accept(join.snapshot);
      if (early) this.#accept(early);
      this.#retryIndex = 0;
      this.#show("playing");
    } catch (error) {
      if (this.#isCurrent(attempt)) this.#handleError(attempt, error, !attempt.connection || attempt.connection.state !== "Connected");
    }
  }

  #schedule(attempt, detail, capacity = false) {
    if (!this.#isCurrent(attempt)) return;
    const { next, previous } = this.#replaceAttempt();
    const delay = capacity ? 10_000 : retryDelays[Math.min(this.#retryIndex++, retryDelays.length - 1)];
    this.#show("reconnecting", detail);
    const stopped = this.#stop(previous);
    this.#retry = this.time.set(async () => {
      await stopped;
      if (this.#isCurrent(next)) await this.checkAccess();
    }, delay);
  }

  #handleError(attempt, error, disconnected = false) {
    const reason = marker(error);
    if (reason === "PONGSOLE_CAPACITY_REACHED") {
      this.#schedule(attempt, "Server full. Retrying in 10 seconds.", true);
      return;
    }
    if (reason === "PONGSOLE_SERVER_STOPPING" || (disconnected && !reason)) {
      this.#schedule(attempt, "Connection lost. Reconnecting…");
      return;
    }

    const { previous } = this.#replaceAttempt();
    void this.#stop(previous);
    if (reason === "PONGSOLE_INVALID_NAME") {
      this.name = "";
      this.#show("ready", nameRule);
      return;
    }
    const messages = {
      PONGSOLE_SESSION_ALREADY_CONNECTED: "This player session is still connected. Close the other connection before joining again.",
      PONGSOLE_ALREADY_JOINED: "The server says this connection already joined.",
      PONGSOLE_INVALID_DIRECTION: "The server rejected the movement direction.",
      PONGSOLE_INVALID_SEQUENCE: "The server rejected the movement sequence.",
      PONGSOLE_NOT_JOINED: "The server no longer recognizes this player.",
    };
    this.#show("terminal", messages[reason] ?? "The server rejected the game request.");
  }

  async send(direction) {
    const attempt = this.#attempt;
    if ((direction !== null && !this.canMove) || attempt?.connection?.state !== "Connected") return;
    if (this.lastSequence >= Number.MAX_SAFE_INTEGER) {
      this.#handleError(attempt, new Error("PONGSOLE_INVALID_SEQUENCE"));
      return;
    }
    const sequence = ++this.lastSequence;
    try {
      await attempt.connection.send("SetHeld", direction, sequence);
    } catch (error) {
      if (!this.#isCurrent(attempt)) return;
      this.#handleError(attempt, error, !marker(error) || attempt.connection.state !== "Connected");
    }
  }
}
