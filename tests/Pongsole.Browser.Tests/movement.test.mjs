import assert from "node:assert/strict";
import test from "node:test";
import { bindControls } from "../../src/Pongsole.Server/wwwroot/controls.mjs";
import { Movement } from "../../src/Pongsole.Server/wwwroot/movement.mjs";
import { deferred, FakeClock, flush } from "./test-support.mjs";

function fixture(send = async () => {}) {
  const time = new FakeClock();
  const calls = [];
  const movement = new Movement({
    time,
    enabled: () => true,
    send: async direction => { calls.push([direction, time.now()]); await send(direction); },
  });
  return { movement, time, calls };
}

test("sends held state immediately, heartbeats every 250 ms, then neutral", async () => {
  const { movement, time, calls } = fixture();
  movement.press("key", "up");
  await time.advance(750);
  movement.release("key");
  await time.advance(500);
  assert.deepEqual(calls, [["up", 0], ["up", 250], ["up", 500], ["up", 750], [null, 750]]);
});

test("opposite held input sends neutral until one side is released", async () => {
  const { movement, time, calls } = fixture();
  movement.press("a", "up");
  await flush();
  movement.press("b", "down");
  await time.advance(500);
  movement.release("a");
  await time.advance(250);
  assert.deepEqual(calls, [["up", 0], [null, 0], ["down", 500], ["down", 750]]);
});

test("slow sends retain the initial press and latest release, with no replay backlog", async () => {
  const pending = deferred();
  let attempts = 0;
  const { movement, time, calls } = fixture(() => ++attempts === 1 ? pending.promise : Promise.resolve());
  movement.press("key", "down");
  await time.advance(750);
  movement.release("key");
  assert.deepEqual(calls, [["down", 0]]);
  pending.resolve();
  await flush();
  await time.advance(500);
  assert.deepEqual(calls, [["down", 0], [null, 750]]);
});

test("disconnect drops unsent input and ignores stale completions", async () => {
  const pending = deferred();
  const resumed = deferred();
  let attempts = 0;
  const { movement, time, calls } = fixture(() => [pending.promise, resumed.promise][attempts++]);
  movement.press("key", "up");
  movement.release("key");
  movement.clear(true);
  await time.advance(100);
  movement.press("key", "down");
  pending.resolve();
  await time.advance(100);
  assert.deepEqual(calls, [["up", 0], ["down", 100]]);
  resumed.resolve();
  movement.release("key");
  await flush();
  assert.deepEqual(calls, [["up", 0], ["down", 100], [null, 200]]);
});

class Target {
  listeners = new Map();
  addEventListener(type, callback) { this.listeners.set(type, callback); }
  emit(type, init = {}) {
    const event = { target: this, preventDefault() { this.defaultPrevented = true; }, ...init };
    this.listeners.get(type)?.(event);
    return event;
  }
}

function inputFixture() {
  const run = fixture();
  const window = new Target();
  const document = new Target();
  const up = Object.assign(new Target(), { dataset: { direction: "up" }, setPointerCapture(id) { this.capture = id; } });
  const down = Object.assign(new Target(), { dataset: { direction: "down" }, setPointerCapture(id) { this.capture = id; } });
  bindControls(run.movement, [up, down], window, document);
  return { ...run, up, down, window, document };
}

test("keyboard ignores editable targets and repeat and requires a new press after blur or hide", async () => {
  const { window, document, time, calls } = inputFixture();
  const key = { key: "w", code: "KeyW" };
  assert.equal(window.emit("keydown", { ...key, target: { tagName: "INPUT" } }).defaultPrevented, undefined);
  assert.equal(window.emit("keydown", key).defaultPrevented, true);
  window.emit("keydown", { ...key, repeat: true });
  window.emit("blur");
  await flush();
  await time.advance(100);
  window.emit("keydown", { ...key, repeat: true });
  assert.deepEqual(calls, [["up", 0], [null, 0]]);
  window.emit("keyup", key);
  window.emit("keydown", { key: "ArrowDown", code: "ArrowDown" });
  document.hidden = true;
  document.emit("visibilitychange");
  await flush();
  await time.advance(200);
  assert.deepEqual(calls, [["up", 0], [null, 0], ["down", 100], [null, 100]]);
});

test("pointer cancellation and lost capture clear held and queued movement without synthetic duplicates", async () => {
  const { up, down, time, calls } = inputFixture();
  up.emit("pointerdown", { pointerId: 1, button: 0 });
  assert.equal(up.capture, 1);
  up.emit("pointerup", { pointerId: 1 });
  up.emit("lostpointercapture", { pointerId: 1 });
  up.emit("click", { detail: 1 });
  await flush();
  await time.advance(100);
  assert.deepEqual(calls, [["up", 0], [null, 0]]);
  down.emit("pointerdown", { pointerId: 2, button: 0 });
  down.emit("pointercancel", { pointerId: 2 });
  await flush();
  await time.advance(200);
  up.emit("pointerdown", { pointerId: 3, button: 0 });
  up.emit("lostpointercapture", { pointerId: 3 });
  await flush();
  await time.advance(200);
  down.emit("click", { detail: 0 });
  await flush();
  assert.deepEqual(calls, [["up", 0], [null, 0], ["down", 100], [null, 100], ["up", 300], [null, 300], ["down", 500], [null, 500]]);
});
