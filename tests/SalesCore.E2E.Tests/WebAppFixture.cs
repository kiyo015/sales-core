using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Npgsql;
using SalesCore.Infrastructure.Persistence;
using SalesCore.Infrastructure.Persistence.Identity;

namespace SalesCore.E2E.Tests;

/// <summary>
/// 端から端までのテストの土台。端から端まで用のDBにマイグレーションを当て、画面(SalesCore.Web)を発行して別のプロセスで起動し、
/// ブラウザ(Chromium)を開く。テストの実行ごとに1回。
/// <para>
/// 画面は環境 E2E で動かす。Development ではないので、各自の user-secrets(接続文字列・初期の利用者)を読まない。
/// 発行するのは、Development でない環境では画面の CSS(wwwroot の app.css と、画面ごとの CSS をまとめた SalesCore.Web.styles.css)が
/// 発行した成果物にしか入らないため。画面の部品のスクリプト(_framework/blazor.web.js)はフレームワークが別に配る
/// (発行した wwwroot には無い。2026-11-06 に UseStaticFiles を外す対照実験で確認)。
/// </para>
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private const string TestKey = "SalesCoreTest";
    private const string Database = "salescore_e2e_test";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly StringBuilder _webLog = new();
    private Process? _web;
    private IPlaywright? _playwright;

    public string ConnectionString { get; } = LoadConnectionString();

    public string BaseUrl { get; private set; } = "";

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>画面のプロセスの出力。失敗した時の手掛かりにする。</summary>
    public string WebLog
    {
        get
        {
            lock (_webLog)
            {
                return _webLog.ToString();
            }
        }
    }

    public SalesCoreDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SalesCoreDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }
        await using (var identity = new SalesCoreIdentityDbContext(new DbContextOptionsBuilder<SalesCoreIdentityDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable(SalesCoreIdentityDbContext.MigrationsHistoryTable)).Options))
        {
            await identity.Database.MigrateAsync();
        }

        await StartWebAsync(await PublishWebAsync());

        // CI はワークフローで先に入れておく(OS の依存も入れるため)。手元では、無ければここで入れる(あれば何もしない)
        if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
        {
            throw new InvalidOperationException("Playwright のブラウザ(Chromium)を入れられなかった。");
        }
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_web is { HasExited: false })
        {
            _web.Kill(entireProcessTree: true);
            await _web.WaitForExitAsync();
        }
        _web?.Dispose();
    }

    /// <summary>
    /// 結合テストの接続文字列(手元は user-secrets の ConnectionStrings:SalesCoreTest、CI は環境変数)の DB 名だけを
    /// salescore_e2e_test に替えて使う。利用者とパスワードは結合テストと同じなので、秘密情報を増やさない。
    /// 別の DB にするのは、画面を別のプロセスで動かすのでデータが残るため(結合テストの DB に残すと、全件を数えるテストが壊れる)。
    /// </summary>
    private static string LoadConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(WebAppFixture).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var testConnectionString = configuration.GetConnectionString(TestKey)
            ?? throw new InvalidOperationException(
                $"接続文字列 ConnectionStrings:{TestKey} が設定されていない。CLAUDE.md の「DBの準備」に従って user-secrets に設定すること。");
        return new NpgsqlConnectionStringBuilder(testConnectionString) { Database = Database }.ConnectionString;
    }

    // ビルド済みの画面を発行する(ビルドし直さない。テストと同じ構成を使う)
    private static async Task<string> PublishWebAsync()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var root = RepositoryRoot();
        var output = Path.Combine(AppContext.BaseDirectory, "web");
        var publish = Process.Start(new ProcessStartInfo("dotnet",
            ["publish", Path.Combine(root, "src", "SalesCore.Web", "SalesCore.Web.csproj"), "--configuration", configuration, "--no-build", "--output", output])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        // 出力は最後まで読む(途中で読むのをやめると、書き手が詰まる。2026-11-05)
        var stdout = publish.StandardOutput.ReadToEndAsync();
        var stderr = publish.StandardError.ReadToEndAsync();
        await publish.WaitForExitAsync();
        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException($"画面を発行できなかった(終了コード {publish.ExitCode})。\n{await stdout}\n{await stderr}");
        }
        return output;
    }

    private async Task StartWebAsync(string published)
    {
        BaseUrl = $"http://127.0.0.1:{FreePort()}";
        var start = new ProcessStartInfo("dotnet", [Path.Combine(published, "SalesCore.Web.dll")])
        {
            WorkingDirectory = published,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "E2E";
        start.Environment["ASPNETCORE_URLS"] = BaseUrl;
        start.Environment["ConnectionStrings__SalesCore"] = ConnectionString;
        _web = Process.Start(start)!;
        _web.OutputDataReceived += (_, e) => Append(e.Data);
        _web.ErrorDataReceived += (_, e) => Append(e.Data);
        _web.BeginOutputReadLine();
        _web.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_web.HasExited)
            {
                throw new InvalidOperationException($"画面が起動の途中で止まった(終了コード {_web.ExitCode})。\n{WebLog}");
            }
            try
            {
                if ((await http.GetAsync($"{BaseUrl}/account/login")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException)
            {
                // まだ待ち受けていない
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"画面が {StartTimeout.TotalSeconds} 秒で起動しなかった。\n{WebLog}");
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_webLog)
        {
            _webLog.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SalesCore.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根(SalesCore.sln のあるフォルダ)が見つからない。");
    }
}

/// <summary>画面の起動とブラウザを、端から端までのテストのクラスで分け合う(クラスごとに発行・起動しない)。同じ集まりのテストは順に動く。</summary>
[CollectionDefinition(Name)]
public sealed class WebCollection : ICollectionFixture<WebAppFixture>
{
    public const string Name = "web";
}
