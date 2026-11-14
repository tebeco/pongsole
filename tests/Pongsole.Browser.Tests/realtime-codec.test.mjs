import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import { kind, readPacket, readSnapshot, SnapshotReassembly, writeEcho, writeInput } from "../../src/Pongsole.Server/wwwroot/realtime-codec.mjs";

const vectors = JSON.parse(fs.readFileSync(new URL("../Fixtures/realtime-v2.json", import.meta.url)));
const bytes = hex => Uint8Array.from(Buffer.from(hex, "hex"));

test("input bytes match the native golden vector and malformed packets are rejected", () => {
  const packet = writeInput(7, 8, "down");
  assert.deepEqual(packet, bytes(vectors.inputHex));
  assert.deepEqual(readPacket(packet), { type: kind.input, epoch: 7, id: 8, index: 0, count: 1, length: 10,
    chunk: Uint8Array.of(2, 0, 0, 0, 0, 0, 0, 0, 0, 0) });
  assert.equal(readPacket(Uint8Array.of(...packet, 0)), null);
  assert.equal(readPacket(packet.slice(1)), null);
  const old = packet.slice();
  old[2] = 1;
  assert.equal(readPacket(old), null);
  assert.throws(() => writeInput(7, Number.MAX_SAFE_INTEGER + 1, "up"), RangeError);
  assert.equal(readPacket(writeEcho(7, 19)).id, 19);
});

test("decodes the shared snapshot vector and rejects malformed data", () => {
  const payload = bytes(vectors.snapshotHex);
  const snapshot = readSnapshot(payload);
  assert.equal(snapshot.arena.tick, 42);
  assert.equal(snapshot.arena.phase, "playing");
  assert.equal(snapshot.arena.balls.length, 10);
  assert.equal(snapshot.arena.pickups.length, 12);
  assert.equal(snapshot.players.length, 64);
  assert.equal(snapshot.arena.mode, "individualPaddles");
  assert.equal(snapshot.arena.playerPaddles.length, 64);
  assert.deepEqual(snapshot.arena.playerPaddles[0], {
    playerId: snapshot.players[0].playerId, team: "right", y: 15, velocity: 2,
  });
  const trailer = payload.length - (3 + 64 * 33);
  const badMode = payload.slice();
  badMode[trailer] = 255;
  assert.equal(readSnapshot(badMode), null);
  const duplicate = payload.slice();
  duplicate.set(payload.subarray(trailer + 3, trailer + 19), trailer + 36);
  assert.equal(readSnapshot(duplicate), null);
  assert.equal(snapshot.players[0].playerId, "00000001-0000-0000-0000-000000000000");
  assert.equal(snapshot.players[0].displayName, "Player0001");
  assert.equal(snapshot.players[0].lastUpTick, 40);
  assert.equal(snapshot.players[0].lastDownTick, null);
  assert.equal(snapshot.players[0].latencyMilliseconds, 2000);
  assert.equal(readSnapshot(payload.slice(0, -1)), null);
  assert.equal(readSnapshot(Uint8Array.of(...payload, 0)), null);
  const invalid = payload.slice();
  invalid[24] = 99;
  assert.equal(readSnapshot(invalid), null);
  const nonfinite = payload.slice();
  new DataView(nonfinite.buffer).setFloat64(27, Infinity, true);
  assert.equal(readSnapshot(nonfinite), null);
});

test("decodes a full Discord username in the roster", () => {
  const payload = Buffer.from(vectors.snapshotHex, "hex");
  const previous = "Player0001";
  const start = payload.indexOf(previous);
  assert.ok(start > 0);
  const username = "long.discord_username0123456789";
  const withDiscordName = Buffer.concat([
    payload.subarray(0, start - 1), Buffer.of(username.length), Buffer.from(username),
    payload.subarray(start + previous.length),
  ]);
  assert.equal(readSnapshot(withDiscordName).players[0].displayName, username);
});

test("reassembly handles reorder, duplicate, conflict, expiry and eviction", () => {
  const payload = bytes(vectors.snapshotHex);
  const reader = new SnapshotReassembly(7);
  function chunk(id, index, epoch = 7) {
    const part = payload.subarray(index * 972, (index + 1) * 972);
    return { type: kind.snapshot, epoch, id, index, count: Math.ceil(payload.length / 972), length: payload.length, chunk: part };
  }
  assert.equal(reader.accept(chunk(1, 0), 0), null);
  assert.equal(reader.accept(chunk(2, 1, 8), 1), null);
  const last = Math.ceil(payload.length / 972) - 1;
  assert.equal(reader.accept(chunk(2, last), 1), null);
  assert.equal(reader.accept(chunk(1, 1), 2), null);
  assert.equal(reader.accept(chunk(2, last), 3), null);
  for (let index = last - 1; index > 0; index--) assert.equal(reader.accept(chunk(2, index), 4), null);
  assert.deepEqual(reader.accept(chunk(2, 0), 5), payload);
  assert.equal(reader.accept(chunk(2, 0), 6), null);
  assert.equal(reader.accept(chunk(3, 0), 7), null);
  assert.equal(reader.accept(chunk(3, 1), 507), null);
  assert.equal(reader.accept(chunk(4, 0), 508), null);
  const conflict = chunk(4, 0);
  conflict.chunk = conflict.chunk.slice();
  conflict.chunk[0] ^= 1;
  assert.equal(reader.accept(conflict, 509), null);
  for (let index = 1; index <= last; index++) assert.equal(reader.accept(chunk(4, index), 510), null);
});
