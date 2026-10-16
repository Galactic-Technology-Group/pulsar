using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Nms.Shared.Contracts;

namespace Nms.Agent.Storage;

/// <summary>
/// Store-and-forward queue for check results, backed by SQLite in {StateDir}/outbox.db.
/// Results are kept in insertion order and deleted only after the API acknowledges them.
/// WAL mode + synchronous=NORMAL keeps SD-card writes modest.
/// </summary>
public sealed class ResultOutbox : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private readonly ILogger<ResultOutbox> _log;
    public const long MaxRows = 500_000; // ~ 500 checks * 1/min * 16 hours

    public ResultOutbox(IOptions<AgentOptions> options, ILogger<ResultOutbox> log)
    {
        _log = log;
        Directory.CreateDirectory(options.Value.StateDir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(options.Value.StateDir, "outbox.db")}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
             CREATE TABLE IF NOT EXISTS outbox(
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 check_id TEXT NOT NULL,
                 time TEXT NOT NULL,
                 ok INTEGER NOT NULL,
                 latency_ms INTEGER NULL,
                 detail TEXT NULL);
             """);
    }

    public void Enqueue(CheckResultDto r)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO outbox(check_id,time,ok,latency_ms,detail) VALUES($c,$t,$o,$l,$d)";
            cmd.Parameters.AddWithValue("$c", r.CheckId.ToString());
            cmd.Parameters.AddWithValue("$t", r.Time.ToString("O"));
            cmd.Parameters.AddWithValue("$o", r.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$l", (object?)r.LatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)r.Detail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public long Count()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Oldest N rows plus the highest id in that page, for acknowledgement.</summary>
    public (List<CheckResultDto> Items, long LastId) Peek(int max)
    {
        lock (_gate)
        {
            var items = new List<CheckResultDto>(max);
            long lastId = 0;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id,check_id,time,ok,latency_ms,detail FROM outbox ORDER BY id LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                lastId = rd.GetInt64(0);
                items.Add(new CheckResultDto(
                    Guid.Parse(rd.GetString(1)),
                    DateTimeOffset.Parse(rd.GetString(2)),
                    rd.GetInt32(3) == 1,
                    rd.IsDBNull(4) ? null : rd.GetInt32(4),
                    rd.IsDBNull(5) ? null : rd.GetString(5)));
            }
            return (items, lastId);
        }
    }

    public void Ack(long upToId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox WHERE id <= $id";
            cmd.Parameters.AddWithValue("$id", upToId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops the oldest rows if the queue grows past <see cref="MaxRows"/> (prolonged outage).</summary>
    public void Trim()
    {
        var count = Count();
        if (count <= MaxRows) return;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox WHERE id IN (SELECT id FROM outbox ORDER BY id LIMIT $n)";
            cmd.Parameters.AddWithValue("$n", count - MaxRows);
            var n = cmd.ExecuteNonQuery();
            _log.LogWarning("Outbox exceeded {Max} rows; dropped {Dropped} oldest results", MaxRows, n);
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
