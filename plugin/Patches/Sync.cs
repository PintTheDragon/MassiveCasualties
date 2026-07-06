using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Behaviors;
using MassiveCasualties.Network;
using UnityEngine;

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

/// <summary>
///     Hooks into the existing sync system to make it work for custom
///     buildings/items.
/// </summary>
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

// TODO: Investigate this.
/// <summary>
///     This fixes a bug where buildings get destroyed when they come
///     into view.
///     It happens when going into the same pair of teleporters three
///     times.
///     I'm not sure what causes it, but a really simple fix is to just
///     not do this on the client.
/// </summary>
[HarmonyPatch(typeof(BuildingEntity))]
internal class BuildingEntitySyncPatch
{
    [HarmonyPatch(nameof(BuildingEntity.CheckSeating))]
    [HarmonyPrefix]
    private static bool CheckSeating()
    {
        if (LobbyManager.IsMcLobby && Net.is_client) return false;

        return true;
    }
}

/// <summary>
///     Prevents dropping items when players disconnect,
///     since they're responsible for managing their own
///     saves, and this just creates duplicate items.
/// </summary>
[HarmonyPatch(typeof(NetPlayer))]
internal class NetPlayerSyncPatch
{
    [HarmonyPatch(nameof(NetPlayer.OnDestroy))]
    [HarmonyPrefix]
    private static void OnDestroy(NetPlayer __instance)
    {
        if (!LobbyManager.IsMcLobby) return;

        var ply = __instance.playerbody;
        if (ply == null) return;

        for (var slot = 0; slot < ply.body.slots.Length; slot++)
        {
            var item = ply.body.GetItem(slot);
            if (item != null)
            {
                Object.DestroyImmediate(item.gameObject);
            }
        }
    }
}