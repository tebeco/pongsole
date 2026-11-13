import assert from "node:assert/strict";
import test from "node:test";
import { browserSession, gameSocket } from "../../src/Pongsole.Server/wwwroot/transport.mjs";

test("uses the same-origin grant endpoint and passes the code in a header only", async () => {
  const calls = [];
  const fetch = async (...args) => {
    calls.push(args);
    return { ok: true, json: async () => ({ mode: "private", authorized: false }) };
  };
  assert.deepEqual(await browserSession(undefined, undefined, fetch), { mode: "private", authorized: false });
  await browserSession(undefined, "secret", fetch);
  assert.equal(calls[0][0], "/browser/session");
  assert.equal(calls[0][1].headers, undefined);
  assert.equal(calls[1][0], "/browser/session");
  assert.deepEqual(calls[1][1].headers, { "X-Pong-Browser": "1", "X-Pong-Code": "secret" });
  assert.equal(calls[1][1].redirect, "error");
});
