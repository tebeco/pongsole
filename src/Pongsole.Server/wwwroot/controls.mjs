import { keyDirection } from "./movement.mjs";

function isEditable(target) {
  return target?.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target?.tagName);
}

export function bindControls(movement, buttons, window, document) {
  window.addEventListener("keydown", event => {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || isEditable(event.target)) return;
    const direction = keyDirection(event.key);
    if (!direction || !movement.enabled()) return;
    event.preventDefault();
    if (!event.repeat) movement.press("key:" + (event.code || event.key.toLowerCase()), direction);
  });
  window.addEventListener("keyup", event => movement.release("key:" + (event.code || event.key.toLowerCase())));
  window.addEventListener("blur", () => movement.clear());
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) movement.clear();
  });

  for (const button of buttons) {
    const direction = button.dataset.direction;
    button.addEventListener("pointerdown", event => {
      if (event.button !== 0 || !movement.enabled()) return;
      button.setPointerCapture(event.pointerId);
      movement.press("pointer:" + event.pointerId, direction);
    });
    button.addEventListener("pointerup", event => movement.release("pointer:" + event.pointerId));
    button.addEventListener("pointercancel", () => movement.clear());
    button.addEventListener("lostpointercapture", event => {
      if (movement.has("pointer:" + event.pointerId)) movement.clear();
    });
    button.addEventListener("click", event => {
      if (event.detail === 0 && !movement.isHeld(direction === "up" ? "down" : "up")) movement.pulse(direction);
    });
  }
}
