using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TavernVault.App;
using TavernVault.App.Hosting;
using Xunit;

namespace TavernVault.IntegrationTests;

/// <summary>数据目录解析合同（v0.7.7 便携模式）：显式 --data &gt; --portable &gt; %APPDATA%。</summary>
public class PortableModeTests
{
    [Fact]
    public async Task Portable_DataDirBesideExecutable()
    {
        var handle = ApiServer.Build(["--server", "--portable", "--port=0"]);
        try
        {
            await handle.App.StartAsync();
            var addr = handle.App.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(addr.TrimEnd('/') + "/") };
            client.DefaultRequestHeaders.Add("X-TV-Token", handle.Token);
            var meta = (System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync("/api/meta")))!.AsObject();
            var expected = Path.Combine(AppContext.BaseDirectory, "data");
            Assert.Equal(expected, (string)meta["dataDir"]!);
            Assert.True(Directory.Exists(expected)); // 数据目录已创建（logs 等）
        }
        finally
        {
            await handle.App.StopAsync();
            await handle.App.DisposeAsync();
            try { Directory.Delete(Path.Combine(AppContext.BaseDirectory, "data"), recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExplicitData_WinsOverPortable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "tavernvault-it-data-" + Guid.NewGuid().ToString("N")[..8]);
        var handle = ApiServer.Build(["--server", "--portable", "--data=" + tmp]);
        try
        {
            await handle.App.StartAsync();
            Assert.Equal(Path.GetFullPath(tmp), handle.DataDir);
        }
        finally
        {
            await handle.App.StopAsync();
            await handle.App.DisposeAsync();
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
