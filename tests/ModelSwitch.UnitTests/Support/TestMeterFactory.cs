using System.Diagnostics.Metrics;

namespace ModelSwitch.UnitTests.Support;

/// <summary>An <see cref="IMeterFactory"/> whose meters are scoped to it, so a test can listen to exactly the instruments it created.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _created = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _created.Add(meter);
        return meter;
    }

    public void Dispose() => _created.ForEach(m => m.Dispose());
}
