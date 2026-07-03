using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Behaviors;
using Steamworks;

namespace MassiveCasualties.Patches;

/// <summary>
///     Fixes the steam lobby owner not being updated on change.
/// </summary>
[HarmonyPatch(typeof(KSteam))]
internal static class PatchSteamLobby
{
    private static readonly MethodInfo UpdateLobbyInfo =
        SymbolExtensions.GetMethodInfo(() => KSteam.UpdateLobbyInfo(default, ref KSteam.CURRENT_LOBBY));

    private static readonly MethodInfo GetLobbyID =
        typeof(KSteam).GetMethod("get_lobbyId");

    private static readonly MethodInfo SetLobbyData =
        SymbolExtensions.GetMethodInfo(() => SteamMatchmaking.SetLobbyData(default, null, null));

    private static readonly MethodInfo AddLobbyDataPrefixMethod =
        SymbolExtensions.GetMethodInfo(() => AddLobbyDataPrefix());

    private static bool _initRan;

    [HarmonyPatch(nameof(KSteam.OnLobbyDataUpdate))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventOwnerUnset(IEnumerable<CodeInstruction> instructions)
    {
        // UpdateLobbyInfo sets the new ownerID, but this method reverts
        // it back to the old ID immediately after.
        // TODO: There might be a reason for this, it looks like KSteam.UpdateLobbyInfo updates
        //       players during lobby search for whatever reason, and this might be a patch to fix
        //       an issue caused by that, but I can't tell. Might be worthwhile rewriting UpdateLobbyInfo.
        return new CodeMatcher(instructions)
            .MatchForward(false, new CodeMatch(OpCodes.Call, UpdateLobbyInfo))
            .ThrowIfInvalid("Failed to find UpdateLobbyInfo!")
            .Advance(1)
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            .Instructions();
    }

    /// <summary>
    ///     This patch is called directly as well, because
    ///     KSteam.Init runs very early and may happen before this
    ///     plugin is loaded.
    /// </summary>
    [HarmonyPatch(nameof(KSteam.Init))]
    [HarmonyPostfix]
    internal static void Init()
    {
        if (_initRan || !KSteam.Loaded) return;
        _initRan = true;

        LobbyManager.Init();
        NewLobbyHost.Init();
    }

    [HarmonyPatch(nameof(KSteam.Server_UpdateLobbyData))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> UpdateLobby1(IEnumerable<CodeInstruction> instructions)
    {
        //    IL_005e: call         valuetype [Steamworks.NET]Steamworks.CSteamID KrokoshaCasualtiesMP.KSteam::get_lobbyId()
        //    IL_0063: ldstr        "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LOBBYNAME"
        //    IL_0068: ldsfld       class KrokoshaCasualtiesMP.NetPublicServerInfo KrokoshaCasualtiesMP.Net::MY_SERVER_INFO
        //    IL_006d: ldfld        string KrokoshaCasualtiesMP.NetPublicServerInfo::name
        //    IL_0072: call         bool [Steamworks.NET]Steamworks.SteamMatchmaking::SetLobbyData(valuetype [Steamworks.NET]Steamworks.CSteamID, string, string)
        //    IL_0077: pop

        var matcher = new CodeMatcher(instructions).MatchForward(false, new CodeMatch(OpCodes.Call, SetLobbyData))
            .ThrowIfInvalid("Failed to find SetLobbyData!")
            .MatchBack(false, new CodeMatch(OpCodes.Call, GetLobbyID))
            .ThrowIfInvalid("Failed to find GetLobbyID!");

        // This might be after an if statement, so labels need to be fixed.
        var labels = new List<Label>(matcher.Labels);
        matcher.Labels.Clear();

        matcher.Insert(new CodeInstruction(OpCodes.Call, AddLobbyDataPrefixMethod));
        matcher.AddLabels(labels);

        return matcher.Instructions();
    }

    [HarmonyPatch(nameof(KSteam.OnLobbyCreated))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> UpdateLobby2(IEnumerable<CodeInstruction> instructions)
    {
        return UpdateLobby1(instructions);
    }

    /// <summary>
    ///     Runs as a prefix (i.e., before the original SetLobbyData calls).
    /// </summary>
    private static void AddLobbyDataPrefix()
    {
        if (LobbyManager.IsMcLobby)
        {
            SteamMatchmaking.SetLobbyData(KSteam.lobbyId, "CASUALTIESUNKNOWN_MASSIVECASUALTIES_VERSION",
                Plugin.ModVersion);
        }
    }

    /// <summary>
    ///     This handles some of the transport creation process, which
    ///     for lobbies that we own, is handled internally.
    ///     Fixes an issue where creating a new lobby creates a new listen socket
    ///     and brings down the network.
    /// </summary>
    [HarmonyPatch(nameof(KSteam.OnLobbyEnter))]
    [HarmonyPrefix]
    private static bool OnLobbyEnter(ref LobbyEnter_t pCallback)
    {
        if (pCallback.m_ulSteamIDLobby == CSteamID.Nil.m_SteamID) return true;

        return !NewLobbyHost.OwnsLobby((CSteamID)pCallback.m_ulSteamIDLobby);
    }
}

[HarmonyPatch(typeof(TransportSteamworks))]
internal static class TransportSteamworksPatch
{
    /// <summary>
    ///     If we already have a lobby available, uses it
    ///     instead of creating a new one.
    /// </summary>
    [HarmonyPatch(nameof(TransportSteamworks.CreateLobby))]
    [HarmonyPrefix]
    private static bool CreateLobby(ref bool __result)
    {
        var lobby = NewLobbyHost.TakeLobby();
        if (lobby == null) return true;

        __result = true;

        KSteam.OnLobbyCreated(lobby.Value, false);

        return false;
    }
}