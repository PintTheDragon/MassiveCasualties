using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using Steamworks;

namespace MassiveCasualties.Patches;

/// <summary>
///     Prevents host transport disconnect from causing the client to stop the session.
/// </summary>
[HarmonyPatch(typeof(TransportSteamworks))]
internal static class DisconnectPatchTransport
{
    private static readonly MethodInfo OriginalDisconnectMethod =
        SymbolExtensions.GetMethodInfo(() => KrokoshaScavMultiplayer.ShutdownNetwork());

    private static readonly MethodInfo NewDisconnectMethod =
        SymbolExtensions.GetMethodInfo(() => DisconnectReplacement(null));

    [HarmonyPatch(nameof(TransportSteamworks.OnConnectionStatusChanged))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventDisconnect(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchForward(false, new CodeMatch(OpCodes.Call, OriginalDisconnectMethod))
            .ThrowIfInvalid("Failed to find ShutdownNetwork!")
            // DisconnectReplacement(this);
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0))
            .SetAndAdvance(OpCodes.Call, NewDisconnectMethod)
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            .Instructions();
    }

    private static void DisconnectReplacement(TransportSteamworks transportSteamworks)
    {
        ConsoleScript.instance.LogToConsole("Hello World!");

        ServerMain._RegisterServerReceivers();

        // These need to be cleared, otherwise the host won't be able
        // to reconnect.
        transportSteamworks.RemoveSteamUser(KSteam.CURRENT_LOBBY.ownerID.m_SteamID);
        /*transportSteamworks.SteamIDToClientIDDict.RemoveByFirst(KSteam.CURRENT_LOBBY.ownerID.m_SteamID);
        transportSteamworks.SteamIDToNetPlayerDict.Remove(KSteam.CURRENT_LOBBY.ownerID.m_SteamID);*/
        // TODO: This is a hack to make it work for right now, but
        //       in reality, the host is chosen by Steam, so we need to
        //       check later (probably in a loop) if we become the host.
        Net.type = Net.NetType.Host;

        var ply = transportSteamworks.SteamIDToNetPlayerDict[KSteam.GetLocalUserSteamID().m_SteamID];
        ply.is_host = true;
        ply.is_local = true;
        ply.server_plrstate = new Server_PlayerState(ply);

        // TODO: Figure out a better place to spawn players.
        Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
        Body_PlaceBody_MultiplayerPatch.spawnlocation = ply.body.transform.position;

        HardcodedServer.curHostID = ply.clientId;

        if (transportSteamworks.listenSocket != HSteamListenSocket.Invalid)
        {
            if (transportSteamworks.is_steamserver)
                SteamGameServerNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);
            else
                SteamNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);
        }

        transportSteamworks.CloseP2PSessions();

        transportSteamworks.CreateServerSocket();
    }
}

/// <summary>
///     Prevents host chat disconnect from causing the client to stop the session.
/// </summary>
[HarmonyPatch(typeof(KSteam))]
internal static class DisconnectPatchChat
{
    private static readonly MethodInfo NewDisconnectMethod =
        SymbolExtensions.GetMethodInfo(() => DisconnectReplacement());

    [HarmonyPatch(nameof(KSteam.OnLobbyChatUpdate))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventDisconnect(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchForward(false, new CodeMatch(OpCodes.Ldstr, "LOBBY OWNER LEFT, LEAVING"))
            .ThrowIfInvalid("Failed to find chat shutdown!")
            // DisconnectReplacement();
            .InsertAndAdvance(new CodeInstruction(OpCodes.Call, NewDisconnectMethod))
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            // TODO: Still run KSteam.UpdateLobbyInfo to reflect the disconnection?
            .Instructions();
    }

    private static void DisconnectReplacement()
    {
        ConsoleScript.instance.LogToConsole("Hello World 2!");
    }
}