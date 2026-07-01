using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using MassiveCasualties.Behaviors;
using MassiveCasualties.Network;

namespace MassiveCasualties.Patches;

/// <summary>
///     Sends the player's data from an existing session on join.
/// </summary>
[HarmonyPatch]
internal class WorldPlacePlayerPatch
{
    // TODO: Saved player state must be cleared before Body_Start_MultiplayerPatch or
    //       else this will cause problems!

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        // WorldgenPatches.Patched_WorldPlacePlayer compiler generated type.
        var type = AccessTools.Inner(typeof(WorldgenPatches), "<Patched_WorldPlacePlayer>d__49");
        return AccessTools.Method(type, "MoveNext");
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        // KrokoshaScavMultiplayer.Client_SendSimpleMessageToServer((ushort) 10167, WorldGeneration.unchipped, true);
        //      IL_005a: ldc.i4       10167 // 0x000027b7
        //      IL_005f: stloc.1      // V_1
        //      IL_0060: ldloca.s     V_1
        //      IL_0062: call         bool ['Assembly-CSharp']WorldGeneration::get_unchipped()
        //      IL_0067: ldc.i4.1
        //      IL_0068: call         void KrokoshaCasualtiesMP.KrokoshaScavMultiplayer::Client_SendSimpleMessageToServer(unsigned int16&, bool, bool)
        var matcher = new CodeMatcher(instructions, generator)
            .MatchForward(false, new CodeMatch(OpCodes.Ldc_I4, 10167))
            .ThrowIfInvalid("Failed to find WorldPlacePlayer call!")
            .InsertAndAdvance(new CodeInstruction(OpCodes.Call,
                SymbolExtensions.GetMethodInfo(() => SendWorldPlacePlayer())));

        while (!(matcher.Instruction.opcode == OpCodes.Call
                 && (MethodInfo)matcher.Instruction.operand ==
                 SymbolExtensions.GetMethodInfo(() =>
                     KrokoshaScavMultiplayer.Client_SendSimpleMessageToServer(0, false, false))))
        {
            matcher.RemoveInstruction();
        }

        matcher.RemoveInstruction();

        return matcher.Instructions();
    }

    private static void SendWorldPlacePlayer()
    {
        // TODO: Does this need custom handling for moving between layers?

        if (SaveManager.LastSessionSave != null && LobbyManager.IsMcLobby)
        {
            try
            {
                var saveData = SaveSystem.Zip(SaveManager.SerializeSave(SaveManager.LastSessionSave));

                var writer = Net.CreateWriter((ushort)MessageType.WorldPlacePlayerWithSave);
                writer.PutBytesWithLength(saveData);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, in writer);

                // Success.
                return;
            }
            catch (Exception e)
            {
                // TODO: Inform player?
                Plugin.Logger.LogError($"Failed to send world place player with save data: {e}");

                // We can still try to connect normally.
            }
        }

        // Normal WorldPlacePlayer message.
        KrokoshaScavMultiplayer.Client_SendSimpleMessageToServer(10167, WorldGeneration.unchipped, true);
    }
}