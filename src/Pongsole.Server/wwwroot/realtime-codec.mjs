import { validName } from "./roster.mjs";

export const headerSize = 28;
export const chunkSize = 972;
export const maxPayloadSize = 8192;
const magic = 0x4750;
const version = 1;
export const kind = Object.freeze({ snapshot: 1, input: 2, probe: 3, echo: 4 });

function validMetadata(type, epoch, id, index, count, length) {
  return Number.isSafeInteger(epoch) && epoch > 0 && Number.isSafeInteger(id) && id >= 0 &&
    length >= 0 && length <= maxPayloadSize && count === Math.max(1, Math.ceil(length / chunkSize)) &&
    index >= 0 && index < count &&
    (type === kind.snapshot && length > 0 || type === kind.input && id > 0 && length === 10 ||
      (type === kind.probe || type === kind.echo) && length === 0);
}

export function readPacket(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length < headerSize || bytes.length > 1000) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint16(0, true) !== magic || view.getUint8(2) !== version) return null;

  const type = view.getUint8(3);
  const epoch = Number(view.getBigUint64(4, true));
  const id = Number(view.getBigUint64(12, true));
  const index = view.getUint16(20, true);
  const count = view.getUint16(22, true);
  const length = view.getUint32(24, true);
  if (!validMetadata(type, epoch, id, index, count, length) ||
      bytes.length !== headerSize + Math.min(chunkSize, length - index * chunkSize)) return null;

  return { type, epoch, id, index, count, length, chunk: bytes.subarray(headerSize) };
}

export function writeInput(epoch, sequence, direction) {
  const code = direction === null ? 0 : direction === "up" ? 1 : direction === "down" ? 2 : -1;
  if (code < 0) throw new RangeError("Invalid input direction.");
  const packet = writePacket(kind.input, epoch, sequence, 10);
  packet[headerSize] = code;
  return packet;
}

export function writeEcho(epoch, id) {
  return writePacket(kind.echo, epoch, id, 0);
}

function writePacket(type, epoch, id, length) {
  if (!validMetadata(type, epoch, id, 0, 1, length)) throw new RangeError("Invalid realtime packet.");
  const bytes = new Uint8Array(headerSize + length);
  const view = new DataView(bytes.buffer);
  view.setUint16(0, magic, true);
  view.setUint8(2, version);
  view.setUint8(3, type);
  view.setBigUint64(4, BigInt(epoch), true);
  view.setBigUint64(12, BigInt(id), true);
  view.setUint16(22, 1, true);
  view.setUint32(24, length, true);
  return bytes;
}

export class SnapshotReassembly {
  #highest = -1;
  #body;
  #received = 0;
  #count = 0;
  #started = 0;

  constructor(epoch) { this.epoch = epoch; }

  accept(packet, now) {
    if (!packet || packet.type !== kind.snapshot || packet.epoch !== this.epoch || packet.id < this.#highest) return null;

    if (packet.id > this.#highest) {
      this.#highest = packet.id;
      this.#body = new Uint8Array(packet.length);
      this.#received = 0;
      this.#count = packet.count;
      this.#started = now;
    } else if (this.#body && (now - this.#started >= 500 ||
      this.#body.length !== packet.length || this.#count !== packet.count)) {
      this.#body = undefined;
    }

    if (!this.#body) return null;
    const bit = 1 << packet.index;
    const offset = packet.index * chunkSize;
    if (this.#received & bit) {
      if (packet.chunk.some((byte, index) => byte !== this.#body[offset + index])) this.#body = undefined;
      return null;
    }

    this.#body.set(packet.chunk, offset);
    this.#received |= bit;
    if (this.#received !== (1 << this.#count) - 1) return null;

    const complete = this.#body;
    this.#body = undefined;
    return complete;
  }
}

const phases = ["waiting", "serving", "playing"];
const teams = ["left", "right"];
const hex = Array.from({ length: 256 }, (_, index) => index.toString(16).padStart(2, "0"));
function check(valid) {
  if (!valid) throw new RangeError("Invalid realtime snapshot.");
}

export function readSnapshot(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length > maxPayloadSize) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 0;
  function take(size) {
    check(offset + size <= bytes.length);
    const start = offset;
    offset += size;
    return start;
  }
  const byte = () => view.getUint8(take(1));
  const short = () => view.getUint16(take(2), true);
  const integer = () => Number(view.getBigInt64(take(8), true));
  const safe = () => {
    const value = integer();
    check(Number.isSafeInteger(value) && value >= 0);
    return value;
  };
  const number = () => {
    const value = view.getFloat64(take(8), true);
    check(Number.isFinite(value));
    return value;
  };
  const paddle = () => ({ y: number(), velocity: number() });
  function list(minimum, maximum, read) {
    const count = byte();
    check(count >= minimum && count <= maximum);
    return Array.from({ length: count }, read);
  }
  function uniquePositive(seen) {
    const id = safe();
    check(id > 0 && !seen.has(id));
    seen.add(id);
    return id;
  }

  try {
    const tick = safe();
    const leftScore = safe();
    const rightScore = safe();
    const phase = phases[byte()];
    const serveTicksRemaining = short();
    check(phase !== undefined && serveTicksRemaining <= 60);
    const leftPaddle = paddle();
    const rightPaddle = paddle();
    const ballIds = new Set();
    const balls = list(1, 10, () => ({
      id: uniquePositive(ballIds), x: number(), y: number(), velocityX: number(), velocityY: number(),
    }));
    const pickupIds = new Set();
    const pickups = list(0, 12, () => {
      const id = uniquePositive(pickupIds);
      const x = number();
      const y = number();
      const amount = byte();
      const remainingTicks = short();
      check(amount >= 1 && amount <= 10 && remainingTicks <= 3600);
      return { id, x, y, amount, remainingTicks };
    });

    function optionalTick() {
      const value = integer();
      check(Number.isSafeInteger(value) && value >= -1 && value <= tick);
      return value === -1 ? null : value;
    }

    const playerIds = new Set();
    const players = list(0, 64, () => {
      const start = take(16);
      const id = Array.from(bytes.subarray(start, start + 16), value => hex[value]).join("");
      check(id !== "0".repeat(32) && !playerIds.has(id));
      playerIds.add(id);
      const playerId = [id.slice(0, 8), id.slice(8, 12), id.slice(12, 16), id.slice(16, 20), id.slice(20)].join("-");
      const nameLength = byte();
      check(nameLength >= 1 && nameLength <= 10);
      const nameStart = take(nameLength);
      const displayName = String.fromCharCode(...bytes.subarray(nameStart, nameStart + nameLength));
      const team = teams[byte()];
      const lastProcessedSequence = safe();
      const recentPushCount = short();
      const lastUpTick = optionalTick();
      const lastDownTick = optionalTick();
      const latency = short();
      check(validName(displayName) && team !== undefined && recentPushCount <= 100 &&
        (latency <= 2000 || latency === 65535));
      return { playerId, displayName, team, lastProcessedSequence, recentPushCount, lastUpTick, lastDownTick,
        latencyMilliseconds: latency === 65535 ? null : latency };
    });
    check(offset === bytes.length);
    return { arena: { tick, leftScore, rightScore, phase, serveTicksRemaining, leftPaddle, rightPaddle, balls, pickups },
      players };
  } catch (error) {
    if (error instanceof RangeError) return null;
    throw error;
  }
}
