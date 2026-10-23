using WorldBirdLauncher.Core.Protocol;
using Xunit.Abstractions;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 真实服务器联通性验证：默认跳过，设置环境变量 <c>MC_LIVE_TEST=1</c> 后运行。
/// </summary>
public class LiveServerTests(ITestOutputHelper output)
{
    private static readonly string[] Candidates =
    [
        "mc.hypixel.net",
        "play.hypixel.net",
        "us.mineplex.com",
    ];

    [Fact]
    public async Task 真实服务器_至少有一台可获取状态()
    {
        if (Environment.GetEnvironmentVariable("MC_LIVE_TEST") != "1")
        {
            output.WriteLine("未设置 MC_LIVE_TEST=1，跳过真实服务器验证。");
            return;
        }

        var ping = new ServerListPing();
        var success = 0;
        foreach (var host in Candidates)
        {
            var status = await ping.QueryAsync(host, 25565, TimeSpan.FromSeconds(5));
            output.WriteLine($"{host}: online={status.IsOnline} players={status.OnlinePlayers}/{status.MaxPlayers} " +
                             $"version={status.VersionName} latency={status.LatencyMs}ms motd={status.Motd} error={status.Error}");
            if (status.IsOnline)
            {
                success++;
            }
        }

        Assert.True(success > 0, "候选服务器全部查询失败，请检查网络连通性。");
    }
}
