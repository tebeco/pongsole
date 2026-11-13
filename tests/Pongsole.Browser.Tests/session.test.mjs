import assert from "node:assert/strict";
import test from "node:test";
import { GameSession } from "../../src/Pongsole.Server/wwwroot/session.mjs";
import { AccessError } from "../../src/Pongsole.Server/wwwroot/transport.mjs";
import { deferred, FakeClock, flush } from "./test-support.mjs";

const snapshot = (tick, sequence = 0, playerId = "one", phase = "playing") => ({
  arena: { tick, phase, leftScore: 0, rightScore: 0, leftPaddle: { y: 15 }, rightPaddle: { y: 15 }, ball: { x: 50, y: 15 } },
  players: [{ playerId, lastProcessedSequence: sequence }],
});
const player = (tick, sequence = 0, playerId = "one") => ({
  playerId, team: "left", resumeToken: "resume-" + playerId, lastProcessedSequence: sequence,
  snapshot: snapshot(tick, sequence, playerId),
});
const fault = marker => new Error("SignalR error: " + marker + ".");

class Socket {
  state = "Disconnected";
  calls = [];
  stops = 0;

  constructor(join = () => player(1), push = () => {}) {
    this.join = join;
    this.push = push;
  }

  on(method, callback) {
    if (method === "LatencyProbe") {
      this.latencyProbe = callback;
      return;
    }
    assert.equal(method, "Snapshot");
    this.snapshot = callback;
  }
  onclose(callback) { this.closed = callback; }
  async start() {
    assert.equal(typeof this.snapshot, "function");
    assert.equal(this.latencyProbe("nonce"), "nonce");
    this.state = "Connected";
  }
  async stop() { this.stops++; this.state = "Disconnected"; }
  close(error) { this.state = "Disconnected"; this.closed(error); }

  async send(method, ...args) {
    assert.equal(method, "SetHeld");
    this.calls.push([method, ...args]);
    return this.push(...args);
  }
  async invoke(method, ...args) {
    this.calls.push([method, ...args]);
    assert.equal(method, "Join");
    return this.join(...args);
  }
}

function fixture(sockets, access = async () => ({ mode: "loopback", authorized: true })) {
  const time = new FakeClock();
  const changes = [];
  const accessCalls = [];
  let cleared = 0;
  let index = 0;
  const session = new GameSession({
    access: (signal, code) => { accessCalls.push(code); return access(signal, code); },
    socket: () => { assert.ok(sockets[index], "Unexpected connection"); return sockets[index++]; },
    changed: () => changes.push(session.state),
    clearMovement: () => { cleared++; },
    time,
  });
  return { session, time, changes, accessCalls, get cleared() { return cleared; } };
}

test("reconciles early and same-tick snapshots and uses the next processed sequence", async () => {
  const socket = new Socket(() => {
    socket.snapshot(snapshot(9, 7));
    return player(8, 2);
  });
  const { session } = fixture([socket]);
  await session.join("Alice");
  await session.send("up");
  socket.snapshot(snapshot(8, 99));
  socket.snapshot(snapshot(9, 10));
  await session.send("down");
  assert.deepEqual(socket.calls, [["Join", "Alice", null], ["SetHeld", "up", 8], ["SetHeld", "down", 11]]);
  assert.equal(session.history.newest.arena.tick, 9);
});

test("reconnect checks access, preserves token and sequence, accepts lower tick and rejects stale callbacks", async () => {
  const inFlight = deferred();
  const first = new Socket(() => player(90, 4), () => inFlight.promise);
  const resumed = new Socket(() => player(2, 4));
  const run = fixture([first, resumed]);
  await run.session.join("Player");
  const pending = run.session.send("up");
  first.close();
  await run.session.send("down");
  await run.time.advance(0);
  first.snapshot(snapshot(500, 98));
  first.closed(fault("PONGSOLE_SERVER_STOPPING"));
  inFlight.reject(new Error("late failure"));
  await pending;
  assert.deepEqual(resumed.calls, [["Join", "Player", "resume-one"]]);
  assert.equal(run.session.history.newest.arena.tick, 2);
  await run.session.send("down");
  assert.deepEqual(resumed.calls.at(-1), ["SetHeld", "down", 6]);
  assert.equal(run.accessCalls.length, 2);
  assert.equal(run.session.state, "playing");
});

test("expired browser grant requests a valid link without losing the player", async () => {
  let authorized = true;
  const first = new Socket();
  const resumed = new Socket();
  const run = fixture([first, resumed], async (_, code) => {
    if (code === "valid") authorized = true;
    return { mode: "private", authorized };
  });
  await run.session.join("Player");
  authorized = false;
  first.close();
  await run.time.advance(0);
  assert.equal(run.session.state, "access");
  assert.equal(run.session.player.resumeToken, "resume-one");
  await run.session.checkAccess("valid");
  assert.deepEqual(resumed.calls, [["Join", "Player", "resume-one"]]);
  assert.deepEqual(run.accessCalls, [undefined, undefined, "valid", undefined]);
});

for (const marker of ["PONGSOLE_INVALID_RESUME_TOKEN", "PONGSOLE_EXPIRED_RESUME_TOKEN"]) {
  test(marker + " clears identity and sequence for one fresh join", async () => {
    const first = new Socket(() => player(90, 40));
    const resumed = new Socket((_, token) => {
      if (token) throw fault(marker);
      return player(1, 2, "new");
    });
    const { session, time } = fixture([first, resumed]);
    await session.join("Player");
    first.close();
    await time.advance(0);
    await session.send("up");
    assert.deepEqual(resumed.calls, [["Join", "Player", "resume-one"], ["Join", "Player", null], ["SetHeld", "up", 3]]);
  });
}

test("capacity backs off before sending held state and active conflict is terminal", async () => {
  const first = new Socket(() => { throw fault("PONGSOLE_CAPACITY_REACHED"); });
  const next = new Socket(() => player(1));
  const conflict = new Socket(() => { throw fault("PONGSOLE_SESSION_ALREADY_CONNECTED"); });
  const { session, time } = fixture([first, next, conflict]);
  await session.join("Player");
  assert.equal(session.state, "reconnecting");
  await time.advance(9_999);
  assert.equal(next.calls.length, 0);
  await time.advance(1);
  await session.send("up");
  await session.send("up");
  assert.deepEqual(next.calls.slice(1), [["SetHeld", "up", 1], ["SetHeld", "up", 2]]);
  next.close();
  await time.advance(0);
  assert.equal(session.state, "terminal");
  assert.match(session.detail, /still connected/);
  assert.equal(conflict.calls.length, 1);
});

test("waiting and leave clear movement, and stale joins cannot recreate a departed player", async () => {
  const first = new Socket();
  const pending = deferred();
  const late = new Socket(() => pending.promise);
  const run = fixture([first, late]);
  await run.session.join("Player");
  const before = run.cleared;
  first.snapshot(snapshot(2, 0, "one", "waiting"));
  assert.ok(run.cleared > before);
  await run.session.send("up");
  assert.equal(first.calls.length, 1);
  await run.session.leave();
  const starting = run.session.join("Next");
  await flush();
  await run.session.leave();
  pending.resolve(player(30));
  await starting;
  assert.equal(run.session.state, "ready");
  assert.equal(run.session.player, null);
  assert.equal(run.session.history.newest, null);
  assert.equal(late.stops, 1);
});

test("handles denied HTTP access and rejected shared code without exposing it", async () => {
  const unavailable = fixture([], async () => { throw new AccessError(403); });
  await unavailable.session.checkAccess();
  assert.equal(unavailable.session.state, "access");
  assert.equal(unavailable.session.detail, "Open a valid invite link to join this game.");
  const rejected = fixture([], async () => { throw new AccessError(403); });
  await rejected.session.checkAccess("secret");
  assert.equal(rejected.session.state, "access");
  assert.doesNotMatch(rejected.session.detail, /secret/);
});

test("reconnect follows 0, 2, 5 and repeated 10-second delays", async () => {
  const connected = new Socket();
  const unavailable = Object.assign(new Socket(), { async start() { throw new Error("socket failed"); } });
  const sockets = [connected, unavailable, unavailable, unavailable, unavailable, unavailable, new Socket()];
  const { session, time, accessCalls } = fixture(sockets);
  await session.join("Player");
  connected.close();
  for (const delay of [0, 2_000, 5_000, 10_000, 10_000, 10_000]) {
    const before = accessCalls.length;
    if (delay > 0) {
      await time.advance(delay - 1);
      assert.equal(accessCalls.length, before);
    }
    await time.advance(delay === 0 ? 0 : 1);
    assert.equal(accessCalls.length, before + 1);
  }
  assert.equal(session.state, "playing");
});

test("Leave and a following Join wait for an old socket to stop", async () => {
  const pendingStop = deferred();
  const first = Object.assign(new Socket(), { stop: () => pendingStop.promise });
  const second = new Socket();
  const { session, time } = fixture([first, second]);
  await session.join("First");
  first.close();
  const leaving = session.leave();
  const joining = session.join("Second");
  await flush();
  assert.equal(second.calls.length, 0);
  pendingStop.resolve();
  await leaving;
  await joining;
  await time.advance(0);
  assert.deepEqual(second.calls, [["Join", "Second", null]]);
});

test("startup joins after granting invite access, Leave stays left, and Play again starts fresh", async () => {
  const first = new Socket();
  const second = new Socket(() => player(2, 0, "fresh"));
  let authorized = false;
  const run = fixture([first, second], async (_, code) => {
    if (code === "fixture") authorized = true;
    return { mode: "private", authorized };
  });
  await run.session.join("FirstName", "fixture");
  assert.equal(run.session.state, "playing");
  assert.deepEqual(run.accessCalls, ["fixture", undefined]);
  assert.deepEqual(first.calls, [["Join", "FirstName", null]]);
  await run.session.leave();
  await run.time.advance(10_000);
  assert.equal(run.session.state, "ready");
  assert.equal(run.session.player, null);
  assert.deepEqual(second.calls, []);
  await run.session.join("NextName");
  assert.deepEqual(second.calls, [["Join", "NextName", null]]);
  assert.equal(run.accessCalls.filter(code => code === "fixture").length, 1);
});

test("missing and invalid invites never open a socket or schedule another access check", async () => {
  for (const code of [undefined, "invalid"]) {
    const run = fixture([], async () => {
      if (code !== undefined) throw new AccessError(403);
      return { mode: "private", authorized: false };
    });
    await run.session.join("Nickname", code);
    assert.equal(run.session.state, "access");
    assert.equal(run.session.detail, "Open a valid invite link to join this game.");
    await run.time.advance(60_000);
    assert.deepEqual(run.accessCalls, [code]);
  }
});