// Turns the tracker orientation (as the old SDK computed it) into Pose messages.
// The only place the Unity layer sends poses from. Pure C#: unit-tested.
namespace YawVR.Next
{
    public sealed class YawMotionSender
    {
        readonly YawSession _session;

        public YawMotionSender(YawSession session) { _session = session; }

        /// <summary>Counts for diagnostics (no allocation).</summary>
        public int Sent, NotStreaming, Rejected, Clamped;

        /// <summary>
        /// Same conversion as the old YawController.SendMotionData: angles >= 180 become negative.
        /// Vibration: the old V[right,center,left,hz]; the original server drove pitch with "right" and roll with "center".
        /// As in the old Commands.MOTION_DATA, vibration is only sent when the buzzer is on AND hz > 0.
        /// </summary>
        public PoseResult Send(float yaw, float pitch, float roll, bool buzzerOn = false, int rightAmp = 0, int centerAmp = 0, int hz = 0)
        {
            bool on = buzzerOn && hz > 0;
            byte a = on ? Byte(rightAmp) : (byte)0;
            byte b = on ? Byte(centerAmp) : (byte)0;
            byte f = on ? Byte(hz) : (byte)0;
            var r = _session.SendPose(Signed(yaw), Signed(pitch), Signed(roll), a, b, f);
            switch (r)
            {
                case PoseResult.Sent: Sent++; break;
                case PoseResult.SentClamped: Sent++; Clamped++; break;
                case PoseResult.NotStreaming: NotStreaming++; break;
                case PoseResult.RejectedNonFinite: Rejected++; break;
            }
            return r;
        }

        static float Signed(float angle) => angle >= 180f ? angle - 360f : angle;
        static byte Byte(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
    }
}
