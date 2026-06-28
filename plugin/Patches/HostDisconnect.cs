using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

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
        ConsoleScript.instance.LogToConsole("Triggered DisconnectPatchTransport");

        // These need to be cleared, otherwise the host won't be able
        // to reconnect.
        transportSteamworks.RemoveSteamUser(KSteam.CURRENT_LOBBY.ownerID.m_SteamID);
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
        ConsoleScript.instance.LogToConsole("Triggered DisconnectPatchChat");
    }
}