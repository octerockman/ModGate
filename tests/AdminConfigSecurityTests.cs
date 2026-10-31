using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ModGate;

internal static partial class Program
{
    private static AdminConfigRequest SecurityRequest(string? nonce = null) => new AdminConfigRequest
    {
        Nonce = nonce ?? Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "read", Path = "example.cfg"
    };
    private static AdminConfigRequest SignedRequest(string key, string nonce)
    {
        var request = SecurityRequest(nonce); request.Proof = AdminConfigSecurity.Sign(request, key); return request;
    }
    private static AdminConfigRequest SignedSequenceRequest(string key, string nonce, long sequence)
    {
        var request = new AdminConfigRequest { Nonce = nonce, RequestId = Guid.NewGuid().ToString("N"), Operation = "health", Sequence = sequence };
        request.Proof = AdminConfigSecurity.Sign(request, key); return request;
    }
    private static void AdminConfigSecurityChecks()
    {
        Test("Unix owner-key writes complete interrupted short writes and flush without a managed file-handle wrapper", () =>
        {
            var content = System.Text.Encoding.ASCII.GetBytes(new string('a',64) + "\n");
            using var actual = new MemoryStream(); var writes = 0; var flushes = 0;
            AdminConfigSecurity.WriteUnixKey(123, content, (fd, pointer, length) =>
            {
                Check(fd == 123); if (++writes == 1) return new IntPtr(-1);
                var count = Math.Min(7, (int)length.ToUInt64()); var chunk = new byte[count];
                System.Runtime.InteropServices.Marshal.Copy(pointer, chunk, 0, count); actual.Write(chunk); return new IntPtr(count);
            }, fd => { Check(fd == 123); return ++flushes == 1 ? -1 : 0; }, () => 4);
            Check(content.SequenceEqual(actual.ToArray()) && writes > 2 && flushes == 2);
        });
        Test("Unix owner-key write and flush failures surface instead of reading back an empty key", () =>
        {
            var content = System.Text.Encoding.ASCII.GetBytes(new string('a',64)); var flushed = false;
            Throws<IOException>(() => AdminConfigSecurity.WriteUnixKey(123, content, (_,_,_) => new IntPtr(-1), _ => { flushed = true; return 0; }, () => 13));
            Check(!flushed);
            Throws<IOException>(() => AdminConfigSecurity.WriteUnixKey(123, content, (_,_,_) => IntPtr.Zero, _ => 0, () => 0));
            Throws<IOException>(() => AdminConfigSecurity.WriteUnixKey(123, content, (_,_,length) => new IntPtr((long)length.ToUInt64()), _ => -1, () => 5));
        });
        Test("an invalid owner key stays protected while normal server config reconciliation adds self-update settings", () =>
        {
            var root = Root(); var keyPath = Put(root, AdminConfigSecurity.OwnerKeyPath, ""); string diagnostic = "";
            Check(!AdminConfigSecurity.TryEnsureOwnerKey(root, message => diagnostic = message));
            Check(diagnostic.Contains("Mod-list discovery will continue") && File.ReadAllText(keyPath) == "");
            var manifestPath = SafePath.Under(root, "config/ModGate/server-mods.json");
            ServerConfig.Save(manifestPath, root, AutoSetup.Reconcile(new ServerManifest(), Array.Empty<ModEntry>(), Array.Empty<string>()));
            var manifest = File.ReadAllText(manifestPath);
            Check(manifest.Contains("\"autoUpdateModGate\": false") && manifest.Contains("\"modGateIncludePrereleases\""));
            Throws<InvalidDataException>(() => AdminConfigSecurity.ReadOwnerKey(root));
            File.Move(keyPath, keyPath + ".backup");
            Check(AdminConfigSecurity.TryEnsureOwnerKey(root, _ => { }) && AdminConfigSecurity.ReadOwnerKey(root).Length == 64 && File.ReadAllText(keyPath + ".backup") == "");
        });
        Test("owner access key is generated privately once and never included in support exports", () =>
        {
            var root = Root(); var key = AdminConfigSecurity.EnsureOwnerKey(root);
            Check(key.Length == 64 && key.All(Uri.IsHexDigit) && key == AdminConfigSecurity.EnsureOwnerKey(root) && key == AdminConfigSecurity.ReadOwnerKey(root));
            Check(File.Exists(SafePath.Under(root, AdminConfigSecurity.OwnerKeyPath)) && SupportReport.AvailableLogs(root).Count == 0);
            var report = SupportReport.Create(root, Path.Combine(root, "support.zip"), new SupportReportOptions());
            using var zip = ZipFile.OpenRead(report);
            Check(zip.Entries.All(e => !e.FullName.Contains("admin-access")));
            foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); Check(!reader.ReadToEnd().Contains(key)); }
        });
        Test("an invalid existing owner key fails closed rather than being silently replaced", () =>
        {
            var root = Root(); var path = Put(root, AdminConfigSecurity.OwnerKeyPath, "keep-invalid-owner-file");
            Throws<InvalidDataException>(() => AdminConfigSecurity.EnsureOwnerKey(root));
            Check(File.ReadAllText(path) == "keep-invalid-owner-file");
        });
        Test("admin proof validates exact payload and connection nonce without mutating the request", () =>
        {
            var key = new string('a', 64); var request = SecurityRequest();
            var before = Json.Write(request); var proof = AdminConfigSecurity.Sign(request, key);
            Check(Json.Write(request) == before && proof.Length == 64 && !proof.Contains(key));
            request.Proof = proof;
            Check(AdminConfigSecurity.Verify(request, key, request.Nonce));
            Check(!AdminConfigSecurity.Verify(request, new string('b', 64), request.Nonce));
            Check(!AdminConfigSecurity.Verify(request, key, Guid.NewGuid().ToString("N")));
            request.Path = "other.cfg"; Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
        });
        Test("admin proof covers operation revision field patches and raw policy text", () =>
        {
            var key = new string('c', 64);
            var request = SecurityRequest(); request.Operation = "save"; request.ExpectedHash = new string('d', 64); request.Changes.Add(new AdminConfigChange { Id = "0", Value = "original" });
            request.Proof = AdminConfigSecurity.Sign(request, key);
            Check(AdminConfigSecurity.Verify(request, key, request.Nonce));
            var changed = Json.CloneLocal(request); changed.Changes[0].Value = "tampered"; Check(!AdminConfigSecurity.Verify(changed, key, changed.Nonce));
            changed = Json.CloneLocal(request); changed.ExpectedHash = new string('e', 64); Check(!AdminConfigSecurity.Verify(changed, key, changed.Nonce));
            changed = Json.CloneLocal(request); changed.Operation = "read"; Check(!AdminConfigSecurity.Verify(changed, key, changed.Nonce));
            request.Path = AdminConfigProtocol.ServerModsPath; request.Changes.Clear(); request.Text = "{}"; request.Proof = ""; request.Proof = AdminConfigSecurity.Sign(request, key);
            Check(AdminConfigSecurity.Verify(request, key, request.Nonce)); request.Text = "{\"changed\":true}"; Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
        });
        Test("malformed signatures schema request IDs and unsigned requests are denied", () =>
        {
            var key = new string('f', 64); var request = SecurityRequest();
            Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
            request.Proof = new string('0', 64); Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
            request.Proof = new string('g', 64); Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
            request = SignedRequest(key, Guid.NewGuid().ToString("N")); request.Version = 2; Check(!AdminConfigSecurity.Verify(request, key, request.Nonce));
            request = SecurityRequest(); request.RequestId = "claimed-player-id"; Throws<InvalidDataException>(() => AdminConfigSecurity.Sign(request, key));
            Throws<InvalidDataException>(() => AdminConfigSecurity.Sign(SecurityRequest(), "short-key"));
        });
        Test("owner proof never substitutes for server admin membership or a live connection", () =>
        {
            var key = new string('1', 64); var nonce = Guid.NewGuid().ToString("N"); var request = SignedRequest(key, nonce);
            var claimedRoleJson = Json.Write(request).TrimEnd('}') + ",\"IsAdmin\":true,\"Role\":\"owner\"}";
            var forgedRole = Json.Read<AdminConfigRequest>(claimedRoleJson);
            var guard = new AdminConfigRequestGuard();
            Check(!guard.Authorize(forgedRole, key, nonce, false, true));
            Check(!guard.Authorize(request, key, nonce, true, false));
            Check(guard.Authorize(request, key, nonce, true, true));
        });
        Test("admin request replay remains denied after the rate window expires", () =>
        {
            var key = new string('2', 64); var nonce = Guid.NewGuid().ToString("N"); var request = SignedRequest(key, nonce); var now = DateTime.UtcNow;
            var guard = new AdminConfigRequestGuard();
            Check(guard.Authorize(request, key, nonce, true, true, now));
            Check(!guard.Authorize(request, key, nonce, true, true, now));
            Check(!guard.Authorize(request, key, nonce, true, true, now.AddHours(1)));
            Check(!new AdminConfigRequestGuard().Authorize(request, key, Guid.NewGuid().ToString("N"), true, true, now));
        });
        Test("admin requests are rate limited and session replay memory never evicts accepted proofs", () =>
        {
            var key = new string('3', 64); var nonce = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow;
            var guard = new AdminConfigRequestGuard(2, TimeSpan.FromSeconds(10));
            Check(guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now));
            Check(guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now));
            Check(!guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now));
            Check(guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now.AddSeconds(11)));
            var bounded = new AdminConfigRequestGuard(maxSessionRequests: 1);
            Check(bounded.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now));
            Check(!bounded.Authorize(SignedRequest(key, nonce), key, nonce, true, true, now.AddHours(1)));
        });
        Test("signed health sequences continue beyond 4096 requests without exhausting legacy replay capacity", () =>
        {
            var key = new string('4', 64); var nonce = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow;
            var guard = new AdminConfigRequestGuard(2, TimeSpan.FromSeconds(1), maxSessionRequests: 1);
            for (long sequence = 1; sequence <= 4105; sequence++)
                Check(guard.Authorize(SignedSequenceRequest(key, nonce, sequence), key, nonce, true, true, now.AddSeconds(sequence * 2)));
        });
        Test("accepted sequences cannot replay or arrive out of order even with a fresh signed request ID", () =>
        {
            var key = new string('5', 64); var nonce = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow; var guard = new AdminConfigRequestGuard();
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 1), key, nonce, true, true, now));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 3), key, nonce, true, true, now));
            Check(!guard.Authorize(SignedSequenceRequest(key, nonce, 1), key, nonce, true, true, now.AddHours(1)));
            Check(!guard.Authorize(SignedSequenceRequest(key, nonce, 2), key, nonce, true, true, now.AddHours(1)));
            Check(!guard.Authorize(SignedSequenceRequest(key, nonce, 3), key, nonce, true, true, now.AddHours(1)));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 4), key, nonce, true, true, now.AddHours(1)));
        });
        Test("the request proof covers its sequence and a failed high sequence cannot consume the watermark", () =>
        {
            var key = new string('6', 64); var nonce = Guid.NewGuid().ToString("N"); var guard = new AdminConfigRequestGuard();
            var changed = SignedSequenceRequest(key, nonce, 1); changed.Sequence = 1000;
            Check(!AdminConfigSecurity.Verify(changed, key, nonce) && !guard.Authorize(changed, key, nonce, true, true));
            var forged = SignedSequenceRequest(new string('7', 64), nonce, long.MaxValue);
            Check(!guard.Authorize(forged, key, nonce, true, true));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 1), key, nonce, true, true));
        });
        Test("rate denied disconnected and non-admin requests never advance the signed sequence", () =>
        {
            var key = new string('8', 64); var nonce = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow;
            var guard = new AdminConfigRequestGuard(1, TimeSpan.FromSeconds(10));
            Check(!guard.Authorize(SignedSequenceRequest(key, nonce, 50), key, nonce, false, true, now));
            Check(!guard.Authorize(SignedSequenceRequest(key, nonce, 50), key, nonce, true, false, now));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 1), key, nonce, true, true, now));
            var next = SignedSequenceRequest(key, nonce, 2); Check(!guard.Authorize(next, key, nonce, true, true, now));
            Check(guard.Authorize(next, key, nonce, true, true, now.AddSeconds(10)));
        });
        Test("positive sequences can replace a full legacy cache and then permanently reject zero-sequence fallback", () =>
        {
            var key = new string('9', 64); var nonce = Guid.NewGuid().ToString("N"); var guard = new AdminConfigRequestGuard(maxSessionRequests: 1);
            Check(guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true));
            Check(!guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 1), key, nonce, true, true));
            Check(!guard.Authorize(SignedRequest(key, nonce), key, nonce, true, true));
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 2), key, nonce, true, true));
        });
        Test("sequence watermarks remain bound to one nonce and reset only with a fresh connection guard", () =>
        {
            var key = new string('a', 64); var nonce = Guid.NewGuid().ToString("N"); var nextNonce = Guid.NewGuid().ToString("N"); var guard = new AdminConfigRequestGuard();
            Check(guard.Authorize(SignedSequenceRequest(key, nonce, 100), key, nonce, true, true));
            Check(!guard.Authorize(SignedSequenceRequest(key, nextNonce, 101), key, nextNonce, true, true));
            Check(!guard.Authorize(SignedSequenceRequest(key, nextNonce, 1), key, nextNonce, true, true));
            Check(new AdminConfigRequestGuard().Authorize(SignedSequenceRequest(key, nextNonce, 1), key, nextNonce, true, true));
        });
        Test("negative sequences are invalid on the wire and cannot be signed or authorized", () =>
        {
            foreach (var sequence in new[] { -1L, long.MinValue })
            {
                var request = SecurityRequest(); request.Sequence = sequence;
                Throws<InvalidDataException>(() => AdminConfigProtocol.ReadRequest(Json.Write(request)));
                Throws<InvalidDataException>(() => AdminConfigSecurity.Sign(request, new string('b', 64)));
                Check(!new AdminConfigRequestGuard().Authorize(request, new string('b', 64), request.Nonce, true, true));
            }
        });
        Test("legacy zero sequences retain their original signed payload shape until positive sequences are adopted", () =>
        {
            var key = new string('c', 64); var nonce = Guid.NewGuid().ToString("N"); var legacy = SignedRequest(key, nonce);
            Check(legacy.Sequence == 0 && !Json.Write(legacy).Contains("\"Sequence\"") && AdminConfigSecurity.Verify(legacy, key, nonce));
            var positive = SignedSequenceRequest(key, nonce, 5); var copy = AdminConfigProtocol.ReadRequest(Json.Write(positive));
            Check(copy.Sequence == 5 && Json.Write(copy).Contains("\"Sequence\":5") && AdminConfigSecurity.Verify(copy, key, nonce));
        });
    }
}
