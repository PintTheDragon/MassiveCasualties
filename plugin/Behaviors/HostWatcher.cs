using System.Collections;
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

        var curID = GetOwnerID().m_SteamID;
        var oldID = _cachedHostID;

        if (curID == oldID) return;
        _cachedHostID = curID;

        // Always needed (not just on host swap), and used for
        // communicating to/from the host in the hardcoded server patches.
        if (curID == CSteamID.Nil.m_SteamID)
            // This relies on the fact that new games start
            // with host ID == 0.
            HardcodedServer.CurHostID = 0;
        else if (transportSteamworks.SteamIDToClientIDDict.TryGetByFirst(curID, out var hostClientID))
            HardcodedServer.CurHostID = hostClientID;


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
    ///     Informs the HostWatcher that we've disconnected from the server,
    ///     and prevents it from messing up the state if it doesn't notice it
    ///     before we connect to a different server.
    /// </summary>
    internal static void CallOnDisconnect()
    {
        Singleton._cachedHostID = GetOwnerID().m_SteamID;
        if (Singleton._cachedHostID == CSteamID.Nil.m_SteamID) HardcodedServer.CurHostID = 0;
    }

    private static CSteamID GetOwnerID()
    {
        // Sometimes, lobby_steamID is unset while ownerID is unchanged.
        if (KSteam.CURRENT_LOBBY.lobby_steamID == CSteamID.Nil) return CSteamID.Nil;
        return KSteam.CURRENT_LOBBY.ownerID;
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

        // Need to be very careful about ordering here, going client -> host, we may
        // have already received some P2P connections, so don't close those.
        CloseOwnSockets(transportSteamworks);

        transportSteamworks.CreateServerSocket();

        // TODO: After changehost 1, client no longer sees
        //       host movement. Changing back fixes the problem.
        //       Maybe there's something that only gets enabled for
        //       clients which makes body sync work?
        //       Also, joinrandom doesn't load in players, which I think
        //       is the same problem.
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

        CloseOwnSockets(transportSteamworks);
        CloseConnectedSockets(transportSteamworks);

        StartCoroutine(ConnectAfterDelay(transportSteamworks));

        // TODO: Need to go through the handshake process
        //       plus fast-track it on the host side for existing
        //       steam IDs.

        // TODO: New client can't disconnect (host should also be able to).
    }

    private IEnumerator ConnectAfterDelay(TransportSteamworks transportSteamworks)
    {
        yield return new WaitForSeconds(0.25f);

        // TODO: Race condition here if the host hasn't changed to host mode yet,
        //       it'll reject the connection. Maybe add rejection and keep retrying, plus
        //       a final ACK + timeout to fail.

        transportSteamworks.CreateClientSocket();
    }

    private void CloseOwnSockets(TransportSteamworks transportSteamworks)
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
    }

    private void CloseConnectedSockets(TransportSteamworks transportSteamworks)
    {
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

        foreach (var item in FindObjectsOfType<Item>()) Item_Start_MultiplayerPatch.Postfix(item);

        FixHostSyncObjects(NewCoolerObjectPacketWriteReadSystem.inst);
        FixHostSyncObjects(CharSync.inst);
        FixHostSyncObjects(PlrSync.inst);

        SharedMain.CreatePlayerCharacters();

        // TODO: There's probably a bunch of other similar patches that need to be called.
    }

    private void FixHostSyncObjects(CoolSyncSubSystemForObjects sync)
    {
        foreach (var clientObj in sync.client_objects)
        {
            if (sync.server_objects.ContainsKey(clientObj.Key)) continue;
            sync.server_objects[clientObj.Key] =
                new CoolSyncSubSystemForObjects.Server_Object
                {
                    netId = clientObj.Value.netId,
                    real_obj = clientObj.Value.real_obj,
                    cur_packet = clientObj.Value.cur_packet
                };
        }

        sync.client_objects.Clear();

        foreach (var ply in ServerMain.AllPlayersExceptHost)
            // Creates a new perplrstate if one doesn't exist.
            sync.GetPerPlrState(ply.clientId);
    }

    /// <summary>
    ///     Turns server-sided objects into client sync objects.
    ///     This can't run unless the networking is in client mode.
    /// </summary>
    private void FixClientSyncObjects()
    {
        FixClientSyncObjects(NewCoolerObjectPacketWriteReadSystem.inst);
        FixClientSyncObjects(CharSync.inst);
        FixClientSyncObjects(PlrSync.inst);
    }

    private void FixClientSyncObjects(CoolSyncSubSystemForObjects sync)
    {
        foreach (var serverObj in sync.server_objects)
        {
            if (sync.client_objects.ContainsKey(serverObj.Key)) continue;
            sync.client_objects[serverObj.Key] =
                new CoolSyncSubSystemForObjects.Client_Object
                {
                    netId = serverObj.Value.netId,
                    real_obj = serverObj.Value.real_obj,
                    cur_packet = serverObj.Value.cur_packet,
                    sys = sync
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

            if (ply.steam_id == GetOwnerID().m_SteamID)
            {
                foundHost = true;
                ply.is_host = true;

                // Local is only true for the host's local instance of itself,
                // as far as I can tell.
                if (ply.clientId == NetPlayer.LOCAL_PLAYER.clientId) ply.is_local = true;

                // TODO: Figure out a better place to spawn players, and refactor this.
                Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
                Body_PlaceBody_MultiplayerPatch.spawnlocation = ply.body.transform.position;
            }

            // If this is incorrect, it causes sync failures from host -> client,
            // so client movement shows up on host, but host movement doesn't show
            // up on client.
            if (ply.server_plrstate == null)
            {
                ply.server_plrstate = new Server_PlayerState(ply);
                // TODO: Race condition here with a join during host transition?
                ply.server_plrstate.is_loaded_in = true;
                ply.server_plrstate.finished_worldgen = true;
                ply.server_plrstate.did_give_spawn_location = true;
            }
        }

        if (!foundHost) Plugin.Logger.LogError("Could not find new host's player object!");
    }
}