using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal sealed class SystemRequest
    {
        public Uri Url = null!;
        public string UserAgent = "ValheimModGate/" + Constants.Version;
        public long? RangeStart, RangeEnd;
        public string IfRange = "";
    }

    internal sealed class SystemResponse : IDisposable
    {
        public int StatusCode;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Stream Body = Stream.Null;
        public bool DisposeBody = true;
        public long? ContentLength => Headers.TryGetValue("Content-Length", out var text) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0 ? (long?)length : null;
        public void Dispose()
        {
            if (!DisposeBody) return;
            DisposeBody = false;
            Body.Dispose();
        }
    }

    // Keep this transport independent of managed HTTP assemblies during preloader startup.
    internal static class SystemHttps
    {
        internal static Task<SystemResponse> Send(SystemRequest request, CancellationToken token) => ReadProcess(StartInfo(request), token);

        internal static ProcessStartInfo StartInfo(SystemRequest request)
        {
            var url = request.Url ?? throw new InvalidDataException("Missing HTTPS address.");
            if (!url.IsAbsoluteUri || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0) throw new InvalidDataException("Native transport supports ordinary HTTPS GET requests only.");
            if (request.RangeStart < 0 || request.RangeEnd < 0 || request.RangeStart.HasValue && request.RangeEnd.HasValue && request.RangeEnd < request.RangeStart || !request.RangeStart.HasValue && request.RangeEnd == 0)
                throw new InvalidDataException("Invalid repository request range.");
            string? executable = null;
            var candidates = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe") }
                : new[] { "/usr/bin/curl", "/bin/curl" };
            foreach (var path in candidates) if (File.Exists(path)) { executable = path; break; }
            if (executable == null) throw new IOException("System curl was not found in the standard operating-system location.");
            var arguments = new List<string> { "--disable", "--silent", "--show-error", "--include", "--no-buffer", "--globoff", "--suppress-connect-headers", "--proto", "=https", "--tlsv1.2", "--connect-timeout", "30", "--speed-time", "120", "--speed-limit", "1", "--header", "Accept-Encoding: identity" };
            AddHeader(arguments, "User-Agent", request.UserAgent);
            if (request.RangeStart.HasValue || request.RangeEnd.HasValue)
                AddHeader(arguments, "Range", "bytes=" + (request.RangeStart?.ToString(CultureInfo.InvariantCulture) ?? "") + "-" + (request.RangeEnd?.ToString(CultureInfo.InvariantCulture) ?? ""));
            AddHeader(arguments, "If-Range", request.IfRange);
            arguments.Add("--url"); arguments.Add(url.AbsoluteUri);
            return new ProcessStartInfo { FileName = executable, Arguments = string.Join(" ", arguments.ConvertAll(Quote)), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        }

        private static void AddHeader(List<string> arguments, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new InvalidDataException("Invalid repository request header.");
            arguments.Add("--header"); arguments.Add(name + ": " + value);
        }
        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        internal static async Task<SystemResponse> ReadProcess(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var process = new Process { StartInfo = start };
            NativeBody? body = null;
            try
            {
                if (!process.Start()) throw new IOException("Could not start system HTTPS transport.");
                body = new NativeBody(process, token);
                var headerBytes = 0;
                while (true)
                {
                    var header = new List<byte>(); var one = new byte[1];
                    while (true)
                    {
                        if (headerBytes >= 65536) throw new InvalidDataException("Native HTTPS response headers exceed the limit.");
                        if (await body.ReadAsync(one, 0, 1, token).ConfigureAwait(false) == 0) throw new IOException("System HTTPS transport returned incomplete headers.");
                        headerBytes++; header.Add(one[0]); var n = header.Count;
                        if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var status = lines[0].Split(' ');
                    if (status.Length < 2 || !status[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(status[1], out var code) || code < 100 || code > 599) throw new InvalidDataException("Invalid system HTTPS status line.");
                    // Informational blocks share the body stream with the final response.
                    if (code < 200) continue;
                    var response = new SystemResponse { StatusCode = code, Body = body };
                    foreach (var line in lines)
                    {
                        var colon = line.IndexOf(':'); if (colon <= 0) continue;
                        var name = line.Substring(0, colon); var value = line.Substring(colon + 1).Trim();
                        // curl removes HTTP chunk framing; do not claim it remains in the pipe.
                        if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                        response.Headers[name] = response.Headers.TryGetValue(name, out var previous) ? previous + ", " + value : value;
                    }
                    return response;
                }
            }
            catch { if (body != null) body.Dispose(); else process.Dispose(); throw; }
        }

        internal static string Details(Exception error)
        {
            var parts = new List<string>();
            for (Exception? current = error; current != null && parts.Count < 8; current = current.InnerException)
            {
                var part = current.GetType().Name + ": " + current.Message.Replace('\r', ' ').Replace('\n', ' ');
                parts.Add(part.Length > 900 ? part.Substring(0, 900) : part);
            }
            return string.Join(" -> ", parts);
        }

        private sealed class NativeBody : Stream
        {
            private readonly Process process;
            private readonly Stream pipe;
            private readonly Task<string> stderr;
            private readonly CancellationTokenRegistration cancellation;
            private bool disposed, completed;
            public NativeBody(Process process, CancellationToken token)
            {
                this.process = process; pipe = process.StandardOutput.BaseStream;
                stderr = ReadError(process.StandardError);
                cancellation = token.Register(Kill);
            }
            private static async Task<string> ReadError(StreamReader reader)
            {
                var text = new StringBuilder(); var buffer = new char[1024]; int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    if (text.Length < 8192) text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
                return text.ToString().Trim();
            }
            private void Kill() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
            private async Task CheckEnd(int count)
            {
                if (count != 0 || completed) return;
                // stdout EOF normally follows curl's exit, but never block indefinitely on a helper.
                if (!process.WaitForExit(5000)) { Kill(); throw new IOException("System HTTPS process did not exit after its response."); }
                var detail = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("curl exit " + process.ExitCode + ": " + detail);
                completed = true;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (count == 0) return 0;
                using (token.Register(Kill))
                {
                    var read = await pipe.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); await CheckEnd(read).ConfigureAwait(false); return read;
                }
            }
            public override int Read(byte[] buffer, int offset, int count) { if (count == 0) return 0; var read = pipe.Read(buffer, offset, count); CheckEnd(read).GetAwaiter().GetResult(); return read; }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed) { disposed = true; cancellation.Dispose(); Kill(); pipe.Dispose(); process.Dispose(); }
                base.Dispose(disposing);
            }
            public override bool CanRead => !disposed; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
