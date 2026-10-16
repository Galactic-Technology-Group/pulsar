using Microsoft.Extensions.Options;
using Nms.Agent.Storage;
using Nms.Agent.Transport;
using Nms.Shared.Contracts;

namespace Nms.Agent.Scheduling;

/// <summary>Drains the outbox to the API in order, every N seconds or sooner when the batch size is reached.</summary>
public sealed class ResultsUploader(
    ResultOutbox outbox,
    ApiClient api,
    CredentialStore creds,
    CheckScheduler scheduler,
    IOptions<AgentOptions> options,
    ILogger<ResultsUploader> log) : BackgroundService
{
    private readonly SemaphoreSlim _kick = new(0);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        scheduler.ResultProduced += _ => { if (outbox.Count() >= options.Value.ResultsBatchSize && _kick.CurrentCount == 0) _kick.Release(); };
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, scheduler.CurrentConfig?.ResultsFlushIntervalSec ?? 10));
            if (failures > 0) interval = TimeSpan.FromSeconds(Math.Min(300, interval.TotalSeconds * Math.Pow(2, failures)));
            try { await _kick.WaitAsync(interval, ct); } catch (OperationCanceledException) { break; }

            var c = creds.Load();
            if (c is null) continue;

            while (!ct.IsCancellationRequested)
            {
                var (items, lastId) = outbox.Peek(options.Value.ResultsBatchSize);
                if (items.Count == 0) { failures = 0; break; }
                try
                {
                    var ack = await api.PostResultsAsync(new ResultsBatch(c.AgentId, items), ct);
                    outbox.Ack(lastId);
                    failures = 0;
                    log.LogDebug("Uploaded {Count} results ({Dup} duplicates)", items.Count, ack.Duplicates);
                }
                catch (UnauthorizedAccessException)
                {
                    log.LogError("Credentials rejected; results will keep queuing until re-enrolled");
                    failures = Math.Min(failures + 1, 5);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures = Math.Min(failures + 1, 5);
                    log.LogWarning("Upload failed ({Message}); {Queued} results queued", ex.Message, outbox.Count());
                    outbox.Trim();
                    break;
                }
            }
        }
    }
}
