using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using MassiveCasualties.Behaviors;
using UnityEngine;

namespace MassiveCasualties.Patches;

[HarmonyPatch(typeof(UIMainMenu))]
public class MainMenuPatch
{
    private static readonly MethodInfo GetIsMcEnabled = typeof(LobbyManager).GetMethod(
        "get_" + nameof(LobbyManager.IsMcEnabled),
        BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly MethodInfo GetIsMcAvailable = typeof(LobbyManager).GetMethod(
        "get_" + nameof(LobbyManager.IsMcAvailable),
        BindingFlags.Static | BindingFlags.NonPublic);

    [HarmonyPatch(nameof(UIMainMenu._GUI__DirectConnect))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> DirectConnnect(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);

        // UIMainMenu._USE_STEAM_MENU = GUILayout.Toggle(num6 != 0, text, guiLayoutOptionArray);
        //     IL_0168: call         bool [UnityEngine.IMGUIModule]UnityEngine.GUILayout::Toggle(bool, string, class [UnityEngine.IMGUIModule]UnityEngine.GUILayoutOption[])
        //     IL_016d: stsfld       bool KrokoshaCasualtiesMP.UIMainMenu::_USE_STEAM_MENU
        matcher.MatchForward(false, new CodeMatch(OpCodes.Call),
                new CodeMatch(OpCodes.Stsfld,
                    typeof(UIMainMenu).GetField("_USE_STEAM_MENU", BindingFlags.Static | BindingFlags.NonPublic)))
            .ThrowIfInvalid("Failed to find GUI Steam toggle!")
            .Advance(1)
            // UIMainMenu._USE_STEAM_MENU = IsMcAvailable || GUILayout.Toggle(num6 != 0, text, guiLayoutOptionArray);
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcAvailable),
                new CodeInstruction(OpCodes.Or));

        // This is the start of an else branch that draws the connection buttons.
        //
        //     IL_06b1: ldloc.s      num4
        //     IL_06b3: brtrue       IL_07ba
        //
        //     // [1026 13 - 1026 77]
        //     IL_06b8: ldsfld       bool KrokoshaCasualtiesMP.KrokoshaScavMultiplayer::SERVER_TOGGLE_SHOULD_HOST_DEDICATED
        matcher.MatchForward(false,
                new CodeMatch(OpCodes.Ldloc_S),
                new CodeMatch(OpCodes.Brtrue),
                new CodeMatch(OpCodes.Ldsfld,
                    typeof(KrokoshaScavMultiplayer).GetField("SERVER_TOGGLE_SHOULD_HOST_DEDICATED")))
            .ThrowIfInvalid("Failed to find start of GUI DirectConnect buttons!");

        // Need to preserve the label.
        var label = matcher.Labels;
        matcher.Labels = [];

        matcher.Insert(new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => DrawMcButton())));
        matcher.Labels = label;

        return matcher.Instructions();
    }

    [HarmonyPatch(nameof(UIMainMenu._GUI____RenderRuleField))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> DisableRuleChange(IEnumerable<CodeInstruction> instructions)
    {
        // can_edit = !LobbyManager.IsMcEnabled
        return new CodeMatcher(instructions).Start()
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcEnabled),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ceq),
                new CodeInstruction(OpCodes.Starg_S, 1))
            .Instructions();
    }

    [HarmonyPatch(nameof(UIMainMenu._GUI___DirectConnect_DoPasswordField))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> DisablePasswordField(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        return new CodeMatcher(instructions, generator)
            .Start()
            // if (IsMcAvailable) {
            //     FillEmpty();
            //     Return();
            // }
            .Insert(new CodeInstruction(OpCodes.Nop))
            .CreateLabel(out var label)
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcAvailable),
                new CodeInstruction(OpCodes.Brfalse, label),
                new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => FillEmpty())),
                new CodeInstruction(OpCodes.Ret))
            .Instructions();
    }

    private static void FillEmpty()
    {
        GUILayout.BeginHorizontal(GUILayout.Height(30f * UIMainMenu.GetMenuUIScale()));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    [HarmonyPatch(nameof(UIMainMenu._GUI___DirectConnect_DoSteamInfoo))]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> DisableLobbyType(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        // IL_0115: call         bool KrokoshaCasualtiesMP.Net::get_running()
        // IL_011a: brfalse.s    IL_0134

        // IL_011c: call         bool KrokoshaCasualtiesMP.Net::get_is_server()
        // IL_0121: brtrue.s     IL_0134

        // // [589 7 - 589 28]
        // IL_0123: ldstr        "  "

        return new CodeMatcher(instructions, generator)
            .MatchForward(false, new CodeMatch(OpCodes.Ldstr, "  "))
            .ThrowIfInvalid("Failed to find GUI empty for lobby!")
            .CreateLabel(out var fillEmptyLabel)
            .MatchBack(false,
                new CodeMatch(OpCodes.Call),
                new CodeMatch(OpCodes.Brfalse),
                new CodeMatch(OpCodes.Call),
                new CodeMatch(OpCodes.Brtrue))
            .ThrowIfInvalid("Failed to find start of if statement!")
            // if (IsMcAvailable) {
            //     jump fillEmptyLabel;
            // }
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Call, GetIsMcAvailable),
                new CodeInstruction(OpCodes.Brtrue, fillEmptyLabel))
            .Instructions();
    }

    /// <summary>
    ///     Draws the MassiveCasualties enabled/disable button.
    /// </summary>
    private static void DrawMcButton()
    {
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Massive Casualties: " + (LobbyManager.IsMcEnabled ? "Enabled" : "Disabled"),
                GUILayout.Height(50f * UIMainMenu.GetMenuUIScale())))
        {
            LobbyManager.IsMcEnabled = !LobbyManager.IsMcEnabled;
        }

        var oldColor = GUI.color;
        var oldLayout = GUI.skin.label.alignment;

        if (!KSteam.Loaded)
        {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.color = Color.red;

            GUILayout.Label("Steam is not available, so MassiveCasualties is disabled");
        }
        else if (LobbyManager.IsMcEnabled)
        {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.color = Color.white;

            GUILayout.Label("Steam is required for MassiveCasualties");
        }

        GUI.color = oldColor;
        GUI.skin.label.alignment = oldLayout;

        GUILayout.Space(15f * UIMainMenu.GetMenuUIScale());
        GUILayout.BeginHorizontal(GUILayout.Height(50f * UIMainMenu.GetMenuUIScale()));
    }
}