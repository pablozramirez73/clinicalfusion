using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Sensors;

/// <summary>
/// Le sorgenti di una registrazione trattate come una sola (Composite): avvio con ritorno allo stato
/// iniziale se una fallisce, arresto che non si interrompe al primo errore, rilascio di tutte.
/// La sequenza è la stessa su ogni piattaforma; i motori di acquisizione scelgono solo quali sorgenti metterci.
/// </summary>
/// <remarks>Come ogni <see cref="ISensorSource"/>, si usa per una sola registrazione: <c>Start → Stop → Dispose</c>.</remarks>
public sealed class SensorSourceGroup(IReadOnlyList<ISensorSource> members, ILogger logger) : ISensorSource
{
    private readonly List<ISensorSource> _started = [];
    private bool _disposed;

    /// <summary>Tutte le sorgenti del gruppo, anche quelle senza hardware (per la schermata di stato).</summary>
    public IReadOnlyList<ISensorSource> Members { get; } = members;

    public string DisplayName => string.Join(", ", Members.Select(m => m.DisplayName));

    public IReadOnlyList<SensorKind> Produces { get; } = [.. members.SelectMany(m => m.Produces).Distinct()];

    public bool IsAvailable => Members.Any(m => m.IsAvailable);

    /// <summary>
    /// Avvia le sorgenti disponibili, nell'ordine dato. Se una fallisce si fermano quelle già partite e si
    /// rilasciano tutte, poi l'eccezione risale: nessun sensore resta acceso a metà.
    /// </summary>
    public async Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started.Count > 0)
        {
            throw new InvalidOperationException("Sorgenti già avviate.");
        }

        try
        {
            foreach (var source in Members.Where(s => s.IsAvailable))
            {
                await source.StartAsync(sink, cancellationToken).ConfigureAwait(false);
                _started.Add(source);
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            Dispose();
            throw;
        }
    }

    /// <summary>Ferma le sorgenti avviate; un errore viene registrato e non impedisce di fermare le altre.</summary>
    public async Task StopAsync()
    {
        foreach (var source in _started)
        {
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Arresto di {Source} fallito.", source.DisplayName);
            }
        }

        _started.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var source in Members)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rilascio di {Source} fallito.", source.DisplayName);
            }
        }
    }
}
