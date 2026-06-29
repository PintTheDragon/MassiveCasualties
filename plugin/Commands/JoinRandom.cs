using CUCoreLib.Registries;
using MassiveCasualties.Behaviors;

namespace MassiveCasualties.Commands;

/// <summary>
///     Joins a random session.
/// </summary>
internal static class JoinRandom
{
    internal static void Register()
    {
        ConsoleCommandRegistry.Register("joinrandom", "Joins a random session.", Run);
    }

    private static void Run(string[] args)
    {
        LobbyManager.JoinRandom();
    }
}