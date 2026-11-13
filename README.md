# Pongsole

Pongsole is multiplayer Pong in your browser or terminal. Everyone on a team controls the
same paddle. New players join the smaller team, with ties decided randomly.
The game serves when both teams have a player and keeps score until the server
stops.

## Play locally

Install .NET SDK **11.0.100**, as pinned in `global.json`. From the repository
root, start the server:

```sh
dotnet run --project src/Pongsole.Server --launch-profile http
```

Open [http://localhost:5187](http://localhost:5187) in your browser, enter a
display name, and join. Open another browser tab or join from an interactive
terminal to start a match. A third player shares one team's paddle.

```sh
dotnet run --project src/Pongsole.Client -- --server http://localhost:5187 --name Bob
```

Browser and terminal players share the same arena and scores. Use **W/S** or
**↑/↓** to move, or press and hold **Up/Down** on the browser's touch controls.
Select **Leave** in the browser or press **Q/Esc** in the terminal to leave.
Use a terminal at least **80×24**. A smaller terminal shows a resize prompt and
pauses movement input. If you omit either terminal option, the client prompts
for it.

Clients retry after a connection drop. A player can resume the same session
within 60 seconds while the server stays running. Reloading the browser page
or selecting **Leave** clears that tab's player session.

The server defaults to Loopback mode and accepts connections from the same
machine without a code. Stop the server with **Ctrl+C**.

## Run with Aspire

With the Aspire CLI installed, run this from the repository root:

```sh
aspire run --apphost src/Pongsole.AppHost/Pongsole.AppHost.csproj
```

In the Aspire dashboard, select **Play Pongsole** under the `server` resource
to open the browser game. Use the same HTTP origin with `--server` to join
from a terminal. Stop the
foreground AppHost with **Ctrl+C**.

### Play from dashboard terminals

Start `terminal-1` and `terminal-2` from the dashboard when you want two
terminal players. For each resource, open **Console logs** and select the
**Terminal** view, or open the terminal from its resource menu. Each resource
runs one player. Multiple views of the same terminal share that player.
You can open a separate browser window for each terminal.

Use **W/S** or **↑/↓** to move. Keep the terminal at least **80×24**.
Press **Q/Esc** to leave and exit the resource. Select **Start** to rejoin.
Closing a terminal view leaves its player running.

## Host a private game over HTTPS

The Docker Compose example runs the server on an internal port and publishes
only Caddy on ports 80 and 443. It requires Docker Compose with Linux containers,
a hostname you control, and a shared code you choose.

1. Copy `.env.example` to `.env`. Set `DOMAIN` to the hostname and fill
   `PONGSOLE_SHARED_CODE` with a strong code. Share the code privately with
   players and keep `.env` out of Git.
2. For automatic public TLS, point the hostname's DNS records at the host and
   make TCP ports 80 and 443 reachable. For a LAN hostname, configure Caddy with
   a certificate trusted by every client, or add `tls internal` to the site
   block and install Caddy's local root CA on each client. Use the hostname
   covered by the certificate.
3. Start the services from the repository root.

   ```sh
   docker compose up --build -d
   ```

4. Open your HTTPS origin in a browser, enter your display name and the shared
   code, and join. A browser session can keep access across tabs and page
   reloads while its access cookie is valid. If access expires, enter the
   code again. Terminal players use the same HTTPS origin:

   ```sh
   dotnet run --project src/Pongsole.Client -- --server https://pong.example.com --name Alice
   ```

   The terminal client prompts for the code without echoing it unless
   `PONGSOLE_SHARED_CODE` is already set in the client's environment. Private
   mode requires the code even on localhost.

Stop the services with `docker compose down`. Keep the Caddy volumes to retain
certificate data. Scores and session state are held in memory and reset when the
server stops.

For a different reverse proxy, set `Pong__Mode=Private`, provide
`PONGSOLE_SHARED_CODE`, and configure `Pong__KnownProxies__0` with the immediate
proxy's IP address. The proxy must support WebSockets and forward the original
HTTPS scheme. The example trusts only Caddy at `172.30.52.2`. Use an unused
subnet and update the Compose network and trusted IP together if needed. Clients
validate the server certificate. The browser page and its WebSocket connection use the same HTTPS origin.

## Build and test

```sh
dotnet restore Pongsole.slnx
dotnet build Pongsole.slnx --no-restore
dotnet test Pongsole.slnx --no-build
```

The .NET build, publish, and server runtime use the checked-in browser assets.
To verify the vendored SignalR files and run the browser unit tests, use Node.js
26 with npm:

```sh
npm ci --ignore-scripts
node scripts/vendor-signalr.mjs --check
node --test "tests/Pongsole.Browser.Tests/*.test.mjs"
```

The GitHub Actions workflow runs the .NET and browser checks on Windows, Linux,
and macOS.
