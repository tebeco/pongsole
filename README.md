# Pongsole

Pongsole is multiplayer Pong in the terminal. Everyone on a team controls the
same paddle. New players join the smaller team, with ties decided randomly.
The game serves when both teams have a player and keeps score until the server
stops.

## Play locally

Install .NET SDK **11.0.100**, as pinned in `global.json`. From the repository
root, start the server:

```sh
dotnet run --project src/Pongsole.Server --launch-profile http
```

Start a client in each of two separate interactive terminals. Start a third to
see two players share a paddle.

```sh
dotnet run --project src/Pongsole.Client -- --server http://localhost:5187 --name Alice
dotnet run --project src/Pongsole.Client -- --server http://localhost:5187 --name Bob
dotnet run --project src/Pongsole.Client -- --server http://localhost:5187 --name Charlie
```

Run each line in its own terminal. If you omit `--server` or `--name`, the client
prompts for it. Pass the server origin and the client connects to `/game`.

Use **W/S** or **↑/↓** to move and **Q/Esc** to exit. Use a terminal at least
**80×24**. A smaller terminal shows a resize prompt and pauses movement input.
A disconnected player has 60 seconds to resume the same session while the server
remains running. The client resumes automatically after a connection drop.

The server defaults to Loopback mode and accepts connections from the same
machine without a code. Stop the server with **Ctrl+C**.

## Run with Aspire

With the Aspire CLI installed, run this from the repository root:

```sh
aspire run --apphost src/Pongsole.AppHost/Pongsole.AppHost.csproj
```

The dashboard shows the assigned HTTP and HTTPS addresses of the `server`
resource. Use its HTTP origin with `--server` in each client terminal. Stop the
foreground AppHost with **Ctrl+C**.

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

4. Players connect to the HTTPS origin, for example:

   ```sh
   dotnet run --project src/Pongsole.Client -- --server https://pong.example.com --name Alice
   ```

   The client prompts for the code without echoing it unless
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
validate the server certificate. The code travels in the `X-Pong-Code` header.

## Build and test

```sh
dotnet restore Pongsole.slnx
dotnet build Pongsole.slnx --no-restore
dotnet test Pongsole.slnx --no-build
```

The GitHub Actions workflow runs these checks in Release on Windows, Linux, and
macOS.
