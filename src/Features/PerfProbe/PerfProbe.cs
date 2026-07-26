using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ChaseView.Core;

namespace ChaseView.Features
{
    /// <summary>
    /// TEMPORARY DIAGNOSTIC. Frame time, bucketed by camera mode, so "chase view drops frames" becomes
    /// a number instead of an impression.
    ///
    /// WHY BUCKET BY CAMERA MODE
    ///   The question is not "what is the framerate", it is "how much does chase cost against cockpit,
    ///   on the same scene, in the same sortie". Bucketing gives that comparison for free: fly, switch
    ///   views, read the delta. Nothing else has to be controlled for.
    ///
    /// AND WHY THAT IS ONLY HALF AN ANSWER
    ///   Chase view costs more in VANILLA too. It renders your own airframe, a wider scene, and
    ///   CameraChaseState.EnterState sets shadowDistance to max(2000, 2000 * orbitDist * 2/30) -
    ///   roughly double the cockpit's 2000m on a typical airframe. [decompiled] So a chase-vs-cockpit
    ///   delta is an upper bound on OUR cost, not our cost. To separate them, set
    ///   PerfProbe.Bypass = true (below), which idles every per-frame thing this mod does while leaving
    ///   the camera state itself alone, and compare chase-with-bypass against chase-without.
    ///
    /// Default OFF: instrumentation that is on by default is instrumentation you ship by accident.
    /// Delete this folder once the question is settled.
    /// </summary>
    internal sealed class PerfProbe : Feature
    {
        public override string Name => "PerfProbe";

        public override string Description =>
            "DIAGNOSTIC, temporary. Logs frame-time statistics split by camera mode so the cost of "
          + "chase view can be measured rather than guessed. Off by default; costs nothing when off.";

        protected override bool DefaultEnabled => false;


        private ConfigEntry<float> _reportSeconds;
        private ConfigEntry<bool> _bypass;

        protected override void BindOptions(ConfigFile config)
        {
            _reportSeconds = config.Bind(Name, "ReportSeconds", 10f, Cfg.Adv("How often to log a frame-time summary.", new AcceptableValueRange<float>(2f, 60f)));

            _bypass = config.Bind(Name, "Bypass", false, Cfg.Adv("Idle everything this mod does per frame, without disabling it. For A/B measurement."));

            Diag.Bypass = _bypass.Value;
            _bypass.SettingChanged += (a, b) => Diag.Bypass = _bypass.Value;
        }

        public override void DumpResolved(Action<string, object> kv)
        {
            kv("ReportSeconds", _reportSeconds.Value);
            kv("Bypass", _bypass.Value);
        }

        public override void Apply(Harmony harmony)
        {
            Plugin.HostObject.AddComponent<Sampler>().Init(_reportSeconds);
            if (Diag.Bypass) Plugin.Log.LogWarning("[PerfProbe] Bypass is ON - ChaseView per-frame work is idle");
        }

        private sealed class Bucket
        {
            internal int Frames;
            internal double TotalMs;
            internal float WorstMs;
            internal int Over33;      // frames slower than 30fps
            internal int Over16;      // frames slower than 60fps

            internal void Add(float ms)
            {
                Frames++; TotalMs += ms;
                if (ms > WorstMs) WorstMs = ms;
                if (ms > 33.3f) Over33++;
                else if (ms > 16.6f) Over16++;
            }

            internal void Clear() { Frames = 0; TotalMs = 0; WorstMs = 0; Over33 = 0; Over16 = 0; }

            public override string ToString()
            {
                if (Frames == 0) return "no frames";
                double mean = TotalMs / Frames;
                return $"{Frames,5} frames  mean {mean,6:0.00}ms ({1000.0 / mean,5:0} fps)  "
                     + $"worst {WorstMs,6:0.0}ms  >33ms {Over33,4}  >16ms {Over16,4}";
            }
        }

        private sealed class Sampler : MonoBehaviour
        {
            private readonly Bucket[] _byMode = new Bucket[8];
            private ConfigEntry<float> _report;
            private float _next;
            private int _warmup = 60;      // discard the first second; load spikes are not the subject

            internal void Init(ConfigEntry<float> report)
            {
                _report = report;
                for (int i = 0; i < _byMode.Length; i++) _byMode[i] = new Bucket();
                _next = Time.realtimeSinceStartup + report.Value;
            }

            private void Update()
            {
                if (_warmup > 0) { _warmup--; return; }

                // unscaledDeltaTime, not deltaTime: a paused or time-scaled game would otherwise
                // report frame times that have nothing to do with rendering cost.
                int mode = (int)CameraStateManager.cameraMode;
                if (mode >= 0 && mode < _byMode.Length)
                    _byMode[mode].Add(Time.unscaledDeltaTime * 1000f);

                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + Mathf.Max(2f, _report.Value);
                Report();
            }

            private void Report()
            {
                var sb = new StringBuilder();
                // #probe-at-cap
                //   A frame limiter makes MEAN frame time measure the limiter, not the work. The first
                //   run of this probe reported 11.2-11.4ms for every camera mode on a 90fps cap - four
                //   identical numbers that looked like "chase costs nothing" and actually meant "every
                //   mode has headroom to spare". Naming the cap in the header, and saying outright when
                //   the mean is sitting on it, is what stops that being read as a result.
                int cap = Application.targetFrameRate;
                float capMs = cap > 0 ? 1000f / cap : 0f;
                sb.AppendLine($"===== ChaseView PerfProbe  (Bypass={Diag.Bypass}  "
                            + $"cap={(cap > 0 ? cap + "fps / " + capMs.ToString("0.00") + "ms" : "uncapped")}"
                            + $"  vSync={QualitySettings.vSyncCount}) =====");
                bool any = false;
                for (int i = 0; i < _byMode.Length; i++)
                {
                    if (_byMode[i].Frames == 0) continue;
                    any = true;
                    double mean = _byMode[i].TotalMs / _byMode[i].Frames;
                    bool atCap = capMs > 0f && mean < capMs * 1.05f;
                    sb.AppendLine($"  {(CameraMode)i,-14} {_byMode[i]}{(atCap ? "   <-- AT CAP: mean measures the limiter, not cost. Compare the tail, or raise the cap." : "")}");
                    _byMode[i].Clear();
                }
                // Log the zero case rather than going silent - "no output" and "no frames sampled"
                // must not look the same.
                if (!any) sb.AppendLine("  (no frames sampled this window)");
                Plugin.Log.LogInfo(sb.ToString());
            }
        }
    }
}
