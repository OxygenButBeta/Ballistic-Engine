namespace BallisticEngine;

public sealed class SendRateClock {
    public const int DefaultDivisor = 3;

    public int Divisor { get; }
    public int LocalTick { get; private set; }

    public SendRateClock(int divisor = DefaultDivisor) {
        if (divisor < 1) throw new ArgumentOutOfRangeException(nameof(divisor), "send-rate divisor must be >= 1");
        Divisor = divisor;
    }

    public bool IsBoundary => LocalTick % Divisor == 0;

    public bool Advance() {
        bool sendBoundary = LocalTick % Divisor == 0;
        LocalTick++;
        return sendBoundary;
    }

    public void Reset() => LocalTick = 0;
}
