using System.Collections;
using KrokoshaCasualtiesMP;
using LiteNetLib.Utils;
using MassiveCasualties.Network;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;

namespace MassiveCasualties.Behaviors;

internal class TeleporterScript : MonoBehaviour, ISyncItemOrBuilding
{
    // TODO: When a player leaves a lobby, record its world data
    //       and timestamp, then use that as a fallback for the linked
    //       lobby in the new one they enter.
    //       Use the largest timestamp as the source of truth, and allow
    //       the player with it to reinstate the lobby (disallow others from doing so).

    /// <summary>
    ///     Whether we want to go through this teleporter as soon
    ///     as it's available.
    ///     This is local.
    /// </summary>
    private bool _queuedUse;

    private UsableObject _usable;

    /// <summary>
    ///     The lobby this teleporter brings the player to.
    ///     0 means unlinked, although it could
    ///     also be linked to a lobby that doesn't exist anymore.
    /// </summary>
    [JsonProperty]
    internal ulong LinkedLobby { get; private set; }

    private void Start()
    {
        _usable = gameObject.AddComponent<UsableObject>();
        _usable.didLangString = true;
        _usable.toggleString = "Enter";
    }

    public void UpdatePacket(ref ItemOrBuildingCoolDeltaCompressablePacket packet, in SyncInfo syncInfo)
    {
        var data = new ULongToShorts { Value = LinkedLobby };

        // We can just encode this into unused parts
        // of the packet.
        // Note: CANNOT use float/double, as they don't
        // have full equality.
        packet.grabberplant_tipPos.x = data.Short0;
        packet.grabberplant_tipPos.y = data.Short1;
        packet.trader_desiredpos.x = data.Short2;
        packet.trader_desiredpos.y = data.Short3;

        // Once we start processing sync packets, it's reasonably certain
        // that the rest of the client will be synchronized soon.
        // Once that happens, we can leave the lobby if we were the one
        // to invoke the teleporter.
        if (_queuedUse) StartCoroutine(TeleportAfterDelay(0.3f));
    }

    public void ApplyPacket(in ItemOrBuildingCoolDeltaCompressablePacket packet, SyncInfo syncInfo)
    {
        var data = new ULongToShorts
        {
            Short0 = packet.grabberplant_tipPos.x,
            Short1 = packet.grabberplant_tipPos.y,
            Short2 = packet.trader_desiredpos.x,
            Short3 = packet.trader_desiredpos.y
        };

        LinkedLobby = data.Value;

        // Try to teleport if we were waiting on the host to do so.
        Teleport();
    }

    private void OnUse()
    {
        // If it's already determined, we can teleport immediately.
        if (LinkedLobby != 0)
        {
            _queuedUse = true;
            Teleport();
            return;
        }

        // Otherwise, request sync from the server.
        if (!KrokoshaScavMultiplayer.network_system_is_running ||
            !NetObjectRegistry.TryGetSyncInfo(gameObject, out var si))
        {
            return;
        }

        _queuedUse = true;
        KrokoshaScavMultiplayer.Client_SendSimpleMessageToServer((ushort)MessageType.GetTeleporterLobby, si.syncId);
    }

    /// <summary>
    ///     Teleports to the lobby in LinkedLobby.
    ///     Only does anything if _queuedUse is true.
    /// </summary>
    private void Teleport()
    {
        if (LinkedLobby == 0 || !_queuedUse) return;

        // TODO: Need to verify the lobby still exists.
        //       If not, this just kills the run.
        _queuedUse = false;
        LobbyManager.ConnectFromGame(new CSteamID(LinkedLobby));
    }

    private IEnumerator TeleportAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        Teleport();
    }

    [ServerReceiver((ushort)MessageType.GetTeleporterLobby)]
    private static void Server_OnUse(knetid clientId, ref NetDataReader reader)
    {
        if (!Net.is_server) return;

        var id = (knetid)reader.GetUShort();

        if (!NetObjectRegistry.TryGetSyncInfo(id, out var syncInfo) || syncInfo.go == null ||
            !syncInfo.go.TryGetComponent<TeleporterScript>(out var tp))
        {
            return;
        }

        if (tp.LinkedLobby == 0)
        {
            tp.LinkedLobby = LobbyManager.GetRandom().m_SteamID;
        }

        // Sync ASAP to reduce delay and let the host leave
        // soon if they're the one who requested it.
        // We'll teleport once the packet starts processing.
        NetObjectRegistry.Server_QueueSync(syncInfo);

        if (tp._queuedUse)
        {
            if (NetPlayer.ClientIdToPlayerDict.Count < 2)
            {
                // If there are no other players, the packet
                // won't get sent, so we can teleport immediately.
                tp.Teleport();
                return;
            }

            // Timeout, after which we'll just teleport anyway
            // and not wait for players.
            tp.StartCoroutine(tp.TeleportAfterDelay(2.0f));
        }
    }
}