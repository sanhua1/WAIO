using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Infrastructure;

/// <summary>
/// SQLite 存储(ADR-0009,参考 MAIO 的 ClipStore):元数据与 ≤64 KB 的数据在 history.sqlite,
/// 更大的数据(主要是图片)存成 blobs/&lt;sha256&gt; 文件,数据库只记引用。
/// 一把锁串行所有操作(单连接);本地库足够快,不需要更复杂的并发。
/// </summary>
internal sealed partial class SqliteClipStore : IClipStore
{
    /// <summary>超过这个大小的格式数据存成 blob 文件。</summary>
    public const int InlineLimit = 64 * 1024;

    private const string Columns = """
        id, kind, summary, source, created_at, last_used_at, byte_size, pinned,
        EXISTS(SELECT 1 FROM clip_data d WHERE d.clip_id = clip.id AND d.format = 'image')
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly string _blobDirectory;

    private SqliteClipStore(SqliteConnection connection, string blobDirectory)
    {
        _connection = connection;
        _blobDirectory = blobDirectory;
    }

    /// <summary>
    /// 打开(必要时创建)<paramref name="directory"/> 下的历史库。数据库损坏时备份成 .bad-时间戳并重建——
    /// 剪贴板历史丢了不致命,但不能因此起不来(ARCHITECTURE §7)。
    /// </summary>
    public static SqliteClipStore Open(string directory, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        var blobs = Path.Combine(directory, "blobs");
        Directory.CreateDirectory(blobs);
        var path = Path.Combine(directory, "history.sqlite");

        try
        {
            return OpenAt(path, blobs);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT / SQLITE_NOTADB
        {
            var backup = $"{path}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            SqliteConnection.ClearAllPools();
            File.Move(path, backup);
            logger.LogError(ex, "剪贴板历史库已损坏,备份为 {Backup},将重建", backup);
            return OpenAt(path, blobs);
        }
    }

    private static SqliteClipStore OpenAt(string path, string blobs)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS clip(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind INTEGER NOT NULL,
                    summary TEXT NOT NULL,
                    source TEXT,
                    created_at INTEGER NOT NULL,
                    last_used_at INTEGER NOT NULL,
                    byte_size INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    exact_hash TEXT NOT NULL,
                    content_key TEXT,
                    search_text TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS clip_last_used ON clip(last_used_at DESC);
                CREATE INDEX IF NOT EXISTS clip_exact ON clip(exact_hash);
                CREATE TABLE IF NOT EXISTS clip_data(
                    clip_id INTEGER NOT NULL REFERENCES clip(id) ON DELETE CASCADE,
                    format TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    data BLOB,
                    blob TEXT,
                    PRIMARY KEY(clip_id, format));
                """);
            return new SqliteClipStore(connection, blobs);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public long Insert(NewClip clip)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            long id;
            using (var insert = Command("""
                INSERT INTO clip(kind, summary, source, created_at, last_used_at, byte_size, pinned, exact_hash, content_key, search_text)
                VALUES($kind, $summary, $source, $at, $at, $size, 0, $hash, $key, $search);
                SELECT last_insert_rowid();
                """, transaction))
            {
                insert.Parameters.AddWithValue("$kind", (int)clip.Kind);
                insert.Parameters.AddWithValue("$summary", clip.Summary);
                insert.Parameters.AddWithValue("$source", (object?)clip.SourceApp ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", clip.At.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$size", clip.ByteSize);
                insert.Parameters.AddWithValue("$hash", clip.ExactHash);
                insert.Parameters.AddWithValue("$key", (object?)clip.ContentKey ?? DBNull.Value);
                insert.Parameters.AddWithValue("$search", clip.SearchText);
                id = (long)insert.ExecuteScalar()!;
            }

            foreach (var (format, data) in Formats(clip.Content))
            {
                InsertData(transaction, id, format, data);
            }

            transaction.Commit();
            return id;
        }
    }

    public long? FindByExactHash(string exactHash)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id FROM clip WHERE exact_hash = $hash LIMIT 1");
            command.Parameters.AddWithValue("$hash", exactHash);
            return command.ExecuteScalar() as long?;
        }
    }

    public IReadOnlyList<RecentClip> Recent(int count)
    {
        lock (_gate)
        {
            using var command = Command(
                "SELECT id, content_key, pinned, source, created_at FROM clip ORDER BY last_used_at DESC, id DESC LIMIT $n");
            command.Parameters.AddWithValue("$n", count);
            using var reader = command.ExecuteReader();
            var list = new List<RecentClip>();
            while (reader.Read())
            {
                list.Add(new RecentClip(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            }

            return list;
        }
    }

    public IReadOnlyList<ClipItem> List(int limit)
    {
        lock (_gate)
        {
            using var command = Command($"""
                SELECT {Columns} FROM clip
                WHERE pinned = 1 OR id IN (SELECT id FROM clip WHERE pinned = 0 ORDER BY last_used_at DESC, id DESC LIMIT $n)
                """);
            command.Parameters.AddWithValue("$n", limit);
            return ReadItems(command);
        }
    }

    public IReadOnlyList<ClipItem> Search(IReadOnlyList<string> lowerTerms, int limit)
    {
        if (lowerTerms.Count == 0)
        {
            return List(limit);
        }

        lock (_gate)
        {
            var where = string.Join(" AND ", lowerTerms.Select((_, i) => $"search_text LIKE $t{i} ESCAPE '\\'"));
            using var command = Command($"SELECT {Columns} FROM clip WHERE {where} ORDER BY last_used_at DESC, id DESC LIMIT $n");
            for (var i = 0; i < lowerTerms.Count; i++)
            {
                command.Parameters.AddWithValue($"$t{i}", "%" + EscapeLike(lowerTerms[i]) + "%");
            }

            command.Parameters.AddWithValue("$n", limit);
            return ReadItems(command);
        }
    }

    public ClipItem? Get(long id)
    {
        lock (_gate)
        {
            using var command = Command($"SELECT {Columns} FROM clip WHERE id = $id");
            command.Parameters.AddWithValue("$id", id);
            return ReadItems(command).FirstOrDefault();
        }
    }

    public ClipboardContent? LoadContent(long id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT format, data, blob FROM clip_data WHERE clip_id = $id");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            string? text = null;
            IReadOnlyList<string> files = [];
            byte[]? image = null, rtf = null, html = null;
            while (reader.Read())
            {
                var data = reader.IsDBNull(1) ? ReadBlob(reader.GetString(2)) : (byte[])reader[1];
                if (data is null)
                {
                    continue; // blob 文件丢了:这个格式放弃,其他格式照常
                }

                switch (reader.GetString(0))
                {
                    case "text": text = Encoding.UTF8.GetString(data); break;
                    case "files": files = Encoding.UTF8.GetString(data).Split('\n'); break;
                    case "image": image = data; break;
                    case "rtf": rtf = data; break;
                    case "html": html = data; break;
                }
            }

            var content = new ClipboardContent { Text = text, Files = files, Image = image, Rtf = rtf, Html = html };
            return content.IsEmpty ? null : content;
        }
    }

    public byte[]? LoadImage(long id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT data, blob FROM clip_data WHERE clip_id = $id AND format = 'image'");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return reader.IsDBNull(0) ? ReadBlob(reader.GetString(1)) : (byte[])reader[0];
        }
    }

    public void Touch(long id, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var command = Command("UPDATE clip SET last_used_at = $at WHERE id = $id");
            command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            using var command = Command("UPDATE clip SET pinned = $p WHERE id = $id");
            command.Parameters.AddWithValue("$p", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var idList = string.Join(',', ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            DeleteWhere($"id IN ({idList})");
        }
    }

    public void DeleteUnpinned()
    {
        lock (_gate)
        {
            DeleteWhere("pinned = 0");
        }
    }

    public IReadOnlyList<RetentionPolicy.Entry> Entries()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id, byte_size, last_used_at, pinned FROM clip");
            using var reader = command.ExecuteReader();
            var list = new List<RetentionPolicy.Entry>();
            while (reader.Read())
            {
                list.Add(new RetentionPolicy.Entry(
                    reader.GetInt64(0), reader.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetInt64(3) != 0));
            }

            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}

