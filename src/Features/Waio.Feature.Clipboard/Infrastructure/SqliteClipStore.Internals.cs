using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Infrastructure;

/// <summary>SqliteClipStore 的私有辅助:blob 文件读写、SQL 辅助、内容拆格式。</summary>
internal sealed partial class SqliteClipStore
{
    /// <summary>先收集这批条目引用的 blob,删完再清理没有任何条目引用的 blob 文件。</summary>
    private void DeleteWhere(string condition)
    {
        var blobs = new List<string>();
        using (var find = Command($"SELECT DISTINCT blob FROM clip_data WHERE blob IS NOT NULL AND clip_id IN (SELECT id FROM clip WHERE {condition})"))
        using (var reader = find.ExecuteReader())
        {
            while (reader.Read())
            {
                blobs.Add(reader.GetString(0));
            }
        }

        using (var delete = Command($"DELETE FROM clip WHERE {condition}"))
        {
            delete.ExecuteNonQuery(); // clip_data 随外键级联删除
        }

        foreach (var hash in blobs)
        {
            using var count = Command("SELECT COUNT(*) FROM clip_data WHERE blob = $b");
            count.Parameters.AddWithValue("$b", hash);
            if ((long)count.ExecuteScalar()! == 0)
            {
                TryDeleteBlob(hash);
            }
        }
    }

    private void InsertData(SqliteTransaction transaction, long id, string format, byte[] data)
    {
        string? blob = null;
        if (data.Length > InlineLimit)
        {
            blob = WriteBlob(data);
        }

        using var command = Command(
            "INSERT INTO clip_data(clip_id, format, size, data, blob) VALUES($id, $f, $size, $data, $blob)", transaction);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$f", format);
        command.Parameters.AddWithValue("$size", data.Length);
        command.Parameters.AddWithValue("$data", blob is null ? data : DBNull.Value);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static IEnumerable<(string Format, byte[] Data)> Formats(ClipboardContent content)
    {
        if (!string.IsNullOrEmpty(content.Text))
        {
            yield return ("text", Encoding.UTF8.GetBytes(content.Text));
        }

        if (content.Files.Count > 0)
        {
            yield return ("files", Encoding.UTF8.GetBytes(string.Join('\n', content.Files)));
        }

        if (content.Image is { Length: > 0 })
        {
            yield return ("image", content.Image);
        }

        if (content.Rtf is { Length: > 0 })
        {
            yield return ("rtf", content.Rtf);
        }

        if (content.Html is { Length: > 0 })
        {
            yield return ("html", content.Html);
        }
    }

    private string WriteBlob(byte[] data)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var path = Path.Combine(_blobDirectory, hash);
        if (!File.Exists(path))
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true); // 原子替换:崩溃不会留下半个 blob
        }

        return hash;
    }

    private byte[]? ReadBlob(string hash)
    {
        var path = Path.Combine(_blobDirectory, hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private void TryDeleteBlob(string hash)
    {
        try
        {
            File.Delete(Path.Combine(_blobDirectory, hash));
        }
        catch (IOException)
        {
            // 删不掉的孤儿 blob 只占空间,不影响正确性
        }
    }

    private static List<ClipItem> ReadItems(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
        {
            items.Add(new ClipItem(
                reader.GetInt64(0),
                (ClipKind)reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetInt64(6),
                reader.GetInt64(7) != 0,
                reader.GetInt64(8) != 0));
        }

        return items;
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

