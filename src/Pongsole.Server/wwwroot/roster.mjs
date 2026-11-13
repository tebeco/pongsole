export const nameRule = "Enter 1–10 letters (A–Z, a–z) or digits (0–9).";

export function validName(value) {
  return typeof value === "string" && value.length >= 1 && value.length <= 10 && !/[^A-Za-z0-9]/u.test(value);
}

const directions = [["up", "lastUpTick"], ["down", "lastDownTick"]];

export class RosterActivity {
  #players = new Map();
  #canFlash = false;

  update(snapshot, connected, now) {
    const canFlash = connected && !!snapshot && snapshot.arena.phase !== "waiting";
    const players = snapshot?.players ?? [];
    const current = new Map();
    for (const player of players) {
      const previous = this.#players.get(player.playerId);
      const activity = {};
      for (const [direction, field] of directions) {
        const tick = player[field];
        if (canFlash && this.#canFlash && tick != null) {
          const old = previous?.[direction];
          const remaining = Math.max(0, 200 - (snapshot.arena.tick - tick) * 1000 / 60);
          activity[direction] = old?.tick === tick ? old : { tick, deadline: now + remaining };
        } else {
          activity[direction] = { tick, deadline: 0 };
        }
      }
      current.set(player.playerId, activity);
    }
    this.#players = current;
    this.#canFlash = canFlash;
    return players;
  }

  active(playerId, direction, now) {
    return (this.#players.get(playerId)?.[direction]?.deadline ?? 0) > now;
  }

  nextDeadline(now) {
    let next = Infinity;
    for (const player of this.#players.values()) {
      for (const [direction] of directions) {
        const deadline = player[direction].deadline;
        if (deadline > now) next = Math.min(next, deadline);
      }
    }
    return next;
  }
}

export function renderRosters(lists, players, activity, now) {
  for (const [team, list] of Object.entries(lists)) {
    const existing = new Map([...list.children].map(row => [row.dataset.playerId, row]));
    let position = list.firstElementChild;
    for (const player of players) {
      if (player.team !== team) continue;
      let row = existing.get(player.playerId);
      if (!row) {
        row = list.ownerDocument.createElement("li");
        row.dataset.playerId = player.playerId;
        const name = list.ownerDocument.createElement("span");
        name.className = "roster-name";
        row.append(name);
        for (const [direction, arrow, label] of [["up", "↑", "Up"], ["down", "↓", "Down"]]) {
          const icon = list.ownerDocument.createElement("span");
          icon.className = "roster-arrow";
          icon.setAttribute("role", "img");
          icon.setAttribute("aria-label", label);
          icon.dataset.rosterDirection = direction;
          icon.textContent = arrow;
          row.append(icon);
        }
      }
      existing.delete(player.playerId);
      if (row.firstElementChild.textContent !== player.displayName) {
        row.firstElementChild.textContent = player.displayName;
      }
      for (const icon of [...row.children].slice(1)) {
        icon.dataset.active = activity.active(player.playerId, icon.dataset.rosterDirection, now);
      }
      if (row !== position) list.insertBefore(row, position);
      position = row.nextElementSibling;
    }
    for (const row of existing.values()) row.remove();
  }
}
