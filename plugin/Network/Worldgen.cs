using System;
using System.Collections;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib.Utils;
using MassiveCasualties.Behaviors;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;

namespace MassiveCasualties.Network;

internal class Worldgen
{
    /// <summary>
    ///     Based on WorldgenPatches.ServerReceiver_WorldPlacePlayer.
    /// </summary>
    [ServerReceiver((ushort)MessageType.WorldPlacePlayerWithSave)]
    internal static void Server_WorldPlacePlayerWithSave(knetid clientId, ref NetDataReader reader)
    {
        if (!Util.IsInWorld() || !LobbyManager.IsMcLobby) return;

        NetPlayer plr;
        if (!NetPlayer.TryGetPlayerFromClientId(clientId, out plr) ||
            plr.server_plrstate.did_give_spawn_location)
        {
            return;
        }

        reader.Get(out ulong fromLobby);
        JObject parsedData = null;

        try
        {
            var saveData = reader.GetBytesWithLength();
            parsedData = JObject.Parse(SaveSystem.Unzip(saveData));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Error parsing JSON for WorldPlacePlayerWithSave: {e}");
        }

        plr.ResetEntropy();

        plr.server_plrstate.did_give_spawn_location = true;
        plr.StartCoroutine(PlacePlayer(clientId, fromLobby, parsedData));
        WorldChunkSync.singleton.timer_TilemapSync += 0.05f;
        WorldChunkSync.singleton.timer_TilemapFluidSync += 0.05f;
    }

    /// <summary>
    ///     Handles placing the player (with save / other details) after they
    ///     spawn in, since the WorldPlacePlayer message might come too early.
    /// </summary>
    private static IEnumerator PlacePlayer(knetid clientId, ulong fromLobby, JObject parsedData)
    {
        // Taken from HeyPlayerJustJoinedGiveHimASpawnLocationOkay,
        // to make sure pb is valid.
        NetPlayer plr;
        NetBody pb;
        while (!NetPlayer.TryGetNetPlayerAndNetBodyFromClientId(clientId, out plr, out pb) ||
               !Util.IsWorldGenerated() || !Body_PlaceBody_MultiplayerPatch.has_spawn_location)
        {
            if (plr != null) plr.ResetEntropy();

            yield return null;
        }

        if (plr != null)
        {
            try
            {
                if (!SaveManager.LoadSaveForPlayer(plr.playerbody, parsedData))
                {
                    // TODO: Inform the client so they can retry / otherwise not lose all their stuff.
                }

                plr.unchipped = pb.unchipped;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error applying save for WorldPlacePlayerWithSave: {e}");
            }

            // If they came from a lobby, we should spawn them at a teleporter.
            try
            {
                if (fromLobby != CSteamID.Nil.m_SteamID)
                {
                    SpawnAtLobbyTeleporter(pb, fromLobby);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Error spawning player at teleporter: {e}");
            }
        }

        yield return ServerMain.HeyPlayerJustJoinedGiveHimASpawnLocationOkay(clientId);
    }

    private static void SpawnAtLobbyTeleporter(NetBody body, ulong fromLobby)
    {
        foreach (var tp in TeleporterScript.Teleporters)
        {
            if (tp.LinkedLobby == fromLobby)
            {
                body.SetBodyPosition(tp.transform.position);
                // Prevent the default spawning logic from taking over.
                body.player.server_plrstate.did_give_spawn_location_from_a_save = true;

                return;
            }
        }

        // No teleporter found, so we need to make one.
        // Prioritize distance to make it easier for the new
        // player.
        // TODO: Prioritize critical injuries first.

        TeleporterScript closest = null;
        var closestDistance = float.MaxValue;

        foreach (var tp in TeleporterScript.Teleporters)
        {
            // Fallback in case all players are dead (not == null because not unity check).
            if (closest is null) closest = tp;

            foreach (var otherBody in NetBody.all_instances)
            {
                if (!otherBody.alive || !otherBody.is_player || otherBody == body) continue;

                var dist = ((Vector2)tp.transform.position - otherBody.position).sqrMagnitude;
                if (dist < closestDistance)
                {
                    closest = tp;
                    closestDistance = dist;
                }
            }
        }

        if (closest != null)
        {
            closest.LinkedLobby = fromLobby;
            body.SetBodyPosition(closest.transform.position);
            // Prevent the default spawning logic from taking over.
            body.player.server_plrstate.did_give_spawn_location_from_a_save = true;

            return;
        }

        Plugin.Logger.LogError("Failed to find spawn teleporter!");
    }
}