namespace Contactor.Boards;

/// <summary>
/// What a relay does by itself when the board gets power back, after a power cut or a restart, before the app can act
/// (<see cref="PowerOnAbility"/>). <see cref="Off"/>, <see cref="AsBefore"/> and <see cref="On"/> are in order from
/// least to most on; <see cref="FollowsInput"/> is outside that order, since it depends on the board's input.
/// </summary>
public enum PowerOnState
{
    /// <summary>The relay stays off.</summary>
    Off,

    /// <summary>The relay goes back to how it was before the power went, on or off.</summary>
    AsBefore,

    /// <summary>The relay turns on.</summary>
    On,

    /// <summary>The relay follows the board's input that belongs to it: on when the input is on.</summary>
    FollowsInput,
}
