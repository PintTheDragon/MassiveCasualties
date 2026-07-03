using System;
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

        var fromLobby = CSteamID.Nil.m_SteamID;

        try
        {
            reader.Get(out fromLobby);
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

        // If they came from a lobby, we should spawn them at a teleporter.
        if (fromLobby != CSteamID.Nil.m_SteamID)
        {
            SpawnAtLobbyTeleporter(plr.playerbody, fromLobby);
        }

        plr.ResetEntropy();

        if (plr.body != null) plr.unchipped = plr.playerbody.unchipped;

        plr.server_plrstate.did_give_spawn_location = true;
        plr.StartCoroutine(ServerMain.HeyPlayerJustJoinedGiveHimASpawnLocationOkay(clientId));
        WorldChunkSync.singleton.timer_TilemapSync += 0.05f;
        WorldChunkSync.singleton.timer_TilemapFluidSync += 0.05f;
    }

    private static void SpawnAtLobbyTeleporter(NetBody body, ulong fromLobby)
    {
        // Prevent the default spawning logic from taking over.
        body.player.server_plrstate.did_give_spawn_location_from_a_save = true;

        foreach (var tp in TeleporterScript.Teleporters)
        {
            if (tp.LinkedLobby == fromLobby)
            {
                body.SetBodyPosition(tp.transform.position);
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

            return;
        }

        Plugin.Logger.LogError("Failed to find spawn teleporter!");
        // Fallback to the default spawning logic.
        body.player.server_plrstate.did_give_spawn_location_from_a_save = false;
    }
}