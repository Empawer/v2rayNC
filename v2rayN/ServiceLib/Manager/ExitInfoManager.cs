namespace ServiceLib.Manager;

/// <summary>Exit IP + country / city of the running config.</summary>
public sealed record ExitInfoResult(string Ip, string Country, string City, string CountryCode)
{
    public string Location
    {
        get
        {
            var country = Country.IsNotEmpty() ? Country : CountryCode;
            if (country.IsNotEmpty() && City.IsNotEmpty())
            {
                return $"{country}, {City}";
            }
            return City.IsNotEmpty() ? City : country;
        }
    }
}

/// <summary>
/// Finds the exit IP and the country / city of the config that is currently running
/// (only the active config, never the others).
///
/// Technique: a request to a public geo-IP API is sent through the local SOCKS5 inbound of
/// the running core, so it leaves via the proxy and the answer describes the EXIT server.
/// The sources are tried one by one in priority order until one of them answers
/// (ipwho.is, ipapi.co, api.myip.com, ipinfo.io, Cloudflare trace, ip-api.com).
/// </summary>
public sealed class ExitInfoManager
{
    private static readonly Lazy<ExitInfoManager> _instance = new(() => new());
    public static ExitInfoManager Instance => _instance.Value;
    private static readonly string _tag = "ExitInfoManager";

    private const int PerSourceTimeoutSeconds = 6;
    private const int RoundDeadlineSeconds = 20;
    private const int MaxRounds = 2;
    private const string Loading = "...";
    private const string Failed = "-";

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private string _indexId = string.Empty;
    private ExitInfoResult? _result;
    private bool _loading;

    #region State (read by the profile list)

    /// <summary>Text for the "Exit IP" column ("" for every config except the running one).</summary>
    public string GetIpText(string indexId)
    {
        lock (_lock)
        {
            if (indexId.IsNullOrEmpty() || _indexId != indexId)
            {
                return string.Empty;
            }
            if (_result != null)
            {
                return _result.Ip;
            }
            return _loading ? Loading : Failed;
        }
    }

    /// <summary>Text for the "Country / City" column ("" for every config except the running one).</summary>
    public string GetLocationText(string indexId)
    {
        lock (_lock)
        {
            return indexId.IsNullOrEmpty() || _indexId != indexId || _result == null
                ? string.Empty
                : _result.Location;
        }
    }

    /// <summary>Forget everything (a new config is about to be started).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _indexId = string.Empty;
            _result = null;
            _loading = false;
        }
    }

    #endregion State (read by the profile list)

    /// <summary>
    /// Looks up the exit info of <paramref name="indexId"/> (the config that was just started).
    /// The "loading" state is set synchronously, before the first await.
    /// </summary>
    public async Task<ExitInfoResult?> RefreshAsync(string indexId)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel(); // a newer start supersedes the previous lookup
            cts = new CancellationTokenSource();
            _cts = cts;
            _indexId = indexId;
            _result = null;
            _loading = true;
        }

        var token = cts.Token;
        try
        {
            await Task.Delay(1000, token); // let the new core open its inbound

            for (var round = 0; round < MaxRounds; round++)
            {
                var info = await FetchOnceAsync(token);
                if (info != null)
                {
                    lock (_lock)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return null;
                        }
                        _result = info;
                    }
                    return info;
                }

                if (round < MaxRounds - 1)
                {
                    await Task.Delay(2000, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer start, or cleared
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _loading = false;
                }
            }
        }

        return null;
    }

    #region Fetch

    private sealed record Source(string Name, string Url, Func<string, ExitInfoResult?> Parse);

    private static readonly Source[] _sources =
    [
        new("ipwho.is", "https://ipwho.is/", ParseIpWho),
        new("ipapi.co", "https://ipapi.co/json/", ParseIpApiCo),
        new("api.myip.com", "https://api.myip.com/", ParseMyIp),
        new("ipinfo.io", "https://ipinfo.io/json", ParseIpInfo),
        new("cloudflare-trace", "https://www.cloudflare.com/cdn-cgi/trace", ParseCloudflareTrace),
        new("ip-api.com", "http://ip-api.com/json/?fields=status,country,countryCode,city,query", ParseIpApiCom),
    ];

    private static async Task<ExitInfoResult?> FetchOnceAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(RoundDeadlineSeconds));

        foreach (var source in _sources)
        {
            if (deadline.IsCancellationRequested)
            {
                break;
            }

            var info = await TrySourceAsync(source, deadline.Token);
            if (info != null)
            {
                return info;
            }

            token.ThrowIfCancellationRequested();
        }

        return null;
    }

    private static async Task<ExitInfoResult?> TrySourceAsync(Source source, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(PerSourceTimeoutSeconds));

            using var client = HttpClientHelper.Instance.GetHttpClient(true); // local SOCKS5 of the running core
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", "curl/8.5.0");

            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim();
            return body.IsNullOrEmpty() ? null : source.Parse(body);
        }
        catch
        {
            return null; // timeout, proxy not ready, bad JSON ... -> next source
        }
    }

    #endregion Fetch

    #region Parsers

    private static string Str(JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool IsTrue(JsonElement obj, string name)
    {
        return obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private static ExitInfoResult? ParseIpWho(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return IsTrue(r, "success")
            ? Build(Str(r, "ip"), Str(r, "country"), Str(r, "city"), Str(r, "country_code"))
            : null;
    }

    private static ExitInfoResult? ParseIpApiCo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return IsTrue(r, "error")
            ? null
            : Build(Str(r, "ip"), Str(r, "country_name"), Str(r, "city"), Str(r, "country_code"));
    }

    private static ExitInfoResult? ParseMyIp(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return Build(Str(r, "ip"), Str(r, "country"), string.Empty, Str(r, "cc"));
    }

    private static ExitInfoResult? ParseIpInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        // ipinfo.io returns only the 2-letter code in "country"
        return Build(Str(r, "ip"), string.Empty, Str(r, "city"), Str(r, "country"));
    }

    private static ExitInfoResult? ParseIpApiCom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return Str(r, "status") == "success"
            ? Build(Str(r, "query"), Str(r, "country"), Str(r, "city"), Str(r, "countryCode"))
            : null;
    }

    private static ExitInfoResult? ParseCloudflareTrace(string text)
    {
        // key=value lines, not JSON
        var ip = string.Empty;
        var loc = string.Empty;
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (key == "ip")
            {
                ip = value;
            }
            else if (key == "loc")
            {
                loc = value;
            }
        }

        return Build(ip, string.Empty, string.Empty, loc);
    }

    private static ExitInfoResult? Build(string ip, string country, string city, string countryCode)
    {
        ip = ip.Trim();
        if (ip.IsNullOrEmpty())
        {
            return null;
        }

        countryCode = countryCode.Trim().ToUpperInvariant();
        country = country.Trim();
        if (countryCode.Length == 2
            && (country.IsNullOrEmpty() || string.Equals(country, countryCode, StringComparison.OrdinalIgnoreCase)))
        {
            country = CountryName(countryCode);
        }

        return new ExitInfoResult(ip, country, city.Trim(), countryCode);
    }

    private static string CountryName(string countryCode)
    {
        try
        {
            return new System.Globalization.RegionInfo(countryCode).EnglishName;
        }
        catch
        {
            return countryCode;
        }
    }

    #endregion Parsers
}
