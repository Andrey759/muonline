using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Client.Data.BMD;
using SysVector3 = System.Numerics.Vector3;
using SysMatrix = System.Numerics.Matrix4x4;

namespace Client.Main.Content
{
    /// <summary>
    /// Measures how fast a planted foot slides backwards in a model's walk cycle (model units per animation key).
    /// MU walk animations are played in place, so the feet stay on the ground only when the walker moves exactly as fast
    /// as its planted foot moves back: worldSpeed = speedPerKey * scale * keysPerSecond. The hand-written PlaySpeed table
    /// does not satisfy this for most monsters (feet visibly skate), so the walk playback is scaled from this measurement.
    /// </summary>
    public static class WalkCycleCalibrator
    {
        private const float MinForwardSpan = 15f;      // a foot must swing at least this far to count as a foot
        private const float MinStep = 0.5f;            // per-key movement below this is noise
        private const float MaxFootDisagreement = 0.3f; // both feet must agree within 30 %

        private static readonly ConditionalWeakTable<BMD, Dictionary<int, float>> Cache = new();

        /// <summary>Returns the planted-foot speed in model units per key, or 0 when the cycle has no clear feet.</summary>
        public static float GetStanceSpeedPerKey(BMD model, int actionIndex)
        {
            if (model?.Bones == null || model.Actions == null || actionIndex < 0 || actionIndex >= model.Actions.Length)
                return 0f;

            var perAction = Cache.GetOrCreateValue(model);
            lock (perAction)
            {
                if (perAction.TryGetValue(actionIndex, out float cached))
                    return cached;
                float value;
                try { value = Measure(model, actionIndex); }
                catch { value = 0f; }
                perAction[actionIndex] = value;
                return value;
            }
        }

        private static float Measure(BMD model, int act)
        {
            var action = model.Actions[act];
            if (action == null) return 0f;
            int keys = action.NumAnimationKeys;
            int boneCount = model.Bones.Length;
            if (keys < 3 || boneCount == 0) return 0f;
            // The played cycle: with LockPositions the last key duplicates the first and is skipped.
            int cycle = action.LockPositions ? keys - 1 : keys;

            var pos = new SysVector3[keys, boneCount];
            var world = new SysMatrix[boneCount];
            for (int f = 0; f < keys; f++)
            {
                for (int i = 0; i < boneCount; i++)
                {
                    var bone = model.Bones[i];
                    if (bone == BMDTextureBone.Dummy || bone.Matrixes == null || act >= bone.Matrixes.Length)
                    {
                        world[i] = SysMatrix.Identity;
                        continue;
                    }
                    var bm = bone.Matrixes[act];
                    if (bm.Position == null || bm.Quaternion == null || f >= bm.Position.Length || f >= bm.Quaternion.Length)
                    {
                        world[i] = SysMatrix.Identity;
                        continue;
                    }
                    var m = SysMatrix.CreateFromQuaternion(bm.Quaternion[f]);
                    var p = bm.Position[f];
                    if (i == 0 && action.LockPositions)
                        p = new SysVector3(bm.Position[0].X, bm.Position[0].Y, p.Z);
                    m.Translation = p;
                    world[i] = bone.Parent >= 0 && bone.Parent < i ? m * world[bone.Parent] : m;
                    pos[f, i] = world[i].Translation;
                }
            }

            // Feet: the two lowest bones that swing noticeably along the walking (Y) axis.
            var feet = Enumerable.Range(0, boneCount)
                .Where(i => model.Bones[i] != BMDTextureBone.Dummy)
                .Select(i =>
                {
                    float minZ = float.MaxValue, minY = float.MaxValue, maxY = float.MinValue;
                    for (int f = 0; f < cycle; f++)
                    {
                        var p = pos[f, i];
                        minZ = Math.Min(minZ, p.Z);
                        minY = Math.Min(minY, p.Y);
                        maxY = Math.Max(maxY, p.Y);
                    }
                    return (Index: i, MinZ: minZ, Span: maxY - minY);
                })
                .Where(x => x.Span >= MinForwardSpan)
                .OrderBy(x => x.MinZ)
                .Take(2)
                .ToList();
            if (feet.Count < 2) return 0f;

            var speeds = new List<float>(2);
            foreach (var foot in feet)
            {
                // A planted foot moves backwards while low; a swinging foot moves forwards while lifted.
                float travelPos = 0, travelNeg = 0, zPos = 0, zNeg = 0;
                int keysPos = 0, keysNeg = 0;
                for (int f = 0; f < cycle; f++)
                {
                    int g = (f + 1) % keys;
                    float d = pos[g, foot.Index].Y - pos[f, foot.Index].Y;
                    float z = (pos[g, foot.Index].Z + pos[f, foot.Index].Z) * 0.5f;
                    if (d > MinStep) { travelPos += d; zPos += z; keysPos++; }
                    else if (d < -MinStep) { travelNeg -= d; zNeg += z; keysNeg++; }
                }
                if (keysPos == 0 || keysNeg == 0) return 0f;
                bool backIsPositive = zPos / keysPos < zNeg / keysNeg;
                speeds.Add(backIsPositive ? travelPos / keysPos : travelNeg / keysNeg);
            }

            float max = Math.Max(speeds[0], speeds[1]);
            if (max <= 0f || Math.Abs(speeds[0] - speeds[1]) / max > MaxFootDisagreement)
                return 0f;
            return (speeds[0] + speeds[1]) * 0.5f;
        }
    }
}
