using CUCoreLib.Helpers;
using CUCoreLib.Registries;
using MassiveCasualties.Behaviors;
using Steamworks;

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
        CUCoreUtils.ConsoleCheckForWorld(ConsoleScript.instance);

        var randomWorld = LobbyManager.GetRandom();
        if (randomWorld != CSteamID.Nil)
        {
            LobbyManager.ConnectFromGame(randomWorld);
            return;
        }

        SaveManager.LastWorldSave.Load();

        // TODO: Spawn lobby.
    }
}