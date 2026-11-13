using System.Diagnostics;
using System.Xml.Linq;

namespace SalesCore.Architecture.Tests;

/// <summary>
/// 画面の部品(.razor)に double・float を書くと、src/Directory.Build.props の SALES001 でビルドが止まることを確かめる。
/// .razor は Compile ではなく Content に入るので、props が Content を足して調べている(2026-10-22、足す前は素通りした)。
/// SDK の更新で Content の扱いが変わると、気付かずに素通りに戻る。それを毎回のテストで見張る(PR #8 の Minor。2026-11-13)。
/// 本物の Web と同じ SDK(Microsoft.NET.Sdk.Web)の小さなプロジェクトを作り、検査の段階(SalesCoreMoneyRules)だけを動かす。
/// </summary>
public class RazorMoneyRuleTests
{
    [Theory]
    [InlineData("double", true)]
    [InlineData("float", true)]
    [InlineData("decimal", false)] // 何でも止める検査になっていないことの確認
    public void 画面の部品に浮動小数点の型を書くと_SALES001で止まる(string type, bool expectStopped)
    {
        var (exitCode, output) = RunMoneyRules($"@code {{ private {type} amount; }}");

        Assert.True(output.Contains("SALES001") == expectStopped, output);
        Assert.True((exitCode != 0) == expectStopped, output);
    }

    private static (int ExitCode, string Output) RunMoneyRules(string razor)
    {
        var repo = FindRepositoryRoot();
        // 出力先(リポジトリの中)に作るので、global.json の SDK の版がそのまま効く。
        // 自前の Directory.Build.props を置くので、それより上の props は読まれない
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "razor-probe", Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Directory.Build.props"),
                $"""<Project><Import Project="{Path.Combine(repo, "src", "Directory.Build.props")}" /></Project>""");
            // SDK と対象のフレームワークは本物の Web から写す(Web の版を上げると、ここも一緒に変わる。PR #23 の AIレビュー)
            var web = XDocument.Load(Path.Combine(repo, "src", "SalesCore.Web", "SalesCore.Web.csproj")).Root!;
            File.WriteAllText(Path.Combine(dir.FullName, "Probe.csproj"),
                $"""<Project Sdk="{web.Attribute("Sdk")!.Value}"><PropertyGroup><TargetFramework>{web.Descendants("TargetFramework").Single().Value}</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(dir.FullName, "Probe.razor"), razor);
            // 本物の Web と同じく .cs も置く(Compile が空だと、検査の段階が空のパスを読んで別の理由で落ちる。2026-11-13 実測)
            File.WriteAllText(Path.Combine(dir.FullName, "Program.cs"), "class Program { }");

            var start = new ProcessStartInfo("dotnet", "msbuild Probe.csproj -t:SalesCoreMoneyRules -nologo -noAutoResponse -nodeReuse:false")
            {
                WorkingDirectory = dir.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // 残った MSBuild のノードが出力を持ち続けても CI を止めないよう、終了と出力の読み取りの両方に時間の上限を置く(PR #23 の AIレビュー)
            var limit = TimeSpan.FromMinutes(2);
            if (!process.WaitForExit(limit) || !Task.WaitAll([stdout, stderr], limit))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("dotnet msbuild が2分で終わらなかった");
            }
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        finally
        {
            // 消せなくても、失敗の本来の理由を上書きしない(出力先の中なので、次のビルドで片付く)
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "src", "Directory.Build.props"))) return d.FullName;
        }
        throw new DirectoryNotFoundException("src/Directory.Build.props が見つからない");
    }
}
