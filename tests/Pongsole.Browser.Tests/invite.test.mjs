import assert from "node:assert/strict";
import test from "node:test";
import { initialInviteCode, inviteAccess } from "../../src/Pongsole.Server/wwwroot/invite.mjs";
import { GameSession } from "../../src/Pongsole.Server/wwwroot/session.mjs";
import { AccessError } from "../../src/Pongsole.Server/wwwroot/transport.mjs";

test("only an HTTPS fragment with one nonempty code can auto-unlock", () => {
  for (const hash of ["", "#section", "#code", "#code=", "#code=one&code=two", "#code=&code=two"]) {
    assert.equal(initialInviteCode(new URL(hash, "https://example.test/")), undefined);
  }
  assert.equal(initialInviteCode(new URL("http://example.test/#code=fixture")), undefined);
  assert.equal(initialInviteCode(new URL("https://example.test/?code=query")), undefined);
  assert.equal(initialInviteCode(new URL("https://example.test/#code=%20%2B%26%3D%E2%98%83%20")), " +&=☃ ");
});

function fixture(href) {
  const location = new URL(href);
  const state = { existing: true };
  const replacements = [];
  const attempts = [];
  let clears = 0;
  let authorized = false;
  const history = { state, replaceState(previous, title, target) {
    assert.equal(previous, state);
    assert.equal(title, "");
    replacements.push(target);
    location.href = target;
  } };
  const access = inviteAccess(async (_, code) => {
    if (code === undefined) return { authorized };
    attempts.push(code);
    if (code !== "correct") throw new AccessError(403);
    authorized = true;
  }, location, history, () => { clears++; });
  const session = new GameSession({ access, changed() {}, clearMovement() {} });
  return { location, session, access, attempts, replacements, get clears() { return clears; } };
}

test("startup unlocks once and preserves the query, other fragment values and history state", async () => {
  const run = fixture("https://example.test/court?view=wide#code=correct&section=one%26two");
  await run.session.checkAccess(initialInviteCode(run.location));
  assert.equal(run.session.state, "ready");
  assert.deepEqual(run.replacements, ["https://example.test/court?view=wide#section=one%26two"]);
  assert.equal(run.clears, 1);
  await run.session.checkAccess();
  assert.deepEqual(run.attempts, ["correct"]);
});

test("wrong invite remains in the address until a successful manual unlock", async () => {
  const run = fixture("https://example.test/#code=wrong");
  await run.session.checkAccess(initialInviteCode(run.location));
  assert.equal(run.session.state, "access");
  assert.match(run.session.detail, /try again/);
  assert.equal(run.location.hash, "#code=wrong");
  await run.session.checkAccess();
  assert.deepEqual(run.attempts, ["wrong"]);
  await run.session.checkAccess("correct");
  assert.equal(run.session.state, "ready");
  assert.equal(run.location.hash, "");
  assert.deepEqual(run.replacements, ["https://example.test/"]);
});

test("aborted grants and manual unlocks without a code fragment preserve the address", async () => {
  const aborted = fixture("https://example.test/#code=correct");
  const controller = new AbortController();
  const pending = aborted.access(controller.signal, "correct");
  controller.abort();
  await pending;
  assert.equal(aborted.location.hash, "#code=correct");
  assert.equal(aborted.clears, 0);

  const manual = fixture("https://example.test/#plain-anchor");
  await manual.session.checkAccess("correct");
  assert.equal(manual.session.state, "ready");
  assert.equal(manual.clears, 1);
  assert.equal(manual.location.hash, "#plain-anchor");
  assert.deepEqual(manual.replacements, []);
});
