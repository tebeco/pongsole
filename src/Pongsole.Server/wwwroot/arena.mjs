const width = 100;
const height = 30;

export class ArenaSnapshots {
  newest = null;
  previous = null;
  receivedAt = 0;

  accept(snapshot, now) {
    if (this.newest && snapshot.arena.tick < this.newest.arena.tick) return false;

    if (snapshot.arena.tick !== this.newest?.arena.tick) {
      this.previous = this.newest?.arena;
      this.receivedAt = now;
    }
    this.newest = snapshot;
    return true;
  }

  frame(now, reduceMotion = false) {
    const current = this.newest?.arena;
    const previous = this.previous;
    if (!current || !previous || reduceMotion || current.phase === "waiting" ||
        current.phase !== previous.phase || current.leftScore !== previous.leftScore ||
        current.rightScore !== previous.rightScore) return current;

    const amount = Math.max(0, Math.min(1, (now - this.receivedAt) / 50));
    const lerp = (start, end) => start + (end - start) * amount;
    return {
      ...current,
      leftPaddle: { y: lerp(previous.leftPaddle.y, current.leftPaddle.y) },
      rightPaddle: { y: lerp(previous.rightPaddle.y, current.rightPaddle.y) },
      ball: {
        x: lerp(previous.ball.x, current.ball.x),
        y: lerp(previous.ball.y, current.ball.y),
      },
    };
  }
}

export function drawArena(canvas, arena) {
  const rect = canvas.getBoundingClientRect();
  const pixelWidth = Math.max(1, Math.round(rect.width * devicePixelRatio));
  const pixelHeight = Math.max(1, Math.round(rect.height * devicePixelRatio));
  if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
  if (canvas.height !== pixelHeight) canvas.height = pixelHeight;

  const ctx = canvas.getContext("2d");
  ctx.setTransform(pixelWidth / width, 0, 0, pixelHeight / height, 0, 0);
  ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "#34445b";
  ctx.lineWidth = 0.18;
  ctx.setLineDash([0.55, 0.75]);
  ctx.beginPath();
  ctx.moveTo(50, 0);
  ctx.lineTo(50, height);
  ctx.stroke();
  ctx.setLineDash([]);

  ctx.strokeStyle = "#233349";
  ctx.beginPath();
  ctx.arc(50, 15, 4, 0, Math.PI * 2);
  ctx.stroke();

  ctx.fillStyle = "#53edf3";
  ctx.fillRect(1.5, (arena?.leftPaddle.y ?? 15) - 3, 1, 6);
  ctx.fillStyle = "#ff927d";
  ctx.fillRect(97.5, (arena?.rightPaddle.y ?? 15) - 3, 1, 6);

  ctx.fillStyle = "#eef4ff";
  ctx.beginPath();
  ctx.arc(arena?.ball.x ?? 50, arena?.ball.y ?? 15, 0.5, 0, Math.PI * 2);
  ctx.fill();
}
