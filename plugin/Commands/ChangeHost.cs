using System.Collections.Generic;
using CUCoreLib.Helpers;
using CUCoreLib.Registries;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Behaviors;
using Steamworks;

namespace MassiveCasualties.Commands;

/// <summary>
///     Allows changing the host to a different user.
/// </summary>
internal static class ChangeHost
{
    internal static void Register()
    {
        ConsoleCommandRegistry.Register("changehost", "Switches the host to a different user.", Run,
            new Dictionary<int, List<string>>(), ("int player", "Player to make the host"));
    }

    private static void Run(string[] args)
    {
        CUCoreUtils.ConsoleCheckForWorld(ConsoleScript.instance);
        if (!Net.running || !Net.is_host || KSteam.CURRENT_LOBBY.lobby_steamID == CSteamID.Nil)
        {
            ConsoleScript.instance.LogToConsole("You must be hosting a game to use this command!");
            return;
        }

        if (Net.TRANSPORT is not TransportSteamworks transportSteamworks || !LobbyManager.IsMcLobby)
        {
            ConsoleScript.instance.LogToConsole("This only works in MassiveCasualties lobbies!");
            return;
        }

        if (args.Length < 2 || !ushort.TryParse(args[1], out var newHostID))
        {
            ConsoleScript.instance.LogToConsole("Please specify a valid integer!");
            return;
        }

        if (!transportSteamworks.SteamIDToClientIDDict.TryGetBySecond(newHostID, out var newHostSteamID))
        {
            ConsoleScript.instance.LogToConsole("Player " + newHostID + " does not exist!");
            return;
        }

        SteamMatchmaking.SetLobbyOwner(KSteam.CURRENT_LOBBY.lobby_steamID, new CSteamID(newHostSteamID));

        ConsoleScript.instance.LogToConsole("Changed host successfully!");
    }
}