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
            SwitchToHost(transportSteamworks);
        else
            ConnectToNewHost(transportSteamworks);
    }

    private void OnDestroy()
    {
        if (Singleton == this) Singleton = null;
    }

    /// <summary>
    ///     Turns this client into a host (only over steam networking).
    /// </summary>
    private void SwitchToHost(TransportSteamworks transportSteamworks)
    {
        ServerMain._RegisterServerReceivers();

        Net.type = Net.NetType.Host;

        FixNetBodies(transportSteamworks);

        FixHostSyncObjects();

        CloseAllSockets(transportSteamworks);
        transportSteamworks.CreateServerSocket();

        // TODO: Host connection works, but something
        //       (probably ID 0 check) is causing it to
        //       not use data properly (client movement doesn't
        //       work, only jumping, and host can't see messages, but
        //       client can). Movement works on reconnect, but not chat.
    }

    /// <summary>
    ///     Connects to the newly elected host (only over steam networking)
    /// </summary>
    private void ConnectToNewHost(TransportSteamworks transportSteamworks)
    {
        // Unregister receivers in case we changed from host -> client.
        Net.SERVER_MESSAGE_HANDLERS.Clear();

        Net.type = Net.NetType.Client;

        FixNetBodies(transportSteamworks);

        FixClientSyncObjects();

        CloseAllSockets(transportSteamworks);
        transportSteamworks.CreateClientSocket();

        // TODO: Need to go through the handshake process
        //       plus fast-track it on the host side for existing
        //       steam IDs.

        // TODO: New client can't disconnect (host should also be able to).
    }

    private void CloseAllSockets(TransportSteamworks transportSteamworks)
    {
        if (transportSteamworks.listenSocket != HSteamListenSocket.Invalid)
        {
            if (transportSteamworks.is_steamserver)
                SteamGameServerNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);
            else
                SteamNetworkingSockets.CloseListenSocket(transportSteamworks.listenSocket);

            transportSteamworks.listenSocket = HSteamListenSocket.Invalid;
        }

        transportSteamworks.CloseConnectionOnServerUser();
        transportSteamworks.CloseP2PSessions();
    }

    /// <summary>
    ///     Turns client-sided objects into host sync objects.
    ///     This can't run unless the networking is in host mode.
    /// </summary>
    private void FixHostSyncObjects()
    {
        // TODO: Are all objects available, or do we need to full sync on interval?

        foreach (var item in FindObjectsOfType<Item>()) Item_Start_MultiplayerPatch.Postfix(item);

        foreach (var building in FindObjectsOfType<BuildingEntity>())
        {
            BuildingEntity_Start_MultiplayerPatch.Prefix(building);
            BuildingEntity_Start_MultiplayerPatch.Postfix(building);
        }

        var sync = NewCoolerObjectPacketWriteReadSystem.inst;
        foreach (var clientObj in sync.client_objects)
        {
            if (sync.server_objects.ContainsKey(clientObj.Key)) continue;
            sync.server_objects[clientObj.Key] =
                new CoolSyncSubSystemForObjects.Server_Object
                {
                    netId = clientObj.Value.netId,
                    real_obj = clientObj.Value.real_obj
                };
        }

        sync.client_objects.Clear();

        // TODO: There's probably a bunch of other similar patches that need to be called.
    }

    /// <summary>
    ///     Turns server-sided objects into client sync objects.
    ///     This can't run unless the networking is in client mode.
    /// </summary>
    private void FixClientSyncObjects()
    {
        var sync = NewCoolerObjectPacketWriteReadSystem.inst;
        foreach (var serverObj in sync.server_objects)
        {
            if (sync.client_objects.ContainsKey(serverObj.Key)) continue;
            sync.client_objects[serverObj.Key] =
                new CoolSyncSubSystemForObjects.Client_Object
                {
                    netId = serverObj.Value.netId,
                    real_obj = serverObj.Value.real_obj
                };
        }

        sync.server_objects.Clear();
    }

    /// <summary>
    ///     Updates all the net bodies to have the correct state
    ///     after a host swap.
    /// </summary>
    private void FixNetBodies(TransportSteamworks transportSteamworks)
    {
        var foundHost = false;

        foreach (var ply in transportSteamworks.SteamIDToNetPlayerDict.Values)
        {
            // Only the LOCAL_PLAYER should be local (set below).
            ply.is_local = false;
            ply.is_host = false;

            if (ply.steam_id == KSteam.CURRENT_LOBBY.ownerID.m_SteamID)
            {
                foundHost = true;
                ply.is_host = true;
                HardcodedServer.CurHostID = ply.clientId;

                // TODO: Figure out a better place to spawn players, and refactor this.
                Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
                Body_PlaceBody_MultiplayerPatch.spawnlocation = ply.body.transform.position;
            }

            if (ply.server_plrstate == null)
            {
                ply.server_plrstate = new Server_PlayerState(ply);
                // TODO: Race condition here with a join during host transition?
                ply.server_plrstate.is_loaded_in = true;
            }
        }

        NetPlayer.LOCAL_PLAYER.is_local = true;

        if (!foundHost) Plugin.Logger.LogError("Could not find new host's player object!");
    }
}