namespace SalesCore.Architecture.Tests;

/// <summary>
/// Application は画面とAPIの両方が使う。画面の回線(Blazor Server)だけの部品は Web に置く
/// (PR #20 の AIレビューの Minor。Application に置くと、AddSalesCoreIdentity を呼ぶ他のホストにも回線の部品が登録される)。
/// </summary>
public class ApplicationLayerTests
{
    [Fact]
    public void Applicationは画面の回線の部品に依存しない()
    {
        var references = AssemblyMetadata.Read("SalesCore.Application", metadata => metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList());

        Assert.Contains("Microsoft.AspNetCore.Identity", references); // 読み方の確認(ログインは Application が扱う)
        Assert.DoesNotContain("Microsoft.AspNetCore.Components.Server", references);
    }
}
