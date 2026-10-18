using Stelliberty.Application.Connections;
using Stelliberty.Application.CoreLogs;
using Stelliberty.Application.Rules;
using Stelliberty.Domain.Connections;
using Stelliberty.Domain.CoreLogs;
using Stelliberty.Domain.Rules;
using Xunit;

namespace Stelliberty.Monitoring.Tests;

public sealed class MonitoringBusinessRulesTests
{
    [Fact(DisplayName = "Connection parser handles core JSON value shapes with explicit fallback time")]
    public void ConnectionParserHandlesCoreJsonValueShapesWithExplicitFallbackTime()
    {
        var connections = new ConnectionParser().Parse(
            """
            {
              "connections": [
                {
                  "id": "c1",
                  "upload": "100",
                  "download": 200,
                  "chains": ["HK", "GLOBAL"],
                  "rule": "DOMAIN",
                  "rulePayload": "example.com",
                  "metadata": {
                    "network": "tcp",
                    "sourceIP": "127.0.0.1",
                    "sourcePort": 5000,
                    "destinationIP": "1.1.1.1",
                    "destinationPort": "443",
                    "destinationGeoIP": ["US"],
                    "host": "example.com",
                    "process": "browser"
                  }
                }
              ]
            }
            """,
            DateTimeOffset.UnixEpoch);

        var connection = Assert.Single(connections);
        Assert.Equal("c1", connection.Id);
        Assert.Equal(100, connection.Upload);
        Assert.Equal(200, connection.Download);
        Assert.Equal(DateTimeOffset.UnixEpoch, connection.Start);
        Assert.Equal("443", connection.Metadata.DestinationPort);
        Assert.Equal(["HK", "GLOBAL"], connection.Chains);
        Assert.Equal("HK", connection.ProxyNode);
        Assert.Equal("GLOBAL", connection.ProxyGroup);
        Assert.Equal(["GLOBAL", "HK"], connection.RoutePath);
    }

    [Fact(DisplayName = "Connection filter classifies by the actual outbound instead of the matched policy group")]
    public void ConnectionFilterClassifiesByTheActualOutboundInsteadOfTheMatchedPolicyGroup()
    {
        var filter = new ConnectionFilter();
        IReadOnlyList<ConnectionInfo> connections =
        [
            new ConnectionInfo("direct-rule", Chains: ["DIRECT"]),
            new ConnectionInfo("direct-via-group", Chains: ["DIRECT", "Final"]),
            new ConnectionInfo("proxy-via-group", Chains: ["HK", "Proxy"]),
            new ConnectionInfo("no-chain")
        ];

        var direct = filter.Apply(connections, ConnectionFilterLevel.Direct, string.Empty);
        var proxy = filter.Apply(connections, ConnectionFilterLevel.Proxy, string.Empty);

        Assert.Equal(["direct-rule", "direct-via-group", "no-chain"], direct.Select(connection => connection.Id));
        Assert.Equal(["proxy-via-group"], proxy.Select(connection => connection.Id));
    }

    [Fact(DisplayName = "Route filtering composes with connection state and search")]
    public void RouteFilteringComposesWithConnectionStateAndSearch()
    {
        var filter = new ConnectionFilter();
        IReadOnlyList<ConnectionInfo> connections =
        [
            new("active-direct", Metadata: new(Host: "example.com"), Chains: ["DIRECT"]),
            new("active-proxy", Metadata: new(Host: "example.com"), Chains: ["HK", "Proxy"]),
            new("closed-proxy", Metadata: new(Host: "example.com"), Chains: ["HK", "Proxy"],
                DisconnectedAt: DateTimeOffset.UnixEpoch),
            new("other-proxy", Metadata: new(Host: "other.test"), Chains: ["HK", "Proxy"])
        ];

        var active = filter.Apply(connections, ConnectionFilterLevel.Active, "example.com");
        Assert.Equal(new[] { "active-proxy" }, filter.Apply(active, ConnectionFilterLevel.Proxy,
            string.Empty).Select(connection => connection.Id));
        Assert.Equal(new[] { "active-direct" }, filter.Apply(active, ConnectionFilterLevel.Direct,
            string.Empty).Select(connection => connection.Id));
        var closed = filter.Apply(connections, ConnectionFilterLevel.Disconnected, "example.com");
        Assert.Equal(new[] { "closed-proxy" }, filter.Apply(closed, ConnectionFilterLevel.Proxy,
            string.Empty).Select(connection => connection.Id));
        Assert.Empty(filter.Apply(closed, ConnectionFilterLevel.Direct, string.Empty));
        Assert.Equal(active, filter.Apply(active, ConnectionFilterLevel.All, string.Empty));
    }

    [Fact(DisplayName = "Connection reducer keeps sampling when display is paused and clamps sample window")]
    public void ConnectionReducerKeepsSamplingWhenPausedAndClampsSampleWindow()
    {
        var reducer = new ConnectionListReducer();
        var first = reducer.ApplyIncoming(
            ConnectionListState.Initial,
            [Connection("c1", upload: 0, download: 0)],
            DateTimeOffset.UnixEpoch);
        var second = reducer.ApplyIncoming(
            first,
            [Connection("c1", upload: 1000, download: 500)],
            DateTimeOffset.UnixEpoch.AddMilliseconds(100));
        var paused = reducer.TogglePause(second);
        var sampled = reducer.ApplyIncoming(
            paused,
            [Connection("c1", upload: 2000, download: 1000)],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(4000, second.Connections[0].UploadSpeed);
        Assert.Equal(2000, second.Connections[0].DownloadSpeed);
        Assert.Equal(2000, sampled.Connections[0].Upload);
        Assert.True(sampled.IsMonitoringPaused);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), sampled.SampledAt);
    }

    [Fact(DisplayName = "Disconnected connections retain their last data and original disconnect time while paused")]
    public void DisconnectedConnectionsRetainLastDataAndDisconnectTimeWhilePaused()
    {
        var reducer = new ConnectionListReducer();
        var first = reducer.ApplyIncoming(ConnectionListState.Initial,
            [Connection("c1", upload: 100, download: 200)], DateTimeOffset.UnixEpoch);
        var disconnectedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var closed = reducer.ApplyIncoming(reducer.TogglePause(first), [], disconnectedAt);
        var later = reducer.ApplyIncoming(closed, [], disconnectedAt.AddMinutes(1));

        var connection = Assert.Single(later.DisconnectedConnections);
        Assert.Empty(later.Connections);
        Assert.False(connection.IsActive);
        Assert.Equal(disconnectedAt, connection.DisconnectedAt);
        Assert.Equal(100, connection.Upload);
        Assert.Equal(200, connection.Download);
        Assert.Equal(0, connection.UploadSpeed);
        Assert.Equal(0, connection.DownloadSpeed);
        var filter = new ConnectionFilter();
        Assert.Empty(filter.Apply([connection], ConnectionFilterLevel.Active, string.Empty));
        Assert.Single(filter.Apply([connection], ConnectionFilterLevel.Disconnected, string.Empty));
        Assert.Empty(filter.Apply([connection], ConnectionFilterLevel.Disconnected, "no-match"));
    }

    [Fact(DisplayName = "Disconnected history keeps the latest 500 entries and removes reactivated IDs")]
    public void DisconnectedHistoryKeepsLatest500AndRemovesReactivatedIds()
    {
        var reducer = new ConnectionListReducer();
        var state = ConnectionListState.Initial;
        for (var index = 0; index < 501; index++)
        {
            var time = DateTimeOffset.UnixEpoch.AddSeconds(index * 2);
            state = reducer.ApplyIncoming(state, [Connection($"c{index}", upload: 0, download: 0)], time);
            state = reducer.ApplyIncoming(state, [], time.AddSeconds(1));
        }

        Assert.Equal(500, state.DisconnectedConnections.Count);
        Assert.Equal("c500", state.DisconnectedConnections[0].Id);
        Assert.DoesNotContain(state.DisconnectedConnections, connection => connection.Id == "c0");
        state = reducer.ApplyIncoming(state, [Connection("c500", upload: 0, download: 0)], DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.True(Assert.Single(state.Connections).IsActive);
        Assert.DoesNotContain(state.DisconnectedConnections, connection => connection.Id == "c500");
    }

    [Theory(DisplayName = "Connection sort fields support ascending and descending numeric order")]
    [InlineData(ConnectionSortMode.StartTime)]
    [InlineData(ConnectionSortMode.Duration)]
    [InlineData(ConnectionSortMode.UploadSpeed)]
    [InlineData(ConnectionSortMode.DownloadSpeed)]
    [InlineData(ConnectionSortMode.UploadAmount)]
    [InlineData(ConnectionSortMode.DownloadAmount)]
    public void ConnectionSortFieldsSupportAscendingAndDescendingOrder(ConnectionSortMode mode)
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var low = new ConnectionInfo("low", Upload: 10, Download: 20,
            UploadSpeed: 1, DownloadSpeed: 2, Start: DateTimeOffset.UnixEpoch.AddSeconds(10));
        var high = new ConnectionInfo("high", Upload: 1000, Download: 2000,
            UploadSpeed: 100, DownloadSpeed: 200,
            Start: mode == ConnectionSortMode.Duration
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.UnixEpoch.AddSeconds(20));
        var sorter = new ConnectionSorter();

        Assert.Equal(new[] { "low", "high" },
            sorter.Apply([high, low], mode, false, now).Select(connection => connection.Id));
        Assert.Equal(new[] { "high", "low" },
            sorter.Apply([low, high], mode, true, now).Select(connection => connection.Id));
    }

    [Fact(DisplayName = "Connection sorting preserves default order and resolves equal values by ID")]
    public void ConnectionSortingPreservesDefaultOrderAndResolvesEqualValuesById()
    {
        var sorter = new ConnectionSorter();
        ConnectionInfo[] connections = [Connection("b", 100, 200), Connection("a", 100, 200)];

        Assert.Same(connections, sorter.Apply(connections, ConnectionSortMode.Default, false, DateTimeOffset.UnixEpoch));
        Assert.Equal(new[] { "a", "b" }, sorter.Apply(connections, ConnectionSortMode.Default,
            true, DateTimeOffset.UnixEpoch).Select(connection => connection.Id));
        Assert.Equal(new[] { "a", "b" }, sorter.Apply(connections, ConnectionSortMode.UploadAmount,
            true, DateTimeOffset.UnixEpoch).Select(connection => connection.Id));
    }

    [Fact(DisplayName = "Disconnected connections sort by frozen duration and zero speed")]
    public void DisconnectedConnectionsSortByFrozenDurationAndZeroSpeed()
    {
        var sorter = new ConnectionSorter();
        var active = new ConnectionInfo("active", Start: DateTimeOffset.UnixEpoch, UploadSpeed: 5, DownloadSpeed: 10);
        var disconnected = new ConnectionInfo("disconnected", Start: DateTimeOffset.UnixEpoch,
            DisconnectedAt: DateTimeOffset.UnixEpoch.AddSeconds(10), UploadSpeed: 1000, DownloadSpeed: 2000);
        foreach (var mode in new[] { ConnectionSortMode.Duration, ConnectionSortMode.UploadSpeed, ConnectionSortMode.DownloadSpeed })
        {
            Assert.Equal(new[] { "active", "disconnected" },
                sorter.Apply([disconnected, active], mode, true, DateTimeOffset.UnixEpoch.AddMinutes(1))
                    .Select(connection => connection.Id));
        }
    }

    [Fact(DisplayName = "Core log parser handles JSON and text lines with explicit fallback time")]
    public void CoreLogParserHandlesJsonAndTextLinesWithExplicitFallbackTime()
    {
        var parser = new CoreLogParser();

        var jsonLogs = parser.Parse(
            """[{"type":"warning","payload":"slow"},{"level":"error","msg":"failed"}]""",
            DateTimeOffset.UnixEpoch);
        var textLog = parser.Parse(
            "time=\"2026-01-01T00:00:00Z\" level=debug msg=\"hello world\"",
            DateTimeOffset.UnixEpoch);

        Assert.Equal([CoreLogLevel.Warning, CoreLogLevel.Error], jsonLogs.Select(log => log.Level));
        Assert.All(jsonLogs, log => Assert.Equal(DateTimeOffset.UnixEpoch, log.Timestamp));
        Assert.Equal(CoreLogLevel.Debug, textLog.Single().Level);
        Assert.Equal("hello world", textLog.Single().Payload);
    }

    [Fact(DisplayName = "Core log reducer truncates old entries and honors pause")]
    public void CoreLogReducerTruncatesOldEntriesAndHonorsPause()
    {
        var reducer = new CoreLogReducer();
        var logs = Enumerable.Range(0, 2100)
            .Select(index => new CoreLogMessage("INFO", $"log-{index}", DateTimeOffset.UnixEpoch))
            .ToList();

        var state = reducer.Append(CoreLogState.Initial, logs);
        var paused = reducer.TogglePause(state);
        var frozen = reducer.Append(
            paused,
            [new CoreLogMessage("ERROR", "new", DateTimeOffset.UnixEpoch)]);

        Assert.Equal(2000, state.Logs.Count);
        Assert.Equal("log-100", state.Logs[0].Payload);
        Assert.Equal(state.Logs.Count, frozen.Logs.Count);
        Assert.DoesNotContain(frozen.Logs, log => log.Payload == "new");
    }

    [Fact(DisplayName = "Rule parser handles core JSON and YAML rules")]
    public void RuleParserHandlesCoreJsonAndYamlRules()
    {
        var parser = new RuleParser();
        var coreRules = parser.Parse("""{"rules":[{"type":"DOMAIN","payload":"example.com","proxy":"PROXY"}]}""");
        var yamlRules = parser.Parse(
            """
            rule-providers:
              reject:
                type: http
                path: ./reject.yaml
                ruleCount: 2
            rules:
              - DOMAIN-SUFFIX,example.com,PROXY
              - MATCH,DIRECT
            """);

        Assert.Equal("example.com", coreRules.Single().Payload);
        Assert.Contains(
            yamlRules,
            rule => rule.Type == "RULE-PROVIDER" && rule.Payload == "reject" && rule.RuleCount == 2);
        Assert.Contains(yamlRules, rule => rule.Type == "MATCH" && rule.Proxy == "DIRECT");
    }

    [Fact(DisplayName = "Rule type classifier buckets known rule types")]
    public void RuleTypeClassifierBucketsKnownRuleTypes()
    {
        Assert.Equal(RuleTypeBucket.Domain, RuleTypeClassifier.Classify("DOMAIN-SUFFIX"));
        Assert.Equal(RuleTypeBucket.Ip, RuleTypeClassifier.Classify("GEOIP"));
        Assert.Equal(RuleTypeBucket.Ip, RuleTypeClassifier.Classify("SRC-IP-CIDR"));
        Assert.Equal(RuleTypeBucket.RuleSet, RuleTypeClassifier.Classify("RULE-PROVIDER"));
        Assert.Equal(RuleTypeBucket.Other, RuleTypeClassifier.Classify("MATCH"));
    }

    private static ConnectionInfo Connection(string id, long upload, long download)
    {
        return new ConnectionInfo(id, upload, download);
    }
}
