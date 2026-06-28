using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace MassiveCasualties.Patches;

/// <summary>
///     Fixes places where the server's client ID is hardcoded to 0,
///     as host swapping causes it to change.
/// </summary>
[HarmonyPatch(typeof(Net))]
internal static class HardcodedServer
{
    public static knetid curHostID = 0;

    private static readonly MethodInfo InvokeServerMessage =
        SymbolExtensions.GetMethodInfo(() => Net.InvokeServerMessage(0, null));

    [HarmonyPatch(nameof(Net.Client_Send))]
    [HarmonyTranspiler]
    [HarmonyDebug]
    private static IEnumerable<CodeInstruction> Client_Send(IEnumerable<CodeInstruction> instructions)
    {
        // Replace hardcoded 0 with the current steam ID.
        // Safe as this part of the code only runs on the server.

        return new CodeMatcher(instructions)
            .MatchForward(false, new CodeMatch(OpCodes.Call, InvokeServerMessage))
            .ThrowIfInvalid("Couldn't find InvokeServerMessage")
            .MatchBack(false, new CodeMatch(OpCodes.Ldc_I4_0))
            // HardcodedServer.curHostID
            .SetAndAdvance(OpCodes.Ldsfld, AccessTools.Field(typeof(HardcodedServer), nameof(curHostID)))
            // This is an implicit conversion from ushort to knetid, which is no longer needed.
            .RemoveInstruction()
            .Instructions();
    }
}