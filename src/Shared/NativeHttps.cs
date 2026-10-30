using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    // Unity's Mono certificate store can differ from the host's. The fallback uses the host's
    // curl with normal certificate verification; redirects still go through RepositorySource.
    internal sealed class NativeHttps : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fallback;
        public NativeHttps(HttpMessageHandler primary, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fallback = null) : base(primary)
        { this.fallback = fallback ?? SendNative; }

        internal static HttpMessageHandler Handler()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            // Some Unity/Mono builds do not implement this setter. Keep their platform default
            // and use the independently validated native transport if the handshake fails.
            try { handler.SslProtocols = SslProtocols.Tls12; } catch (NotSupportedException) { } catch (NotImplementedException) { }
            return new NativeHttps(handler);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try { return await base.SendAsync(request, token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && TlsFailure(error))
            {
                try { return await fallback(request, token).ConfigureAwait(false); }
                catch (Exception native) when (!(native is OperationCanceledException))
                { throw new HttpRequestException("HTTPS failed in Unity/Mono and the secure system fallback. Managed: " + Details(error) + "; system: " + Details(native) + ". Check the host clock, CA certificates and HTTPS access; ask the host to provide curl if unavailable.", native); }
            }
        }
        internal static bool TlsFailure(Exception error)
        {
            for (Exception? current = error; current != null; current = current.InnerException)
            {
                if (current is AuthenticationException || current is WebException web && (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure)) return true;
                var text = current.Message.ToLowerInvariant();
                if (text.Contains("ssl") || text.Contains("tls") || text.Contains("certificate")) return true;
            }
            return false;
        }
        internal static string Details(Exception error) => SystemHttps.Details(error);
        internal static ProcessStartInfo StartInfo(HttpRequestMessage request) => SystemHttps.StartInfo(ToSystemRequest(request));

        private static SystemRequest ToSystemRequest(HttpRequestMessage request)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidDataException("Native transport supports ordinary HTTPS GET requests only.");
            var native = new SystemRequest { Url = request.RequestUri ?? throw new InvalidDataException("Missing HTTPS address.") };
            if (request.Headers.TryGetValues("User-Agent", out var agent)) native.UserAgent = string.Join(" ", agent);
            if (request.Headers.TryGetValues("If-Range", out var validator)) native.IfRange = string.Join(", ", validator);
            var range = request.Headers.Range;
            if (range != null)
            {
                if (!range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.Ranges.Count != 1) throw new InvalidDataException("Native transport supports one byte range per request.");
                foreach (var item in range.Ranges) { native.RangeStart = item.From; native.RangeEnd = item.To; }
            }
            return native;
        }
        private static Task<HttpResponseMessage> SendNative(HttpRequestMessage request, CancellationToken token) => ReadProcess(StartInfo(request), token);

        internal static async Task<HttpResponseMessage> ReadProcess(ProcessStartInfo start, CancellationToken token)
        {
            var native = await SystemHttps.ReadProcess(start, token).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                response = new HttpResponseMessage((HttpStatusCode)native.StatusCode) { Content = new StreamContent(native.Body) };
                native.DisposeBody = false;
                foreach (var header in native.Headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return response;
            }
            catch { response?.Dispose(); throw; }
            finally { native.Dispose(); }
        }
    }
}
