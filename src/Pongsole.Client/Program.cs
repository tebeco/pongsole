using Pongsole.Client;

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancel;

try
{
    ClientOptions options;
    try
    {
        options = ClientOptions.Parse(args);
    }
    catch (ArgumentException error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
    var app = new ClientApplication(new ConsoleTerminal(), TimeProvider.System,
        (endpoint, code, snapshot) => new GameConnection(endpoint, code, snapshot));
    await app.RunAsync(options, Environment.GetEnvironmentVariable("PONGSOLE_SHARED_CODE"), shutdown.Token);
    return 0;
}
catch (ClientFailure error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine("Pongsole could not continue. Check the terminal and server connection.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}
