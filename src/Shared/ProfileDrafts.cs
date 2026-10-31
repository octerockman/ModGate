using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModGate
{
    // A retained preparation is never a profile-request.json and cannot run on startup.
    internal static class ProfileDrafts
    {
        private static string PathFor(string root, string slot)
        {
            if (!Enumerable.Range(1, 4).Any(i => slot == "server-" + i)) throw new InvalidDataException("Invalid prepared profile slot.");
            return SafePath.Under(root, "ModGate/prepared-profiles/" + slot + ".json");
        }
        internal static void Save(string root, ProfileCommand command)
        {
            if (Profiles.Pending(root)) throw new InvalidOperationException("Finish the scheduled installation before saving another setup.");
            Validate(root, command);
            if (List(root).Any(d => d.Target == command.Target && d.Profile!.ServerKey != command.Profile!.ServerKey)) throw new InvalidDataException("Another server already has a prepared setup saved in this slot. Resume it first or select a different slot.");
            Json.Save(PathFor(root, command.Target), command);
        }
        private static void Validate(string root, ProfileCommand command)
        {
            if (command.Kind != "install" || command.Profile == null || command.Install == null || command.Profile.ServerPolicy == null) throw new InvalidDataException("The prepared setup is incomplete.");
            Profiles.ValidatePreparedCommand(root, command);
            PolicyEngine.ValidatePolicy(command.Profile.ServerPolicy);
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + command.Install.Id), command.Install);
        }
        internal static ProfileCommand Read(string root, string slot)
        {
            var command = Json.ReadLocal<ProfileCommand>(PathFor(root, slot));
            if (command == null || command.Target != slot) throw new InvalidDataException("Prepared setup slot does not match its file.");
            Validate(root, command); return command;
        }
        internal static List<ProfileCommand> List(string root)
        {
            var result = new List<ProfileCommand>();
            foreach (var slot in Enumerable.Range(1, 4).Select(i => "server-" + i))
            {
                var path = PathFor(root, slot); if (!File.Exists(path)) continue;
                try
                {
                    var command = Json.ReadLocal<ProfileCommand>(path);
                    if (command != null && command.Target == slot && command.Kind == "install" && command.Profile?.Slot == slot && command.Install != null && Guid.TryParseExact(command.Install.Id, "N", out _)) result.Add(command);
                }
                catch (Exception failure) when (failure is IOException || failure is System.Runtime.Serialization.SerializationException || failure is InvalidDataException) { }
            }
            return result;
        }
        internal static bool Retains(string root, string id) => List(root).Any(d => d.Install!.Id == id);
        internal static void Forget(string root, string slot, string id)
        {
            if (List(root).Any(d => d.Target == slot && d.Install!.Id == id)) File.Delete(PathFor(root, slot));
        }
    }
}
