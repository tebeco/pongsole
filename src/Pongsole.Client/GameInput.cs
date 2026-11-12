using Pongsole.Contracts;

namespace Pongsole.Client;

internal static class GameInput
{
    public static bool IsExit(ConsoleKey key) => key is ConsoleKey.Q or ConsoleKey.Escape;

    public static Direction? Movement(ConsoleKey key) => key switch
    {
        ConsoleKey.W or ConsoleKey.UpArrow => Direction.Up,
        ConsoleKey.S or ConsoleKey.DownArrow => Direction.Down,
        _ => null
    };
}
