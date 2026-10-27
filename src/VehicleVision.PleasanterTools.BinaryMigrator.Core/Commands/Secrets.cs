using System.Data.Common;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>秘密（接続文字列）を、ファイル・標準入力・環境変数から読む。コマンドラインの引数には直書きさせない。</summary>
public static class SecretResolver
{
    /// <summary>
    /// 優先順は ファイル → 標準入力 → 環境変数。見つからなければ null。前後の空白と改行は除く。
    /// 読めなかったときの例外の文言に、秘密やファイルの中身は含めない。
    /// </summary>
    public static string? Resolve(
        SecretSource source,
        string environmentName,
        Func<string, string?> getEnvironmentVariable,
        Func<TextReader> openStandardInput,
        Func<string, string> readAllText)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? text;
        if (source.File is not null)
        {
            try
            {
                text = readAllText(source.File);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FatalMigrationException("Cannot read the secret file: " + source.File + " (" + ex.GetType().Name + ")");
            }
        }
        else if (source.FromStdin)
        {
            text = openStandardInput().ReadToEnd();
        }
        else
        {
            text = getEnvironmentVariable(environmentName);
        }
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

/// <summary>
/// コンソールへ出す文字列から、秘密を伏せる。接続文字列そのものと、その中のパスワード・キー類、
/// および「Password=...」の形を <c>***</c> に置き換える。
/// </summary>
public sealed class SecretRedactor
{
    private const string Mask = "***";

    private static readonly string[] SecretKeys =
    [
        "password", "pwd", "accountkey", "sharedaccesssignature", "sas", "secret", "token", "sig",
    ];

    private static readonly Regex KeyValuePattern = new(
        @"(?<key>\b(?:password|pwd|accountkey|sharedaccesssignature|secret|token|sig))\s*=\s*(?<value>[^;&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<string> _secrets = [];

    /// <summary>伏せる秘密を足す。接続文字列なら、その中のパスワード等の値も足す。</summary>
    public void Add(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }
        AddOne(secret);
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = secret };
            foreach (var key in SecretKeys)
            {
                if (builder.TryGetValue(key, out var value) && value is string text)
                {
                    AddOne(text);
                }
            }
        }
        catch (ArgumentException)
        {
            // 接続文字列の形ではない（単なる秘密の文字列）。そのまま伏せるだけにする
        }
    }

    private void AddOne(string value)
    {
        if (value.Length >= 4 && !_secrets.Contains(value, StringComparer.Ordinal))
        {
            _secrets.Add(value);
        }
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        // 長い順に置き換える（接続文字列全体を、その中のパスワードより先に伏せる）
        foreach (var secret in _secrets.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }
        return KeyValuePattern.Replace(text, "${key}=" + Mask);
    }
}
