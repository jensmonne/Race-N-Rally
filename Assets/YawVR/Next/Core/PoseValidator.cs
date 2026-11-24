// Defence in depth for poses (the server validates too, but the client must never be the
// reason a bad value reaches the rig). NaN/infinity -> rejected; yaw wrapped to [-180, 180];
// pitch/roll clamped to the limits the server announced (HelloAck / Telemetry).
using System;

namespace YawVR.Next
{
    public enum PoseCheck { Ok, Clamped, Rejected }

    public static class PoseValidator
    {
        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Wraps any finite angle into [-180, 180] without loops (no hang on huge values).</summary>
        public static float Wrap180(float a)
        {
            float r = (float)Math.IEEERemainder(a, 360.0); // in [-180, 180]
            return r;
        }

        /// <summary>
        /// Validates and adjusts a pose in place. Returns Rejected (and leaves the values alone) if
        /// anything is NaN or infinite: such a pose must never be sent.
        /// </summary>
        public static PoseCheck Prepare(ref float yaw, ref float pitch, ref float roll, in YawRigState limits)
        {
            if (!IsFinite(yaw) || !IsFinite(pitch) || !IsFinite(roll)) return PoseCheck.Rejected;
            var result = PoseCheck.Ok;
            yaw = Wrap180(yaw);
            pitch = Wrap180(pitch);
            roll = Wrap180(roll);
            if (limits.HasLimits)
            {
                float p = Clamp(pitch, limits.PitchBack, limits.PitchForward);
                float r = Clamp(roll, -limits.RollLimit, limits.RollLimit);
                if (p != pitch || r != roll) result = PoseCheck.Clamped;
                pitch = p;
                roll = r;
            }
            return result;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
