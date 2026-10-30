using HarmonyLib;
using UnityEngine;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private int gameplayBlockedThrough = -1;
        private static readonly AccessTools.FieldRef<Character, bool> BlockingState = AccessTools.FieldRefAccess<Character, bool>("m_blocking");
        private bool GameplayInputBlocked
        {
            get
            {
                if (Application.isBatchMode) return false;
                if (prompt?.IsVisible == true) { gameplayBlockedThrough = Time.frameCount + 1; return true; }
                return Time.frameCount <= gameplayBlockedThrough;
            }
        }
        private static bool BlockGameInput => Instance != null && Instance.GameplayInputBlocked;
        private static bool BlockGameKey => BlockGameInput && Instance.prompt?.ReadingPromptInput != true;
        private static void ClearLocalGameplayInput()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            // SetControls alone preserves autorun and a toggled block. Cancel both before
            // following the game's ordinary no-input path; leave physics and networking running.
            player.m_autoRun = false;
            BlockingState(player) = false;
            player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false, false);
        }
        [HarmonyPatch(typeof(PlayerController), "TakeInput"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepControllerInput(ref bool __result)
        {
            if (!BlockGameInput) return true;
            ClearLocalGameplayInput(); __result = false; return false;
        }
        [HarmonyPatch(typeof(Player), "SetControls"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void KeepLocalControlsNeutral(Player __instance, ref Vector3 movedir, ref bool attack, ref bool attackHold,
            ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold, ref bool jump,
            ref bool crouch, ref bool run, ref bool autoRun, ref bool dodge)
        {
            if (!BlockGameInput || __instance != Player.m_localPlayer) return;
            __instance.m_autoRun = false; BlockingState(__instance) = false;
            movedir = Vector3.zero;
            attack = attackHold = secondaryAttack = secondaryAttackHold = block = blockHold = jump = crouch = run = autoRun = dodge = false;
        }
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepCameraStill() => !BlockGameInput;
        private static bool SuppressButton(ref bool result) { if (!BlockGameInput) return true; result = false; return false; }
        private static bool SuppressKey(ref bool result) { if (!BlockGameKey) return true; result = false; return false; }
        [HarmonyPatch(typeof(ZInput), "GetButton"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameButton(ref bool __result) => SuppressButton(ref __result);
        [HarmonyPatch(typeof(ZInput), "GetButtonDown"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameButtonDown(ref bool __result) => SuppressButton(ref __result);
        [HarmonyPatch(typeof(ZInput), "GetButtonUp"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameButtonUp(ref bool __result) => SuppressButton(ref __result);
        [HarmonyPatch(typeof(ZInput), "GetKey"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameKey(ref bool __result) => SuppressKey(ref __result);
        [HarmonyPatch(typeof(ZInput), "GetKeyDown"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameKeyDown(ref bool __result) => SuppressKey(ref __result);
        [HarmonyPatch(typeof(ZInput), "GetKeyUp"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepGameKeyUp(ref bool __result) => SuppressKey(ref __result);
    }
}
