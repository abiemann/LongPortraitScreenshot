namespace LongPortraitScreenshot.Capture;

internal sealed class ScrollSettleTracker
{
    private const int AbsoluteRetryDelayMilliseconds = 300;
    private readonly double _startPercent;
    private readonly double _minimumMovementPercent;
    private readonly bool _movementRequired;
    private double _previousPercent;
    private int _stablePolls;
    private bool _retriedAbsoluteRequest;

    public ScrollSettleTracker(
        double startPercent,
        double requestedPercent,
        double initialPercent,
        double minimumMovementPercent)
    {
        _startPercent = startPercent;
        _minimumMovementPercent = minimumMovementPercent;
        _movementRequired = Math.Abs(requestedPercent - startPercent) > minimumMovementPercent;
        _previousPercent = initialPercent;
        HasDepartedStart = HasMovedFromStart(initialPercent);
    }

    public bool HasDepartedStart { get; private set; }

    public bool TryBeginAbsoluteRetry(long elapsedMilliseconds)
    {
        if (!_movementRequired || HasDepartedStart || _retriedAbsoluteRequest
            || elapsedMilliseconds < AbsoluteRetryDelayMilliseconds)
        {
            return false;
        }

        _retriedAbsoluteRequest = true;
        return true;
    }

    public bool Observe(double currentPercent)
    {
        HasDepartedStart |= HasMovedFromStart(currentPercent);
        if (_movementRequired && !HasDepartedStart)
        {
            _stablePolls = 0;
            _previousPercent = currentPercent;
            return false;
        }

        if (Math.Abs(currentPercent - _previousPercent) <= _minimumMovementPercent)
        {
            _stablePolls++;
        }
        else
        {
            _stablePolls = 0;
        }

        _previousPercent = currentPercent;
        return _stablePolls >= 2;
    }

    public bool ConfirmAfterRender(double currentPercent)
    {
        HasDepartedStart |= HasMovedFromStart(currentPercent);
        bool unchanged = Math.Abs(currentPercent - _previousPercent) <= _minimumMovementPercent;
        if ((_movementRequired && !HasDepartedStart) || !unchanged)
        {
            _stablePolls = 0;
            _previousPercent = currentPercent;
            return false;
        }

        _previousPercent = currentPercent;
        return true;
    }

    private bool HasMovedFromStart(double currentPercent) =>
        Math.Abs(currentPercent - _startPercent) > _minimumMovementPercent;
}
