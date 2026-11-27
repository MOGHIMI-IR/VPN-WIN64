[CmdletBinding()]
param(
    [string]$SingBox = (Join-Path $PSScriptRoot '../src/MixMoghimi/Assets/engine/sing-box.exe'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/public-node-tests.json'),
    [string]$PassedConfigsPath = (Join-Path $PSScriptRoot '../artifacts/public-test-configs.json'),
    [int]$MaxCandidates = 10
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $SingBox)) { throw "sing-box executable not found: $SingBox" }
if ($MaxCandidates -lt 1 -or $MaxCandidates -gt 30) { throw 'MaxCandidates must be between 1 and 30.' }

# Pull fresh public test nodes at build time. No proxy credentials are hard-coded in this repository.
# These nodes are untrusted, short-lived community infrastructure, not Moghimi-operated servers.
$FeedUrl = 'https://radarhub.top/sing-box.json'

function ConvertTo-Base64Url([string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertTo-SsUri($Outbound, [string]$Label) {
    $method = [string]$Outbound.method
    $password = [string]$Outbound.password
    $hostName = [string]$Outbound.server
    $port = 0
    if (-not [int]::TryParse([string]$Outbound.server_port, [ref]$port)) { throw 'Invalid server port' }
    if ([string]::IsNullOrWhiteSpace($hostName) -or $port -lt 1 -or $port -gt 65535) { throw 'Invalid server endpoint' }
    if ([string]::IsNullOrWhiteSpace($password) -or [string]::IsNullOrWhiteSpace($method)) { throw 'Missing Shadowsocks credentials' }
    $auth = ConvertTo-Base64Url ("$method`:$password")
    $uriHost = $hostName
    if ($hostName.Contains(':') -and -not $hostName.StartsWith('[')) { $uriHost = "[$hostName]" }
    $safeLabel = [Uri]::EscapeDataString($Label)
    return "ss://$auth@$uriHost`:$port#$safeLabel"
}

function Get-Candidates {
    $response = Invoke-RestMethod -Uri $FeedUrl -Method Get -TimeoutSec 35 -Headers @{ 'User-Agent' = 'MIX-MOGHIMI-Windows-CI/0.1' }
    if ($null -eq $response.outbounds -or $response.outbounds -isnot [System.Collections.IEnumerable]) {
        throw 'Public feed is not a valid sing-box JSON document (outbounds[] missing).'
    }

    $allowedMethods = @(
        '2022-blake3-aes-128-gcm', '2022-blake3-aes-256-gcm', '2022-blake3-chacha20-poly1305',
        'aes-128-gcm', 'aes-192-gcm', 'aes-256-gcm',
        'chacha20-ietf-poly1305', 'xchacha20-ietf-poly1305'
    )
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($outbound in $response.outbounds) {
        if ([string]$outbound.type -ne 'shadowsocks') { continue }
        # Skip chained/plugin nodes: this client currently implements plain Shadowsocks only.
        if ($outbound.detour -or $outbound.plugin -or $outbound.plugin_opts -or $outbound.transport) { continue }
        $method = ([string]$outbound.method).ToLowerInvariant()
        if ($allowedMethods -notcontains $method) { continue }
        if (-not $outbound.server -or -not $outbound.server_port -or -not $outbound.password) { continue }
        $tag = [string]$outbound.tag
        if ([string]::IsNullOrWhiteSpace($tag)) { $tag = 'SS' }
        $label = ('عمومی آزمایشی · ' + $tag).Substring(0, [Math]::Min(65, ('عمومی آزمایشی · ' + $tag).Length))
        try {
            $uri = ConvertTo-SsUri $outbound $label
            $items.Add([pscustomobject]@{
                Label = $label
                Host = [string]$outbound.server
                Port = [int]$outbound.server_port
                Method = $method
                Password = [string]$outbound.password
                Uri = $uri
            })
        } catch { continue }
        if ($items.Count -ge $MaxCandidates) { break }
    }
    return $items.ToArray()
}

$results = [System.Collections.Generic.List[object]]::new()
$passedConfigs = [System.Collections.Generic.List[object]]::new()
$root = Join-Path $env:RUNNER_TEMP 'mix-public-node-tests'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$portBase = 22100

try {
    $candidates = @(Get-Candidates)
    if ($candidates.Count -eq 0) { throw 'The public feed contained no compatible plain Shadowsocks outbounds.' }
} catch {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PassedConfigsPath) | Out-Null
    @([pscustomobject]@{ label = 'feed'; endpoint = ''; method = ''; result = 'feed_error'; detail = $_.Exception.Message }) |
        ConvertTo-Json -Depth 5 | Set-Content -Path $OutputPath -Encoding utf8
    '[]' | Set-Content -Path $PassedConfigsPath -Encoding utf8
    Write-Warning "Public feed could not be loaded: $($_.Exception.Message)"
    exit 0
}

$index = 0
foreach ($candidate in $candidates) {
    $index++
    $process = $null
    $proxyPort = $portBase + $index
    $configPath = Join-Path $root "node-$index.json"
    $stdout = Join-Path $root "node-$index.out.log"
    $stderr = Join-Path $root "node-$index.err.log"
    $status = 'failed'
    $detail = ''
    try {
        $config = @{
            log = @{ level = 'error'; timestamp = $true }
            inbounds = @(@{ type = 'mixed'; tag = 'local'; listen = '127.0.0.1'; listen_port = $proxyPort })
            outbounds = @(
                @{ type = 'shadowsocks'; tag = 'proxy'; server = $candidate.Host; server_port = $candidate.Port; method = $candidate.Method; password = $candidate.Password },
                @{ type = 'direct'; tag = 'direct' }
            )
            route = @{ final = 'proxy'; auto_detect_interface = $true }
        } | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))

        & $SingBox check -c $configPath *> $null
        if ($LASTEXITCODE -ne 0) { throw 'sing-box rejected this server configuration' }

        $process = Start-Process -FilePath $SingBox -ArgumentList @('run', '-c', $configPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $listening = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if ($process.HasExited) { throw "sing-box exited during startup ($($process.ExitCode))" }
            $tcp = [System.Net.Sockets.TcpClient]::new()
            try {
                $connect = $tcp.BeginConnect('127.0.0.1', $proxyPort, $null, $null)
                if ($connect.AsyncWaitHandle.WaitOne(250)) {
                    $tcp.EndConnect($connect)
                    $listening = $true
                    break
                }
            } catch { } finally { $tcp.Dispose() }
            Start-Sleep -Milliseconds 200
        }
        if (-not $listening) { throw 'local proxy listener did not start' }

        $probe = & curl.exe --silent --show-error --fail --proxy "http://127.0.0.1:$proxyPort" --connect-timeout 6 --max-time 14 'https://www.cloudflare.com/cdn-cgi/trace' 2>&1
        if ($LASTEXITCODE -ne 0 -or ($probe -notmatch '(^|\n)ip=')) {
            $detail = (($probe | Out-String).Trim())
            throw 'HTTPS connectivity probe through this proxy failed'
        }
        $status = 'passed'
        $detail = 'HTTPS request reached the external probe through sing-box'
        $passedConfigs.Add([pscustomobject]@{
            title = $candidate.Label
            config_text = $candidate.Uri
            priority = 0
            source = 'NodesRadar public feed; untrusted test node'
        })
    }
    catch {
        if (-not $detail) { $detail = $_.Exception.Message }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            try { $process.WaitForExit(3000) | Out-Null } catch { }
        }
        if (Test-Path $configPath) { Remove-Item $configPath -Force -ErrorAction SilentlyContinue }
    }

    $results.Add([pscustomobject]@{
        label = $candidate.Label
        endpoint = "$($candidate.Host):$($candidate.Port)"
        method = $candidate.Method
        result = $status
        detail = $detail
    })
    Write-Host ("{0} {1} {2}" -f $status.ToUpperInvariant(), $candidate.Label, "$($candidate.Host):$($candidate.Port)")
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PassedConfigsPath) | Out-Null
$results | ConvertTo-Json -Depth 5 | Set-Content -Path $OutputPath -Encoding utf8
$passedConfigs | ConvertTo-Json -Depth 5 | Set-Content -Path $PassedConfigsPath -Encoding utf8
$passed = @($results | Where-Object result -eq 'passed').Count
Write-Host "Public Shadowsocks connectivity: $passed / $($results.Count) passed."
Write-Host "Passed public test configs saved to: $PassedConfigsPath (credentials are not printed)."
# Public free proxies are volatile and sometimes blocked from CI. Record results without blocking the app build.
exit 0
