using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using DXVision;
using TABCoopMod.Core;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Saves PNG screenshots of the live render target via DXVision's DXGame.CaptureScreen().
    /// Mutating control tools request a DEFERRED capture (a couple of frames later) so the frame
    /// reflects the simulated operation. All capture runs on the game main thread (StartFrame).
    /// </summary>
    public static class ScreenshotService
    {
        private sealed class PendingShot
        {
            public int DueFrame;
            public string Path;
            public string Tag;
        }

        private static readonly Queue<PendingShot> _pending = new Queue<PendingShot>();
        private static readonly object _lock = new object();
        private static int _frame;
        private static int _seq;
        private static string _dir;

        public static bool Enabled => TestHarnessConfig.AutoShot;

        public static string Directory_
        {
            get
            {
                if (_dir == null)
                {
                    // Prefer the workspace folder supplied by the launcher (TABCOOP_SHOT_DIR), so
                    // screenshots land in the repo for review. Fall back to <game dir>\tabcoop_shots.
                    string configured = TestHarnessConfig.ShotDir;
                    if (!string.IsNullOrEmpty(configured))
                    {
                        _dir = configured;
                    }
                    else
                    {
                        string baseDir = TestHarnessConfig.OutputDirectory;
                        if (string.IsNullOrEmpty(baseDir)) baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        _dir = Path.Combine(baseDir, "tabcoop_shots");
                    }
                }
                return _dir;
            }
        }

        /// <summary>Called once per render frame from ControlServer.Tick.</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            _frame++;

            while (true)
            {
                PendingShot due = null;
                lock (_lock)
                {
                    if (_pending.Count > 0 && _pending.Peek().DueFrame <= _frame)
                        due = _pending.Dequeue();
                }
                if (due == null) break;
                Capture(due.Path, due.Tag);
            }
        }

        /// <summary>Schedules a screenshot a few frames after the current one; returns its path immediately.</summary>
        public static string RequestAfterFrames(string tag, int frames = 2)
        {
            if (!Enabled) return null;

            int seq;
            lock (_lock) { seq = ++_seq; }
            string path = BuildPath(seq, tag);

            lock (_lock)
            {
                _pending.Enqueue(new PendingShot
                {
                    DueFrame = _frame + Math.Max(1, frames),
                    Path = path,
                    Tag = tag
                });
            }
            return path;
        }

        /// <summary>Captures the current render target immediately and returns the saved path.</summary>
        public static string CaptureNow(string tag)
        {
            if (!Enabled) return null;

            int seq;
            lock (_lock) { seq = ++_seq; }
            string path = BuildPath(seq, tag);
            Capture(path, tag);
            return path;
        }

        private static void Capture(string path, string tag)
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                using (Bitmap bmp = game.CaptureScreen())
                {
                    if (bmp == null) return;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    bmp.Save(path, ImageFormat.Png);
                }
                ModLogger.Info($"[Screenshot] saved '{tag}' -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Screenshot] capture failed for '{tag}': {ex.Message}");
            }
        }

        private static string BuildPath(int seq, string tag)
        {
            int tick = DXGame.Current?.IAIterationInt ?? 0;
            string name = $"{seq:D5}_t{tick}_{DateTime.Now:HHmmss_fff}_{Sanitize(tag)}.png";
            return Path.Combine(Directory_, name);
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "shot";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
