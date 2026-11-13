import assert from "node:assert/strict";
import test from "node:test";
import { nicknames, pickNickname } from "../../src/Pongsole.Server/wwwroot/nicknames.mjs";

test("provides 200 immutable, unique names that satisfy the game rules", () => {
  assert.equal(nicknames.length, 200);
  assert.ok(Object.isFrozen(nicknames));
  assert.equal(new Set(nicknames).size, 200);
  assert.equal(new Set(nicknames.map(name => name.toLowerCase())).size, 200);
  for (const name of nicknames) {
    assert.match(name, /^[A-Za-z0-9]{1,10}$/);
  }
});

test("picks the first, a middle, and the last entry using one random draw each", () => {
  for (const [draw, expected] of [[0, "MaybeToast"], [0.502, "FakeFlash"], [1 - Number.EPSILON, "PongFable"]]) {
    let draws = 0;
    assert.equal(pickNickname(() => { draws++; return draw; }), expected);
    assert.equal(draws, 1);
  }
});
