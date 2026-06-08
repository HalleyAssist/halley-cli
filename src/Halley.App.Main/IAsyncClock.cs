namespace Halley.App.Main;

public interface IAsyncClock
{
    DateTimeOffset Now { get; }

    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemAsyncClock : IAsyncClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}
