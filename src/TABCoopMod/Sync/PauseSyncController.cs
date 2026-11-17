using System;
using System.Drawing;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Sync
{
    /// <summary>
    /// Coordinates local pause/resume. Every state change is emitted as a NetCommandType.SetPaused
    /// command through CommandInterceptor.Dispatch, so in a live session it is broadcast and applied
    /// on every peer at the same turn instead of only mutating the local game.
    ///
    /// Resume is delayed by a short countdown (rendered with the top banner) so peers can react. In
    /// deterministic replay and on Clients the resume is applied immediately in turn order; only the
    /// Host owns the countdown, so its expiry moment is the single authoritative resume tick.
    /// </summary>
    public static class PauseSyncController
    {
        public static int CountdownSeconds { get; set; } = 3;
        public static bool CountdownActive { get; private set; }
        public static int SecondsRemaining { get; private set; }

        /// <summary>True while this controller is applying a pause command. Kept for compatibility
        /// with any input-layer producer that must not re-capture its own application.</summary>
        public static bool IsApplying { get; private set; }

        private static DateTime _resumeAtUtc;
        private static int _lastShown = -1;
        private static DXTextObject _countdownBanner;

        private static bool InNetworkSession =>
            CoopSessionManager.IsSessionActive && CoopSessionManager.IsInSyncedLevel;

        /// <summary>Local human intent: pause now, or resume after the countdown.</summary>
        public static void RequestPause(bool paused)
        {
            if (!paused)
            {
                if (CountdownActive) return; // already counting down

                // Deterministic replay has no wall-clock peers to wait for.
                if (LockstepClock.IsDeterministicModeActive && !CoopSessionManager.IsSessionActive)
                {
                    Apply(false);
                    return;
                }

                // Only the Host runs the authoritative countdown; a Client's resume request is
                // emitted immediately and ordered by the Host's turn broadcast.
                if (InNetworkSession && CoopSessionManager.Transport != null && !CoopSessionManager.Transport.IsHost)
                {
                    Apply(false);
                    return;
                }
            }
            else
            {
                CancelCountdown();
                if (InNetworkSession && CoopSessionManager.Transport != null && !CoopSessionManager.Transport.IsHost)
                {
                    GameBridge.ShowTopBanner("Pause requested...", Color.LightSkyBlue, 900);
                }
                Apply(true);
                return;
            }

            CountdownActive = true;
            SecondsRemaining = Math.Max(1, CountdownSeconds);
            _resumeAtUtc = DateTime.UtcNow.AddSeconds(SecondsRemaining);
            _lastShown = -1;
            if (!LockstepClock.PausedBySession)
                Apply(true); // ensure game stays paused during the countdown
            Show(SecondsRemaining);
        }

        /// <summary>Called every render frame.</summary>
        public static void Tick()
        {
            if (!CountdownActive) return;

            double remain = (_resumeAtUtc - DateTime.UtcNow).TotalSeconds;
            if (remain <= 0.0)
            {
                CountdownActive = false;
                SecondsRemaining = 0;
                HideCountdownBanner();
                Apply(false);
                GameBridge.ShowTopBanner("Resumed", Color.Gold, 900);
                return;
            }

            int n = (int)Math.Ceiling(remain);
            SecondsRemaining = n;
            if (n != _lastShown) Show(n);
        }

        public static void CancelCountdown()
        {
            CountdownActive = false;
            SecondsRemaining = 0;
            _lastShown = -1;
            HideCountdownBanner();
        }

        private static void Show(int n)
        {
            _lastShown = n;
            try
            {
                var grid = DXIsometricGridObject.Current;
                if (grid != null)
                {
                    if (_countdownBanner == null || !object.ReferenceEquals(_countdownBanner.Parent, grid))
                    {
                        HideCountdownBanner();
                        _countdownBanner = new DXTextObject
                        {
                            Z = 2147483600
                        };
                        _countdownBanner.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
                        _countdownBanner.TextRenderer.TextAlign = ContentAlignment.MiddleCenter;
                        _countdownBanner.TextRenderer.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
                        _countdownBanner.TextRenderer.ColorText = Color.Gold;
                        _countdownBanner.TextRenderer.ColorBackground = Color.FromArgb(210, 14, 22, 16);
                        _countdownBanner.TextRenderer.ColorAreaBorder = Color.Goldenrod;
                        _countdownBanner.TextRenderer.AreaBorderWidth = 2f;
                        _countdownBanner.TextRenderer.MakeTransparent = false;
                        grid.AddObject(_countdownBanner);
                    }

                    float sw = DXScene.ScreenArea.Width > 0 ? DXScene.ScreenArea.Width : 1920f;
                    _countdownBanner.State.Area = new RectangleF((sw - 340f) * 0.5f, 84f, 340f, 52f);
                    _countdownBanner.Visible = true;
                    _countdownBanner.Text = $"Resume in {n}...";
                    _countdownBanner.Create();
                    return;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[PauseSync] Failed to render countdown overlay: {ex.Message}");
            }

            GameBridge.ShowTopBanner($"Resume in {n}...", Color.Yellow, 0);
        }

        private static void HideCountdownBanner()
        {
            try
            {
                if (_countdownBanner != null)
                {
                    _countdownBanner.Visible = false;
                    _countdownBanner.RemoveFromParent();
                    _countdownBanner = null;
                }
            }
            catch
            {
                _countdownBanner = null;
            }
        }

        private static void Apply(bool paused)
        {
            bool prev = IsApplying;
            IsApplying = true;
            try
            {
                CommandInterceptor.Dispatch(new NetCommand
                {
                    Type = NetCommandType.SetPaused,
                    PlayerId = CommandInterceptor.LocalPlayerId,
                    TargetPausedState = paused
                });
            }
            finally
            {
                IsApplying = prev;
            }
        }
    }
}
