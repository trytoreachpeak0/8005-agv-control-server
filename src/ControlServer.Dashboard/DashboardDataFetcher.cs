using System.Net.Http.Json;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 每 2 秒把目录里每张卡片的数据面取一遍。
/// </summary>
/// <remarks>
/// 取不到就把原因原样带回去。这里**不缓存上一次成功的值**：缓存了它，下游迟早会显示它，而那正是
/// REQ-0269 禁止的「已过期」展示状态。
/// </remarks>
public sealed class DashboardDataFetcher(HttpClient client)
{
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<IReadOnlyDictionary<string, DashboardCardData>> FetchAsync(
        DashboardCardCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        Dictionary<string, DashboardCardData> byCardId = new(StringComparer.Ordinal);
        foreach (IDashboardCard card in catalog.Cards)
        {
            byCardId[card.CardId] = await FetchOneAsync(card, cancellationToken);
        }
        return byCardId;
    }

    private async Task<DashboardCardData> FetchOneAsync(IDashboardCard card, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _client.GetAsync(card.SourcePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return DashboardCardData.Unavailable(
                    $"ControlServer 返回 {(int)response.StatusCode}，本轮取不到当前事实。");
            }
            JsonElement fact = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return DashboardCardData.Available(fact);
        }
        catch (HttpRequestException exception)
        {
            return DashboardCardData.Unavailable($"设备连接失败：{exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DashboardCardData.Unavailable("ControlServer 未在本轮 2 秒内应答。");
        }
        catch (JsonException exception)
        {
            return DashboardCardData.Unavailable($"ControlServer 的应答不是合法 JSON：{exception.Message}");
        }
    }
}
