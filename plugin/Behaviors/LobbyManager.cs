using System;
using System.Collections;
using System.Collections.Generic;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using MassiveCasualties.Network;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        (KSteam.CURRENT_LOBBY.metadata.ContainsKey("CASUALTIESUNKNOWN_MASSIVECASUALTIES_VERSION") ||
         // If we're the host and MC is running, it means it was always an MC lobby.
         // Host swapping could have happened, but only if it was an MC lobby to begin with.
         (Net.is_server && IsMcAvailable));

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

        if (IsMcEnabled)
        {
            SetMultiplayerSettings();
        }
    }

    /// <summary>
    ///     Configures the required multiplayer preset for MC.
    /// </summary>
    internal static void SetMultiplayerSettings()
    {
        // TODO: This gets cleared when aborting and creating a new game.

        // TODO: Need to enfore mod list, but probably with a custom system.

        // TODO: Need game mode select to be limited.

        UIMainMenu._USE_STEAM_MENU = true;
        // TODO: Some sort of invisible?
        UIMainMenu._STEAM_CHOSEN_LOBBYTYPE = (int)ELobbyType.k_ELobbyTypePublic;

        KrokoshaScavMultiplayer.INPUT_PASSWORD = "";

        var rules = KrokoshaScavMultiplayer.rules;

        rules.sv_cheats = Plugin.DEV;
        rules.AllowClientCheatCommands = Plugin.DEV;
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

    /// <summary>
    ///     Connects to a different lobby while currently in a game.
    /// </summary>
    internal static void ConnectFromGame(CSteamID lobbyID)
    {
        Singleton.StartCoroutine(ConnectCoroutine(lobbyID));
    }

    private static IEnumerator ConnectCoroutine(CSteamID lobbyID)
    {
        if (!Net.TryGetSteamTransport(out _)) yield break;
        if (lobbyID == KSteam.CURRENT_LOBBY.lobby_steamID || lobbyID == CSteamID.Nil) yield break;

        try
        {
            SaveManager.SaveBeforeSessionChange();

            KrokoshaScavMultiplayer.ShutdownNetwork();

            // There's severe desync if we don't enter the lobby first.
            // I'm guessing it's because the client fails to perform the handshake,
            // since it thinks it's already in game.
            KrokoshaScavMultiplayer.showMultiplayerMenu = true;
            PlayerCamera.main.ToMainMenu();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e.ToString());
            yield break;
        }

        // TODO: Improve this.
        yield return new WaitForSeconds(1.0f);

        try
        {
            TransportSteamworks.OnWantToJoinLobby(lobbyID.m_SteamID);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e.ToString());
            yield break;
        }
    }

    /// <summary>
    ///     Returns a random valid MC lobby that isn't the current one,
    ///     or Nil if none can be found.
    /// </summary>
    internal static CSteamID GetRandom()
    {
        foreach (var lobby in Lobbies)
        {
            if (lobby.lobby_steamID == KSteam.CURRENT_LOBBY.lobby_steamID) continue;

            return lobby.lobby_steamID;
        }

        return CSteamID.Nil;
    }

    private static IEnumerator UpdateLobbiesLoop()
    {
        while (true)
        {
            if (!KSteam.Loaded || _lobbyDataCallback == null || !IsMcAvailable)
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

/// <summary>
///     Manages the lifecycle of gracefully creating a new
///     lobby while already connected to one, informing the current
///     host of the new lobby, then swapping over to it.
/// </summary>
internal class NewLobbyHost : MonoBehaviour
{
    internal static NewLobbyHost Singleton;

    protected static CallResult<LobbyCreated_t> _lobbyCreatedCallback;

    private static CSteamID _lastLobbyId = CSteamID.Nil;
    private static WorldSave _lastSave;
    private static LobbyCreated_t? _cachedLobbyResult;

    private static bool _startingGame;

    /// <summary>
    ///     This serves as a debouncer, since if a player spams lobby creation,
    ///     it could cause the server to tell them to go to a lobby created before
    ///     the one now in _cachedLobbyResult, which will no longer result, leading to
    ///     an error.
    /// </summary>
    private static bool _canCreateLobby = true;

    private static Coroutine _cleanup;

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
        _lobbyCreatedCallback = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
    }

    /// <summary>
    ///     Starts a new lobby from a save, optionally forwarding an old lobby ID to
    ///     the ID of the newly created lobby in every teleporter on the current lobby.
    /// </summary>
    /// <param name="oldLobbyID">The original ID of the lobby the save comes from, or Nil if there's no save.</param>
    /// <param name="oldLobbySave">The save to load the lobby from, or null if there's no save.</param>
    internal static void HostNewLobby(CSteamID oldLobbyID, WorldSave oldLobbySave)
    {
        if (!_canCreateLobby) return;
        _canCreateLobby = false;

        _lastLobbyId = oldLobbyID;
        _lastSave = oldLobbySave;

        ClearCachedLobby();

        // It needs to be invisible, otherwise steam will kick us out of
        // the old lobby.
        var lobby = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeInvisible,
            KrokoshaScavMultiplayer.PLAYER_COUNT_LIMIT);
        _lobbyCreatedCallback.Set(lobby);

        if (_cleanup != null) Singleton.StopCoroutine(_cleanup);
        _cleanup = Singleton.StartCoroutine(LobbyCleanup());
    }

    /// <summary>
    ///     If the given lobby ID is one we queued to start,
    ///     switches the host over to it and returns true.
    ///     Otherwise, returns false.
    ///     This should be called after confirming the new lobby ID
    ///     got round tripped and is now synced to every client.
    ///     This can be called multiple times, without causing issues.
    /// </summary>
    internal static bool SwitchToLobby(CSteamID lobbyID, float delay)
    {
        if (!Net.TryGetSteamTransport(out _) || _cachedLobbyResult == null ||
            _cachedLobbyResult.Value.m_ulSteamIDLobby != lobbyID.m_SteamID)
        {
            return false;
        }

        // This should be idempotent, but still make it clear to the
        // caller that we are going to be starting the game.
        if (_startingGame) return true;
        _startingGame = true;

        Plugin.Logger.LogInfo("Switching to host lobby " + lobbyID.m_SteamID);

        Singleton.StartCoroutine(HostCoroutine(delay));

        return true;
    }

    /// <summary>
    ///     If there's currently a cached lobby,
    ///     extracts it and prevent it from being cleaned up.
    /// </summary>
    internal static LobbyCreated_t? TakeLobby()
    {
        var lobby = _cachedLobbyResult;
        _cachedLobbyResult = null;

        return lobby;
    }

    /// <summary>
    ///     This puts the system back into a good state if it takes too
    ///     long for any part of it to be processed.
    /// </summary>
    private static IEnumerator LobbyCleanup()
    {
        yield return new WaitForSeconds(10.0f);

        ClearCachedLobby();

        _canCreateLobby = true;
    }

    private static void ClearCachedLobby()
    {
        if (_cachedLobbyResult != null && _cachedLobbyResult.Value.m_ulSteamIDLobby != CSteamID.Nil.m_SteamID)
        {
            SteamMatchmaking.LeaveLobby(new CSteamID(_cachedLobbyResult.Value.m_ulSteamIDLobby));
            _cachedLobbyResult = null;
        }
    }

    private static IEnumerator HostCoroutine(float delay)
    {
        if (delay != 0f) yield return new WaitForSeconds(delay);

        ulong newLobbyID;

        try
        {
            newLobbyID = _cachedLobbyResult.Value.m_ulSteamIDLobby;

            SaveManager.SaveBeforeSessionChange();

            KrokoshaScavMultiplayer.ShutdownNetwork();

            KrokoshaScavMultiplayer.showMultiplayerMenu = true;
            PlayerCamera.main.ToMainMenu();

            // It was previously invisible.
            SteamMatchmaking.SetLobbyType(new CSteamID(newLobbyID), ELobbyType.k_ELobbyTypePublic);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e.ToString());

            _startingGame = false;
            yield break;
        }

        // TODO: Improve this.
        yield return new WaitForSeconds(1.0f);

        try
        {
            // This will pull from cachedLobbyResult instead of creating a new lobby.
            // After this runs, everything is initialized and _cachedLobbyResult is null.
            LobbyManager.SetMultiplayerSettings();
            TransportSteamworks.OnWantToHostLobby(Net.NetType.Host, ELobbyType.k_ELobbyTypePublic);

            KSteam.CURRENT_LOBBY.locked = false;
            KSteam.UpdateLobbyInfo((CSteamID)newLobbyID, ref KSteam.CURRENT_LOBBY);
            // OnLobbyEnter normally creates this socket, but it triggered a while
            // ago when the lobby was first created, and has since been closed.
            // So, we need to recreate it.
            ((TransportSteamworks)Net.TRANSPORT).CreateServerSocket();

            if (_lastSave != null)
            {
                _lastSave.Load();
            }

            // Load game.
            WorldgenPatches._CheckIfCanLoadAWorld();
            WorldgenPatches.SetTutorialPlayerPrefs(false);
            if (KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer())
            {
                ServerMain.Server_Announce_GAME_START();
            }

            SceneManager.LoadScene("SampleScene");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e.ToString());

            _startingGame = false;
            yield break;
        }

        _startingGame = false;
    }

    [ServerReceiver((ushort)MessageType.ForwardLobby)]
    private static void Server_ForwardLobby(knetid _, ref NetDataReader reader)
    {
        reader.Get(out ulong fromID);
        reader.Get(out ulong toID);

        if (fromID == CSteamID.Nil.m_SteamID || toID == CSteamID.Nil.m_SteamID) return;

        // TODO: Don't switch if the lobby still exists.
        foreach (var teleporter in TeleporterScript.Teleporters)
        {
            if (teleporter.LinkedLobby == fromID) teleporter.LinkedLobby = toID;
        }
    }

    private static void OnLobbyCreated(LobbyCreated_t pCallback, bool bIOFailure)
    {
        if (bIOFailure)
        {
            Plugin.Logger.LogError("bIOFailure during lobby creation!");
            _canCreateLobby = true;
            return;
        }

        if (pCallback.m_eResult != EResult.k_EResultOK)
        {
            Plugin.Logger.LogError("OnLobbyCreated didn't succeed!");
            _canCreateLobby = true;
            return;
        }

        _cachedLobbyResult = pCallback;

        if (_lastLobbyId == CSteamID.Nil)
        {
            // We're not waiting on a sync, so we can move to the lobby
            // immediately.
            SwitchToLobby(new CSteamID(_cachedLobbyResult.Value.m_ulSteamIDLobby), 0f);
            return;
        }

        // Once the host updates the teleporters with this new ID,
        // we'll see the change, which will trigger SwitchToLobby.
        // We can't switch until all the clients are informed, though.
        var writer = Net.CreateWriter((ushort)MessageType.ForwardLobby);
        writer.Put(_lastLobbyId.m_SteamID);
        writer.Put(pCallback.m_ulSteamIDLobby);

        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }
}