namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// 移行先（書く側）。Local・AzureBlob の実装を差し替えられるようにする抽象。
/// </summary>
public interface IBinarySink
{
    /// <summary>コンソールへ出す名前（秘密を含めない）。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 接続・認証・書き込み先の存在を確かめる。全件に効く失敗は <see cref="FatalMigrationException"/> で知らせる。
    /// </summary>
    /// <param name="forWrite">書き込みを行うか。ドライランでは false（保管先のフォルダなどを作らない）。</param>
    Task CheckAsync(bool forWrite, CancellationToken cancellationToken);

    /// <summary>存在確認。あれば大きさ（バイト）、無ければ null。</summary>
    Task<long?> GetSizeAsync(string objectName, CancellationToken cancellationToken);

    /// <summary>書き込む。同名があれば置き換える。途中で失敗したとき、中途半端なものを正規の名前で残さない。</summary>
    Task WriteAsync(string objectName, Stream content, string? contentType, CancellationToken cancellationToken);

    /// <summary>書いたものを読み戻して、大きさと SHA-256 を求める（検証用）。無ければ例外。</summary>
    Task<BinaryFingerprint> ReadBackAsync(string objectName, CancellationToken cancellationToken);

    /// <summary>移行先のオブジェクトを削除する（検証に失敗したコピーの後始末）。無ければ何もしない。</summary>
    Task DeleteAsync(string objectName, CancellationToken cancellationToken);
}
