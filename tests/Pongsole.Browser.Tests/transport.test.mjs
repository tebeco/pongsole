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

test("uses same-origin WebSockets without negotiate or access tokens", () => {
  let configuration;
  const signalR = {
    HttpTransportType: { WebSockets: 1 },
    LogLevel: { None: 6 },
    HubConnectionBuilder: class {
      withUrl(...args) { configuration = args; return this; }
      configureLogging() { return this; }
      build() { return {}; }
    },
  };
  gameSocket(signalR);
  assert.equal(configuration[0], "/game");
  assert.equal(configuration[1].transport, 1);
  assert.equal(configuration[1].skipNegotiation, true);
  assert.equal(configuration[1].accessTokenFactory, undefined);
});
