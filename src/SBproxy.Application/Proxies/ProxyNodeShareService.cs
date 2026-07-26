using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SBproxy.Application.Proxies;

public sealed class ProxyNodeShareService(IProxyNodeShareSource source)
{
    public ProxyNodeShareResult? Read(string nodeName, string? providerName)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            return null;
        }

        try
        {
            var runtimeRoot = LoadRoot(source.ReadRuntimeConfig());
            var proxy = string.IsNullOrWhiteSpace(providerName)
                ? FindProxy(runtimeRoot, nodeName)
                : FindProviderProxy(runtimeRoot, providerName, nodeName);
            if (proxy is null)
            {
                return null;
            }

            var type = Scalar(proxy, "type");
            var content = SerializeShareUri(proxy, type);
            var isYaml = string.IsNullOrWhiteSpace(content);
            content = isYaml ? SerializeSingleProxyYaml(proxy) : content;
            return new ProxyNodeShareResult(
                Scalar(proxy, "name"),
                type,
                Scalar(proxy, "server"),
                Scalar(proxy, "port"),
                providerName ?? string.Empty,
                content,
                isYaml);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException or JsonException or FormatException)
        {
            return null;
        }
    }

    private YamlMappingNode? FindProviderProxy(YamlMappingNode runtimeRoot, string providerName, string nodeName)
    {
        if (!TryMapping(runtimeRoot, "proxy-providers", out var providers)
            || !TryMapping(providers, providerName, out var provider))
        {
            return null;
        }

        var inlineProxy = FindProxySequence(provider, nodeName);
        if (inlineProxy is not null)
        {
            return inlineProxy;
        }

        var path = Scalar(provider, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return FindProxy(LoadRoot(source.ReadProviderConfig(path)), nodeName);
    }

    private static YamlMappingNode? FindProxy(YamlMappingNode root, string nodeName)
        => FindProxySequence(root, nodeName);

    private static YamlMappingNode? FindProxySequence(YamlMappingNode root, string nodeName)
    {
        foreach (var key in new[] { "proxies", "payload" })
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out var value)
                || value is not YamlSequenceNode proxies)
            {
                continue;
            }

            var match = proxies.Children
                .OfType<YamlMappingNode>()
                .FirstOrDefault(proxy => string.Equals(Scalar(proxy, "name"), nodeName, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string SerializeShareUri(YamlMappingNode proxy, string type)
    {
        if (!CanEncodeAsUri(proxy, type))
        {
            return string.Empty;
        }

        return type.ToLowerInvariant() switch
        {
            "ss" => SerializeShadowsocks(proxy),
            "ssr" => SerializeShadowsocksR(proxy),
            "vmess" => SerializeVmess(proxy),
            "vless" => SerializeVless(proxy),
            "trojan" => SerializeTrojan(proxy),
            "hysteria" => SerializeHysteria(proxy),
            "hysteria2" or "hy2" => SerializeHysteria2(proxy),
            "tuic" => SerializeTuic(proxy),
            "http" => SerializeHttp(proxy),
            "socks5" or "socks" => SerializeSocks5(proxy),
            _ => string.Empty,
        };
    }

    private static bool CanEncodeAsUri(YamlMappingNode proxy, string type)
    {
        var allowed = type.ToLowerInvariant() switch
        {
            "ss" => Keys("cipher", "password", "plugin", "plugin-opts"),
            "ssr" => Keys("protocol", "cipher", "obfs", "password", "obfs-param", "protocol-param"),
            "vmess" => Keys(
                "uuid", "alterId", "alter-id", "cipher", "network", "tls", "servername", "sni",
                "skip-cert-verify", "client-fingerprint", "alpn", "ws-opts", "grpc-opts", "h2-opts"),
            "vless" => Keys(
                "uuid", "network", "encryption", "flow", "tls", "servername", "sni",
                "skip-cert-verify", "client-fingerprint", "alpn", "reality-opts", "ws-opts", "grpc-opts", "h2-opts"),
            "trojan" => Keys(
                "password", "network", "tls", "servername", "sni", "skip-cert-verify",
                "client-fingerprint", "alpn", "ws-opts", "grpc-opts"),
            "hysteria" => Keys(
                "auth", "auth-str", "protocol", "up", "up-speed", "down", "down-speed",
                "obfs", "sni", "servername", "alpn", "skip-cert-verify"),
            "hysteria2" or "hy2" => Keys(
                "password", "sni", "servername", "obfs", "obfs-password", "fingerprint",
                "up", "up-speed", "down", "down-speed", "alpn", "skip-cert-verify"),
            "tuic" => Keys(
                "uuid", "password", "token", "sni", "servername", "alpn", "udp-relay-mode",
                "congestion-controller", "disable-sni", "skip-cert-verify"),
            "http" => Keys("username", "password", "tls"),
            "socks5" or "socks" => Keys("username", "password"),
            _ => [],
        };
        if (allowed.Count == 0
            || proxy.Children.Keys.OfType<YamlScalarNode>().Any(key => !allowed.Contains(key.Value ?? string.Empty))
            || proxy.Children.ContainsKey(new YamlScalarNode("udp")) && !Bool(proxy, "udp"))
        {
            return false;
        }

        return MappingContainsOnly(proxy, "reality-opts", "public-key", "short-id")
            && MappingContainsOnly(proxy, "ws-opts", "path", "headers")
            && NestedMappingContainsOnly(proxy, "ws-opts", "headers", "Host", "host")
            && MappingContainsOnly(proxy, "grpc-opts", "grpc-service-name")
            && MappingContainsOnly(proxy, "h2-opts", "path", "host");
    }

    private static HashSet<string> Keys(params string[] protocolKeys)
    {
        var keys = new HashSet<string>(protocolKeys, StringComparer.Ordinal)
        {
            "name",
            "type",
            "server",
            "port",
            "udp",
        };
        return keys;
    }

    private static bool MappingContainsOnly(YamlMappingNode parent, string key, params string[] allowedKeys)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var value))
        {
            return true;
        }

        return value is YamlMappingNode mapping
            && mapping.Children.Keys.OfType<YamlScalarNode>()
                .All(child => allowedKeys.Contains(child.Value ?? string.Empty, StringComparer.Ordinal));
    }

    private static bool NestedMappingContainsOnly(
        YamlMappingNode parent,
        string parentKey,
        string key,
        params string[] allowedKeys)
    {
        return !TryMapping(parent, parentKey, out var mapping)
            || MappingContainsOnly(mapping, key, allowedKeys);
    }

    private static string SerializeShadowsocks(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy) || Blank(proxy, "cipher") || Blank(proxy, "password"))
        {
            return string.Empty;
        }

        var auth = Base64Url($"{Scalar(proxy, "cipher")}:{Scalar(proxy, "password")}");
        var plugin = Scalar(proxy, "plugin");
        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(plugin))
        {
            var pluginValue = plugin;
            if (TryMapping(proxy, "plugin-opts", out var options))
            {
                foreach (var option in options.Children)
                {
                    pluginValue += $";{option.Key}={option.Value}";
                }
            }

            AddQuery(query, "plugin", pluginValue);
        }

        return $"ss://{auth}@{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeShadowsocksR(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy)
            || Blank(proxy, "protocol")
            || Blank(proxy, "cipher")
            || Blank(proxy, "obfs")
            || Blank(proxy, "password"))
        {
            return string.Empty;
        }

        var body = $"{Scalar(proxy, "server")}:{Scalar(proxy, "port")}:{Scalar(proxy, "protocol")}:{Scalar(proxy, "cipher")}:{Scalar(proxy, "obfs")}:{Base64Url(Scalar(proxy, "password"))}";
        var query = new List<string>
        {
            $"remarks={Base64Url(Scalar(proxy, "name"))}",
        };
        AddBase64Query(query, "obfsparam", Scalar(proxy, "obfs-param"));
        AddBase64Query(query, "protoparam", Scalar(proxy, "protocol-param"));
        return $"ssr://{Base64Url($"{body}/?{string.Join('&', query)}")}";
    }

    private static string SerializeVmess(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy) || Blank(proxy, "uuid"))
        {
            return string.Empty;
        }

        var network = Default(Scalar(proxy, "network"), "tcp");
        var document = new Dictionary<string, object?>
        {
            ["v"] = "2",
            ["ps"] = Scalar(proxy, "name"),
            ["add"] = Scalar(proxy, "server"),
            ["port"] = Scalar(proxy, "port"),
            ["id"] = Scalar(proxy, "uuid"),
            ["aid"] = Default(Scalar(proxy, "alterId"), Scalar(proxy, "alter-id"), "0"),
            ["scy"] = Default(Scalar(proxy, "cipher"), "auto"),
            ["net"] = network,
            ["type"] = "none",
            ["host"] = TransportHost(proxy, network),
            ["path"] = TransportPath(proxy, network),
            ["tls"] = Bool(proxy, "tls") ? "tls" : string.Empty,
            ["sni"] = Default(Scalar(proxy, "servername"), Scalar(proxy, "sni")),
            ["alpn"] = string.Join(',', Sequence(proxy, "alpn")),
            ["fp"] = Scalar(proxy, "client-fingerprint"),
            ["allowInsecure"] = Bool(proxy, "skip-cert-verify") ? "1" : "0",
        };
        return $"vmess://{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document)))}";
    }

    private static string SerializeVless(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy) || Blank(proxy, "uuid"))
        {
            return string.Empty;
        }

        var network = Default(Scalar(proxy, "network"), "tcp");
        var query = V2RayQuery(proxy, network);
        AddQuery(query, "encryption", Default(Scalar(proxy, "encryption"), "none"));
        AddQuery(query, "flow", Scalar(proxy, "flow"));
        return $"vless://{Escape(Scalar(proxy, "uuid"))}@{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeTrojan(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy) || Blank(proxy, "password"))
        {
            return string.Empty;
        }

        var network = Default(Scalar(proxy, "network"), "tcp");
        var query = V2RayQuery(proxy, network, tlsByDefault: true);
        return $"trojan://{Escape(Scalar(proxy, "password"))}@{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeHysteria(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy))
        {
            return string.Empty;
        }

        var query = new List<KeyValuePair<string, string>>();
        AddQuery(query, "auth", Default(Scalar(proxy, "auth-str"), Scalar(proxy, "auth")));
        AddQuery(query, "protocol", Scalar(proxy, "protocol"));
        AddQuery(query, "up", Default(Scalar(proxy, "up"), Scalar(proxy, "up-speed")));
        AddQuery(query, "down", Default(Scalar(proxy, "down"), Scalar(proxy, "down-speed")));
        AddQuery(query, "obfs", Scalar(proxy, "obfs"));
        AddQuery(query, "peer", Default(Scalar(proxy, "sni"), Scalar(proxy, "servername")));
        AddQuery(query, "alpn", string.Join(',', Sequence(proxy, "alpn")));
        AddBoolQuery(query, "insecure", Bool(proxy, "skip-cert-verify"));
        return $"hysteria://{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeHysteria2(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy) || Blank(proxy, "password"))
        {
            return string.Empty;
        }

        var query = new List<KeyValuePair<string, string>>();
        AddQuery(query, "sni", Default(Scalar(proxy, "sni"), Scalar(proxy, "servername")));
        AddQuery(query, "obfs", Scalar(proxy, "obfs"));
        AddQuery(query, "obfs-password", Scalar(proxy, "obfs-password"));
        AddQuery(query, "pinSHA256", Scalar(proxy, "fingerprint"));
        AddQuery(query, "up", Default(Scalar(proxy, "up"), Scalar(proxy, "up-speed")));
        AddQuery(query, "down", Default(Scalar(proxy, "down"), Scalar(proxy, "down-speed")));
        AddQuery(query, "alpn", string.Join(',', Sequence(proxy, "alpn")));
        AddBoolQuery(query, "insecure", Bool(proxy, "skip-cert-verify"));
        return $"hysteria2://{Escape(Scalar(proxy, "password"))}@{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeTuic(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy))
        {
            return string.Empty;
        }

        var userInfo = !Blank(proxy, "uuid")
            ? $"{Escape(Scalar(proxy, "uuid"))}:{Escape(Scalar(proxy, "password"))}"
            : Escape(Scalar(proxy, "token"));
        if (string.IsNullOrWhiteSpace(userInfo))
        {
            return string.Empty;
        }

        var query = new List<KeyValuePair<string, string>>();
        AddQuery(query, "sni", Default(Scalar(proxy, "sni"), Scalar(proxy, "servername")));
        AddQuery(query, "alpn", string.Join(',', Sequence(proxy, "alpn")));
        AddQuery(query, "udp_relay_mode", Scalar(proxy, "udp-relay-mode"));
        AddQuery(query, "congestion_control", Scalar(proxy, "congestion-controller"));
        AddBoolQuery(query, "disable_sni", Bool(proxy, "disable-sni"));
        AddBoolQuery(query, "insecure", Bool(proxy, "skip-cert-verify"));
        return $"tuic://{userInfo}@{Endpoint(proxy)}{Query(query)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeHttp(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy))
        {
            return string.Empty;
        }

        var userInfo = UserInfo(proxy);
        var scheme = Bool(proxy, "tls") ? "https" : "http";
        return $"{scheme}://{userInfo}{Endpoint(proxy)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static string SerializeSocks5(YamlMappingNode proxy)
    {
        if (!HasEndpoint(proxy))
        {
            return string.Empty;
        }

        return $"socks5://{UserInfo(proxy)}{Endpoint(proxy)}#{Escape(Scalar(proxy, "name"))}";
    }

    private static List<KeyValuePair<string, string>> V2RayQuery(
        YamlMappingNode proxy,
        string network,
        bool tlsByDefault = false)
    {
        var query = new List<KeyValuePair<string, string>>();
        AddQuery(query, "type", network);
        var reality = TryMapping(proxy, "reality-opts", out var realityOptions);
        AddQuery(query, "security", reality ? "reality" : Bool(proxy, "tls") || tlsByDefault ? "tls" : "none");
        AddQuery(query, "sni", Default(Scalar(proxy, "servername"), Scalar(proxy, "sni")));
        AddQuery(query, "fp", Scalar(proxy, "client-fingerprint"));
        AddQuery(query, "alpn", string.Join(',', Sequence(proxy, "alpn")));
        AddBoolQuery(query, "allowInsecure", Bool(proxy, "skip-cert-verify"));
        if (reality)
        {
            AddQuery(query, "pbk", Scalar(realityOptions, "public-key"));
            AddQuery(query, "sid", Scalar(realityOptions, "short-id"));
        }

        switch (network)
        {
            case "ws" when TryMapping(proxy, "ws-opts", out var ws):
                AddQuery(query, "path", Scalar(ws, "path"));
                if (TryMapping(ws, "headers", out var headers))
                {
                    AddQuery(query, "host", Default(Scalar(headers, "Host"), Scalar(headers, "host")));
                }
                break;
            case "grpc" when TryMapping(proxy, "grpc-opts", out var grpc):
                AddQuery(query, "serviceName", Scalar(grpc, "grpc-service-name"));
                break;
            case "h2" when TryMapping(proxy, "h2-opts", out var h2):
                AddQuery(query, "path", Scalar(h2, "path"));
                AddQuery(query, "host", string.Join(',', Sequence(h2, "host")));
                break;
        }

        return query;
    }

    private static string TransportHost(YamlMappingNode proxy, string network)
    {
        if (network == "ws"
            && TryMapping(proxy, "ws-opts", out var ws)
            && TryMapping(ws, "headers", out var headers))
        {
            return Default(Scalar(headers, "Host"), Scalar(headers, "host"));
        }

        if (network == "h2" && TryMapping(proxy, "h2-opts", out var h2))
        {
            return string.Join(',', Sequence(h2, "host"));
        }

        return string.Empty;
    }

    private static string TransportPath(YamlMappingNode proxy, string network)
    {
        if (network == "ws" && TryMapping(proxy, "ws-opts", out var ws))
        {
            return Scalar(ws, "path");
        }

        if (network == "grpc" && TryMapping(proxy, "grpc-opts", out var grpc))
        {
            return Scalar(grpc, "grpc-service-name");
        }

        return network == "h2" && TryMapping(proxy, "h2-opts", out var h2)
            ? Scalar(h2, "path")
            : string.Empty;
    }

    private static string SerializeSingleProxyYaml(YamlMappingNode proxy)
    {
        var root = new YamlMappingNode
        {
            { "proxies", new YamlSequenceNode(Clone(proxy)) },
        };
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().Trim();
    }

    private static YamlMappingNode Clone(YamlMappingNode source)
    {
        var stream = new YamlStream(new YamlDocument(source));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return LoadRoot(writer.ToString());
    }

    private static YamlMappingNode LoadRoot(string content)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(content));
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
            ? root
            : new YamlMappingNode();
    }

    private static bool TryMapping(YamlMappingNode parent, string key, out YamlMappingNode mapping)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode found)
        {
            mapping = found;
            return true;
        }

        mapping = new YamlMappingNode();
        return false;
    }

    private static string Scalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? value.ToString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> Sequence(YamlMappingNode mapping, string key)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var value))
        {
            return [];
        }

        return value is YamlSequenceNode sequence
            ? sequence.Children.Select(item => item.ToString()).ToList()
            : value.ToString()?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
    }

    private static bool Bool(YamlMappingNode mapping, string key)
        => bool.TryParse(Scalar(mapping, key), out var result) && result;

    private static bool Blank(YamlMappingNode mapping, string key)
        => string.IsNullOrWhiteSpace(Scalar(mapping, key));

    private static bool HasEndpoint(YamlMappingNode proxy)
        => !Blank(proxy, "server") && !Blank(proxy, "port");

    private static string Endpoint(YamlMappingNode proxy)
    {
        var server = Scalar(proxy, "server");
        if (server.Contains(':', StringComparison.Ordinal) && !server.StartsWith('[', StringComparison.Ordinal))
        {
            server = $"[{server}]";
        }

        return $"{server}:{Scalar(proxy, "port")}";
    }

    private static string UserInfo(YamlMappingNode proxy)
    {
        var username = Scalar(proxy, "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

        var password = Scalar(proxy, "password");
        return string.IsNullOrWhiteSpace(password)
            ? $"{Escape(username)}@"
            : $"{Escape(username)}:{Escape(password)}@";
    }

    private static string Query(IReadOnlyList<KeyValuePair<string, string>> values)
        => values.Count == 0
            ? string.Empty
            : $"?{string.Join('&', values.Select(item => $"{Escape(item.Key)}={Escape(item.Value)}"))}";

    private static void AddQuery(List<KeyValuePair<string, string>> values, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static void AddBoolQuery(List<KeyValuePair<string, string>> values, string key, bool value)
    {
        if (value)
        {
            AddQuery(values, key, "1");
        }
    }

    private static void AddBase64Query(List<string> values, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add($"{key}={Base64Url(value)}");
        }
    }

    private static string Default(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
