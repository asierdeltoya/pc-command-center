using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;

namespace PCCommandCenter.Network;

/// <summary>
/// Measures latency, jitter, download and upload throughput against Cloudflare's public speed-test service
/// (the same endpoints used by speed.cloudflare.com). Runs only when the user starts it.
/// </summary>
public sealed partial class InternetSpeedTest : IInternetSpeedTest
{
    private const string Base = "https://speed.cloudflare.com";
    private static readonly TimeSpan DownloadDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadDuration = TimeSpan.FromSeconds(8);
    private const long MaxBytesPerPhase = 1_500_000_000; // safety cap on very fast links

    public string Provider => "Cloudflare (speed.cloudflare.com)";

    [GeneratedRegex(@"dur=([\d.]+)")]
    private static partial Regex ServerTimingRegex();

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", "PCCommandCenter/1.0 speedtest" } } };

    public async Task<InternetSpeedResult> RunAsync(IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        using var http = CreateClient();
        string? colo = null;
        try
        {
            var trace = await http.GetStringAsync($"{Base}/cdn-cgi/trace", ct).ConfigureAwait(false);
            colo = trace.Split('\n').FirstOrDefault(l => l.StartsWith("colo="))?[5..].Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Speed test server unreachable", ex);
            return new InternetSpeedResult { Server = Provider, Error = Loc.T("Unable to reach the speed test server. Check your internet connection.") };
        }

        try
        {
            // 1. Idle latency
            progress?.Report(new SpeedProgress(Loc.T("Measuring latency"), 0, null));
            var idle = await MeasureLatencyAsync(http, 12, ct).ConfigureAwait(false);

            // 2. Download (with loaded-latency probes in parallel)
            var loaded = new List<double>();
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var prober = Task.Run(async () =>
            {
                while (!probeCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(700, probeCts.Token).ConfigureAwait(false);
                        var l = await MeasureLatencyAsync(http, 1, probeCts.Token).ConfigureAwait(false);
                        if (l.Samples.Count > 0) lock (loaded) loaded.Add(l.Samples[0]);
                    }
                    catch { }
                }
            });
            var (down, downBytes) = await MeasureThroughputAsync(http, upload: false, DownloadDuration, 6, progress, Loc.T("Testing download"), ct).ConfigureAwait(false);
            probeCts.Cancel();
            try { await prober.ConfigureAwait(false); } catch { }

            // 3. Upload
            var (up, upBytes) = await MeasureThroughputAsync(http, upload: true, UploadDuration, 4, progress, Loc.T("Testing upload"), ct).ConfigureAwait(false);

            progress?.Report(new SpeedProgress(Loc.T("Done"), 1, null));
            var result = new InternetSpeedResult
            {
                DownloadMbps = down, UploadMbps = up, LatencyMs = idle.Median, JitterMs = idle.Jitter,
                LoadedLatencyMs = loaded.Count > 0 ? Median(loaded) : null,
                BytesDownloaded = downBytes, BytesUploaded = upBytes, Server = Provider, Colo = colo,
            };
            AppLog.Info($"Speed test: down {down:0.0} Mbps, up {up:0.0} Mbps, latency {idle.Median:0.0} ms, colo {colo}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Speed test failed", ex);
            return new InternetSpeedResult { Server = Provider, Colo = colo, Error = OperationResult.FriendlyError(ex) };
        }
    }

    private sealed record Latency(List<double> Samples, double? Median, double? Jitter);

    private static async Task<Latency> MeasureLatencyAsync(HttpClient http, int count, CancellationToken ct)
    {
        var samples = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            double serverMs = 0;
            // Server-Timing lists several stages (edge, worker…); all of them are server time, not network time.
            if (resp.Headers.TryGetValues("Server-Timing", out var timing))
                foreach (Match m in ServerTimingRegex().Matches(string.Join(",", timing)))
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) serverMs += ms;
            if (i == 0 && count > 1) continue; // first request includes the TLS handshake
            samples.Add(Math.Max(0.1, sw.Elapsed.TotalMilliseconds - serverMs));
        }
        double? jitter = samples.Count > 1 ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        if (count > 1) AppLog.Info($"Speed test latency samples (ms): {string.Join(", ", samples.Select(s => s.ToString("0.0")))}");
        return new Latency(samples, samples.Count > 0 ? Median(samples) : null, jitter);
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Saturates the link with parallel streams for a fixed time; the first second is excluded as ramp-up.</summary>
    private static async Task<(double? Mbps, long Bytes)> MeasureThroughputAsync(HttpClient http, bool upload, TimeSpan duration, int streams,
        IProgress<SpeedProgress>? progress, string phase, CancellationToken ct)
    {
        long total = 0;
        long measuredStartBytes = -1;
        var clock = Stopwatch.StartNew();
        double measuredStartSec = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        var payload = new byte[upload ? 8_000_000 : 0];
        if (upload) Random.Shared.NextBytes(payload);

        async Task Worker()
        {
            var buffer = new byte[128 * 1024];
            while (!stop.IsCancellationRequested && Interlocked.Read(ref total) < MaxBytesPerPhase)
            {
                try
                {
                    if (upload)
                    {
                        using var content = new CountingContent(payload, n => Interlocked.Add(ref total, n));
                        using var resp = await http.PostAsync($"{Base}/__up", content, stop.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var resp = await http.GetAsync($"{Base}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                        using var stream = await resp.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                        int read;
                        while ((read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                            Interlocked.Add(ref total, read);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException) { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
            }
        }

        var workers = Enumerable.Range(0, streams).Select(_ => Task.Run(Worker)).ToList();
        long lastBytes = 0; double lastSec = 0;
        while (!workers.All(w => w.IsCompleted))
        {
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            double sec = clock.Elapsed.TotalSeconds;
            long bytes = Interlocked.Read(ref total);
            if (measuredStartBytes < 0 && sec >= 1) { measuredStartBytes = bytes; measuredStartSec = sec; }
            double instant = sec > lastSec ? (bytes - lastBytes) * 8 / 1e6 / (sec - lastSec) : 0;
            lastBytes = bytes; lastSec = sec;
            progress?.Report(new SpeedProgress(phase, Math.Min(1, sec / duration.TotalSeconds), instant));
        }
        ct.ThrowIfCancellationRequested();
        double elapsed = clock.Elapsed.TotalSeconds - measuredStartSec;
        long counted = total - Math.Max(0, measuredStartBytes);
        double? mbps = elapsed > 0.5 && counted > 0 ? counted * 8 / 1e6 / elapsed : null;
        return (mbps, total);
    }

    /// <summary>Upload body that reports bytes as they are actually written to the socket.</summary>
    private sealed class CountingContent(byte[] data, Action<int> onWritten) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            const int chunk = 64 * 1024;
            for (int offset = 0; offset < data.Length; offset += chunk)
            {
                int n = Math.Min(chunk, data.Length - offset);
                await stream.WriteAsync(data.AsMemory(offset, n), ct).ConfigureAwait(false);
                onWritten(n);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length) { length = data.Length; return true; }

        public CountingContent Init() { Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); return this; }
    }
}
