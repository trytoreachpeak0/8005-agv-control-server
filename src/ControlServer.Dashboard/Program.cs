using ControlServer.Dashboard;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// 插在最前面：文件是底，环境变量与命令行盖在它上面。追加在最后会让文件反过来压住环境变量，
// 那是配置来源顺序反了。
builder.Configuration.Sources.Insert(
    0,
    new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = "dashboard.settings.json",
        Optional = true,
        ReloadOnChange = false,
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory)
    });
builder.WebHost.UseUrls(builder.Configuration["Dashboard:url"] ?? "http://127.0.0.1:58009");
builder.Services.AddSingleton(DashboardCardCatalog.Discovered);
builder.Services.AddHttpClient<DashboardDataFetcher>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Dashboard:controlServerBaseUrl"] ?? "http://127.0.0.1:58007");
    // 一轮取不完就该说取不到，而不是拖到下一轮还在等上一轮。
    client.Timeout = DashboardRefresh.Interval;
});

WebApplication app = builder.Build();

// 车队与仓位是同一页的两个视图，所以它们天然共用同一次取数与同一个刷新节奏——
// 不是「各刷各的、恰好都是 2 秒」。
app.MapGet("/", async (
    DashboardCardCatalog catalog,
    DashboardDataFetcher fetcher,
    CancellationToken cancellationToken) =>
{
    IReadOnlyDictionary<string, DashboardCardData> data =
        await fetcher.FetchAsync(catalog, cancellationToken);
    return Results.Content(DashboardPageRenderer.RenderPage(catalog, data), "text/html; charset=utf-8");
});

await app.RunAsync();
