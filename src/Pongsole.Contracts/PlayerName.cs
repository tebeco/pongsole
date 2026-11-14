using System.Diagnostics.CodeAnalysis;

namespace Pongsole.Contracts;

public static class PlayerName
{
    public const int MaximumLength = 10;
    public const int MaximumDisplayLength = 32;
    public const string ValidationMessage = "Enter 1–10 letters (A–Z, a–z) or digits (0–9).";

    public static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= MaximumLength } && value.All(char.IsAsciiLetterOrDigit);

    public static bool IsValidDisplayName([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= MaximumDisplayLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.');
}
