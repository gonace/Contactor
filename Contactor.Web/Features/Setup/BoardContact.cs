using Contactor.Boards;

namespace Contactor.Web.Features.Setup;

/// <summary>Waits for the board to answer, for the setup guide's "Board online" check.</summary>
public static class BoardContact
{
    /// <summary>How long the guide waits for the board before it says it found none.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True as soon as the board is connected, false if it isn't within <paramref name="timeout"/>. With
    /// <paramref name="moved"/> (the address or login was just changed) a connection that was already open doesn't
    /// count: it is to the old address, and is about to end. Only a connection made after it has ended does.
    /// </summary>
    public static async Task<bool> WaitAsync(IRelayBoard board, bool moved, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = !moved;
        var gate = new Lock();

        void OnChanged(object? sender, BoardState state)
        {
            lock (gate)
            {
                if (!state.IsConnected)
                {
                    ended = true;
                }
                else if (ended)
                {
                    connected.TrySetResult();
                }
            }
        }

        board.StateChanged += OnChanged;
        try
        {
            lock (gate)
            {
                if (!board.State.IsConnected)
                {
                    ended = true;
                }
                else if (ended)
                {
                    return true;
                }
            }

            await connected.Task.WaitAsync(timeout, time, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            board.StateChanged -= OnChanged;
        }
    }
}
