import { kind, readPacket, readSnapshot, SnapshotReassembly, writeEcho, writeInput } from "./realtime-codec.mjs";

const connectLimit = 30_000;
const udpUrls = new Set(["turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:443?transport=udp"]);
const controlLimit = 4096;
const sessionPattern = /^[A-Za-z0-9_-]{20,128}$/;
const markers = new Set([
  "PONGSOLE_TURN_NOT_CONFIGURED", "PONGSOLE_TURN_UNAVAILABLE", "PONGSOLE_CAPACITY_REACHED", "PONGSOLE_RTC_RATE_LIMIT",
  "PONGSOLE_INVALID_RESUME_TOKEN", "PONGSOLE_EXPIRED_RESUME_TOKEN", "PONGSOLE_SESSION_ALREADY_CONNECTED",
  "PONGSOLE_INVALID_NAME", "PONGSOLE_ALREADY_JOINED", "PONGSOLE_SERVER_STOPPING",
]);

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function until(promise, signal) {
  if (signal.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

function relayDescription(sdp) {
  if (typeof sdp !== "string" || sdp.length > 65536) throw new Error("Invalid relay description.");
  const lines = sdp.trimEnd().split(/\r?\n/);
  const result = [];
  let candidates = 0;
  for (const line of lines) {
    if (/^a=candidate:/iu.test(line)) {
      const fields = line.split(/ +/u);
      if (fields[2]?.toLowerCase() !== "udp" || fields[6] !== "typ" || fields[7] !== "relay") continue;
      for (let index = 8; index < fields.length; index += 2) {
        if (fields[index] === "raddr") fields[index + 1] = fields[4].includes(":") ? "::" : "0.0.0.0";
        if (fields[index] === "rport") fields[index + 1] = "0";
      }
      result.push(fields.join(" "));
      candidates++;
    } else if (/^[oc]=/u.test(line)) {
      const fields = line.split(/ +/u);
      fields[fields.length - 1] = fields.at(-2) === "IP6" ? "::" : "0.0.0.0";
      result.push(fields.join(" "));
    } else {
      if (/^a=remote-candidates:/iu.test(line)) continue;
      result.push(line);
    }
  }
  if (!candidates) throw new Error("No UDP relay candidate.");
  return result.join("\r\n") + "\r\n";
}

function validateAnswer(sdp) {
  if (typeof sdp !== "string" || sdp.length > 65536) throw new Error("Invalid relay answer.");
  let origins = 0;
  let media = 0;
  let candidates = 0;
  for (const line of sdp.trimEnd().split(/\r?\n/u)) {
    const fields = line.split(/ +/u);
    if (line.startsWith("o=")) origins++;
    if (/^[oc]=/u.test(line)) {
      const zero = fields.at(-2) === "IP6" ? "::" : "0.0.0.0";
      if (!["IP4", "IP6"].includes(fields.at(-2)) || fields.at(-1) !== zero)
        throw new Error("Invalid relay answer address.");
    }
    if (line.startsWith("m=") && (++media !== 1 || fields[0] !== "m=application" ||
        fields[2] !== "UDP/DTLS/SCTP" || fields[3] !== "webrtc-datachannel"))
      throw new Error("Invalid relay answer media.");
    if (/^a=candidate:/iu.test(line)) {
      if (++candidates > 16 || fields[2]?.toLowerCase() !== "udp" || fields[6] !== "typ" || fields[7] !== "relay")
        throw new Error("Invalid relay answer candidate.");
      for (let index = 8; index < fields.length; index += 2) {
        if (fields[index] === "raddr" && !["0.0.0.0", "::"].includes(fields[index + 1]) ||
            fields[index] === "rport" && fields[index + 1] !== "0")
          throw new Error("Invalid relay answer related address.");
      }
    }
    if (/^a=remote-candidates:/iu.test(line)) throw new Error("Invalid relay answer.");
  }
  if (origins !== 1 || media !== 1 || !candidates) throw new Error("Invalid relay answer.");
}

function iceConfiguration(servers) {
  if (!Array.isArray(servers) || servers.length === 0 || servers.length > 16) throw new Error("Invalid relay configuration.");
  return servers.map(server => {
    if (!Array.isArray(server.urls) || !server.urls.length || !server.urls.every(url => udpUrls.has(url)) ||
        typeof server.username !== "string" || !server.username || typeof server.credential !== "string" || !server.credential) {
      throw new Error("Invalid relay configuration.");
    }
    return { urls: server.urls, username: server.username, credential: server.credential };
  });
}

export class RelayGameConnection {
  state = "Disconnected";
  #request;
  #makePeer;
  #controller = new AbortController();
  #handlers = new Map();
  #peer;
  #control;
  #realtime;
  #session;
  #reassembly;
  #joining;
  #joined;
  #latestSnapshot;
  #pending;
  #disconnectTimer;
  #closeHandler;
  #lifetimeTimer;
  #stopTask;

  constructor({ request = (...args) => fetch(...args), makePeer = config => new RTCPeerConnection(config) } = {}) {
    this.#request = request;
    this.#makePeer = makePeer;
  }

  on(method, handler) { this.#handlers.set(method, handler); }
  onclose(handler) { this.#closeHandler = handler; }

  async #call(path, method, signal, body, sessionToken) {
    const headers = { "X-Pong-Browser": "1" };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (sessionToken) headers["X-Pong-Session"] = sessionToken;
    const response = await this.#request(path, {
      method, headers, body: body === undefined ? undefined : JSON.stringify(body),
      signal, cache: "no-store", credentials: "same-origin", mode: "same-origin", redirect: "error",
    });
    if (!response.ok) {
      console.debug("Pongsole relay HTTP status " + response.status);
      const reply = await response.json().catch(() => null);
      throw new Error(markers.has(reply?.code) ? reply.code : "Relay connection unavailable.");
    }
    return method === "DELETE" ? undefined : response.json();
  }

  async start() {
    this.state = "Connecting";
    this.#lifetimeTimer = setTimeout(() => this.#closeUnexpectedly(), 50 * 60 * 1000);
    const signal = AbortSignal.any([this.#controller.signal, AbortSignal.timeout(connectLimit)]);
    let stage = "session request";
    try {
      const session = await this.#call("/rtc/sessions", "POST", signal);
      stage = "session validation";
      if (!sessionPattern.test(session?.sessionId ?? "") || !sessionPattern.test(session?.sessionToken ?? "") ||
          !Number.isSafeInteger(session.epoch) || session.epoch <= 0) throw new Error("Invalid relay session.");
      this.#session = session;
      signal.throwIfAborted();
      this.#reassembly = new SnapshotReassembly(session.epoch);
      stage = "relay peer initialization";
      const peer = this.#peer = this.#makePeer({ iceTransportPolicy: "relay", iceServers: iceConfiguration(session.iceServers) });
      const control = this.#control = peer.createDataChannel("control", { ordered: true });
      const realtime = this.#realtime = peer.createDataChannel("realtime", { ordered: false, maxRetransmits: 0 });
      realtime.binaryType = "arraybuffer";
      control.binaryType = "arraybuffer";
      realtime.bufferedAmountLowThreshold = 0;
      realtime.onbufferedamountlow = () => this.#flush();
      realtime.onmessage = event => this.#readRealtime(event.data);
      control.onmessage = event => this.#readControl(event.data);
      for (const channel of [control, realtime]) channel.onclose = () => this.#closeUnexpectedly();
      peer.onconnectionstatechange = () => {
        clearTimeout(this.#disconnectTimer);
        if (["failed", "closed"].includes(peer.connectionState)) this.#closeUnexpectedly();
        else if (peer.connectionState === "disconnected") this.#disconnectTimer = setTimeout(() => this.#closeUnexpectedly(), 3000);
      };
      const opened = Promise.all([control, realtime].map(channel => new Promise(resolve => {
        if (channel.readyState === "open") resolve();
        else channel.onopen = resolve;
      })));
      stage = "local offer";
      const offer = await until(peer.createOffer(), signal);
      await until(peer.setLocalDescription(offer), signal);
      const gathered = deferred();
      peer.onicegatheringstatechange = () => {
        if (peer.iceGatheringState === "complete") gathered.resolve();
      };
      if (peer.iceGatheringState !== "complete") await until(gathered.promise, signal);
      stage = "relay answer";
      const answer = await this.#call("/rtc/sessions/" + session.sessionId + "/offer", "POST", signal,
        { sdp: relayDescription(peer.localDescription.sdp) }, session.sessionToken);
      stage = "answer validation";
      validateAnswer(answer?.sdp);
      stage = "remote description";
      await until(peer.setRemoteDescription({ type: "answer", sdp: answer.sdp }), signal);
      stage = "data channels";
      await until(opened, signal);
      this.state = "Connected";
      void this.#verifySelectedRelay(peer);
    } catch (error) {
      console.debug("Pongsole relay setup failed at " + stage + " (" + (error instanceof TypeError ? "TypeError" : error instanceof DOMException ? error.name : "request error") + ").");
      await this.stop();
      throw error;
    }
  }

  async #verifySelectedRelay(peer) {
    try {
      const stats = await peer.getStats();
      const transport = [...stats.values()].find(report => report.type === "transport" && report.selectedCandidatePairId);
      const pair = stats.get(transport?.selectedCandidatePairId);
      const local = stats.get(pair?.localCandidateId);
      const remote = stats.get(pair?.remoteCandidateId);
      if (local?.candidateType === "relay" && remote?.candidateType === "relay" &&
          local.protocol === "udp" && remote.protocol === "udp" &&
          (!local.relayProtocol || local.relayProtocol === "udp")) {
        console.debug("Pongsole selected UDP relay candidates at both ends.");
      } else if (pair) {
        this.#closeUnexpectedly();
      }
    } catch {
      // Some browsers do not expose candidate statistics.
    }
  }

  async invoke(method, name, resumeToken) {
    if (method !== "Join" || this.state !== "Connected" || this.#joining) throw new Error("Relay join unavailable.");
    const joining = this.#joining = deferred();
    const signal = AbortSignal.any([this.#controller.signal, AbortSignal.timeout(connectLimit)]);
    let stage = "session request";
    try {
      this.#sendControl({ type: "join", name, resumeToken });
      return await until(joining.promise, signal);
    } finally {
      this.#joining = undefined;
    }
  }

  #sendControl(message) {
    const data = new TextEncoder().encode(JSON.stringify(message));
    if (data.length > controlLimit || this.#control.readyState !== "open" || this.#control.bufferedAmount + data.length > 16384)
      throw new Error("Relay control channel unavailable.");
    this.#control.send(data);
  }

  #readControl(data) {
    if (!(data instanceof ArrayBuffer) || data.byteLength > controlLimit) return;
    try {
      const message = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(data));
      if (message?.type === "error" && this.#joining) {
        this.#joining.reject(new Error(markers.has(message.code) ? message.code : "Relay join rejected."));
      } else if (message?.type === "joined" && this.#joining &&
          typeof message.playerId === "string" && ["left", "right"].includes(message.team) &&
          typeof message.resumeToken === "string" && Number.isSafeInteger(message.lastProcessedSequence) &&
          message.lastProcessedSequence >= 0) {
        this.#joined = message;
        this.#completeJoin();
      }
    } catch {
      this.#closeUnexpectedly();
    }
  }

  #readRealtime(data) {
    if (!(data instanceof ArrayBuffer)) return;
    const packet = readPacket(new Uint8Array(data));
    if (!packet || packet.epoch !== this.#session.epoch) return;
    if (packet.type === kind.probe) {
      if (this.#realtime.bufferedAmount === 0 && this.#realtime.readyState === "open") {
        try { this.#realtime.send(writeEcho(packet.epoch, packet.id)); } catch { this.#closeUnexpectedly(); }
      }
      return;
    }
    const bytes = this.#reassembly.accept(packet, performance.now());
    const snapshot = bytes && readSnapshot(bytes);
    if (!snapshot || snapshot.arena.tick !== packet.id) return;
    this.#latestSnapshot = snapshot;
    this.#completeJoin();
    this.#handlers.get("Snapshot")?.(snapshot);
  }

  #completeJoin() {
    const snapshot = this.#latestSnapshot;
    if (this.#joining && this.#joined && snapshot?.players.some(player => player.playerId === this.#joined.playerId))
      this.#joining.resolve({ ...this.#joined, snapshot });
  }

  async send(method, direction, sequence) {
    if (method !== "SetHeld" || this.state !== "Connected") throw new Error("Relay input unavailable.");
    this.#pending = writeInput(this.#session.epoch, sequence, direction);
    this.#flush();
  }

  #flush() {
    const channel = this.#realtime;
    if (this.#pending && channel?.readyState === "open" && channel.bufferedAmount === 0) {
      const packet = this.#pending;
      this.#pending = undefined;
      try { channel.send(packet); } catch { this.#closeUnexpectedly(); }
    }
  }

  #closeUnexpectedly() {
    if (this.state === "Disconnected") return;
    const wasConnected = this.state === "Connected";
    void this.stop();
    if (wasConnected) this.#closeHandler?.(new Error("Relay connection closed."));
  }

  stop() {
    if (this.#stopTask) return this.#stopTask;
    this.#stopTask = Promise.resolve();
    this.state = "Disconnected";
    this.#controller.abort();
    clearTimeout(this.#disconnectTimer);
    clearTimeout(this.#lifetimeTimer);
    this.#pending = undefined;
    try { if (this.#joined) this.#sendControl({ type: "leave" }); } catch {}
    this.#peer?.close();
    const session = this.#session;
    this.#stopTask = session ? this.#call("/rtc/sessions/" + session.sessionId, "DELETE", AbortSignal.timeout(2000),
      undefined, session.sessionToken).catch(() => {}) : Promise.resolve();
    return this.#stopTask;
  }
}
