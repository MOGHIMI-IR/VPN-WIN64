using MixMoghimi.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MixMoghimi.Services;

public sealed class SingBoxService : IAsyncDisposable
{
    public const string CoreVersion = "1.14.4";
    private const string CoreZipSha256 = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df";
    private const string CoreZipUrl = "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip";

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIX-MOGHIMI", "runtime");
    private readonly HttpClient _downloadClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private WindowsKillJob? _processJob;
    private HttpClient? _controller;
    private CancellationTokenSource? _trafficCts;
    private Task? _trafficTask;
    private int _mixedPort;
    private long _downloadBytes;
    private long _uploadBytes;
    private string? _configPath;

    public event EventHandler? EngineExited;

    public int MixedPort => _mixedPort;
    public bool IsRunning => _process is { HasExited: false };
    public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
    public long UploadBytes => Interlocked.Read(ref _uploadBytes);

    public async Task StartAsync(ShadowsocksServer server, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) throw new InvalidOperationException("موتور اتصال از قبل فعال است.");
            Directory.CreateDirectory(_root);
            string corePath = await EnsureCoreAsync(cancellationToken);
            _mixedPort = GetFreePort();
            int clashPort = GetFreePort(exclude: _mixedPort);
            string clashSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            RuntimePorts ports = new(_mixedPort, clashPort, clashSecret);

            _configPath = Path.Combine(_root, "active-session.json");
            string config = SingBoxConfigFactory.Build(server, ports);
            await File.WriteAllTextAsync(_configPath, config, cancellationToken);
            File.SetAttributes(_configPath, FileAttributes.Hidden);

            await CheckConfigAsync(corePath, _configPath, cancellationToken);
            var start = new ProcessStartInfo
            {
                FileName = corePath,
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(_configPath);
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.Exited += (_, _) => EngineExited?.Invoke(this, EventArgs.Empty);
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            if (!_process.Start()) throw new InvalidOperationException("موتور sing-box اجرا نشد.");
            _processJob = WindowsKillJob.Assign(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _controller = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{clashPort}/"), Timeout = TimeSpan.FromSeconds(2) };
            _controller.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clashSecret);
            bool ready = false;
            for (int i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    throw new InvalidOperationException("موتور اتصال هنگام راه‌اندازی متوقف شد.");
                try
                {
                    using HttpResponseMessage response = await _controller.GetAsync("version", cancellationToken);
                    if (response.IsSuccessStatusCode) { ready = true; break; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(250, cancellationToken);
            }
            if (!ready) throw new TimeoutException("موتور اتصال در زمان مقرر آماده نشد.");

            _downloadBytes = 0;
            _uploadBytes = 0;
            _trafficCts = new CancellationTokenSource();
            _trafficTask = Task.Run(() => ReadTrafficStreamAsync(_controller, _trafficCts.Token));
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public static async Task<bool> TestConnectivityAsync(int proxyPort, CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}")
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        string[] probes =
        {
            "https://www.cloudflare.com/cdn-cgi/trace",
            "https://www.msftconnecttest.com/connecttest.txt"
        };
        foreach (string address in probes)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 200 and < 400) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        return false;
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        try { _trafficCts?.Cancel(); } catch { }
        if (_trafficTask is not null)
        {
            try { await _trafficTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }
        _trafficCts?.Dispose();
        _trafficCts = null;
        _trafficTask = null;
        _controller?.Dispose();
        _controller = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { _process.Dispose(); _process = null; }
        }
        _processJob?.Dispose();
        _processJob = null;

        if (_configPath is not null)
        {
            try { File.SetAttributes(_configPath, FileAttributes.Normal); File.Delete(_configPath); }
            catch (IOException) { }
            _configPath = null;
        }
    }

    private async Task ReadTrafficStreamAsync(HttpClient controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await controller.GetAsync("traffic", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    if (!TryParseTrafficSample(line, out long up, out long down)) continue;
                    AddSaturating(ref _uploadBytes, up);
                    AddSaturating(ref _downloadBytes, down);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (HttpRequestException) { }
            catch (IOException) { }
            try { await Task.Delay(600, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public static bool TryParseTrafficSample(string line, out long uploadBytes, out long downloadBytes)
    {
        uploadBytes = 0;
        downloadBytes = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        string payload = line.Trim();
        // sing-box Clash API currently emits newline-delimited JSON; accept SSE frames too.
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            payload = payload[5..].Trim();
        if (!payload.StartsWith('{')) return false;
        try
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            JsonElement root = json.RootElement;
            uploadBytes = ReadCounter(root, "up");
            downloadBytes = ReadCounter(root, "down");
            return true;
        }
        catch (JsonException)
        {
            uploadBytes = 0;
            downloadBytes = 0;
            return false;
        }
    }

    private static long ReadCounter(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number) ? Math.Max(0, number) : 0;

    private static void AddSaturating(ref long location, long delta)
    {
        if (delta <= 0) return;
        while (true)
        {
            long current = Interlocked.Read(ref location);
            long next = current > long.MaxValue - delta ? long.MaxValue : current + delta;
            if (Interlocked.CompareExchange(ref location, next, current) == current) return;
        }
    }

    private static async Task CheckConfigAsync(string corePath, string configPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = corePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("check");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("اعتبارسنجی موتور اجرا نشد.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            string details = (await stderr) + " " + (await stdout);
            throw new InvalidDataException("قالب کانفیگ با موتور sing-box سازگار نیست. " + SafeDiagnostic(details));
        }
    }

    private async Task<string> EnsureCoreAsync(CancellationToken cancellationToken)
    {
        string engineDirectory = Path.Combine(AppContext.BaseDirectory, "engine");
        string bundled = Path.Combine(engineDirectory, "sing-box.exe");
        if (File.Exists(bundled)) return bundled;

        string local = Path.Combine(_root, "engine", "sing-box.exe");
        if (File.Exists(local)) return local;
        string downloadDirectory = Path.Combine(_root, "download");
        Directory.CreateDirectory(downloadDirectory);
        string zipPath = Path.Combine(downloadDirectory, $"sing-box-{CoreVersion}.zip");
        await using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        using (HttpResponseMessage response = await _downloadClient.GetAsync(CoreZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(file, cancellationToken);
        }

        string hash;
        await using (var input = File.OpenRead(zipPath))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(CoreZipSha256)))
        {
            File.Delete(zipPath);
            throw new CryptographicException("فایل موتور از نظر SHA-256 معتبر نیست.");
        }

        string extractDirectory = Path.Combine(downloadDirectory, "extracted");
        if (Directory.Exists(extractDirectory)) Directory.Delete(extractDirectory, recursive: true);
        Directory.CreateDirectory(extractDirectory);
        ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);
        string? extractedExe = Directory.GetFiles(extractDirectory, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (extractedExe is null) throw new FileNotFoundException("فایل اجرایی sing-box در بسته پیدا نشد.");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.Copy(extractedExe, local, overwrite: true);
        return local;
    }

    private static int GetFreePort(int exclude = -1)
    {
        for (int i = 0; i < 20; i++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != exclude) return port;
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static string SafeDiagnostic(string message)
    {
        string compact = string.Join(' ', message.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return compact.Length > 260 ? compact[..260] : compact;
    }

    public static void RemoveStaleRuntimeFiles()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIX-MOGHIMI", "runtime");
        string file = Path.Combine(folder, "active-session.json");
        try { if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _downloadClient.Dispose();
        _lifecycle.Dispose();
    }
}
