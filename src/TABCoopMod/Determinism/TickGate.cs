using System;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Decides whether the next simulation tick may advance. In single-process deterministic
    /// replay this is always ready; network lockstep can block a tick until its inputs arrive.
    /// </summary>
    public interface ITickGate
    {
        bool IsTickReady(int nextSimTick);
    }

    public sealed class AlwaysReadyTickGate : ITickGate
    {
        public bool IsTickReady(int nextSimTick) => true;
    }

    /// <summary>
    /// Manual gate for debugging: ticks stay blocked until explicitly released.
    /// </summary>
    public sealed class ManualTickGate : ITickGate
    {
        private int _releasedThrough = int.MaxValue;

        public void Release(int tick) { _releasedThrough = tick; }
        public void ReleaseAll() { _releasedThrough = int.MaxValue; }
        public bool IsTickReady(int nextSimTick) => nextSimTick <= _releasedThrough;
    }

    /// <summary>
    /// Network lockstep tick gate: a tick is ready once the host's turn input for it
    /// has been received (wired in CoopSessionManager; see docs/02_ARCHITECTURE_DESIGN.md).
    /// </summary>
    public sealed class TurnInputTickGate : ITickGate
    {
        private int _readyThrough = -1;

        public int ReadyThrough => _readyThrough;
        public void MarkReady(int tick) { if (tick > _readyThrough) _readyThrough = tick; }
        public bool IsTickReady(int nextSimTick) => nextSimTick <= _readyThrough;
    }
}
