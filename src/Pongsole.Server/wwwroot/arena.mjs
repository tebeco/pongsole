const width = 100;
const height = 30;
const radius = 0.5;
const leftFace = 3;
const rightFace = width - leftFace;
const maxLead = 125;
const maxHorizon = 250;
const correctionDuration = 100;
const maxCorrectionDistance = 8;

const clamp = (value, lower, upper) => Math.min(upper, Math.max(lower, value));
const lerp = (from, to, amount) => from + (to - from) * amount;

function bounds(ball) {
  if (ball.velocityX < 0) return [ball.x >= leftFace ? leftFace : -radius, width + radius];
  if (ball.velocityX > 0) return [-radius, ball.x <= rightFace ? rightFace : width + radius];
  return [-radius, width + radius];
}

function project(ball, horizonMs) {
  const [left, right] = bounds(ball);
  const velocity = ball.velocityX;
  const collisionSeconds = velocity === 0 ? Infinity :
    ((velocity < 0 ? left : right) - ball.x) / velocity;
  const seconds = Math.min(horizonMs / 1000, Math.max(0, collisionSeconds));
  const span = height - 2 * radius;
  const period = 2 * span;
  const travelled = ball.y + ball.velocityY * seconds - radius;
  const folded = ((travelled % period) + period) % period;
  return {
    x: clamp(ball.x + velocity * seconds, left, right),
    y: radius + Math.min(folded, period - folded),
  };
}

export class ArenaSnapshots {
  newest = null;
  previous = null;
  receivedAt = 0;
  #lead = 0;
  #corrections = new Map();

  constructor(playerId) {
    this.playerId = playerId;
  }

  accept(snapshot, now) {
    const previous = this.newest?.arena;
    const arena = snapshot.arena;
    if (previous && arena.tick < previous.tick) return false;

    if (arena.tick !== previous?.tick) {
      const displayed = previous?.phase === "playing" && arena.phase === "playing" ? this.frame(now) : null;
      this.previous = previous;
      this.receivedAt = now;
      const rtt = snapshot.players.find(player => player.playerId === this.playerId)?.latencyMilliseconds;
      this.#lead = Number.isFinite(rtt) && rtt >= 0 ? Math.min(maxLead, rtt / 2) : 0;
      this.#corrections.clear();

      if (displayed) {
        const byId = new Map(displayed.balls.map(ball => [ball.id, ball]));
        for (const ball of arena.balls) {
          const old = byId.get(ball.id);
          if (!old || Math.sign(old.velocityX) !== Math.sign(ball.velocityX)) continue;
          const target = project(ball, this.#lead);
          const x = old.x - target.x;
          const y = old.y - target.y;
          if (Math.hypot(x, y) <= maxCorrectionDistance) this.#corrections.set(ball.id, { x, y });
        }
      }
    }

    this.newest = snapshot;
    return true;
  }

  frame(now, reduceMotion = false) {
    const current = this.newest?.arena;
    if (!current || reduceMotion) return current;

    const previous = this.previous;
    const elapsed = Math.max(0, now - this.receivedAt);
    const smoothPaddles = previous && current.phase !== "waiting" && current.phase === previous.phase &&
      current.leftScore === previous.leftScore && current.rightScore === previous.rightScore;
    if (current.phase !== "playing" && !smoothPaddles) return current;

    const amount = Math.min(1, elapsed / 50);
    const horizon = Math.min(maxHorizon, this.#lead + elapsed);
    const blend = Math.max(0, 1 - elapsed / correctionDuration);
    return {
      ...current,
      leftPaddle: smoothPaddles ? { y: lerp(previous.leftPaddle.y, current.leftPaddle.y, amount) } : current.leftPaddle,
      rightPaddle: smoothPaddles ? { y: lerp(previous.rightPaddle.y, current.rightPaddle.y, amount) } : current.rightPaddle,
      balls: current.phase === "playing" ? current.balls.map(ball => {
        const position = project(ball, horizon);
        const correction = this.#corrections.get(ball.id);
        const [left, right] = bounds(ball);
        return {
          ...ball,
          x: clamp(position.x + (correction?.x ?? 0) * blend, left, right),
          y: clamp(position.y + (correction?.y ?? 0) * blend, radius, height - radius),
        };
      }) : current.balls,
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

  for (const pickup of arena?.pickups ?? []) {
    const label = "+" + pickup.amount;
    const seconds = Math.ceil(pickup.remainingTicks / 60) + "s";
    ctx.font = "1.55px ui-monospace, monospace";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#101927";
    ctx.strokeStyle = "#f7d368";
    ctx.lineWidth = 0.15;
    ctx.beginPath();
    ctx.arc(pickup.x, pickup.y, 1.6, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = "#f7d368";
    ctx.fillText(label, pickup.x, pickup.y);
    ctx.font = "1.2px ui-monospace, monospace";
    ctx.fillText(seconds, pickup.x, pickup.y + 2.55);
  }

  ctx.fillStyle = "#eef4ff";
  for (const ball of arena?.balls ?? [{ x: 50, y: 15 }]) {
    ctx.beginPath();
    ctx.arc(ball.x, ball.y, radius, 0, Math.PI * 2);
    ctx.fill();
  }
}
