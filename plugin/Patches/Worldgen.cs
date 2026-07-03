using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CUCoreLib.Registries;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using MassiveCasualties.Behaviors;
using MassiveCasualties.Network;
using Newtonsoft.Json.Linq;

namespace MassiveCasualties.Patches;

/// <summary>
///     Sends the player's data from an existing session on join,
///     or loads it directly if it's the host.
/// </summary>
[HarmonyPatch(typeof(WorldgenPatches))]
internal class WorldPlacePlayerPatch
{
    // TODO: Saved player state must be cleared before Body_Start_MultiplayerPatch or
    //       else this will cause problems!

    [HarmonyPatch(nameof(WorldgenPatches.Patched_WorldPlacePlayer), MethodType.Enumerator)]
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

        // The last statement loads in a string.
        matcher.MatchForward(false, new CodeMatch(OpCodes.Ldstr, "WorldGeneration.WorldPlacePlayer"))
            .ThrowIfInvalid("Failed to find the end of WorldPlacePlayer!")
            .InsertAndAdvance(new CodeInstruction(OpCodes.Call,
                SymbolExtensions.GetMethodInfo(() => HostWorldPlacePlayer())));

        return matcher.Instructions();
    }

    private static void SendWorldPlacePlayer()
    {
        // TODO: Does this need custom handling for moving between layers?

        if (SaveManager.LastSessionSave != null && LobbyManager.IsMcLobby)
        {
            try
            {
                var saveData = SaveSystem.Zip(SaveManager.SerializePlayerSave(SaveManager.LastSessionSave));

                var writer = Net.CreateWriter((ushort)MessageType.WorldPlacePlayerWithSave);
                writer.Put(SaveManager.LastWorldSaveLobby.m_SteamID);
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

    private static void HostWorldPlacePlayer()
    {
        if (!KrokoshaScavMultiplayer.network_system_is_running || !Net.is_host ||
            SaveManager.LastSessionSave == null || !LobbyManager.IsMcLobby)
        {
            return;
        }

        SaveManager.LoadSaveForPlayer(NetPlayer.LOCAL_PLAYER.playerbody,
            JObject.FromObject(SaveManager.LastSessionSave));

        WorldSave.PlacePlayer(NetPlayer.LOCAL_PLAYER.playerbody);
    }
}

/// <summary>
///     Prevent spawning in MC buildings when in a non-MC lobby.
/// </summary>
[HarmonyPatch(typeof(BuildingEntityRegistry))]
internal class BuildingEntityRegistryPatch
{
    [HarmonyPatch(nameof(BuildingEntityRegistry.DistributeInWorld))]
    [HarmonyPrefix]
    private static bool DistributeInWorld(string id)
    {
        return !id.StartsWith("mc_") || LobbyManager.IsMcLobby;
    }
}

/// <summary>
///     Lets us apply rules (like saved random state) before
///     worldgen starts on the host, so we can load from a save.
/// </summary>
[HarmonyPatch(typeof(LastBeforeGenerationState))]
internal static class WorldGenSeedPatch
{
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPostfix]
    private static void Constructor(ref LastBeforeGenerationState __instance)
    {
        WorldSave.ApplyWorldgenRules(ref __instance);
    }
}

/// <summary>
/// </summary>
[HarmonyPatch(typeof(WorldGeneration))]
internal static class WorldGenerationPatch
{
    [HarmonyPatch(nameof(WorldGeneration.WorldGenerateTerrain), MethodType.Enumerator)]
    [HarmonyPrefix]
    private static bool WorldGenerateTerrain()
    {
        if (WorldSave.CustomWorldgen())
        {
            WorldSave.PlaceTilesIdempotent();
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(WorldGeneration.WorldGenerateWorldBorders), MethodType.Enumerator)]
    [HarmonyPrefix]
    private static bool WorldGenerateWorldBorders()
    {
        return !WorldSave.CustomWorldgen();
    }

    [HarmonyPatch(nameof(WorldGeneration.WorldPlaceEntities), MethodType.Enumerator)]
    [HarmonyPrefix]
    private static bool WorldPlaceEntities()
    {
        if (WorldSave.CustomWorldgen())
        {
            WorldSave.PlaceEntitiesIdempotent();
            return false;
        }

        return true;
    }
}