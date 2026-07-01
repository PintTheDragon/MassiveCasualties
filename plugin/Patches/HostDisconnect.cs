using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Behaviors;

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

    private static readonly MethodInfo GetIsMcLobby = typeof(LobbyManager).GetMethod(
        "get_" + nameof(LobbyManager.IsMcLobby),
        BindingFlags.Static | BindingFlags.NonPublic);

    [HarmonyPatch(nameof(TransportSteamworks.OnConnectionStatusChanged))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventDisconnect(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        return new CodeMatcher(instructions, generator)
            // Prevent disconnecting when the host leaves, since steam will
            // assign a new host (maybe us).
            // TODO: Need to restrict to only MC lobbies
            .MatchForward(false, new CodeMatch(OpCodes.Call, OriginalDisconnectMethod))
            .ThrowIfInvalid("Failed to find ShutdownNetwork!")
            .CreateLabel(out var originalCode)
            // if (!IsMcLobby) jump to original
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcLobby),
                new CodeInstruction(OpCodes.Brfalse, originalCode))
            // DisconnectReplacement(this);
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0))
            .SetAndAdvance(OpCodes.Call, NewDisconnectMethod)
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            .Instructions();
    }

    /// <summary>
    ///     This happens when either the host actually disconnects from the lobby,
    ///     or the host's connection gets disconnected (which can happen on host change).
    /// </summary>
    private static void DisconnectReplacement(TransportSteamworks transportSteamworks)
    {
        ConsoleScript.instance.LogToConsole("Triggered DisconnectPatchTransport");

        if (!LobbyManager.IsMcLobby)
        {
            KrokoshaScavMultiplayer.ShutdownNetwork();
        }
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

    private static readonly MethodInfo GetIsMcLobby = typeof(LobbyManager).GetMethod(
        "get_" + nameof(LobbyManager.IsMcLobby),
        BindingFlags.Static | BindingFlags.NonPublic);

    [HarmonyPatch(nameof(KSteam.OnLobbyChatUpdate))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventDisconnect(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        return new CodeMatcher(instructions, generator)
            .MatchForward(false, new CodeMatch(OpCodes.Ldstr, "LOBBY OWNER LEFT, LEAVING"))
            .ThrowIfInvalid("Failed to find chat shutdown!")
            .CreateLabel(out var originalCode)
            // if (!IsMcLobby) jump to original
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcLobby),
                new CodeInstruction(OpCodes.Brfalse, originalCode))
            // DisconnectReplacement();
            .InsertAndAdvance(new CodeInstruction(OpCodes.Call, NewDisconnectMethod))
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            // TODO: Still run KSteam.UpdateLobbyInfo to reflect the disconnection?
            .Instructions();
    }

    /// <summary>
    ///     This happens when the host actually leaves the lobby.
    /// </summary>
    private static void DisconnectReplacement()
    {
        if (!Net.TryGetSteamTransport(out var transportSteamworks))
        {
            Plugin.Logger.LogError("DisconnectReplacement couldn't find TransportSteamworks (shouldn't be possible)!");
            return;
        }

        ConsoleScript.instance.LogToConsole("Triggered DisconnectPatchChat");

        // These need to be cleared, otherwise the host won't be able
        // to reconnect.
        // It can't be cleared in OnConnectionStatusChanged, since that would
        // interfere with host swaps.
        // TODO: Sometimes the client disconnects the host (maybe caused here, but I'm unsure).
        transportSteamworks.RemoveSteamUser(KSteam.CURRENT_LOBBY.ownerID.m_SteamID);
    }
}

/// <summary>
///     Informs the HostWatcher when we disconnect.
/// </summary>
[HarmonyPatch(typeof(Net))]
internal static class DisconnectPatchShutdown
{
    [HarmonyPatch(nameof(Net.ShutdownReset))]
    [HarmonyPostfix]
    private static void Postfix()
    {
        HostWatcher.CallOnDisconnect();
    }
}