using System.Net;
using Azure;
using Azure.Identity;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

/// <summary>Azure へ接続せずに試せる部分（引数・クライアントの組み立て・例外の分類）。実機の Azure での確認は別。</summary>
public sealed class AzureBlobTests
{
    private const string FakeConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=ZmFrZWtleWZha2VrZXlmYWtla2V5ZmFrZWtleQ==;EndpointSuffix=core.windows.net";

    [Fact]
    public void 接続の指定の検査()
    {
        Assert.Null(new AzureBlobConnection("c", new Uri("https://acct.blob.core.windows.net"), null).FindProblem());
        Assert.Null(new AzureBlobConnection("c", null, FakeConnectionString).FindProblem());
        Assert.NotNull(new AzureBlobConnection("", new Uri("https://acct.blob.core.windows.net"), null).FindProblem());
        Assert.NotNull(new AzureBlobConnection("c", null, null).FindProblem());
        Assert.NotNull(new AzureBlobConnection("c", new Uri("ftp://acct.blob.core.windows.net"), null).FindProblem());
    }

    [Fact]
    public void アカウントURIの指定では_DefaultAzureCredential_でクライアントを作る_接続はしない()
    {
        var store = AzureBlobBinaryStore.Create(
            new AzureBlobConnection("pleasanter", new Uri("https://myaccount.blob.core.windows.net"), null));

        Assert.Contains("myaccount", store.DisplayName);
        Assert.Contains("pleasanter", store.DisplayName);
    }

    [Fact]
    public void 接続文字列の指定でもクライアントを作れ_表示名にキーを出さない()
    {
        var store = AzureBlobBinaryStore.Create(new AzureBlobConnection("pleasanter", null, FakeConnectionString));

        Assert.Contains("fakeaccount", store.DisplayName);
        Assert.DoesNotContain("AccountKey", store.DisplayName);
        Assert.DoesNotContain("ZmFrZWtleWZha2V", store.DisplayName);
    }

    [Fact]
    public void 壊れた接続文字列は_文言に中身を含めずに拒む()
    {
        var ex = Assert.Throws<FatalMigrationException>(
            () => AzureBlobBinaryStore.Create(new AzureBlobConnection("c", null, "not-a-connection-string;Password=hunter2hunter2")));

        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Fact]
    public void 不正な指定では作れない()
        => Assert.Throws<ArgumentException>(() => AzureBlobBinaryStore.Create(new AzureBlobConnection("", null, null)));

    [Fact]
    public void 認証の失敗は_全件に効く失敗_Fatal_へ分類する()
    {
        var fatal = AzureBlobBinaryStore.ToFatal(new AuthenticationFailedException("secret detail that must not leak"));

        Assert.NotNull(fatal);
        Assert.DoesNotContain("secret detail", fatal.Message);
        Assert.Contains("authentication", fatal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 資格情報が使えない場合も_Fatal()
        => Assert.NotNull(AzureBlobBinaryStore.ToFatal(new CredentialUnavailableException("none")));

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void 権限の拒否は_Fatal(int status)
        => Assert.NotNull(AzureBlobBinaryStore.ToFatal(new RequestFailedException(status, "denied", "AuthorizationFailure", null)));

    [Fact]
    public void コンテナーが無いのは_Fatal()
        => Assert.NotNull(AzureBlobBinaryStore.ToFatal(new RequestFailedException(404, "no container", "ContainerNotFound", null)));

    [Theory]
    [InlineData(404, "BlobNotFound")]
    [InlineData(500, "InternalError")]
    [InlineData(503, "ServerBusy")]
    public void ブロブ単位の失敗は_Fatal_にしない(int status, string code)
        => Assert.Null(AzureBlobBinaryStore.ToFatal(new RequestFailedException(status, "x", code, null)));

    // ---- 一時的な失敗の分類 ----

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(0, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    public void Azureの状態コードの分類(int status, bool transient)
        => Assert.Equal(transient, TransientErrors.IsTransient(new RequestFailedException(status, "x")));

    [Fact]
    public void ネットワークとタイムアウトは一時的_認証と整合の不一致は一時的でない()
    {
        Assert.True(TransientErrors.IsTransient(new HttpRequestException("net", null, HttpStatusCode.BadGateway)));
        Assert.True(TransientErrors.IsTransient(new TimeoutException()));
        Assert.True(TransientErrors.IsTransient(new IOException()));
        Assert.False(TransientErrors.IsTransient(new FatalMigrationException("x")));
        Assert.False(TransientErrors.IsTransient(new MigrationIntegrityException("x")));
        Assert.False(TransientErrors.IsTransient(new UnauthorizedAccessException()));
        Assert.False(TransientErrors.IsTransient(new FileNotFoundException()));
        Assert.False(TransientErrors.IsTransient(new ArgumentException()));
        Assert.False(TransientErrors.IsTransient(new OperationCanceledException()));
    }
}
