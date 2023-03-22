﻿using HarmonyLib;
using KSP.Api.CoreTypes;
using KSP.Game.StartupFlow;
using SpaceWarp.API.UI;
using TMPro;

namespace SpaceWarp.Patching;

[HarmonyPatch(typeof(LandingHUD))]
[HarmonyPatch("Start")]
internal class MainMenuPatcher
{
    public static void Postfix(LandingHUD instance)
    {
        var menuItemsGroupTransform = instance.transform.FindChildEx("MenuItemsGroup");
        var singleplayerButtonTransform = menuItemsGroupTransform.FindChildEx("Singleplayer");

        foreach (var menuButtonToBeAdded in MainMenu.MenuButtonsToBeAdded)
        {
            var newButton =
                UnityObject.Instantiate(singleplayerButtonTransform.gameObject, menuItemsGroupTransform, false);
            newButton.name = menuButtonToBeAdded.name;

            // Move the button to be above the Exit button.
            newButton.transform.SetSiblingIndex(newButton.transform.GetSiblingIndex() - 1);

            // Rebind the button's action to call the action
            var uiAction = newButton.GetComponent<UIAction_Void_Button>();
            DelegateAction action = new();
            action.BindDelegate(() => menuButtonToBeAdded.onClicked.Invoke());
            uiAction.BindAction(action);

            // Set the label to "Mods".
            var tmp = newButton.GetComponentInChildren<TextMeshProUGUI>();

            tmp.SetText(menuButtonToBeAdded.name);
        }
    }
}