import assert from "node:assert/strict";
import test from "node:test";
import { ArenaSnapshots } from "../../src/Pongsole.Server/wwwroot/arena.mjs";

const snapshot = (tick, x, phase = "playing") => ({
  arena: { tick, phase, leftScore: 0, rightScore: 0, leftPaddle: { y: 15 }, rightPaddle: { y: 15 }, ball: { x, y: 15 } },
  players: [],
});

test("interpolates geometry while retaining the latest accepted tick", () => {
  const history = new ArenaSnapshots();
  history.accept(snapshot(30, 40), 100);
  history.accept(snapshot(33, 44), 150);
  assert.equal(history.accept(snapshot(32, 80), 160), false);
  assert.equal(history.frame(175).ball.x, 42);
  assert.equal(history.frame(175).tick, 33);
  assert.equal(history.frame(175, true).ball.x, 44);
  assert.equal(history.frame(250).ball.x, 44);
});

test("phase changes snap, and same tick refreshes membership", () => {
  const history = new ArenaSnapshots();
  history.accept(snapshot(30, 44), 100);
  history.accept(snapshot(33, 50, "serving"), 150);
  history.accept({ ...snapshot(33, 50, "serving"), players: [{ displayName: "Player" }] }, 160);
  assert.equal(history.frame(160).ball.x, 50);
  assert.equal(history.newest.players.length, 1);
});
