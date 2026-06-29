using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace MassiveCasualties.Patches;

/// <summary>
///     Fixes places where the server's client ID is hardcoded to 0,
///     as host swapping causes it to change.
/// </summary>
[HarmonyPatch]
internal static class HardcodedServer
{
    internal static knetid CurHostID = 0;

    private static readonly MethodInfo ImplicitIDFromUshortCast =
        typeof(knetid).GetMethod("op_Implicit", [typeof(ushort)]);

    private static readonly MethodInfo UshortFromImplicitIDCast =
        typeof(knetid).GetMethod("op_Implicit", [typeof(knetid)]);

    /// <summary>
    ///     Turns all instances of (knetid)0 into CurHostID.
    ///     Errors if the number of replacements is != the expected value.
    /// </summary>
    private static IEnumerable<CodeInstruction> GeneralRewrite(IEnumerable<CodeInstruction> instructions, int expected)
    {
        var count = 0;
        var matcher = new CodeMatcher(instructions);

        while (true)
        {
            matcher.MatchForward(false, new CodeMatch(OpCodes.Ldc_I4_0),
                new CodeMatch(OpCodes.Call, ImplicitIDFromUshortCast));
            if (matcher.IsInvalid) break;

            // Current state:
            // -> ldc.i4.0
            //    call implicit

            // HardcodedServer.curHostID
            matcher.SetAndAdvance(OpCodes.Ldsfld, AccessTools.Field(typeof(HardcodedServer), nameof(CurHostID)));
            // This is an implicit conversion from ushort to knetid, which is no longer needed.
            matcher.RemoveInstruction();

            count++;
        }

        if (count != expected)
            throw new Exception("GeneralRewrite failed, expected " + expected + " rewrites, got " + count);

        return matcher.Instructions();
    }

    [HarmonyPatch(typeof(Net), nameof(Net.Client_Send))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Client_Send(IEnumerable<CodeInstruction> instructions)
    {
        return GeneralRewrite(instructions, 1);
    }

    [HarmonyPatch(typeof(Net), nameof(Net.Server_SendToClients), [
        typeof(DeliveryMethod), typeof(NetDataWriter),
        typeof(IEnumerable<knetid>)
    ], [ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref])]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Server_SendToClients(IEnumerable<CodeInstruction> instructions)
    {
        return GeneralRewrite(instructions, 2);
    }

    [HarmonyPatch(typeof(Net), nameof(Net.Server_SendTo), [
        typeof(DeliveryMethod), typeof(NetDataWriter),
        typeof(knetid)
    ], [ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref])]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Server_SendTo(IEnumerable<CodeInstruction> instructions)
    {
        // There's an if statement here that uses ushorts and not knetid for
        // comparison, so GeneralRewrite doesn't pick it up.
        //     IL_0000: ldarg.2      // clientid
        //     IL_0001: ldobj        KrokoshaCasualtiesMP.knetid
        //     IL_0006: call         unsigned int16 KrokoshaCasualtiesMP.knetid::op_Implicit(valuetype KrokoshaCasualtiesMP.knetid)
        //     IL_000b: brtrue.s     IL_0020

        return new CodeMatcher(GeneralRewrite(instructions, 1))
            .MatchForward(false, new CodeMatch(OpCodes.Call, UshortFromImplicitIDCast))
            .ThrowIfInvalid("Could not find ushort -> knetid in Server_SendTo")
            .Advance(1)
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(HardcodedServer), nameof(CurHostID))),
                new CodeInstruction(OpCodes.Call, UshortFromImplicitIDCast),
                new CodeInstruction(OpCodes.Ceq))
            .SetOpcodeAndAdvance(OpCodes.Brfalse_S)
            .Instructions();
    }

    [HarmonyPatch(typeof(Net), nameof(Net.InvokeClientMessage))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> InvokeClientMessage(IEnumerable<CodeInstruction> instructions)
    {
        //      IL_0036: ldarg.0      // callerclientId
        //      IL_0037: call         unsigned int16 KrokoshaCasualtiesMP.knetid::op_Implicit(valuetype KrokoshaCasualtiesMP.knetid)
        //      IL_003c: brfalse.s    IL_0040

        // GeneralRewrite is still good to have to detect future changes.
        return new CodeMatcher(GeneralRewrite(instructions, 0))
            .MatchForward(false, new CodeMatch(OpCodes.Call, UshortFromImplicitIDCast))
            .ThrowIfInvalid("Could not find ushort -> knetid in InvokeClientMessage")
            .Advance(1)
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(HardcodedServer), nameof(CurHostID))),
                new CodeInstruction(OpCodes.Call, UshortFromImplicitIDCast),
                new CodeInstruction(OpCodes.Ceq))
            .SetOpcodeAndAdvance(OpCodes.Brtrue_S)
            .Instructions();
    }

    [HarmonyPatch(typeof(TransportSteamworks), nameof(TransportSteamworks._K_OnReceiveNetworkingMessage))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> _K_OnReceiveNetworkingMessage(IEnumerable<CodeInstruction> instructions)
    {
        return GeneralRewrite(instructions, 1);
    }

    // TODO: Do a search through the whole codebase for "(ushort) 0" and "(knetid)".

    // TODO: Might be worth patching Net.CreatePlayer and NetPlayer.ApplyNameAndColor
}