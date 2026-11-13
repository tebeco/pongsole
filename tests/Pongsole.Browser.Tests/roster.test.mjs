import assert from "node:assert/strict";
import test from "node:test";
import { RosterActivity, validName } from "../../src/Pongsole.Server/wwwroot/roster.mjs";

const player = (id, activity = {}) => ({ playerId: id, team: "left", ...activity });
const snapshot = (tick, players, phase = "playing") => ({ arena: { tick, phase }, players });

test("browser name rule requires one to ten unmodified ASCII letters or digits", () => {
  for (const value of ["a", "Z", "0", "Aa01234567"]) assert.equal(validName(value), true, value);
  for (const value of ["", "Aa012345678", " Alice", "Alice ", "A B", "A-1", "A_1", "Ä", "１", "A\n", null]) {
    assert.equal(validName(value), false, JSON.stringify(value));
  }
});

test("rosters retain canonical order and do not extend identical flashes", () => {
  const activity = new RosterActivity();
  const players = [player("b"), player("a")];
  assert.deepEqual(activity.update(snapshot(100, players), true, 0), players);
  const recent = [player("a", { lastUpTick: 101, lastDownTick: 102 }), player("b")];
  assert.deepEqual(activity.update(snapshot(104, recent), true, 50), recent);
  assert.equal(activity.active("a", "up", 199), true);
  assert.equal(activity.active("a", "down", 199), true);
  activity.update(snapshot(107, recent), true, 100);
  assert.equal(activity.active("a", "up", 200), false);
  assert.equal(activity.active("a", "down", 210), true);
  assert.equal(activity.active("a", "down", 217), false);
  assert.equal(activity.nextDeadline(217), Infinity);
});

test("waiting and reconnect seed activity silently and departed players are removed", () => {
  const activity = new RosterActivity();
  activity.update(snapshot(20, [player("a", { lastUpTick: 19 })]), true, 0);
  assert.equal(activity.active("a", "up", 0), false);
  activity.update(snapshot(21, [player("a", { lastUpTick: 21 })]), true, 20);
  assert.equal(activity.active("a", "up", 20), true);
  activity.update(snapshot(21, [player("a", { lastUpTick: 21 })]), false, 30);
  activity.update(snapshot(22, [player("a", { lastUpTick: 22 })]), true, 40);
  assert.equal(activity.active("a", "up", 40), false);
  activity.update(snapshot(23, [player("a", { lastDownTick: 23 })], "waiting"), true, 50);
  assert.equal(activity.active("a", "down", 50), false);
  activity.update(snapshot(24, [player("a", { lastDownTick: 23 })]), true, 60);
  assert.equal(activity.active("a", "down", 60), false);
  activity.update(snapshot(25, [player("a", { lastDownTick: 25 })]), true, 70);
  assert.equal(activity.active("a", "down", 70), true);
  activity.update(snapshot(26, []), true, 80);
  assert.equal(activity.active("a", "down", 80), false);
});
