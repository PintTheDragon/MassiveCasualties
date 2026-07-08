using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MassiveCasualties.Behaviors;
using MassiveCasualties.Commands;
using MassiveCasualties.Network;
using MassiveCasualties.Objects;
using MassiveCasualties.Patches;

namespace MassiveCasualties;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string ModGUID = "pintthedragon.massivecasualties";
    public const string ModName = "Massive Casualties";
    public const string ModVersion = "0.0.2";

    /// <summary>
    ///     Patch versions don't affect network compatibility, everything else does.
    /// </summary>
    public static readonly string NetVersion = ModVersion.Substring(0, ModVersion.LastIndexOf('.'));

    internal new static ManualLogSource Logger;

    internal static readonly bool DEV = false;
    private readonly Harmony _harmony = new(ModGUID);
    public static Plugin Instance { get; private set; } = null!;

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        try
        {
            _harmony.PatchAll();

            gameObject.AddComponent<HostWatcher>();
            gameObject.AddComponent<LobbyManager>();
            gameObject.AddComponent<SaveManager>();
            gameObject.AddComponent<NewLobbyHost>();
            gameObject.AddComponent<EntitySync>();
            gameObject.AddComponent<VersionChecker>();

            ChangeHost.Register();
            JoinRandom.Register();

            Teleporter.Register();

            NetRegistration.Register();

            PatchSteamLobby.Init();
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }

        Logger.LogInfo($"Plugin {ModName} is loaded!");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        Instance = null!;
    }
}