using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TavernVault.App;
using TavernVault.App.Hosting;
using Xunit;

namespace TavernVault.IntegrationTests;

/// <summary>
/// API 集成测试共享夹具：真实 Kestrel 进程内启动 ApiServer（随机端口 + 隔离数据目录）。
/// 预置 settings.json 登记测试根——绕过 EnsureDefaultRoot 对 %USERPROFILE%\酒馆PR 的探测，
/// 保证测试永不触碰真实用户库。整个测试树在 Dispose 时直接删除（不进回收站）。
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), "tavernvault-it-" + Guid.NewGuid().ToString("N")[..8]);

    public string DataDir { get; } = default!;
    public string TestRoot { get; }
    public string TavernRoot { get; }
    public ApiServerHandle Handle { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;

    public ApiFixture()
    {
        DataDir = Path.Combine(_tempBase, "data");
        TestRoot = Path.Combine(_tempBase, "库");
        TavernRoot = Path.Combine(_tempBase, "酒馆源");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TestRoot);
        Directory.CreateDirectory(DataDir);
        var settings = new JsonObject
        {
            ["LibraryRoots"] = new JsonArray
            {
                new JsonObject { ["Path"] = TestRoot, ["Source"] = 0 },
            },
            ["UiTheme"] = "auto",
            ["AutoBackup"] = true,
            ["MaxBackupsPerFile"] = 5,
            ["AutoWatch"] = true,
        };
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), settings.ToJsonString());

        Handle = ApiServer.Build(["--server", "--data=" + DataDir]); // 无 --port → 127.0.0.1:0 随机端口
        await Handle.App.StartAsync();
        var addr = Handle.App.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
        Client = new HttpClient { BaseAddress = new Uri(addr.TrimEnd('/') + "/") };
        Client.DefaultRequestHeaders.Add("X-TV-Token", Handle.Token);
        await Post("/api/rescan");
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        try { await Handle.App.StopAsync(); } catch { }
        try { await Handle.App.DisposeAsync(); } catch { }
        try { Directory.Delete(_tempBase, recursive: true); } catch { }
    }

    // ---- JSON 便捷调用 ----

    public async Task<JsonObject> Get(string url)
    {
        using var resp = await Client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        return await ReadJson(resp);
    }

    /// <summary>GET 返回顶层数组的端点（/api/items）。</summary>
    public async Task<JsonArray> GetArray(string url)
    {
        using var resp = await Client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        return await ReadArray(resp);
    }

    /// <summary>返回 (状态码, 响应体)；非 JSON 响应体为 null。</summary>
    public async Task<(int Status, JsonObject? Body)> Call(HttpMethod method, string url, object? body = null,
        string? host = null, string? token = null)
    {
        using var msg = new HttpRequestMessage(method, url);
        if (host is not null) msg.Headers.Host = host;
        if (token is not null) msg.Headers.Add("X-TV-Token", token);
        if (body is not null)
            msg.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
        using var resp = await Client.SendAsync(msg);
        return ((int)resp.StatusCode, resp.Content.Headers.ContentType?.MediaType == "application/json"
            ? await ReadJson(resp)
            : null);
    }

    public async Task<JsonObject> Post(string url, object? body = null)
    {
        var (status, json) = await Call(HttpMethod.Post, url, body);
        Assert.Equal(200, status);
        return json!;
    }

    public async Task<JsonObject> Put(string url, object body)
    {
        var (status, json) = await Call(HttpMethod.Put, url, body);
        Assert.Equal(200, status);
        return json!;
    }

    public async Task<JsonObject> Delete(string url, object body)
    {
        var (status, json) = await Call(HttpMethod.Delete, url, body);
        Assert.Equal(200, status);
        return json!;
    }

    private static async Task<JsonObject> ReadJson(System.Net.Http.HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return Assert.IsType<JsonObject>(System.Text.Json.Nodes.JsonNode.Parse(text));
    }

    private static async Task<JsonArray> ReadArray(System.Net.Http.HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return Assert.IsType<JsonArray>(System.Text.Json.Nodes.JsonNode.Parse(text));
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
