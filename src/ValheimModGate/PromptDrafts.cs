using System;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private List<ProfileCommand> savedPreparations = new List<ProfileCommand>();
        private void SavePreparationForLater()
        {
            if (prepared == null || preparedProfile == null || Busy) return;
            var root = Paths.BepInExRootPath;
            var command = new ProfileCommand { Kind = "install", Target = preparedProfile.Slot, Profile = preparedProfile, Install = prepared, ReplaceConfig = !configSource.StartsWith("saved:", StringComparison.Ordinal) };
            RunFeature("Saving prepared profile for later…", token =>
            {
                token.ThrowIfCancellationRequested(); ProfileDrafts.Save(root, command);
                return () => { prepared = null; preparedProfile = null; preparedIssues.Clear(); OpenManager(); progress = "Prepared profile saved for later. Nothing is scheduled for the next restart."; };
            });
        }
        private void ResumePreparation(string slot)
        {
            var root = Paths.BepInExRootPath;
            RunFeature("Checking saved preparation…", token =>
            {
                token.ThrowIfCancellationRequested(); var draft = ProfileDrafts.Read(root, slot);
                return () =>
                {
                    var snapshot = draft.Profile!.ServerPolicy!; var inventory = Inventory.Loaded();
                    Show(snapshot, PolicyEngine.Compare(snapshot, inventory), inventory, draft.Profile.ServerKey);
                    prepared = draft.Install; preparedProfile = draft.Profile; configSource = draft.ReplaceConfig ? "active" : "saved:" + slot;
                    workflow = "Review"; progress = "Saved preparation restored. Install only when ready; server requirements may have changed since download.";
                };
            });
        }
        private void DrawSavedPreparations()
        {
            foreach (var draft in savedPreparations)
            {
                Label("Prepared for later: " + draft.Profile!.Name);
                Label("Downloaded and saved · Not active · Not scheduled");
                GUI.enabled = !Busy;
                var slot = draft.Target;
                if (LayoutButton("Resume saved setup (" + slot + ")")) ResumePreparation(slot);
                GUI.enabled = true;
            }
        }
    }
}
