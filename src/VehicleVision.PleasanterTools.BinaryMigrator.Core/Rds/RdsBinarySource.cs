using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// Rds の接続の指定。接続文字列は秘密（パスワードを含み得る）のため、ログやコンソールへ出さない。
/// </summary>
/// <param name="Dbms">DBMS の種類。</param>
/// <param name="ConnectionString">読み取り用の接続文字列。</param>
/// <param name="WriteConnectionString">
/// <c>--delete-source</c>（<c>Bin</c> を NULL にする更新）に使う接続文字列。省略時は <paramref name="ConnectionString"/>。
/// 読み取り専用の資格情報と、更新できる資格情報を分けたいときに使う。
/// </param>
public sealed record RdsConnection(RdsDbms Dbms, string ConnectionString, string? WriteConnectionString = null);

/// <summary>
/// Rds（Pleasanter の DB の <c>Binaries</c> 表）の移行元。DB へ直接つなぐ。
/// 読み取りは SELECT だけで、読み取り専用の接続・セッションにする。巨大な本体はストリームで読む（<c>SequentialAccess</c>）。
/// 更新は <see cref="DeleteSourceAsync"/>（<c>--delete-source</c>）だけで、別の接続を使う。
/// </summary>
public sealed class RdsBinarySource : IBinarySource, ISourceDeleter
{
    private const int DefaultPageSize = 200;
    private const int CommandTimeoutSeconds = 300;

    private readonly RdsConnection _connection;
    private readonly IReadOnlyList<BinaryGroup> _groups;
    private readonly HashSet<BinaryGroup> _groupSet;
    private readonly int _pageSize;

    public RdsBinarySource(RdsConnection connection, IReadOnlyList<BinaryGroup>? groups = null, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            throw new ArgumentException("The Rds connection string is empty.", nameof(connection));
        }
        _connection = connection;
        _groups = groups ?? BinaryGroups.All;
        _groupSet = [.. _groups];
        _pageSize = pageSize;
    }

    public string DisplayName => "Rds (" + RdsDbmsNames.ToDisplayName(_connection.Dbms) + ", Binaries table)";

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(readOnly: true, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // 表と列が読めることを、1 件の一覧で確かめる
            command.CommandText = RdsSql.PageQuery(_connection.Dbms, _groups.ToList(), 1);
            AddParameter(command, "@last", long.MinValue);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new FatalMigrationException(
                "Cannot read the Binaries table from the database (" + ex.GetType().Name + "): " + FirstLine(ex.Message), ex);
        }
        catch (ArgumentException)
        {
            // 例外の文言に接続文字列が混ざらないよう、固定の文言にする
            throw new FatalMigrationException("The Rds connection string is invalid for " + RdsDbmsNames.ToDisplayName(_connection.Dbms) + ".");
        }
    }

    public async IAsyncEnumerable<SourceObject> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var last = long.MinValue;
        while (true)
        {
            var rows = await ReadPageAsync(last, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                foreach (var item in RdsRowMapper.Map(row, _groupSet))
                {
                    yield return item;
                }
            }
            if (rows.Count < _pageSize)
            {
                yield break;
            }
            last = rows[^1].BinaryId;
        }
    }

    private async Task<List<RdsRow>> ReadPageAsync(long last, CancellationToken cancellationToken)
    {
        // 頁ごとに接続を開閉する（長い移行の間、1 本の読み取りを開きっぱなしにしない）
        await using var connection = await OpenAsync(readOnly: true, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = RdsSql.PageQuery(_connection.Dbms, _groups.ToList(), _pageSize);
        command.CommandTimeout = CommandTimeoutSeconds;
        AddParameter(command, "@last", last);
        var rows = new List<RdsRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RdsRow(
                BinaryId: ToLong(reader.GetValue(0)),
                ReferenceId: ToLong(reader.GetValue(1)),
                Guid: reader.IsDBNull(2) ? null : reader.GetString(2),
                BinaryType: reader.IsDBNull(3) ? null : reader.GetString(3),
                ContentType: reader.IsDBNull(4) ? null : reader.GetString(4),
                BinSize: reader.IsDBNull(5) ? null : ToLong(reader.GetValue(5)),
                ThumbnailSize: reader.IsDBNull(6) ? null : ToLong(reader.GetValue(6)),
                IconSize: reader.IsDBNull(7) ? null : ToLong(reader.GetValue(7))));
        }
        return rows;
    }

    public async Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken)
    {
        if (!RdsRowMapper.TryParseKey(item.Key, out var binaryId, out var column))
        {
            throw new ArgumentException("The source key is not a Binaries key.", nameof(item));
        }

        DbConnection? connection = null;
        DbCommand? command = null;
        DbDataReader? reader = null;
        try
        {
            connection = await OpenAsync(readOnly: true, cancellationToken).ConfigureAwait(false);
            command = connection.CreateCommand();
            command.CommandText = RdsSql.ReadColumn(_connection.Dbms, column);
            command.CommandTimeout = CommandTimeoutSeconds;
            AddParameter(command, "@id", binaryId);
            // 巨大な本体を全部メモリへ載せず、順に読む
            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow,
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new MigrationIntegrityException("The column is now NULL or the row no longer exists (already migrated or changed).");
            }
            return new OwningStream(reader.GetStream(0), reader, command, connection);
        }
        catch
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            if (command is not null)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// 移行元の削除: 該当の列を NULL にする UPDATE（書き込み権限が要る）。
    /// 読んだときと長さが変わっている行は更新せず、失敗として扱う。
    /// </summary>
    public async Task DeleteSourceAsync(SourceObject item, CancellationToken cancellationToken)
    {
        if (!RdsRowMapper.TryParseKey(item.Key, out var binaryId, out var column))
        {
            throw new ArgumentException("The source key is not a Binaries key.", nameof(item));
        }
        await using var connection = await OpenAsync(readOnly: false, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = RdsSql.ClearColumn(_connection.Dbms, column);
        command.CommandTimeout = CommandTimeoutSeconds;
        AddParameter(command, "@id", binaryId);
        AddParameter(command, "@size", item.Size);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new MigrationIntegrityException(
                "The row changed after it was read (or is already cleared); the column was not set to NULL.");
        }
    }

    // ---- 接続 ----

    private async Task<DbConnection> OpenAsync(bool readOnly, CancellationToken cancellationToken)
    {
        var text = readOnly
            ? _connection.ConnectionString
            : string.IsNullOrWhiteSpace(_connection.WriteConnectionString) ? _connection.ConnectionString : _connection.WriteConnectionString;
        DbConnection connection = _connection.Dbms switch
        {
            RdsDbms.SqlServer => new SqlConnection(readOnly ? AsReadOnlyIntent(text) : text),
            RdsDbms.PostgreSql => new NpgsqlConnection(text),
            RdsDbms.MySql => new MySqlConnection(text),
            _ => throw new ArgumentOutOfRangeException(nameof(_connection)),
        };
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (readOnly)
            {
                // 接続池から再利用された接続でも、毎回指定する
                foreach (var statement in RdsSql.ReadOnlySessionStatements(_connection.Dbms))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string AsReadOnlyIntent(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationIntent = ApplicationIntent.ReadOnly };
        return builder.ConnectionString;
    }

    private static void AddParameter(DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int64;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static long ToLong(object value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0];
        return line.Length > 300 ? line[..300] : line;
    }

    /// <summary>本体のストリームと、それを読んでいるリーダー・コマンド・接続を一緒に閉じる。</summary>
    private sealed class OwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IAsyncDisposable[] _owners;
        private bool _disposed;

        public OwningStream(Stream inner, params IAsyncDisposable[] owners)
        {
            _inner = inner;
            _owners = owners;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            foreach (var owner in _owners)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            GC.SuppressFinalize(this);
        }
    }
}
