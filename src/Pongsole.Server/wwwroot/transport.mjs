export class AccessError extends Error {
  constructor(status) {
    super("Arena access request failed.");
    this.status = status;
  }
}

export async function browserSession(signal, code, request = fetch) {
  const options = { credentials: "same-origin", mode: "same-origin", redirect: "error", cache: "no-store", signal };
  if (code !== undefined) {
    options.method = "POST";
    options.headers = { "X-Pong-Browser": "1", "X-Pong-Code": code };
  }

  const response = await request("/browser/session", options);
  if (!response.ok) throw new AccessError(response.status);
  return code === undefined ? response.json() : undefined;
}

export function gameSocket(signalR = globalThis.signalR) {
  return new signalR.HubConnectionBuilder()
    .withUrl("/game", {
      transport: signalR.HttpTransportType.WebSockets,
      skipNegotiation: true,
      withCredentials: true,
      logger: signalR.LogLevel.None,
    })
    .configureLogging(signalR.LogLevel.None)
    .build();
}
