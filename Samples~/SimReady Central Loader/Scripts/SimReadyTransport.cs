using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>How the downloader reaches the network, so tests can serve fixtures instead.</summary>
    public interface ISimReadyTransport
    {
        /// <summary>The body of a small text response (Hub API JSON), at most <paramref name="maxBytes"/>.</summary>
        Task<SimReadyResponse> GetTextAsync(string url, long maxBytes, CancellationToken cancellation);

        /// <summary>
        /// Streams a file to <paramref name="partPath"/>, appending from <paramref name="resumeFrom"/>
        /// when the server honours a range request, and refusing more than <paramref name="maxBytes"/>.
        /// </summary>
        Task<SimReadyResponse> DownloadAsync(string url, string partPath, long resumeFrom, long maxBytes,
                                             Action<long> bytesReceived, CancellationToken cancellation);
    }

    public sealed class SimReadyResponse
    {
        public long StatusCode;
        public string Text;
        public string Error;

        /// <summary>The server sent the rest of the file (206) rather than all of it (200).</summary>
        public bool Resumed;

        /// <summary>The Link header's rel="next" URL (Hub API pagination), if any.</summary>
        public string NextPage;

        public bool Ok => Error == null && StatusCode >= 200 && StatusCode < 300;
    }

    /// <summary>
    /// <see cref="UnityWebRequest"/> transport. Redirects are followed by hand so every hop is
    /// checked against the <see cref="SimReadyHostPolicy"/> before it is requested; a body is
    /// written only for the final response, and a size cap aborts the request as soon as the
    /// Content-Length or the bytes received pass it.
    /// </summary>
    public sealed class SimReadyWebTransport : ISimReadyTransport
    {
        private const int MaxRedirects = 5;
        private const int TimeoutSeconds = 120;

        private readonly SimReadyHostPolicy policy;

        public SimReadyWebTransport(SimReadyHostPolicy policy)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public async Task<SimReadyResponse> GetTextAsync(string url, long maxBytes, CancellationToken cancellation)
        {
            var sink = new MemorySink(maxBytes);
            SimReadyResponse response = await SendAsync(url, sink, 0, cancellation);
            if (response.Ok)
            {
                response.Text = Encoding.UTF8.GetString(sink.ToArray());
            }

            return response;
        }

        public async Task<SimReadyResponse> DownloadAsync(string url, string partPath, long resumeFrom, long maxBytes,
                                                          Action<long> bytesReceived, CancellationToken cancellation)
        {
            using (var sink = new FileSink(partPath, resumeFrom, maxBytes, bytesReceived))
            {
                SimReadyResponse response = await SendAsync(url, sink, resumeFrom, cancellation);
                response.Resumed = sink.Appended;
                return response;
            }
        }

        private async Task<SimReadyResponse> SendAsync(string url, ISink sink, long resumeFrom, CancellationToken cancellation)
        {
            string current = url;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!Uri.TryCreate(current, UriKind.Absolute, out Uri uri))
                {
                    return new SimReadyResponse { Error = $"Bad URL {current}" };
                }

                if (!policy.Permits(uri, out string refused))
                {
                    return new SimReadyResponse { Error = refused };
                }

                sink.Reset();
                using (var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbGET))
                {
                    var handler = new SinkHandler(sink, request);
                    request.downloadHandler = handler;
                    request.redirectLimit = 0;
                    request.timeout = TimeoutSeconds;
                    request.SetRequestHeader("Accept-Encoding", "identity");
                    if (resumeFrom > 0)
                    {
                        request.SetRequestHeader("Range", $"bytes={resumeFrom}-");
                    }

                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    using (cancellation.Register(() => request.Abort()))
                    {
                        while (!operation.isDone)
                        {
                            await Task.Yield();
                        }
                    }

                    cancellation.ThrowIfCancellationRequested();
                    long code = request.responseCode;
                    if (sink.CapExceeded)
                    {
                        return new SimReadyResponse { StatusCode = code, Error = $"{uri.AbsolutePath} is larger than the {Megabytes(sink.Cap)} cap." };
                    }

                    if (code >= 300 && code < 400)
                    {
                        string location = request.GetResponseHeader("Location");
                        if (string.IsNullOrEmpty(location))
                        {
                            return new SimReadyResponse { StatusCode = code, Error = $"Redirect without a Location from {uri.Host}." };
                        }

                        current = new Uri(uri, location).AbsoluteUri;
                        continue;
                    }

                    if (request.result == UnityWebRequest.Result.ConnectionError)
                    {
                        return new SimReadyResponse { StatusCode = code, Error = $"{uri.Host}: {request.error}" };
                    }

                    if (code < 200 || code >= 300)
                    {
                        return new SimReadyResponse { StatusCode = code, Error = $"HTTP {code} from {uri.Host} for {uri.AbsolutePath}." };
                    }

                    sink.Complete(code == 206);
                    return new SimReadyResponse { StatusCode = code, NextPage = NextLink(request.GetResponseHeader("Link"), uri) };
                }
            }

            return new SimReadyResponse { Error = $"More than {MaxRedirects} redirects for {url}." };
        }

        // RFC 8288: <https://...>; rel="next"
        private static string NextLink(string header, Uri baseUri)
        {
            if (string.IsNullOrEmpty(header))
            {
                return null;
            }

            foreach (string part in header.Split(','))
            {
                int open = part.IndexOf('<');
                int close = part.IndexOf('>');
                if (open >= 0 && close > open && part.IndexOf("rel=\"next\"", StringComparison.OrdinalIgnoreCase) > close)
                {
                    return new Uri(baseUri, part.Substring(open + 1, close - open - 1)).AbsoluteUri;
                }
            }

            return null;
        }

        public static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";

        // ---- Where the body goes ----

        private interface ISink
        {
            long Cap { get; }
            bool CapExceeded { get; }
            void Reset();
            bool Accepts(long code);
            bool Write(byte[] data, int length, long code);
            bool CheckLength(ulong contentLength);
            void Complete(bool partial);
        }

        private sealed class SinkHandler : DownloadHandlerScript
        {
            private readonly ISink sink;
            private readonly UnityWebRequest request;

            public SinkHandler(ISink sink, UnityWebRequest request)
                : base(new byte[64 * 1024])
            {
                this.sink = sink;
                this.request = request;
            }

            protected override void ReceiveContentLengthHeader(ulong contentLength)
            {
                if (sink.Accepts(request.responseCode) && !sink.CheckLength(contentLength))
                {
                    request.Abort();
                }
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                // A redirect's body is not the file.
                long code = request.responseCode;
                return !sink.Accepts(code) || sink.Write(data, dataLength, code);
            }
        }

        private sealed class MemorySink : ISink
        {
            private readonly MemoryStream stream = new MemoryStream();

            public MemorySink(long cap) => Cap = cap;

            public long Cap { get; }
            public bool CapExceeded { get; private set; }

            public void Reset()
            {
                stream.SetLength(0);
                CapExceeded = false;
            }

            public bool Accepts(long code) => code >= 200 && code < 300;

            public bool Write(byte[] data, int length, long code)
            {
                if (stream.Length + length > Cap)
                {
                    CapExceeded = true;
                    return false;
                }

                stream.Write(data, 0, length);
                return true;
            }

            public bool CheckLength(ulong contentLength)
            {
                CapExceeded = (long)contentLength > Cap;
                return !CapExceeded;
            }

            public void Complete(bool partial)
            {
            }

            public byte[] ToArray() => stream.ToArray();
        }

        private sealed class FileSink : ISink, IDisposable
        {
            private readonly string path;
            private readonly long resumeFrom;
            private readonly Action<long> progress;
            private FileStream stream;
            private long written;
            private bool decided;

            public FileSink(string path, long resumeFrom, long cap, Action<long> progress)
            {
                this.path = path;
                this.resumeFrom = resumeFrom;
                this.progress = progress;
                Cap = cap;
            }

            public long Cap { get; }
            public bool CapExceeded { get; private set; }
            public bool Appended { get; private set; }

            public void Reset()
            {
                stream?.Dispose();
                stream = null;
                decided = false;
                written = 0;
                CapExceeded = false;
            }

            public bool Accepts(long code) => code >= 200 && code < 300;

            public bool CheckLength(ulong contentLength)
            {
                // A range response's length is the remainder; a full response's the whole file.
                CapExceeded = (long)contentLength > Cap;
                return !CapExceeded;
            }

            public bool Write(byte[] data, int length, long code)
            {
                if (!decided)
                {
                    // 206 continues the part file; 200 (the server ignored the range) starts it over.
                    decided = true;
                    Appended = resumeFrom > 0 && code == 206;
                    stream = new FileStream(path, Appended ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                }

                long total = (Appended ? resumeFrom : 0) + written + length;
                if (total > Cap)
                {
                    CapExceeded = true;
                    return false;
                }

                stream.Write(data, 0, length);
                written += length;
                progress?.Invoke(total);
                return true;
            }

            public void Complete(bool partial)
            {
                if (!decided)
                {
                    // An empty body: an empty file (or nothing more to append).
                    Appended = partial && resumeFrom > 0;
                    if (!Appended)
                    {
                        File.WriteAllBytes(path, Array.Empty<byte>());
                    }
                }

                stream?.Flush();
            }

            public void Dispose() => stream?.Dispose();
        }
    }
}
