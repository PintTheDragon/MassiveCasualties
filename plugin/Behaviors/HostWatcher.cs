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

        var ply = transportSteamworks.SteamIDToNetPlayerDict[KSteam.GetLocalUserSteamID().m_SteamID];
        ply.is_host = true;
        ply.is_local = true;
        ply.server_plrstate = new Server_PlayerState(ply);

        NetPlayer.LOCAL_PLAYER = ply;

        // TODO: Figure out a better place to spawn players.
        Body_PlaceBody_MultiplayerPatch.has_spawn_location = true;
        Body_PlaceBody_MultiplayerPatch.spawnlocation = ply.body.transform.position;

        HardcodedServer.CurHostID = ply.clientId;

        FixSyncObjects();

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

    /// <summary>
    ///     Connects to the newly elected host (only over steam networking)
    /// </summary>
    private void ConnectToNewHost(TransportSteamworks transportSteamworks)
    {
        // TODO: What if steam makes the original host a client?

        transportSteamworks.CloseP2PSessions();

        transportSteamworks.CreateClientSocket();

        // TODO: Need to go through the handshake process
        //       plus fast-track it on the host side for existing
        //       steam IDs.
    }

    /// <summary>
    ///     Turns client-sided objects into host sync objects.
    ///     This can't run unless the networking is in host mode.
    /// </summary>
    private void FixSyncObjects()
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
}