namespace Runtime.Time;

/// <summary>Unit-preserving adapters, not a clock registry or mutable global clock.</summary>
public static class ClockExtensions
{
    // Only for transient UI/cache coordinates previously expressed as TickCount64.
    // Persisted timestamps must use GetUtcNow().ToUnixTimeMilliseconds() instead.
    public static long GetTimestampMilliseconds(this TimeProvider clock) =>
        clock.GetElapsedTime(0, clock.GetTimestamp()).Ticks / TimeSpan.TicksPerMillisecond;

    public static CancellationTokenSource CreateLinkedCancellationTokenSource(this TimeProvider clock, params CancellationToken[] tokens) =>
        new LinkedClockCancellation(clock, tokens);

    private sealed class LinkedClockCancellation : CancellationTokenSource
    {
        private readonly CancellationTokenRegistration[] _links;

        internal LinkedClockCancellation(TimeProvider clock, CancellationToken[] tokens)
            : base(Timeout.InfiniteTimeSpan, clock)
        {
            // The BCL provider-backed CTS retains its ITimer for later CancelAfter
            // changes. Linking owns only registrations; it does not introduce an
            // independently racing timeout callback or alter the caller's token.
            _links = tokens.Select(token => token.UnsafeRegister(static state =>
                ((CancellationTokenSource)state!).Cancel(), this)).ToArray();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var link in _links) link.Dispose();
            base.Dispose(disposing);
        }
    }
}
