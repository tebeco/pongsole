# Pongsole

Pongsole is a multiplayer terminal Pong game with one shared paddle per team.

The solution uses .NET SDK 11.0.100, pinned in `global.json`. Source projects
live in `src/` and their test projects live in `tests/`.

```sh
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Run the local server and Aspire 13.6.5 dashboard with `aspire run`. The server's
assigned addresses appear in the dashboard. The console client runs in its
own terminal.

The standalone server's local HTTP launch profile uses `http://localhost:5187`.
