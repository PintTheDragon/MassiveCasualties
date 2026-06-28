using KrokoshaCasualtiesMP;
using MassiveCasualties.Patches;
using Steamworks;
using UnityEngine;

namespace MassiveCasualties.Behaviors;

/// <summary>
///     Watches for when the steam lobby switches hosts
///     and updates the connection state accordingly.
/// </summary>
internal class HostWatcher : MonoBehaviour
{
    internal static HostWatcher Singleton;

    /// <summary>
    ///     0 is the null steam ID (i.e., not in lobby).
    /// </summary>
    private ulong _cachedHostID = CSteamID.Nil.m_SteamID;

    private void Awake()
    {
        Singleton = this;
    }

    private void Update()
    {
        if (Net.TRANSPORT is not TransportSteamworks transportSteamworks) return;

        var curID = KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
        var oldID = _cachedHostID;

        if (curID == oldID) return;
        _cachedHostID = curID;

        // curID == 0 means we left the lobby, oldID == 0
        // means we just joined one.
        // In any case, we can defer to the default multiplayer
        // behavior.
        if (curID == CSteamID.Nil.m_SteamID || oldID == CSteamID.Nil.m_SteamID) return;

        if (curID == KSteam.GetLocalUserSteamID().m_SteamID)
        {
            // We need to take over as the host.

            ServerMain._RegisterServerReceivers();

            Net.type = Net.NetType.Host;

            var ply = transportSteamworks.SteamIDToNetPlayerDict[KSteam.GetLocalUserSteamID().m_SteamID];
            ply.is_host = true;
            ply.is_local = true;
            ply.server_plrstate = new Server_PlayerState(ply);

            NetPlayer.LOCAL_PLAYER = ply;

            // TODO: Figure out a better place to spawn players.
            Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
            Body_PlaceBody_MultiplayerPatch.spawnlocation = ply.body.transform.position;

            HardcodedServer.CurHostID = ply.clientId;

            if (transportSteamworks.listenSocket != HSteamListenSocket.Invalid)
            {
                if (transportSteamworks.is_steamserver)
                    SteamGameServerNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);
                else
                    SteamNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);
            }

            transportSteamworks.CloseP2PSessions();

            transportSteamworks.CreateServerSocket();
        }
        else
        {
            // There's a new host we need to connect to.
            // TODO: What if steam makes the original host a client?

            transportSteamworks.CloseP2PSessions();

            transportSteamworks.CreateClientSocket();

            // TODO: Need to go through the handshake process
            //       plus fast-track it on the host side for existing
            //       steam IDs.
        }
    }

    private void OnDestroy()
    {
        if (Singleton == this) Singleton = null;
    }
}