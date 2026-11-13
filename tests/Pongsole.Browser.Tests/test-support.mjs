export class FakeClock {
  current = 0;
  serial = 0;
  timers = new Map();

  now = () => this.current;
  set = (callback, delay) => {
    const id = ++this.serial;
    this.timers.set(id, { callback, at: this.current + delay });
    return id;
  };
  clear = id => this.timers.delete(id);

  async advance(elapsed) {
    const end = this.current + elapsed;
    while (true) {
      const next = [...this.timers].sort((a, b) => a[1].at - b[1].at)[0];
      if (!next || next[1].at > end) break;
      const [id, timer] = next;
      this.current = timer.at;
      this.timers.delete(id);
      await timer.callback();
      await flush();
    }
    this.current = end;
    await flush();
  }
}

export async function flush() {
  for (let index = 0; index < 8; index++) await Promise.resolve();
}

export function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((accept, fail) => { resolve = accept; reject = fail; });
  return { promise, resolve, reject };
}
