using System;
using System.Collections.Generic;

namespace MouseIA.Core;

/// <summary>
/// Detects a quick back-and-forth "shake" of the mouse from raw pointer positions.
/// A shake is several direction reversals on the horizontal axis within a short window,
/// each leg long enough that ordinary pointing doesn't count.
/// </summary>
public sealed class ShakeDetector
{
    private readonly Queue<long> _reversals = new();
    private int _lastX, _lastY;
    private long _lastTime;
    private bool _hasLast;
    private int _direction;
    private int _legDistance;
    private int _legVertical;
    private long _legStart;
    private long _cooldownUntil;

    /// <summary>1 (needs a strong, fast shake) .. 10 (very sensitive).</summary>
    public int Sensitivity { get; set; } = 5;

    /// <summary>Screen scale, so legs are measured in logical pixels.</summary>
    public double Scale { get; set; } = 1.0;

    private double T => (Math.Clamp(Sensitivity, 1, 10) - 1) / 9.0;                  // 0 strong .. 1 light
    private int MinLeg => (int)Math.Round((170 - 125 * T) * Scale);                  // 170..45 px each way
    private double MinSpeed => (1.7 - 1.1 * T) * Scale;                              // px per ms during a leg
    private int Needed => Sensitivity <= 3 ? 6 : Sensitivity <= 6 ? 5 : Sensitivity <= 8 ? 4 : 3;
    private const long WindowMs = 1100;
    private const long LegTimeoutMs = 220;

    public void Reset()
    {
        _reversals.Clear();
        _hasLast = false;
        _direction = 0;
        _legDistance = 0;
        _legVertical = 0;
    }

    /// <summary>Feeds a pointer position; returns true when a shake has just completed.</summary>
    public bool Add(int x, int y, long timeMs)
    {
        if (!_hasLast)
        {
            _lastX = x; _lastY = y; _lastTime = timeMs; _hasLast = true;
            _legStart = timeMs;
            return false;
        }
        if (timeMs - _lastTime > LegTimeoutMs)
        {
            // A pause ends any shake in progress.
            Reset();
            _lastX = x; _lastY = y; _lastTime = timeMs; _hasLast = true;
            _legStart = timeMs;
            return false;
        }

        int dx = x - _lastX;
        int dy = y - _lastY;
        _lastX = x;
        _lastY = y;
        _lastTime = timeMs;
        if (dx == 0)
        {
            _legVertical += Math.Abs(dy);
            return false;
        }
        int dir = Math.Sign(dx);

        if (dir == _direction)
        {
            _legDistance += Math.Abs(dx);
            _legVertical += Math.Abs(dy);
            return false;
        }

        // Direction changed: the finished leg counts if it was long, fast and mostly sideways.
        long legTime = Math.Max(1, timeMs - _legStart);
        bool countedLeg = _direction != 0 && _legDistance >= MinLeg &&
                          _legDistance / (double)legTime >= MinSpeed &&
                          _legVertical <= _legDistance * 0.6;
        _direction = dir;
        _legDistance = Math.Abs(dx);
        _legVertical = Math.Abs(dy);
        _legStart = timeMs;
        if (!countedLeg)
        {
            // A weak leg breaks the rhythm.
            _reversals.Clear();
            return false;
        }

        _reversals.Enqueue(timeMs);
        while (_reversals.Count > 0 && timeMs - _reversals.Peek() > WindowMs) _reversals.Dequeue();

        if (_reversals.Count >= Needed && timeMs >= _cooldownUntil)
        {
            _cooldownUntil = timeMs + 1500;
            Reset();
            return true;
        }
        return false;
    }
}
