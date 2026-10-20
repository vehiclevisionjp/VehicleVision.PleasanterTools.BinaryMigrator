namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

/// <summary>テスト用の一時フォルダ。破棄すると中身ごと消す。</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>オブジェクト名（/ 区切り）の位置へファイルを作る。</summary>
    public string Write(string objectName, byte[] data)
    {
        var full = System.IO.Path.Combine(Path, objectName.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストを落とさない
        }
    }
}
