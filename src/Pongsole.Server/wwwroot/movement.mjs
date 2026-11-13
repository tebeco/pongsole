const heartbeatInterval = 50;

export function keyDirection(key) {
  switch (key.toLowerCase()) {
    case "w":
    case "arrowup": return "up";
    case "s":
    case "arrowdown": return "down";
    default: return undefined;
  }
}

export class Movement {
  #held = new Map();
  #timer;
  #connected = false;
  #pending;
  #sending = false;
  #generation = 0;

  constructor({ time, enabled, send, changed = () => {} }) {
    this.time = time;
    this.enabled = enabled;
    this.send = send;
    this.changed = changed;
  }

  has(id) { return this.#held.has(id); }

  isHeld(direction) {
    return [...this.#held.values()].includes(direction);
  }

  get #direction() {
    const up = this.isHeld("up");
    const down = this.isHeld("down");
    return up === down ? null : up ? "up" : "down";
  }

  press(id, direction) {
    if (!this.enabled() || this.#held.has(id)) return;
    const previous = this.#direction;
    this.#held.set(id, direction);
    this.#update(previous);
  }

  release(id) {
    const previous = this.#direction;
    if (this.#held.delete(id)) this.#update(previous);
  }

  pulse(direction) {
    if (!this.enabled() || this.#held.size) return;
    this.#queue(direction);
    this.#queue(null);
  }

  setConnected(connected) {
    if (connected === this.#connected) return;
    this.#connected = connected;
    this.time.clear(this.#timer);
    if (connected) this.#scheduleHeartbeat();
  }

  #update(previous) {
    const direction = this.#direction;
    this.changed();
    if (direction !== previous) this.#queue(direction);
  }

  #scheduleHeartbeat() {
    this.#timer = this.time.set(() => {
      if (!this.#connected) return;
      this.#queue(this.enabled() ? this.#direction : null);
      this.#scheduleHeartbeat();
    }, heartbeatInterval);
  }

  #queue(direction) {
    this.#pending = direction;
    this.#drain();
  }

  #drain() {
    if (this.#sending || this.#pending === undefined) return;
    const direction = this.#pending;
    this.#pending = undefined;
    this.#sending = true;
    void this.#sendNext(direction, this.#generation);
  }

  async #sendNext(direction, generation) {
    try {
      await this.send(direction);
    } finally {
      if (generation === this.#generation) {
        this.#sending = false;
        this.#drain();
      }
    }
  }

  clear(disconnected = false) {
    const previous = this.#direction;
    const needsNeutral = previous !== null || this.#pending === null;
    this.#held.clear();
    if (disconnected) {
      this.setConnected(false);
      this.#generation++;
      this.#pending = undefined;
      this.#sending = false;
    } else if (previous !== null) {
      this.#queue(null);
    }
    this.changed();
    return needsNeutral;
  }
}
