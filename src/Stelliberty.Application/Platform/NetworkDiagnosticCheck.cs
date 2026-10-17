namespace Stelliberty.Application.Platform;

public sealed record NetworkDiagnosticCheck(
    string Id,
    string Name,
    string Url,
    bool IsEnabled = true,
    NetworkDiagnosticTestType TestType = NetworkDiagnosticTestType.Http)
{
    public static IReadOnlyList<NetworkDiagnosticCheck> CreateDefaults() =>
    [
        new("Bilibili", "Bilibili", "https://i0.hdslb.com/bfs/face/member/noface.jpg@24w_24h_1c"),
        new("ByteDance", "ByteDance", "https://lf3-zlink-tos.ugurl.cn/obj/zebra-public/resource_lmmizj_1632398893.png"),
        new("WeChat", "WeChat", "https://res.wx.qq.com/a/wx_fed/assets/res/NTI4MWU5.ico"),
        new("JsDelivr", "jsDelivr", "https://cdn.jsdelivr.net/npm/jquery@3.7.1/dist/jquery.min.js"),
        new("GitHub", "GitHub", "https://github.com/"),
        new("Cloudflare", "Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace"),
        new("Google", "Google", "https://www.google.com/generate_204"),
        new("GitHubSsh", "GitHub SSH", "github.com:22", TestType: NetworkDiagnosticTestType.Ssh)
    ];
}
