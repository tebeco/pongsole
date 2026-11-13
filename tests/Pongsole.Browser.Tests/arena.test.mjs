import assert from "node:assert/strict";
import test from "node:test";
import { ArenaSnapshots, drawArena } from "../../src/Pongsole.Server/wwwroot/arena.mjs";

const ball = (id, x, y = 15, velocityX = 40, velocityY = 0) => ({ id, x, y, velocityX, velocityY });
const snapshot = (tick, balls, { phase = "playing", rtt, leftScore = 0 } = {}) => ({
  arena: { tick, phase, leftScore, rightScore: 0, leftPaddle: { y: 15 }, rightPaddle: { y: 15 }, balls },
  players: [{ playerId: "other", latencyMilliseconds: 400 }, { playerId: "local", latencyMilliseconds: rtt }],
});
const closeTo = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-9, `${actual} ≈ ${expected}`);
const position = (history, time) => history.frame(time).balls[0];

test("projects using local half RTT and monotonic receipt age, with a bounded horizon", () => {
  const history = new ArenaSnapshots("local");
  history.accept(snapshot(30, [ball(1, 40)], { rtt: 100 }), 100);
  closeTo(position(history, 100).x, 42);
  closeTo(position(history, 175).x, 45);
  closeTo(position(history, 1000).x, 50);
  assert.equal(history.frame(175, true).balls[0].x, 40);
  assert.equal(history.accept(snapshot(29, [ball(1, 80)]), 160), false);
  history.accept(snapshot(30, [ball(1, 40)], { rtt: 300 }), 200);
  closeTo(position(history, 200).x, 46);
  assert.equal(history.newest.players[1].latencyMilliseconds, 300);
  const noSample = new ArenaSnapshots("missing");
  noSample.accept(snapshot(30, [ball(1, 40)], { rtt: 200 }), 100);
  closeTo(position(noSample, 100).x, 40);
  const capped = new ArenaSnapshots("local");
  capped.accept(snapshot(1, [ball(1, 40)], { rtt: 1200 }), 100);
  closeTo(position(capped, 100).x, 45);
});

test("reflects walls and freezes the whole path at an unconfirmed paddle or goal", () => {
  const history = new ArenaSnapshots();
  history.accept(snapshot(1, [
    ball(1, 50, 1, 20, -20), ball(2, 96, 15, 40, 20),
    ball(3, 1, 15, -40, 20), ball(4, 3, 15, 40), ball(5, 50, 15, 0, 800),
  ]), 0);
  const [wall, face, goal, outgoing, repeatedWalls] = history.frame(200).balls;
  closeTo(wall.y, 4);
  closeTo(face.x, 97);
  closeTo(face.y, 15.5);
  closeTo(goal.x, -0.5);
  closeTo(goal.y, 15.75);
  closeTo(outgoing.x, 11);
  closeTo(repeatedWalls.y, 1);
});

test("smooths correction by ID across reordered balls and score changes, then freezes", () => {
  const history = new ArenaSnapshots("local");
  const first = snapshot(1, [ball(1, 30), ball(2, 70)], { rtt: 40 });
  history.accept(first, 0);
  closeTo(position(history, 150).x, 36.8);
  const current = snapshot(10, [ball(3, 60), ball(1, 34)], { rtt: 40, leftScore: 1 });
  history.accept(current, 150);
  const initial = history.frame(150).balls;
  closeTo(initial[0].x, 60.8);
  closeTo(initial[1].x, 36.8);
  closeTo(history.frame(200).balls[1].x, 37.8);
  closeTo(history.frame(250).balls[1].x, 38.8);
  closeTo(history.frame(800).balls[1].x, 44);
  assert.equal(history.frame(200).leftScore, 1);
  assert.deepEqual(history.frame(150).balls.map(candidate => candidate.id), [3, 1]);
  assert.equal(first.arena.balls[0].x, 30);
  assert.equal(current.arena.balls[1].x, 34);
});

test("snaps on reversal, large corrections, phase changes, and new baseline", () => {
  const history = new ArenaSnapshots("local");
  history.accept(snapshot(10, [ball(1, 90), ball(2, 20)]), 0);
  history.accept(snapshot(13, [ball(1, 96, 15, -40), ball(2, 60)]), 50);
  closeTo(history.frame(50).balls[0].x, 96);
  closeTo(history.frame(50).balls[1].x, 60);
  history.accept(snapshot(16, [ball(3, 50, 15, 0)], { phase: "serving" }), 100);
  closeTo(position(history, 700).x, 50);
  history.accept(snapshot(76, [ball(4, 50)], { rtt: 100 }), 1000);
  closeTo(position(history, 1000).x, 52);
  const rejoined = new ArenaSnapshots("local");
  rejoined.accept(snapshot(1, [ball(1, 40)]), 1100);
  closeTo(position(rejoined, 1100).x, 40);
});

test("draws all balls and the authoritative pickup amount and countdown beneath it", () => {
  const labels = [];
  const arcs = [];
  const noop = () => {};
  const context = {
    setTransform: noop, clearRect: noop, setLineDash: noop, beginPath: noop,
    moveTo: noop, lineTo: noop, stroke: noop, fillRect: noop, fill: noop,
    arc: (x, y, radius) => arcs.push([x, y, radius]),
    fillText: (value, x, y) => labels.push([value, x, y]),
  };
  const canvas = { getBoundingClientRect: () => ({ width: 100, height: 30 }), getContext: () => context };
  const arena = snapshot(1, [ball(1, 40), ball(2, 60)]).arena;
  arena.pickups = [{ id: 1, x: 25, y: 8, amount: 10, remainingTicks: 61 }];
  const ratio = Object.getOwnPropertyDescriptor(globalThis, "devicePixelRatio");
  Object.defineProperty(globalThis, "devicePixelRatio", { value: 1, configurable: true });
  try {
    drawArena(canvas, arena);
  } finally {
    if (ratio) Object.defineProperty(globalThis, "devicePixelRatio", ratio);
    else delete globalThis.devicePixelRatio;
  }

  assert.ok(arcs.some(([x, y, radius]) => x === 25 && y === 8 && radius === 1.6));
  assert.deepEqual(arcs.filter(([, , radius]) => radius === .5), [[40, 15, .5], [60, 15, .5]]);
  assert.equal(labels[0][0], "+10");
  assert.equal(labels[1][0], "2s");
  assert.equal(labels[0][1], labels[1][1]);
  assert.ok(labels[1][2] > labels[0][2]);
});
test("interpolates personal paddles by identity and snaps new members and mode changes", () => {
  const history = new ArenaSnapshots("local");
  function frame(tick, mode, playerPaddles) {
    const next = snapshot(tick, [ball(1, 40)]);
    Object.assign(next.arena, { mode, playerPaddles });
    return next;
  }
  history.accept(frame(1, "allTogether", []), 0);
  history.accept(frame(2, "individualPaddles", [{ playerId: "local", y: 12 }, { playerId: "left", y: 9 }]), 50);
  assert.equal(history.frame(50).playerPaddles[0].y, 12);
  history.accept(frame(3, "individualPaddles", [{ playerId: "new", y: 15 }, { playerId: "local", y: 20 }]), 100);
  assert.deepEqual(history.frame(125).playerPaddles.map(({ playerId, y }) => [playerId, y]), [["new", 15], ["local", 16]]);
  assert.equal(history.frame(125, true).playerPaddles[1].y, 20);
});

test("draws each half-height paddle with the local solid paddle above overlapping ghosts", () => {
  const rects = [];
  const noop = () => {};
  const context = {
    setTransform: noop, clearRect: noop, setLineDash: noop, beginPath: noop,
    moveTo: noop, lineTo: noop, stroke: noop, fill: noop, arc: noop,
    fillRect(...rect) { rects.push([this.globalAlpha, ...rect]); },
  };
  const canvas = { getBoundingClientRect: () => ({ width: 100, height: 30 }), getContext: () => context };
  const arena = {
    mode: "individualPaddles", balls: [],
    playerPaddles: [
      { playerId: "local", team: "left", y: 15 }, { playerId: "opponent", team: "right", y: 21 },
      { playerId: "teammate", team: "left", y: 15 },
    ],
  };
  const ratio = Object.getOwnPropertyDescriptor(globalThis, "devicePixelRatio");
  Object.defineProperty(globalThis, "devicePixelRatio", { value: 1, configurable: true });
  try {
    drawArena(canvas, arena, { playerId: "local", team: "left" });
  } finally {
    if (ratio) Object.defineProperty(globalThis, "devicePixelRatio", ratio);
    else delete globalThis.devicePixelRatio;
  }
  assert.deepEqual(rects, [[.25, 1.5, 13.5, 1, 3], [.7, 97.5, 19.5, 1, 3], [1, 1.5, 13.5, 1, 3]]);
  assert.equal(context.globalAlpha, 1);
});
