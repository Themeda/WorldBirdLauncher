using McServerList.Core.Models;

namespace McServerList.Core.Services;

/// <summary>
/// 服务器状态查询抽象：便于在自研协议与第三方 API 之间切换。
/// </summary>
public interface IServerStatusProvider
{
    Task<ServerStatus> QueryAsync(ServerEntry entry, CancellationToken cancellationToken = default);
}
