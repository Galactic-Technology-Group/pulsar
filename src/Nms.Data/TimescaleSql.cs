namespace Nms.Data;

/// <summary>
/// Timescale objects are created outside EF's model so the migration stays portable.
/// Applied by <see cref="DbInitializer"/> after migrations; every statement is idempotent.
/// <para>
/// <see cref="Required"/> needs only the Apache-licensed build (what Azure Database for PostgreSQL ships).
/// <see cref="Optional"/> features need the Community (TSL) build; each is applied independently and
/// skipped with a warning where unsupported. Without them, history is still a hypertable: the worker's
/// RetentionJob prunes raw rows and the API falls back to raw results when the 5-minute aggregate is missing.
/// </para>
/// </summary>
public static class TimescaleSql
{
    public const string Required = """
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        SELECT create_hypertable('nms.check_results', 'time',
            chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);
        """;

    public static readonly IReadOnlyList<(string Feature, string Sql)> Optional =
    [
        ("compression", """
            ALTER TABLE nms.check_results SET (
                timescaledb.compress,
                timescaledb.compress_segmentby = 'check_id',
                timescaledb.compress_orderby = 'time DESC');
            SELECT add_compression_policy('nms.check_results', INTERVAL '7 days', if_not_exists => TRUE);
            """),
        ("retention policy", """
            SELECT add_retention_policy('nms.check_results', INTERVAL '90 days', if_not_exists => TRUE);
            """),
        ("5-minute continuous aggregate", """
            CREATE MATERIALIZED VIEW IF NOT EXISTS nms.check_results_5m
            WITH (timescaledb.continuous) AS
            SELECT time_bucket('5 minutes', time) AS bucket,
                   check_id,
                   count(*)                         AS samples,
                   count(*) FILTER (WHERE ok)       AS ok_samples,
                   avg(latency_ms)                  AS avg_latency_ms,
                   max(latency_ms)                  AS max_latency_ms
            FROM nms.check_results
            GROUP BY bucket, check_id
            WITH NO DATA;
            SELECT add_continuous_aggregate_policy('nms.check_results_5m',
                start_offset => INTERVAL '1 day',
                end_offset   => INTERVAL '5 minutes',
                schedule_interval => INTERVAL '5 minutes',
                if_not_exists => TRUE);
            """),
    ];
}
