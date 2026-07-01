using System;
using System.Collections;
using System.Collections.Generic;
using KrokoshaCasualtiesMP;
using Steamworks;
using UnityEngine;

namespace MassiveCasualties.Behaviors;

internal class LobbyManager : MonoBehaviour
{
    internal static LobbyManager Singleton;

    protected static CallResult<LobbyMatchList_t> _lobbyDataCallback;

    internal static readonly List<KSteam.Lobby> Lobbies = new();
    private static int _lastLobbyIdx;

    /// <summary>
    ///     Returns whether we're connected to a MC-enabled lobby.
    /// </summary>
    internal static bool IsMcLobby =>
        KSteam.CURRENT_LOBBY != null &&
        KSteam.CURRENT_LOBBY.metadata.ContainsKey("CASUALTIESUNKNOWN_MASSIVECASUALTIES_VERSION");

    private void Awake()
    {
        Singleton = this;
    }

    private void OnDestroy()
    {
        Singleton = null;
    }

    internal static void Init()
    {
        _lobbyDataCallback = CallResult<LobbyMatchList_t>.Create(OnLobbyList);

        Singleton.StartCoroutine(UpdateLobbiesLoop());
    }

    private static IEnumerator Connect(CSteamID lobbyID)
    {
        if (!Net.TryGetSteamTransport(out _)) yield break;
        if (lobbyID == KSteam.CURRENT_LOBBY.lobby_steamID || lobbyID == CSteamID.Nil) yield break;

        SaveManager.SaveBeforeSessionChange();

        KrokoshaScavMultiplayer.ShutdownNetwork();

        // There's severe desync if we don't enter the lobby first.
        // I'm guessing it's because the client fails to perform the handshake,
        // since it thinks it's already in game.
        KrokoshaScavMultiplayer.showMultiplayerMenu = true;
        PlayerCamera.main.ToMainMenu();

        // TODO: Improve this.
        yield return new WaitForSeconds(1.0f);

        TransportSteamworks.OnWantToJoinLobby(lobbyID.m_SteamID);
    }

    internal static void JoinRandom()
    {
        foreach (var lobby in Lobbies)
        {
            if (lobby.lobby_steamID == KSteam.CURRENT_LOBBY.lobby_steamID) continue;

            Singleton.StartCoroutine(Connect(lobby.lobby_steamID));
            break;
        }
    }

    private static IEnumerator UpdateLobbiesLoop()
    {
        while (true)
        {
            if (!KSteam.Loaded || _lobbyDataCallback == null)
            {
                yield return new WaitForSeconds(1.0f);
                continue;
            }

            var lastIdx = _lastLobbyIdx;

            SteamMatchmaking.AddRequestLobbyListStringFilter("CASUALTIESUNKNOWN_MASSIVECASUALTIES_VERSION",
                Plugin.ModVersion, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_VERSION",
                KrokoshaScavMultiplayer.FULL_VERSION_TAG, ELobbyComparison.k_ELobbyComparisonEqual);
            /*SteamMatchmaking.AddRequestLobbyListNumericalFilter(
                "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_CURRENTLAYER", Net.cur_server_info.cur_layer,
                ELobbyComparison.k_ELobbyComparisonEqual);*/
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_HASPASSWORD", "0",
                ELobbyComparison.k_ELobbyComparisonEqual);

            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            var hAPICall = SteamMatchmaking.RequestLobbyList();
            _lobbyDataCallback.Set(hAPICall);

            // This is just a timeout in case the request fails.
            var time = Time.realtimeSinceStartup;

            while (_lastLobbyIdx == lastIdx && Time.realtimeSinceStartup - time < 20.0f)
            {
                yield return new WaitForSeconds(0.1f);
            }

            yield return new WaitForSeconds(10.0f);
        }
    }

    private static void OnLobbyList(LobbyMatchList_t pCallback, bool bIOFailure)
    {
        try
        {
            if (bIOFailure)
            {
                Plugin.Logger.LogError("Error fetching lobby list!");
                // Needed to trigger a retry.
                _lastLobbyIdx++;

                return;
            }

            Lobbies.Clear();

            for (var iLobby = 0; iLobby < pCallback.m_nLobbiesMatching; ++iLobby)
            {
                var outLobby = new KSteam.Lobby();
                UpdateLobbyInfo(SteamMatchmaking.GetLobbyByIndex(iLobby), ref outLobby);
                if (outLobby.lobby_steamID == CSteamID.Nil) continue;

                Lobbies.Add(outLobby);
            }

            // Inform the caller that we processed the lobby list.
            _lastLobbyIdx++;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e.ToString());
        }
    }

    /// <summary>
    ///     This is a version of KSteam.UpdateLobbyInfo without some of the parts
    ///     that update the current lobby.
    /// </summary>
    private static void UpdateLobbyInfo(CSteamID steamIDLobby, ref KSteam.Lobby outLobby)
    {
        outLobby.metadata.Clear();
        outLobby.members.Clear();

        outLobby.lobby_steamID = steamIDLobby;
        outLobby.ownerID = SteamMatchmaking.GetLobbyOwner(steamIDLobby);
        outLobby.memberlimit = SteamMatchmaking.GetLobbyMemberLimit(steamIDLobby);

        var lobbyDataCount = SteamMatchmaking.GetLobbyDataCount(steamIDLobby);
        for (var iLobbyData = 0; iLobbyData < lobbyDataCount; ++iLobbyData)
        {
            string pchKey;
            string pchValue;
            var num = SteamMatchmaking.GetLobbyDataByIndex(steamIDLobby, iLobbyData, out pchKey, byte.MaxValue,
                out pchValue, 8192)
                ? 1
                : 0;
            outLobby.metadata[pchKey] = pchValue;
            if (num == 0)
            {
                Plugin.Logger.LogError("SteamMatchmaking.GetLobbyDataByIndex returned false.");
            }
        }

        var numLobbyMembers = SteamMatchmaking.GetNumLobbyMembers(steamIDLobby);
        // TODO: Use lobby members, also unsure what's the difference between that and
        //       metadata CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_PLRCOUNT
    }
}