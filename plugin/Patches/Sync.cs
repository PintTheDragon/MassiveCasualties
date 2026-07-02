using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Network;

namespace MassiveCasualties.Patches;

[HarmonyPatch(typeof(NetObjectRegistry))]
internal class NetObjectRegistryPatch
{
    /// <summary>
    ///     There's a bug where non-networked objects are added to the server.
    ///     I'm pretty sure it's caused by a race condition in BuildingEntity_Start_MultiplayerPatch.Postfix,
    ///     but the fix is to just not add non-networked objects (which is already what the client does).
    /// </summary>
    [HarmonyPatch(nameof(NetObjectRegistry.Server_EnsureItemIsNetworkRegistered))]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> FixNonNetworkedObjects(IEnumerable<CodeInstruction> instructions,
        ILGenerator iLGenerator)
    {
        return new CodeMatcher(instructions, iLGenerator)
            .Start()
            .CreateLabel(out var originalFunction)
            .InsertAndAdvance(
                // v = ObjectCanBeIgnoredForNetwork(go)
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(
                    OpCodes.Call,
                    SymbolExtensions.GetMethodInfo(() => NetObjectRegistry.ObjectCanBeIgnoredForNetwork(null))
                ),
                // if !v, continue running
                new CodeInstruction(OpCodes.Brfalse_S, originalFunction),
                // otherwise, return null
                new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Ret)
            )
            .Instructions();
    }
}

[HarmonyPatch(typeof(ItemOrBuildingCoolDeltaCompressablePacket))]
internal class ItemBuildingSyncPacketPatch
{
    [HarmonyPatch(nameof(ItemOrBuildingCoolDeltaCompressablePacket.WriteObjectIntoPacket))]
    [HarmonyPostfix]
    private static void WriteObjectIntoPacket(ref ItemOrBuildingCoolDeltaCompressablePacket __instance, SyncInfo si)
    {
        ObjectSync.UpdatePacket(ref __instance, si);
    }

    [HarmonyPatch(nameof(ItemOrBuildingCoolDeltaCompressablePacket.ReadPacketIntoObject))]
    [HarmonyPostfix]
    private static void ReadPacketIntoObject(ref ItemOrBuildingCoolDeltaCompressablePacket __instance, SyncInfo si)
    {
        ObjectSync.ApplyPacket(__instance, si);
    }
}