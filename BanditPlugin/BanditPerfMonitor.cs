using System;
using UnityEngine;

namespace BanditPlugin
{
    /// <summary>
    /// Records how long each server frame took, so the cost of a change can be read off a
    /// distribution instead of guessed at from how the game feels.
    ///
    /// This times the whole frame on purpose, rather than this plugin's own methods. Most of what a
    /// bandit costs the server is not in this assembly at all: a fake player is a real Player to
    /// vanilla, and gets simulated, replicated and physics-stepped like one. A stopwatch around
    /// BanditBotController.Update would measure a fraction of the true bill and report it as the
    /// total.
    ///
    /// Read the tail, not the mean. A server that sleeps to hit a target frame rate spends its
    /// spare time idle, which pins mean frame time near that target however much work it is really
    /// doing - only frames that overrun the sleep can move at all. So if the mean sits on a
    /// suspiciously round number, this cannot see the remaining headroom and process CPU% is the
    /// honest measure instead; tools/banditperf-sample.sh reads it.
    ///
    /// It is also blind to the client by construction. Frames rendered on a player's machine are
    /// not frames stepped on the server, so a frame rate drop while standing in front of fifty
    /// bandits may not show up here at all. That same script samples the client process and the GPU
    /// for the other half of the answer.
    /// </summary>
    [DisallowMultipleComponent]
    public class BanditPerfMonitor : MonoBehaviour
    {
        /// <summary>
        /// Roughly two minutes at 60 frames a second - comfortably more than the 60s window a
        /// measurement run actually needs, while keeping the sort a report does trivially cheap.
        /// </summary>
        private const int Capacity = 8192;

        private const float HitchMs = 33.3f;
        private const float BadHitchMs = 50f;

        private readonly float[] _frameMs = new float[Capacity];
        private readonly float[] _sortScratch = new float[Capacity];

        private int _count;
        private int _next;

        private int _gen0AtReset;
        private int _gen1AtReset;
        private int _gen2AtReset;

        private void Awake()
        {
            Reset();
        }

        /// <summary>
        /// Throws away everything recorded so far and starts a fresh window. This is what makes an
        /// A/B honest: reset, let the scenario run for a fixed time, then read - otherwise the
        /// numbers still carry the spawn storm, or the last scenario, or the map load.
        /// </summary>
        public void Reset()
        {
            _count = 0;
            _next = 0;
            _gen0AtReset = GC.CollectionCount(0);
            _gen1AtReset = GC.CollectionCount(1);
            _gen2AtReset = GC.CollectionCount(2);
        }

        private void Update()
        {
            // Unscaled, because a measurement has to be in wall-clock seconds to mean anything -
            // scaled time would silently rescale every figure if anything ever touched timeScale.
            _frameMs[_next] = Time.unscaledDeltaTime * 1000f;
            _next = (_next + 1) % Capacity;

            if (_count < Capacity)
            {
                _count++;
            }
        }

        /// <summary>
        /// The current window as a set of finished figures, or null if no frame has been recorded
        /// since the last reset.
        /// </summary>
        public BanditPerfReport Snapshot()
        {
            if (_count == 0)
            {
                return null;
            }

            // Percentiles do not care what order the samples arrived in, and a full ring occupies
            // the whole array regardless of where _next currently points - so the first _count
            // entries are the window either way.
            Array.Copy(_frameMs, _sortScratch, _count);
            Array.Sort(_sortScratch, 0, _count);

            double total = 0d;
            int over33 = 0;
            int over50 = 0;

            for (int i = 0; i < _count; i++)
            {
                float ms = _sortScratch[i];
                total += ms;

                if (ms >= BadHitchMs)
                {
                    over50++;
                }

                if (ms >= HitchMs)
                {
                    over33++;
                }
            }

            // Summing the frames themselves rather than reading a clock, so a window that wrapped
            // the ring reports the span it actually still holds instead of the span since reset.
            float windowSeconds = (float)(total / 1000d);

            return new BanditPerfReport
            {
                Frames = _count,
                Truncated = _count >= Capacity,
                WindowSeconds = windowSeconds,
                MeanMs = (float)(total / _count),
                MedianMs = Percentile(_sortScratch, _count, 0.50f),
                P95Ms = Percentile(_sortScratch, _count, 0.95f),
                P99Ms = Percentile(_sortScratch, _count, 0.99f),
                MaxMs = _sortScratch[_count - 1],
                FramesOver33Ms = over33,
                FramesOver50Ms = over50,
                Gen0Collections = GC.CollectionCount(0) - _gen0AtReset,
                Gen1Collections = GC.CollectionCount(1) - _gen1AtReset,
                Gen2Collections = GC.CollectionCount(2) - _gen2AtReset,
            };
        }

        /// <summary>
        /// Nearest-rank percentile: the smallest sample at or above the requested fraction of the
        /// window. No interpolation, so every figure printed is a frame time that genuinely
        /// happened rather than an average of two that did.
        /// </summary>
        private static float Percentile(float[] sorted, int count, float fraction)
        {
            int index = Mathf.Clamp(Mathf.CeilToInt(fraction * count) - 1, 0, count - 1);
            return sorted[index];
        }
    }

    /// <summary>
    /// One window's worth of frame timing, already reduced to the figures worth printing.
    /// </summary>
    public sealed class BanditPerfReport
    {
        public int Frames;

        /// <summary>
        /// Whether the ring buffer wrapped, meaning the window shown is the most recent slice
        /// rather than everything since the reset. Worth saying out loud - a truncated window
        /// silently drops exactly the early frames a spawn storm lives in.
        /// </summary>
        public bool Truncated;

        public float WindowSeconds;
        public float MeanMs;
        public float MedianMs;
        public float P95Ms;
        public float P99Ms;
        public float MaxMs;
        public int FramesOver33Ms;
        public int FramesOver50Ms;
        public int Gen0Collections;
        public int Gen1Collections;
        public int Gen2Collections;

        public float MeanFps => MeanMs > 0f ? 1000f / MeanMs : 0f;
        public float HitchesPerMinute => WindowSeconds > 0f ? FramesOver33Ms * 60f / WindowSeconds : 0f;
        public float BadHitchesPerMinute => WindowSeconds > 0f ? FramesOver50Ms * 60f / WindowSeconds : 0f;
        public float Gen0PerMinute => WindowSeconds > 0f ? Gen0Collections * 60f / WindowSeconds : 0f;
    }
}
