const pulseInterval = 100;
const maxQueue = 10;
const sendWindow = 1_000;

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
  #queue = [];
  #sendTimes = [];
  #holdTimer;
  #sendTimer;
  #sending = false;
  #sendGeneration = 0;

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
    return up === down ? undefined : up ? "up" : "down";
  }

  press(id, direction) {
    if (!this.enabled() || this.#held.has(id)) return;
    const previous = this.#direction;
    this.#held.set(id, direction);
    this.#updateHold(previous);
  }

  release(id) {
    const previous = this.#direction;
    if (this.#held.delete(id)) this.#updateHold(previous, false);
  }

  pulse(direction) {
    if (!this.enabled() || this.#queue.length >= maxQueue) return;
    this.#queue.push(direction);
    this.#drain();
  }

  #updateHold(previous, initial = true) {
    this.changed();
    const direction = this.#direction;
    if (direction === previous) return;
    this.time.clear(this.#holdTimer);
    this.#holdTimer = undefined;
    if (!direction) {
      if (this.#held.size) this.#queue = [];
      return;
    }
    if (initial) this.pulse(direction);
    this.#holdTimer = this.time.set(() => this.#repeat(), pulseInterval);
  }

  #repeat() {
    this.#holdTimer = undefined;
    const direction = this.#direction;
    if (!this.enabled() || !direction) return;
    this.pulse(direction);
    this.#holdTimer = this.time.set(() => this.#repeat(), pulseInterval);
  }

  #drain() {
    if (this.#sending || this.#sendTimer !== undefined || !this.#queue.length || !this.enabled()) return;
    const now = this.time.now();
    this.#sendTimes = this.#sendTimes.filter(sent => now - sent < sendWindow);
    const lastSend = this.#sendTimes.at(-1) ?? -Infinity;
    const delay = Math.max(lastSend + pulseInterval - now,
      this.#sendTimes.length >= 10 ? this.#sendTimes[0] + sendWindow - now : 0);
    if (delay > 0) {
      this.#sendTimer = this.time.set(() => {
        this.#sendTimer = undefined;
        this.#drain();
      }, delay);
      return;
    }

    this.#sendTimes.push(now);
    this.#sending = true;
    const generation = this.#sendGeneration;
    void this.#sendNext(this.#queue.shift(), generation);
  }

  async #sendNext(direction, generation) {
    try {
      await this.send(direction);
    } finally {
      if (generation === this.#sendGeneration) {
        this.#sending = false;
        this.#drain();
      }
    }
  }

  clear(disconnected = false) {
    this.#held.clear();
    this.#queue = [];
    this.time.clear(this.#holdTimer);
    this.time.clear(this.#sendTimer);
    this.#holdTimer = this.#sendTimer = undefined;
    if (disconnected) {
      this.#sendGeneration++;
      this.#sending = false;
    }
    this.changed();
  }
}
