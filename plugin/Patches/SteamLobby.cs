using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace MassiveCasualties.Patches;

/// <summary>
///     Fixes the steam lobby owner not being updated on change.
/// </summary>
[HarmonyPatch(typeof(KSteam))]
internal static class PatchSteamLobbyOwner
{
    private static readonly MethodInfo UpdateLobbyInfo =
        SymbolExtensions.GetMethodInfo(() => KSteam.UpdateLobbyInfo(default, ref KSteam.CURRENT_LOBBY));

    [HarmonyPatch(nameof(KSteam.OnLobbyDataUpdate))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> PreventUnset(IEnumerable<CodeInstruction> instructions)
    {
        // UpdateLobbyInfo sets the new ownerID, but this method reverts
        // it back to the old ID immediately after.
        return new CodeMatcher(instructions)
            .MatchForward(false, new CodeMatch(OpCodes.Call, UpdateLobbyInfo))
            .ThrowIfInvalid("Failed to find UpdateLobbyInfo!")
            .Advance(1)
            // return;
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ret))
            .Instructions();
    }
}