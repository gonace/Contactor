using System.Security.Cryptography;
using Contactor.Boards;

namespace Contactor.Demo;

/// <summary>
/// Each copy's own block of board ports, one more than the boards it may have (<see cref="DemoOptions.MaxBoards"/>),
/// which no other copy shares while either runs (Issue #606). The copy's simulator opens its boards only there, and its
/// app connects only there, so a visitor never reaches another's boards. The blocks lie from <see cref="First"/> to
/// <see cref="Last"/>, below the random ports of the copies' programs (<see cref="LoopbackPorts"/>), so they never meet.
/// A block is taken at random among the free ones, and given back once the copy's processes have exited; never when
/// they didn't stop cleanly.
/// </summary>
public sealed class BoardPortBlocks
{
    public const int First = 10000;
    public const int Last = LoopbackPorts.First - 1;

    /// <summary>How many ports the blocks have together.</summary>
    public const int Room = Last - First + 1;

    private readonly int _size;
    private readonly bool[] _taken;
    private readonly Lock _lock = new();

    /// <param name="size">The ports in each block.</param>
    public BoardPortBlocks(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, Room);
        _size = size;
        _taken = new bool[Room / size];
    }

    /// <summary>How many blocks there are: at least <see cref="DemoOptions.MaxVisitors"/> (checked at start).</summary>
    public int Count => _taken.Length;

    /// <summary>Whether a block is free.</summary>
    public bool HasFree
    {
        get
        {
            lock (_lock)
            {
                return _taken.Contains(false);
            }
        }
    }

    /// <summary>A free block, now taken; throws when none is free.</summary>
    public PortRange Take()
    {
        lock (_lock)
        {
            var free = Enumerable.Range(0, _taken.Length).Where(i => !_taken[i]).ToList();
            if (free.Count == 0)
            {
                throw new InvalidOperationException("Every block of board ports is taken.");
            }

            var index = free[RandomNumberGenerator.GetInt32(free.Count)];
            _taken[index] = true;
            var first = First + (index * _size);
            return new PortRange(first, first + _size - 1);
        }
    }

    /// <summary>Gives a block back. Nothing happens for one that isn't taken.</summary>
    public void Release(PortRange block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var offset = block.First - First;
        if (offset < 0 || offset % _size != 0 || offset / _size >= _taken.Length || block.Count != _size)
        {
            return;
        }

        lock (_lock)
        {
            _taken[offset / _size] = false;
        }
    }
}
