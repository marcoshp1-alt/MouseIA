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
    private int _lastX;
    private long _lastTime;
    private bool _hasLast;
    private int _direction;
    private int _legDistance;
    private long _cooldownUntil;

    /// <summary>1 (needs a strong shake) .. 10 (very sensitive).</summary>
    public int Sensitivity { get; set; } = 5;

    /// <summary>Screen scale, so legs are measured in logical pixels.</summary>
    public double Scale { get; set; } = 1.0;

    private int MinLeg => (int)Math.Round((90 - Sensitivity * 6) * Scale);      // 84..30 px
    private int Needed => Sensitivity >= 8 ? 3 : 4;                               // reversals
    private const long WindowMs = 900;
    private const long LegTimeoutMs = 220;

    public void Reset()
    {
        _reversals.Clear();
        _hasLast = false;
        _direction = 0;
        _legDistance = 0;
    }

    /// <summary>Feeds a pointer position; returns true when a shake has just completed.</summary>
    public bool Add(int x, int y, long timeMs)
    {
        if (!_hasLast)
        {
            _lastX = x; _lastTime = timeMs; _hasLast = true;
            return false;
        }
        if (timeMs - _lastTime > LegTimeoutMs)
        {
            // A pause ends any shake in progress.
            Reset();
            _lastX = x; _lastTime = timeMs; _hasLast = true;
            return false;
        }

        int dx = x - _lastX;
        _lastX = x;
        _lastTime = timeMs;
        if (dx == 0) return false;
        int dir = Math.Sign(dx);

        if (dir == _direction)
        {
            _legDistance += Math.Abs(dx);
            return false;
        }

        // Direction changed: the finished leg counts if it was long enough.
        bool countedLeg = _direction != 0 && _legDistance >= MinLeg;
        _direction = dir;
        _legDistance = Math.Abs(dx);
        if (!countedLeg) return false;

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
