namespace PCCommandCenter.Core;

/// <summary>Fixed-capacity, thread-safe history of samples. NaN marks a missing sample.</summary>
public sealed class RingBuffer
{
    private readonly double[] _data;
    private readonly object _lock = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity) => _data = new double[Math.Max(2, capacity)];

    public int Capacity => _data.Length;

    public int Count { get { lock (_lock) return _count; } }

    public void Add(double value)
    {
        lock (_lock)
        {
            if (_count < _data.Length)
            {
                _data[(_start + _count) % _data.Length] = value;
                _count++;
            }
            else
            {
                _data[_start] = value;
                _start = (_start + 1) % _data.Length;
            }
        }
    }

    public double[] ToArray()
    {
        lock (_lock)
        {
            var result = new double[_count];
            for (int i = 0; i < _count; i++) result[i] = _data[(_start + i) % _data.Length];
            return result;
        }
    }

    public double Last
    {
        get { lock (_lock) return _count == 0 ? double.NaN : _data[(_start + _count - 1) % _data.Length]; }
    }

    /// <summary>Average of the last <paramref name="n"/> valid samples, or NaN.</summary>
    public double Average(int n)
    {
        lock (_lock)
        {
            double sum = 0; int valid = 0;
            for (int i = Math.Max(0, _count - n); i < _count; i++)
            {
                var v = _data[(_start + i) % _data.Length];
                if (double.IsNaN(v)) continue;
                sum += v; valid++;
            }
            return valid == 0 ? double.NaN : sum / valid;
        }
    }

    public void Clear() { lock (_lock) { _start = 0; _count = 0; } }
}
