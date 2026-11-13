import assert from "node:assert/strict";
import test from "node:test";
import fs from "node:fs";
import { gameSocket } from "../../src/Pongsole.Server/wwwroot/transport.mjs";
import { readPacket } from "../../src/Pongsole.Server/wwwroot/realtime-codec.mjs";

const sdp = [
  "v=0", "o=- 1 1 IN IP4 0.0.0.0", "s=-", "t=0 0",
  "m=application 9 UDP/DTLS/SCTP webrtc-datachannel", "c=IN IP4 0.0.0.0",
  "a=candidate:1 1 UDP 123 162.159.198.1 3478 typ relay raddr 0.0.0.0 rport 0", "",
].join("\r\n");
const sessionId = "a".repeat(24);
const sessionToken = "b".repeat(24);

function fixture(answer = sdp) {
  const requests = [];
  const channels = new Map();
  let configuration;
  let closed = false;
  const peer = {
    iceGatheringState: "complete",
    createDataChannel(label, options) {
      const channel = { options, readyState: "open", bufferedAmount: 0, sent: [], send(data) { this.sent.push(data); } };
      channels.set(label, channel);
      return channel;
    },
    async createOffer() { return { type: "offer", sdp }; },
    async setLocalDescription(offer) { this.localDescription = offer; },
    async setRemoteDescription() {},
    close() { closed = true; },
  };
  const connection = gameSocket({
    makePeer(config) { configuration = config; return peer; },
    async request(path, options) {
      requests.push({ path, options });
      const body = options.method === "DELETE" ? null : path.endsWith("/offer") ? { sdp: answer } : {
        sessionId, sessionToken, epoch: 7,
        iceServers: [{ urls: ["turn:turn.cloudflare.com:3478?transport=udp"], username: "temporary", credential: "fixture" }],
      };
      return { ok: true, json: async () => body };
    },
  });
  return { connection, requests, channels, get configuration() { return configuration; }, get closed() { return closed; } };
}

test("authenticates signaling separately and sends only newest held state when drained", async () => {
  const run = fixture();
  await run.connection.start();
  assert.equal(run.configuration.iceTransportPolicy, "relay");
  assert.deepEqual(run.channels.get("control").options, { ordered: true });
  const realtime = run.channels.get("realtime");
  assert.deepEqual(realtime.options, { ordered: false, maxRetransmits: 0 });
  assert.equal(run.requests[0].path, "/rtc/sessions");
  assert.deepEqual(run.requests[0].options.headers, { "X-Pong-Browser": "1" });
  assert.equal(run.requests[1].path, "/rtc/sessions/" + sessionId + "/offer");
  assert.equal(run.requests[1].options.headers["X-Pong-Session"], sessionToken);
  assert.equal(run.requests[1].options.credentials, "same-origin");
  realtime.bufferedAmount = 50;
  await run.connection.send("SetHeld", "down", 4);
  await run.connection.send("SetHeld", null, 5);
  assert.equal(realtime.sent.length, 0);
  realtime.bufferedAmount = 0;
  realtime.onbufferedamountlow();
  assert.equal(realtime.sent.length, 1);
  const sent = readPacket(realtime.sent[0]);
  assert.equal(sent.id, 5);
  assert.equal(sent.chunk[0], 0);
  await run.connection.stop();
  assert.equal(run.requests.at(-1).options.method, "DELETE");
  assert.ok(run.closed);
});

test("rejects nonrelay answer and releases its reservation", async () => {
  const run = fixture(sdp.replace("typ relay", "typ host"));
  await assert.rejects(run.connection.start(), /relay answer candidate/);
  assert.equal(run.requests.at(-1).options.method, "DELETE");
  assert.equal(run.connection.state, "Disconnected");
});

test("joins only after control admission and a matching decoded snapshot", async () => {
  const run = fixture();
  await run.connection.start();
  const joining = run.connection.invoke("Join", "Player0001", null);
  let completed = false;
  joining.then(() => { completed = true; });
  const control = run.channels.get("control");
  control.onmessage({ data: new TextEncoder().encode(JSON.stringify({
    type: "joined", playerId: "00000001-0000-0000-0000-000000000000",
    team: "right", resumeToken: "fixture", lastProcessedSequence: 1,
  })).buffer });
  await Promise.resolve();
  assert.equal(completed, false);
  const fixtureData = JSON.parse(fs.readFileSync(new URL("../Fixtures/realtime-v2.json", import.meta.url)));
  const payload = Buffer.from(fixtureData.snapshotHex, "hex");
  const count = Math.ceil(payload.length / 972);
  for (let index = count - 1; index >= 0; index--) {
    const part = payload.subarray(index * 972, (index + 1) * 972);
    const packet = new ArrayBuffer(28 + part.length);
    const view = new DataView(packet);
    view.setUint16(0, 0x4750, true);
    view.setUint8(2, 2);
    view.setUint8(3, 1);
    view.setBigUint64(4, 7n, true);
    view.setBigUint64(12, 42n, true);
    view.setUint16(20, index, true);
    view.setUint16(22, count, true);
    view.setUint32(24, payload.length, true);
    new Uint8Array(packet).set(part, 28);
    run.channels.get("realtime").onmessage({ data: packet });
  }
  assert.equal((await joining).snapshot.arena.tick, 42);
  await run.connection.stop();
});
