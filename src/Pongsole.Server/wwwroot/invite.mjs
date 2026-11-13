export function initialInviteCode(location) {
  if (location.protocol !== "https:") return undefined;

  const values = new URLSearchParams(location.hash.slice(1)).getAll("code");
  return values.length === 1 && values[0] !== "" ? values[0] : undefined;
}

export function withoutInviteCode(location) {
  const fragment = new URLSearchParams(location.hash.slice(1));
  if (!fragment.has("code")) return undefined;

  fragment.delete("code");
  const hash = fragment.toString();
  return location.origin + location.pathname + location.search + (hash ? "#" + hash : "");
}

export function inviteAccess(request, location, history, clearCode) {
  return async (signal, code) => {
    const result = await request(signal, code);
    if (code !== undefined && !signal.aborted) {
      clearCode();
      const cleaned = withoutInviteCode(location);
      if (cleaned !== undefined) history.replaceState(history.state, "", cleaned);
    }
    return result;
  };
}
