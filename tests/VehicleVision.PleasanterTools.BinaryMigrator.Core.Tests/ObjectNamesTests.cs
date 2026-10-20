namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class ObjectNamesTests
{
    private const string Guid32 = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void 添付はAttachmentsのGuid()
        => Assert.Equal("Attachments/" + Guid32, ObjectNames.Attachment(Guid32));

    [Fact]
    public void 画像とサムネイルはImagesのGuid()
    {
        Assert.Equal("Images/" + Guid32, ObjectNames.Image(Guid32));
        Assert.Equal("Images/" + Guid32 + "_thumbnail", ObjectNames.ImageThumbnail(Guid32));
    }

    [Fact]
    public void サイト画像とテナント画像は_参照IDと大きさの種別から決まる()
    {
        Assert.Equal("SiteImage/123_Regular.png", ObjectNames.SiteImage(123, SiteImageSize.Regular));
        Assert.Equal("SiteImage/123_Thumbnail.png", ObjectNames.SiteImage(123, SiteImageSize.Thumbnail));
        Assert.Equal("SiteImage/123_Icon.png", ObjectNames.SiteImage(123, SiteImageSize.Icon));
        Assert.Equal("TenantImage/7_Logo.png", ObjectNames.TenantImageLogo(7));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789ABCDEF0123456789ABCDEG")] // 16 進でない
    [InlineData("0123456789ABCDEF0123456789ABCDEF0")] // 33 桁
    [InlineData("../0123456789ABCDEF0123456789ABC")]
    public void Guidの形でなければ_オブジェクト名を作らない(string guid)
    {
        Assert.False(ObjectNames.IsValidGuid(guid));
        Assert.Throws<ArgumentException>(() => ObjectNames.Attachment(guid));
    }

    [Fact]
    public void Guidは_大文字小文字のどちらも受ける()
        => Assert.True(ObjectNames.IsValidGuid(Guid32.ToLowerInvariant()));

    [Theory]
    [InlineData("Attachments/" + Guid32)]
    [InlineData("Images/" + Guid32 + "_thumbnail")]
    [InlineData("SiteImage/12_Regular.png")]
    [InlineData("a/b/c.txt")]
    public void 安全な名前は問題なし(string name)
        => Assert.Null(ObjectNames.FindProblem(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../x")]
    [InlineData("Attachments/../../x")]
    [InlineData("Attachments/..")]
    [InlineData("a..b")] // Pleasanter の Local も ".." を含む名前を拒む
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    [InlineData("C:x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share/x")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/")]
    [InlineData("a/b ")]
    [InlineData("a/b.")]
    [InlineData("a/b:stream")]
    [InlineData("a/\u0001b")]
    [InlineData("a/CON")]
    [InlineData("a/nul.txt")]
    [InlineData("a/b*c")]
    [InlineData("a/b?c")]
    public void 危険な名前は拒む(string? name)
    {
        Assert.NotNull(ObjectNames.FindProblem(name));
        Assert.Throws<ArgumentException>(() => ObjectNames.Validate(name));
    }

    [Fact]
    public void 長すぎる名前は拒む()
        => Assert.NotNull(ObjectNames.FindProblem("a/" + new string('x', 1100)));

    [Fact]
    public void オブジェクト名と相対パスは相互に写せる()
    {
        var relative = ObjectNames.ToRelativePath("Attachments/" + Guid32);
        Assert.Equal(Path.Combine("Attachments", Guid32), relative);
        Assert.Equal("Attachments/" + Guid32, ObjectNames.FromRelativePath(relative));
    }

    [Fact]
    public void 種別のまとまりは_外部ストレージの先頭フォルダに対応する()
    {
        Assert.Equal(["Attachments/"], ObjectNames.Prefixes(BinaryGroup.Attachments));
        Assert.Equal(["Images/"], ObjectNames.Prefixes(BinaryGroup.Images));
        Assert.Equal(["SiteImage/"], ObjectNames.Prefixes(BinaryGroup.SiteImage));
        Assert.Equal(["TenantImage/"], ObjectNames.Prefixes(BinaryGroup.TenantImage));
    }

    [Fact]
    public void types_の解析()
    {
        Assert.True(BinaryGroups.TryParse("Attachments, images", out var groups, out _));
        Assert.Equal([BinaryGroup.Attachments, BinaryGroup.Images], groups);
        Assert.True(BinaryGroups.TryParse("images,images", out groups, out _));
        Assert.Single(groups);
        Assert.False(BinaryGroups.TryParse("videos", out _, out var error));
        Assert.Contains("videos", error);
        Assert.False(BinaryGroups.TryParse("", out _, out _));
        Assert.False(BinaryGroups.TryParse("1", out _, out _)); // 数値の別名を受けない
    }
}
