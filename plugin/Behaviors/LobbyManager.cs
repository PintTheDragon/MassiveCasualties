using System;
using System.Collections;
using System.Collections.Generic;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using Steamworks;
using UnityEngine;

namespace MassiveCasualties.Behaviors;

internal class LobbyManager : MonoBehaviour
{
    internal static LobbyManager Singleton;

    protected static CallResult<LobbyMatchList_t> _lobbyDataCallback;

    internal static readonly List<KSteam.Lobby> Lobbies = new();
    private static int _lastLobbyIdx;

    private static bool _isMcEnabled;

    /// <summary>
    ///     Whether the user enabled MC for their lobbies.
    ///     Use IsMcAvailable for game behavior, since it might
    ///     be enabled but unavailable.
    /// </summary>
    internal static bool IsMcEnabled
    {
        get => _isMcEnabled;
        set
        {
            _isMcEnabled = value;
            if (_isMcEnabled)
            {
                SetMultiplayerSettings();
            }

            PlayerPrefsExtended.SetBool("MASSIVECASUALTIES_SERVER_ENABLED", value);
        }
    }

    /// <summary>
    ///     Whether MC is enabled for lobbies this player is hosting.
    ///     They may not be in an MC-label lobby, however.
    /// </summary>
    internal static bool IsMcAvailable => IsMcEnabled && KSteam.Loaded;

    /// <summary>
    ///     Returns whether we're connected to a MC-enabled lobby.
    /// </summary>
    internal static bool IsMcLobby =>
        KSteam.CURRENT_LOBBY != null &&
        KSteam.CURRENT_LOBBY.metadata.ContainsKey("CASUALTIESUNKNOWN_MASSIVECASUALTIES_VERSION");

    private void Awake()
    {
        Singleton = this;

        _isMcEnabled = PlayerPrefsExtended.GetBool("MASSIVECASUALTIES_SERVER_ENABLED", true);
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

    /// <summary>
    ///     Configures the required multiplayer preset for MC.
    /// </summary>
    private static void SetMultiplayerSettings()
    {
        // TODO: Need to enfore mod list, but probably with a custom system.

        UIMainMenu._USE_STEAM_MENU = true;
        // TODO: Some sort of invisible?
        UIMainMenu._STEAM_CHOSEN_LOBBYTYPE = (int)ELobbyType.k_ELobbyTypePublic;

        KrokoshaScavMultiplayer.INPUT_PASSWORD = "";

        var rules = KrokoshaScavMultiplayer.rules;

        rules.sv_cheats = false;
        rules.AllowClientCheatCommands = false;
        // TODO: Allow editing?
        rules.PLAYER_COUNT_LIMIT = 6;
        rules.ShowPlayerDirections = true;
        rules.EnableNametags = true;
        rules.EnableStatusIcons = true;
        rules.UnchippedHideNametags = true;
        rules.EnableChatbox = true;
        rules.OnlyProximityChat = false;
        rules.UnchippedProximityChat = true;
        rules.UnchippedIsIndividual = true;
        rules.ScatterMinGroupSize = 0;
        rules.ScatterPunishDistance = 0.0f;
        // TODO: Layer system needs to be reworked, spawn off to a separate instance.
        rules.LayerFinishPlrPercent = 60;
        rules.LayerFinishKeepXOffset = true;
        rules.StragglerRadlinePercent = 30;
        rules.NoInventoryLock = false;
        rules.EnableSleep = true;
        rules.EnableTimeManipulation = false;
        rules.SpeechImpairedChat = true;
        rules.HearingLossChat = true;
        rules.MindwipeDisablesChat = false;
        rules.DeadTextchat = true;
        rules.DeadVoicechat = true;
        rules.SleepingMute = false;
        rules.Permadeath = false;
        rules.ReviveOnNextLevel = false;
        rules.ReviveFromTrader = true;
        rules.RespawnKeepInventory = false;
        rules.RespawnKeepSkills = false;
        rules.AllowSpectatorFreecam = true;
        rules.AllowPush = true;
        rules.AlwaysAllowCarry = false;
        rules.PiggybackMaxStack = 1;
        rules.PiggybackWeightMultiplier = 0.8f;
        rules.SpectateWhileUnconscious = false;
        rules.EnableMP3Sync = false;
        rules.VoicechatQuality = 4;
        rules.VoicechatEnabled = true;
        rules.ProximityHearDistance = 55f;
        rules.CharacterYapPublic = true;
        rules.Teams = false;
        rules.PVP = false;
        rules.PVPCombatDismember = true;
        rules.PVPMoodDebuff = 0.5f;
        rules.PVPDamageMultiplier = 1f;
        rules.LateJoinAllowed = true;
        rules.LateJoinSpectate = false;
        rules.AmputateHealthyPlayers = false;
        rules.AdditionalBrainRegen = 1f;
        rules.AdditionalHealthRegen = 1f;
        rules.AdditionalHealthDecay = 1f;
        rules.LastStandAllowed = true;
        rules.SelfharmWitnessMoodDebuff = 3f;
        rules.SavePlayerState = false;
        rules.SavePlayerInventory = false;
        rules.SavePlayerPosition = true;
        rules.AutoContinue = false;
        rules.AutoMinPlrsToStart = 2;
        rules.AutoExitWhenAllDied = false;
        rules.AutoExitWhenAllLeft = true;

        KrokoshaScavMultiplayer.rules = rules;
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