namespace VTTranslate.Core.Providers;

/// <summary>Optional capability: a provider that can report reconnect attempts after a transient failure.</summary>
public interface IReconnectingProvider
{
    event EventHandler<string>? StatusChanged;
}
