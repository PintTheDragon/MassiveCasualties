using System;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib.Utils;
using MassiveCasualties.Behaviors;
using Newtonsoft.Json.Linq;

namespace MassiveCasualties.Network;

internal class Worldgen
{
    /// <summary>
    ///     Based on WorldgenPatches.ServerReceiver_WorldPlacePlayer.
    /// </summary>
    [ServerReceiver((ushort)MessageType.WorldPlacePlayerWithSave)]
    internal static void Server_WorldPlacePlayerWithSave(knetid clientId, ref NetDataReader reader)
    {
        if (!Util.IsInWorld()) return;

        NetPlayer plr;
        if (!NetPlayer.TryGetPlayerFromClientId(clientId, out plr) ||
            plr.server_plrstate.did_give_spawn_location)
        {
            return;
        }

        try
        {
            var saveData = reader.GetBytesWithLength();
            var parsedData = JObject.Parse(SaveSystem.Unzip(saveData));

            if (!SaveManager.LoadSaveForPlayer(plr.playerbody, parsedData))
            {
                // TODO: Inform the client so they can retry / otherwise not lose all their stuff.
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Error parsing JSON for WorldPlacePlayerWithSave: {e}");
        }

        plr.ResetEntropy();

        if (plr.body != null) plr.unchipped = plr.playerbody.unchipped;

        plr.server_plrstate.did_give_spawn_location = true;
        plr.StartCoroutine(ServerMain.HeyPlayerJustJoinedGiveHimASpawnLocationOkay(clientId));
        WorldChunkSync.singleton.timer_TilemapSync += 0.05f;
        WorldChunkSync.singleton.timer_TilemapFluidSync += 0.05f;
    }
}