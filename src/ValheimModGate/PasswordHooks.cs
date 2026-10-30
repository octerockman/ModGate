using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private sealed class PasswordContext { public string Key = "", ServerId = "", Pending = ""; public bool Tried; }
        private readonly Dictionary<ZRpc, PasswordContext> passwordContexts = new Dictionary<ZRpc, PasswordContext>();
        private PasswordVault Vault => new PasswordVault(Paths.BepInExRootPath);
        private void DrawPasswordMemory()
        {
            if (Application.isBatchMode || prompt.IsVisible || !PasswordVault.Supported || ZNet.instance == null || !ZNet.instance.InPasswordDialog()) return;
            var rpc = AccessTools.Field(typeof(ZNet), "m_tempPasswordRPC").GetValue(ZNet.instance) as ZRpc;
            if (rpc == null || !passwordContexts.TryGetValue(rpc, out var context)) return;
            try
            {
                var oldEnabled = GUI.enabled;
                GUI.enabled = true;
                try {
                var before = Vault.Enabled(context.Key);
                var after = prompt.PasswordRememberToggle(new Rect(20, Screen.height - 122, 470, 40), before);
                if (after != before) Vault.Enable(context.Key, after);
                } finally { GUI.enabled = oldEnabled; }
            }
            catch { Logger.LogWarning("Could not update password remembering preferences."); }
        }
        [HarmonyPatch(typeof(ZNet), "RPC_ClientHandshake"), HarmonyPostfix]
        private static void FillSavedPassword(ZNet __instance, ZRpc rpc, bool needPassword)
        {
            if (__instance.IsServer() || !needPassword || !Instance.passwordContexts.TryGetValue(rpc, out var context) || context.Tried) return;
            context.Tried = true;
            if (AccessTools.Field(typeof(ZNet), "m_tempPasswordRPC").GetValue(__instance) == null) return;
            try
            {
                var password = Instance.Vault.Read(context.Key);
                if (!string.IsNullOrEmpty(password)) AccessTools.Method(typeof(ZNet), "OnPasswordEntered").Invoke(__instance, new object[] { password });
            }
            catch { Instance.Logger.LogWarning("Saved password could not be used. Enter it normally."); }
        }
        [HarmonyPatch(typeof(ZNet), "OnPasswordEntered"), HarmonyPrefix]
        private static void CaptureEnteredPassword(ZNet __instance, string pwd)
        {
            if (__instance.IsServer() || string.IsNullOrEmpty(pwd)) return;
            var rpc = AccessTools.Field(typeof(ZNet), "m_tempPasswordRPC").GetValue(__instance) as ZRpc;
            if (rpc != null && Instance.passwordContexts.TryGetValue(rpc, out var context)) context.Pending = pwd;
        }
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo"), HarmonyPostfix]
        private static void SaveAcceptedPassword(ZNet __instance, ZRpc rpc)
        {
            if (__instance.IsServer() || ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected || !Instance.passwordContexts.TryGetValue(rpc, out var context)) return;
            try
            {
                Instance.Vault.Save(context.Key, context.Pending);
                var catalog = Profiles.Load(Paths.BepInExRootPath);
                var profile = catalog.Servers.Find(p => p.ServerPolicy?.ServerId == context.ServerId);
                if (profile != null) { profile.CredentialKey = context.Key; Json.Save(SafePath.Under(Paths.BepInExRootPath, "ModGate/profiles.json"), catalog); }
            }
            catch { Instance.Logger.LogWarning("Password remembering could not save this entry. No plain-text password was written."); }
            finally { context.Pending = ""; }
        }
        [HarmonyPatch(typeof(ZNet), "RPC_Error"), HarmonyPostfix]
        private static void ForgetRejectedPassword(ZNet __instance, ZRpc rpc, int error)
        {
            if (__instance.IsServer() || (ZNet.ConnectionStatus)error != ZNet.ConnectionStatus.ErrorPassword || !Instance.passwordContexts.TryGetValue(rpc, out var context)) return;
            context.Pending = "";
            try { Instance.Vault.Forget(context.Key); } catch { Instance.Logger.LogWarning("Could not remove a rejected saved password."); }
            Instance.Logger.LogInfo("Saved password was rejected. Reconnect and enter the current server password.");
        }
    }
}
