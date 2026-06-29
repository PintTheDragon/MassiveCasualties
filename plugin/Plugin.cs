using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MassiveCasualties.Behaviors;
using MassiveCasualties.Commands;

namespace MassiveCasualties;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string ModGUID = "pintthedragon.massivecasualties";
    public const string ModName = "Massive Casualties";
    public const string ModVersion = "0.0.0";

    internal new static ManualLogSource Logger;
    private readonly Harmony _harmony = new(ModGUID);
    public static Plugin Instance { get; private set; } = null!;

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        try
        {
            _harmony.PatchAll();
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }

        gameObject.AddComponent<HostWatcher>();

        ChangeHost.Register();

        Logger.LogInfo($"Plugin {ModName} is loaded!");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        Instance = null!;
    }
}