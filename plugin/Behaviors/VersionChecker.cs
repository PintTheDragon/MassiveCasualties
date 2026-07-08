using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace MassiveCasualties.Behaviors;

internal class VersionChecker : MonoBehaviour
{
    private const string VERSION_CHECK_URL =
        "https://raw.githubusercontent.com/PintTheDragon/MassiveCasualties/refs/heads/master/plugin/version.txt";

    internal const string DOWNLOAD_URL = "https://www.nexusmods.com/scavprototype/mods/400?tab=files";

    internal static bool IsOutdated;

    private void Awake()
    {
        StartCoroutine(CheckVersion());
    }

    private IEnumerator CheckVersion()
    {
        var www = UnityWebRequest.Get(VERSION_CHECK_URL);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Plugin.Logger.LogError(new Exception("Error sending version check: " + www.error));
            yield break;
        }

        var latest = new Version(www.downloadHandler.text);
        var cur = new Version(Plugin.ModVersion);

        IsOutdated = cur < latest;
    }
}