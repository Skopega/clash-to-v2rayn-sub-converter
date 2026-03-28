using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class SubscriptionParser
{
    private record Strategy(string Name, string UserAgent, Dictionary<string, string>? ExtraHeaders = null);

    private static Strategy[] BuildStrategies(string hwid)
    {
        var hwidHeaders = new Dictionary<string, string>
        {
            ["x-hwid"] = hwid,
            ["x-device-os"] = "Windows",
            ["x-ver-os"] = "10.0.22631",
            ["x-device-model"] = "Desktop"
        };

        return new Strategy[]
        {
            new("Happ+HWID",         "Happ/1.0",                    hwidHeaders),
            new("Happ/2+HWID",       "Happ/2.0.0",                  hwidHeaders),
            new("V2rayTun+HWID",     "V2rayTun/1.0",                hwidHeaders),
            new("INCY+HWID",         "INCY/1.0",                    hwidHeaders),
            new("FlClashX+HWID",     "FlClashX/1.0",                hwidHeaders),
            new("KoalaClash+HWID",   "clash-verge/v2.2.0",          hwidHeaders),
            new("clash-meta",        "clash-meta"),
            new("clash-verge",       "clash-verge/v2.2.0"),
            new("ClashForAndroid",   "ClashForAndroid/2.5.12"),
            new("Hiddify",           "HiddifyNext/2.5.0"),
            new("FlClash",           "FlClash/0.8.0"),
            new("v2rayN",            "v2rayN/7.0"),
            new("v2rayNG",           "v2rayNG/1.9.0"),
            new("Stash",             "Stash/2.7.0"),
            new("Browser",           "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"),
        };
    }

    public async Task<IReadOnlyList<string>> GetVlessNodesAsync(
        string providerUrl,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerUrl))
            throw new ArgumentException("Provider URL is empty.", nameof(providerUrl));

        var url = providerUrl.Trim();
        log?.Invoke($"Target URL: {url}");

        var hwid = GetOrCreateHwid(log);
        var strategies = BuildStrategies(hwid);

        var collectedBodies = new List<(string Label, string Body)>();

        foreach (var useProxy in new[] { false, true })
        {
            foreach (var strategy in strategies)
            {
                var label = $"{strategy.Name} (proxy={useProxy})";
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    log?.Invoke($"Trying strategy: {label}");
                    using var client = CreateHttpClient(strategy.UserAgent, useProxy, strategy.ExtraHeaders);
                    using var response = await client.GetAsync(url, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);

                    log?.Invoke($"  HTTP {(int)response.StatusCode} | body {body.Length} chars");

                    if (body.Length == 0)
                        continue;

                    if (body.Length > 150)
                        log?.Invoke($"  Preview: {body[..Math.Min(200, body.Length)]}");

                    var nodes = ExtractVlessNodes(body, log);
                    var realNodes = nodes.Where(n => !IsPlaceholderNode(n)).ToList();

                    log?.Invoke($"  VLESS found: {nodes.Count} (real: {realNodes.Count}, placeholder: {nodes.Count - realNodes.Count})");

                    if (realNodes.Count > 0)
                    {
                        log?.Invoke($"SUCCESS via direct extraction [{label}]: {realNodes.Count} node(s)");

                        var xrayExtra = ExtractVlessFromXrayJsonDeep(body, log);
                        var merged = MergeNodes(realNodes, xrayExtra, log);
                        log?.Invoke($"  After Xray-json merge: {merged.Count} total node(s)");
                        return merged;
                    }

                    var xrayNodes = ExtractVlessFromXrayJsonDeep(body, log);
                    if (xrayNodes.Count > 0)
                    {
                        log?.Invoke($"SUCCESS via Xray-json [{label}]: {xrayNodes.Count} node(s)");
                        return xrayNodes;
                    }

                    if (body.Length > 200 && !collectedBodies.Any(b => b.Body == body))
                        collectedBodies.Add((label, body));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Invoke($"  Failed: {ex.Message}");
                }
            }

            log?.Invoke($"--- Trying Xray-json & Clash YAML on {collectedBodies.Count} unique response(s) ---");
            foreach (var (label, body) in collectedBodies)
            {
                var xrayJsonNodes = ExtractVlessFromXrayJson(body, log);
                if (xrayJsonNodes.Count > 0)
                {
                    log?.Invoke($"SUCCESS via Xray-json [{label}]: {xrayJsonNodes.Count} node(s)");
                    return xrayJsonNodes;
                }

                log?.Invoke($"Parsing YAML from [{label}] ({body.Length} chars)...");

                var yamlNodes = ExtractVlessFromClashYaml(body, log);
                var realYamlNodes = yamlNodes.Where(n => !IsPlaceholderNode(n)).ToList();

                if (realYamlNodes.Count > 0)
                {
                    log?.Invoke($"SUCCESS via Clash YAML [{label}]: {realYamlNodes.Count} node(s)");
                    return realYamlNodes;
                }

                if (yamlNodes.Count > 0)
                    log?.Invoke($"  YAML had {yamlNodes.Count} node(s) but ALL are placeholders.");
                else
                    log?.Invoke($"  No vless proxy blocks found in this response.");
            }
        }

        if (collectedBodies.Count > 0)
        {
            var allBodiesText = string.Join("\n", collectedBodies.Select(b => b.Body));
            log?.Invoke($"All strategies exhausted. Collected {collectedBodies.Count} unique response(s).");

            bool hasAnyPlaceholder = allBodiesText.Contains("App not supported", StringComparison.OrdinalIgnoreCase)
                || allBodiesText.Contains("0.0.0.0", StringComparison.Ordinal);

            if (hasAnyPlaceholder)
            {
                log?.Invoke("CONCLUSION: Provider returns 'App not supported' placeholder in ALL formats.");
                log?.Invoke("This provider intentionally blocks all external clients. Only the apps listed on their subscription page can be used.");
                throw new InvalidOperationException(
                    "This provider blocks all external clients (returns 'App not supported'). " +
                    "Real configs are only available through the apps listed on the provider's page (e.g. Happ, FlClashX, Koala Clash).");
            }

            foreach (var (label, body) in collectedBodies)
            {
                log?.Invoke($"Full response from [{label}]:");
                log?.Invoke(body);
            }
        }

        throw new InvalidOperationException(
            "Could not retrieve real VLESS nodes. The provider may block external clients. Check the debug log.");
    }

    private static string GetOrCreateHwid(Action<string>? log)
    {
        var hwidPath = Path.Combine(AppContext.BaseDirectory, "hwid.txt");

        try
        {
            if (File.Exists(hwidPath))
            {
                var existing = File.ReadAllText(hwidPath).Trim();
                if (existing.Length > 10)
                {
                    log?.Invoke($"Using saved HWID: {existing}");
                    return existing;
                }
            }
        }
        catch { }

        var hwid = Guid.NewGuid().ToString();
        log?.Invoke($"Generated new HWID: {hwid}");

        try { File.WriteAllText(hwidPath, hwid); }
        catch { }

        return hwid;
    }

    public static void ClearHwid()
    {
        try
        {
            var hwidPath = Path.Combine(AppContext.BaseDirectory, "hwid.txt");
            if (File.Exists(hwidPath))
                File.Delete(hwidPath);
        }
        catch { }
    }

    private static string? ExtractAddressPort(string vlessUri)
    {
        var match = Regex.Match(vlessUri, @"^vless://[^@]+@([^?#]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static List<string> MergeNodes(List<string> primary, List<string> extra, Action<string>? log)
    {
        if (extra.Count == 0) return primary;

        var existingEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in primary)
        {
            var ep = ExtractAddressPort(node);
            if (ep != null) existingEndpoints.Add(ep);
        }

        var merged = new List<string>(primary);
        foreach (var node in extra)
        {
            var ep = ExtractAddressPort(node);
            if (ep != null && !existingEndpoints.Contains(ep))
            {
                existingEndpoints.Add(ep);
                merged.Add(node);
                log?.Invoke($"  +Xray-json new endpoint: {ep}");
            }
        }

        return merged;
    }

    private static List<string> ExtractVlessNodes(string content, Action<string>? log)
    {
        var allNodes = ExtractNodes(content, log);
        return allNodes
            .Where(n => n.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ExtractVlessFromXrayJsonDeep(string body, Action<string>? log)
    {
        var results = ExtractVlessFromXrayJson(body, log);
        if (results.Count > 0)
            return results;

        foreach (var token in ExtractPossibleBase64Tokens(body))
        {
            if (TryDecodeBase64(token, out var decoded))
            {
                var sub = ExtractVlessFromXrayJson(decoded, log);
                if (sub.Count > 0)
                    results.AddRange(sub);
            }
        }

        return results;
    }

    private static List<string> ExtractVlessFromXrayJson(string body, Action<string>? log)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            JsonElement outbounds;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("outbounds", out outbounds))
            {
                log?.Invoke("  Detected Xray-json config format.");
                return ParseXrayOutbounds(outbounds, root, log);
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var allNodes = new List<string>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("outbounds", out var ob))
                        allNodes.AddRange(ParseXrayOutbounds(ob, item, log));
                }
                if (allNodes.Count > 0)
                    log?.Invoke($"  Detected Xray-json array with {allNodes.Count} vless node(s).");
                return allNodes;
            }
        }
        catch { }

        return new List<string>();
    }

    private static readonly HashSet<string> GenericTags = new(StringComparer.OrdinalIgnoreCase)
        { "proxy", "proxy-2", "proxy-3", "proxy-4", "proxy-5", "proxy-6", "proxy-7", "proxy-8",
          "proxy-9", "proxy-10", "direct", "block", "reject", "dns-out" };

    private static string BuildXrayNodeName(string remarks, string tag, string address, int port)
    {
        var isGenericTag = string.IsNullOrEmpty(tag) || GenericTags.Contains(tag);

        if (!string.IsNullOrEmpty(remarks) && isGenericTag)
            return $"{remarks} [{address}:{port}]";

        if (!string.IsNullOrEmpty(remarks))
            return $"{remarks} [{tag}]";

        if (!isGenericTag)
            return tag;

        return $"{address}:{port}";
    }

    private static List<string> ParseXrayOutbounds(JsonElement outbounds, JsonElement root, Action<string>? log)
    {
        var results = new List<string>();
        var remarks = "";
        if (root.TryGetProperty("remarks", out var rem))
            remarks = rem.GetString() ?? "";

        foreach (var ob in outbounds.EnumerateArray())
        {
            try
            {
                var protocol = ob.GetProperty("protocol").GetString();
                if (!string.Equals(protocol, "vless", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tag = ob.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
                var settings = ob.GetProperty("settings");
                var vnext = settings.GetProperty("vnext");

                foreach (var server in vnext.EnumerateArray())
                {
                    var address = server.GetProperty("address").GetString()!;
                    var port = server.GetProperty("port").GetInt32();
                    var users = server.GetProperty("users");

                    foreach (var user in users.EnumerateArray())
                    {
                        var uuid = user.GetProperty("id").GetString()!;
                        var flow = user.TryGetProperty("flow", out var f) ? f.GetString() ?? "" : "";
                        var encryption = user.TryGetProperty("encryption", out var enc) ? enc.GetString() ?? "none" : "none";

                        var network = "tcp";
                        var security = "";
                        var sni = address;
                        var fp = "";
                        var pbk = "";
                        var sid = "";
                        var serviceName = "";
                        var wsPath = "";

                        if (ob.TryGetProperty("streamSettings", out var ss))
                        {
                            if (ss.TryGetProperty("network", out var net))
                                network = net.GetString() ?? "tcp";
                            if (ss.TryGetProperty("security", out var sec))
                                security = sec.GetString() ?? "";

                            if (ss.TryGetProperty("realitySettings", out var rs))
                            {
                                if (rs.TryGetProperty("serverName", out var sn))
                                    sni = sn.GetString() ?? address;
                                if (rs.TryGetProperty("fingerprint", out var fpEl))
                                    fp = fpEl.GetString() ?? "";
                                if (rs.TryGetProperty("publicKey", out var pk))
                                    pbk = pk.GetString() ?? "";
                                if (rs.TryGetProperty("shortId", out var si))
                                    sid = si.GetString() ?? "";
                            }
                            else if (ss.TryGetProperty("tlsSettings", out var ts))
                            {
                                if (ts.TryGetProperty("serverName", out var sn))
                                    sni = sn.GetString() ?? address;
                                if (ts.TryGetProperty("fingerprint", out var fpEl))
                                    fp = fpEl.GetString() ?? "";
                            }

                            if (ss.TryGetProperty("grpcSettings", out var gs))
                            {
                                if (gs.TryGetProperty("serviceName", out var sn))
                                    serviceName = sn.GetString() ?? "";
                            }

                            if (ss.TryGetProperty("wsSettings", out var ws))
                            {
                                if (ws.TryGetProperty("path", out var p))
                                    wsPath = p.GetString() ?? "";
                            }
                        }

                        var nodeName = BuildXrayNodeName(remarks, tag, address, port);

                        var uri = BuildVlessUri(uuid, address, port.ToString(), nodeName,
                            security == "tls" ? "true" : null,
                            network, sni, flow, fp, pbk, sid, serviceName, wsPath,
                            encryption != "none" ? encryption : null);

                        if (!IsPlaceholderNode(uri))
                        {
                            log?.Invoke($"  Xray outbound [{tag}]: {address}:{port}");
                            results.Add(uri);
                        }
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private static HttpClient CreateHttpClient(
        string userAgent,
        bool useProxy,
        Dictionary<string, string>? extraHeaders = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = useProxy,
            CheckCertificateRevocationList = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.Timeout = TimeSpan.FromSeconds(20);

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        return client;
    }

    private static List<string> ExtractNodes(string content, Action<string>? log)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var seenPayloads = new HashSet<string>(StringComparer.Ordinal);
        var base64Decoded = 0;

        Enqueue(content);
        Enqueue(WebUtility.UrlDecode(content));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seenPayloads.Add(current))
                continue;

            CollectDirectLinks(current, candidates);

            foreach (var token in ExtractPossibleBase64Tokens(current))
            {
                if (TryDecodeBase64(token, out var decoded))
                {
                    base64Decoded++;
                    Enqueue(decoded);
                    Enqueue(WebUtility.UrlDecode(decoded));
                }
            }

            foreach (var token in ExtractPercentEncodedBlocks(current))
            {
                var decoded = WebUtility.UrlDecode(token);
                if (!string.Equals(decoded, token, StringComparison.Ordinal))
                    Enqueue(decoded);
            }
        }

        log?.Invoke($"  Base64 payloads decoded: {base64Decoded} | Direct links: {candidates.Count}");
        return candidates.ToList();

        void Enqueue(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                queue.Enqueue(value);
        }
    }

    private static List<string> ExtractVlessFromClashYaml(string yaml, Action<string>? log)
    {
        var results = new List<string>();

        var inlineBlocks = Regex.Matches(yaml,
            @"-\s*\{[^}]*?type\s*:\s*vless[^}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        log?.Invoke($"  Clash YAML inline vless blocks: {inlineBlocks.Count}");

        foreach (Match block in inlineBlocks)
        {
            var node = ParseInlineClashProxy(block.Value, log);
            if (node != null)
                results.Add(node);
        }

        if (results.Count > 0)
            return results;

        var multilineBlocks = Regex.Matches(yaml,
            @"-\s+name\s*:.*?(?=\n\s*-\s+name|\n\s*proxy-groups|\nrules:|\z)",
            RegexOptions.Singleline);

        log?.Invoke($"  Clash YAML multiline proxy blocks: {multilineBlocks.Count}");

        foreach (Match block in multilineBlocks)
        {
            var text = block.Value;
            if (!Regex.IsMatch(text, @"type\s*:\s*vless", RegexOptions.IgnoreCase))
                continue;

            log?.Invoke($"  Found multiline vless block ({text.Length} chars)");
            var node = ParseMultilineClashProxy(text, log);
            if (node != null)
                results.Add(node);
        }

        if (results.Count > 0)
            return results;

        if (Regex.IsMatch(yaml, @"type\s*:\s*vless", RegexOptions.IgnoreCase))
        {
            log?.Invoke("  Found 'type: vless' in body but could not parse proxy blocks.");
            log?.Invoke("  Trying line-by-line field extraction...");

            var node = ParseLooseClashFields(yaml, log);
            if (node != null)
                results.Add(node);
        }

        return results;
    }

    private static string? ParseInlineClashProxy(string text, Action<string>? log)
    {
        try
        {
            var server = ExtractYamlField(text, "server");
            var port = ExtractYamlField(text, "port");
            var uuid = ExtractYamlField(text, "uuid");
            var name = ExtractYamlField(text, "name") ?? "node";

            if (server == null || port == null || uuid == null)
            {
                log?.Invoke($"  Inline block missing fields: server={server}, port={port}, uuid={uuid != null}");
                return null;
            }

            return BuildVlessUri(uuid, server, port, name,
                ExtractYamlField(text, "tls"),
                ExtractYamlField(text, "network"),
                ExtractYamlField(text, "servername") ?? ExtractYamlField(text, "sni"),
                ExtractYamlField(text, "flow"),
                ExtractYamlField(text, "client-fingerprint") ?? ExtractYamlField(text, "fingerprint"),
                ExtractYamlField(text, "public-key"),
                ExtractYamlField(text, "short-id"),
                ExtractYamlField(text, "grpc-service-name"),
                ExtractYamlField(text, "ws-path") ?? ExtractYamlField(text, "path"));
        }
        catch { return null; }
    }

    private static string? ParseMultilineClashProxy(string text, Action<string>? log)
    {
        try
        {
            var server = ExtractYamlFieldMultiline(text, "server");
            var port = ExtractYamlFieldMultiline(text, "port");
            var uuid = ExtractYamlFieldMultiline(text, "uuid");
            var name = ExtractYamlFieldMultiline(text, "name") ?? "node";

            if (server == null || port == null || uuid == null)
            {
                log?.Invoke($"  Multiline block missing fields: server={server}, port={port}, uuid={uuid != null}");
                return null;
            }

            return BuildVlessUri(uuid, server, port, name,
                ExtractYamlFieldMultiline(text, "tls"),
                ExtractYamlFieldMultiline(text, "network"),
                ExtractYamlFieldMultiline(text, "servername") ?? ExtractYamlFieldMultiline(text, "sni"),
                ExtractYamlFieldMultiline(text, "flow"),
                ExtractYamlFieldMultiline(text, "client-fingerprint"),
                ExtractYamlFieldMultiline(text, "public-key"),
                ExtractYamlFieldMultiline(text, "short-id"),
                ExtractYamlFieldMultiline(text, "grpc-service-name"),
                ExtractYamlFieldMultiline(text, "ws-path") ?? ExtractYamlFieldMultiline(text, "path"));
        }
        catch { return null; }
    }

    private static string? ParseLooseClashFields(string yaml, Action<string>? log)
    {
        try
        {
            var server = ExtractYamlFieldMultiline(yaml, "server");
            var port = ExtractYamlFieldMultiline(yaml, "port");
            var uuid = ExtractYamlFieldMultiline(yaml, "uuid");

            if (server == null || port == null || uuid == null)
            {
                log?.Invoke($"  Loose fields: server={server}, port={port}, uuid={uuid != null}");
                return null;
            }

            return BuildVlessUri(uuid, server, port, "node",
                ExtractYamlFieldMultiline(yaml, "tls"),
                ExtractYamlFieldMultiline(yaml, "network"),
                ExtractYamlFieldMultiline(yaml, "servername"),
                ExtractYamlFieldMultiline(yaml, "flow"),
                ExtractYamlFieldMultiline(yaml, "client-fingerprint"),
                ExtractYamlFieldMultiline(yaml, "public-key"),
                ExtractYamlFieldMultiline(yaml, "short-id"),
                ExtractYamlFieldMultiline(yaml, "grpc-service-name"),
                ExtractYamlFieldMultiline(yaml, "ws-path") ?? ExtractYamlFieldMultiline(yaml, "path"));
        }
        catch { return null; }
    }

    private static string BuildVlessUri(
        string uuid, string server, string port, string name,
        string? tls, string? network, string? sni, string? flow,
        string? fp, string? pbk, string? sid,
        string? grpcServiceName, string? wsPath,
        string? encryption = null)
    {
        network ??= "tcp";
        sni ??= server;

        var hasPbk = !string.IsNullOrEmpty(pbk);
        var security = hasPbk ? "reality" : (tls == "true" ? "tls" : "");

        var parameters = new List<string>
        {
            $"security={security}",
            $"type={network}",
            $"sni={sni}"
        };

        if (!string.IsNullOrEmpty(flow)) parameters.Add($"flow={flow}");
        if (!string.IsNullOrEmpty(fp)) parameters.Add($"fp={fp}");
        if (hasPbk) parameters.Add($"pbk={pbk}");
        if (!string.IsNullOrEmpty(sid)) parameters.Add($"sid={sid}");
        if (!string.IsNullOrEmpty(encryption)) parameters.Add($"encryption={Uri.EscapeDataString(encryption)}");

        if (network == "grpc" && !string.IsNullOrEmpty(grpcServiceName))
            parameters.Add($"serviceName={grpcServiceName}");
        if (network == "ws" && !string.IsNullOrEmpty(wsPath))
            parameters.Add($"path={Uri.EscapeDataString(wsPath)}");

        return $"vless://{uuid}@{server}:{port}?{string.Join("&", parameters)}#{Uri.EscapeDataString(name)}";
    }

    private static string? ExtractYamlField(string inlineBlock, string key)
    {
        var match = Regex.Match(inlineBlock,
            $@"(?:^|[\s,{{]){Regex.Escape(key)}\s*:\s*([^,\}}]+)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private static string? ExtractYamlFieldMultiline(string block, string key)
    {
        var match = Regex.Match(block,
            $@"^\s*{Regex.Escape(key)}\s*:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private static readonly char[] LinkTerminators = { '\r', '\n', '"', '\'', '<', '>' };

    private static void CollectDirectLinks(string content, HashSet<string> results)
    {
        foreach (Match match in Regex.Matches(content,
            @"(?:vless|vmess|trojan|ss|socks)://\S+",
            RegexOptions.IgnoreCase))
        {
            var startPos = match.Index;
            var fragment = match.Value;
            var hashPos = fragment.IndexOf('#');

            if (hashPos >= 0)
            {
                var tailStart = startPos + hashPos;
                var tailEnd = content.IndexOfAny(LinkTerminators, tailStart);
                if (tailEnd < 0) tailEnd = content.Length;
                fragment = content[startPos..tailEnd];
            }

            results.Add(NormalizeNode(fragment));
        }
    }

    private static IEnumerable<string> ExtractPossibleBase64Tokens(string content)
    {
        foreach (Match match in Regex.Matches(content,
            @"(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{32,}={0,2}(?![A-Za-z0-9+/=])"))
        {
            yield return match.Value;
        }
    }

    private static IEnumerable<string> ExtractPercentEncodedBlocks(string content)
    {
        foreach (Match match in Regex.Matches(content,
            @"(?:[A-Za-z0-9_.~-]|%[0-9A-Fa-f]{2}){24,}"))
        {
            if (match.Value.Contains('%'))
                yield return match.Value;
        }
    }

    private static bool TryDecodeBase64(string input, out string decoded)
    {
        decoded = string.Empty;
        var normalized = input.Trim().Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            decoded = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrWhiteSpace(decoded);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeNode(string node)
    {
        var value = WebUtility.UrlDecode(node.Trim().TrimEnd('\r', '\n', '\t', ' '));
        value = value.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, @"[&?](headerType|path|host)=(?=&|#|$)", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"[?&]+#", "#");
        value = Regex.Replace(value, @"[?&]+$", "");
        return value;
    }

    private static bool IsPlaceholderNode(string node)
    {
        return node.Contains("@0.0.0.0:", StringComparison.OrdinalIgnoreCase)
            || node.Contains("App not supported", StringComparison.OrdinalIgnoreCase)
            || node.Contains("@127.0.0.1:1", StringComparison.OrdinalIgnoreCase);
    }
}
