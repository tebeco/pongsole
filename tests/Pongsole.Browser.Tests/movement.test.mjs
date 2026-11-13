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

test("retains rapid taps and paces held movement at 100 ms", async () => {
  const { movement, time, calls } = fixture();
  movement.press("a", "up");
  movement.release("a");
  movement.press("b", "down");
  movement.release("b");
  assert.deepEqual(calls, [["up", 0]]);
  await time.advance(100);
  movement.press("a", "up");
  await time.advance(300);
  movement.release("a");
  await time.advance(300);
  assert.deepEqual(calls, [["up", 0], ["down", 100], ["up", 200], ["up", 300], ["up", 400], ["up", 500]]);
});

test("opposite held input cancels pending pulses until one side is released", async () => {
  const { movement, time, calls } = fixture();
  movement.press("a", "up");
  movement.press("b", "down");
  await time.advance(500);
  assert.deepEqual(calls, [["up", 0]]);
  movement.release("a");
  await time.advance(100);
  assert.deepEqual(calls, [["up", 0], ["down", 600]]);
});

test("serializes ten queued movements and drops newest on overflow without catch-up bursts", async () => {
  const pending = deferred();
  let attempts = 0;
  const { movement, time, calls } = fixture(() => ++attempts === 1 ? pending.promise : Promise.resolve());
  movement.pulse("up");
  for (let index = 0; index < 10; index++) movement.pulse(index % 2 ? "down" : "up");
  movement.pulse("up");
  await time.advance(500);
  assert.equal(calls.length, 1);
  pending.resolve();
  await flush();
  assert.equal(calls.length, 2);
  await time.advance(900);
  assert.deepEqual(calls, [["up", 0], ...Array.from({ length: 10 }, (_, index) => [index % 2 ? "down" : "up", 500 + index * 100])]);
  for (const [, time] of calls) {
    assert.ok(calls.filter(([, sent]) => sent > time - 1000 && sent <= time).length <= 10);
  }
});

test("disconnect drops unsent input and ignores stale completions", async () => {
  const pending = deferred();
  const resumed = deferred();
  let attempts = 0;
  const { movement, time, calls } = fixture(() => [pending.promise, resumed.promise][attempts++]);
  movement.press("key", "up");
  movement.pulse("down");
  movement.clear(true);
  await time.advance(100);
  movement.press("key", "up");
  movement.pulse("down");
  pending.resolve();
  await time.advance(100);
  assert.deepEqual(calls, [["up", 0], ["up", 100]]);
  resumed.resolve();
  movement.release("key");
  await flush();
  assert.deepEqual(calls, [["up", 0], ["up", 100], ["down", 200]]);
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
  await time.advance(100);
  window.emit("keydown", { ...key, repeat: true });
  assert.deepEqual(calls, [["up", 0]]);
  window.emit("keyup", key);
  window.emit("keydown", { key: "ArrowDown", code: "ArrowDown" });
  document.hidden = true;
  document.emit("visibilitychange");
  await time.advance(200);
  assert.deepEqual(calls, [["up", 0], ["down", 100]]);
});

test("pointer cancellation and lost capture clear held and queued movement without synthetic duplicates", async () => {
  const { up, down, time, calls } = inputFixture();
  up.emit("pointerdown", { pointerId: 1, button: 0 });
  assert.equal(up.capture, 1);
  up.emit("pointerup", { pointerId: 1 });
  up.emit("lostpointercapture", { pointerId: 1 });
  up.emit("click", { detail: 1 });
  await time.advance(100);
  assert.deepEqual(calls, [["up", 0]]);
  down.emit("pointerdown", { pointerId: 2, button: 0 });
  down.emit("pointercancel", { pointerId: 2 });
  await time.advance(200);
  up.emit("pointerdown", { pointerId: 3, button: 0 });
  up.emit("lostpointercapture", { pointerId: 3 });
  await time.advance(200);
  down.emit("click", { detail: 0 });
  assert.deepEqual(calls, [["up", 0], ["down", 100], ["up", 300], ["down", 500]]);
});
