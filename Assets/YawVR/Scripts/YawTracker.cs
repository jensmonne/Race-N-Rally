using UnityEngine;

namespace YawVR
{
    /// <summary>
    /// Modifies and stores orientation data before sending to the simulator.
    /// </summary>
    public class YawTracker : MonoBehaviour
    {
        private YawController yawController;

        private void Awake()
        {
            yawController = GetComponentInParent<YawController>();
            if (yawController == null) yawController = YawController.Instance;
        }

        /// <summary>
        /// Sets the YawTracker's orientation, multiplier and limits are applied here
        /// </summary>
        public void SetRotation(Vector3 rot)
        {
            if (yawController == null) return;

            rot = SignedVector(rot);
            rot = ApplyMultipliers(rot);
            rot = ApplyLimits(rot);

            transform.eulerAngles = rot;
        }

        private Vector3 ApplyMultipliers(Vector3 rot)
        {
            return Vector3.Scale(rot, yawController.RotationMultiplier);
        }

        private Vector3 ApplyLimits(Vector3 rot)
        {
            Limits limits = yawController.Limits;

            if (limits.pitch != -1) rot.x = Mathf.Clamp(rot.x, -limits.pitch, limits.pitch);
            if (limits.yaw != -1)   rot.y = Mathf.Clamp(rot.y, -limits.yaw, limits.yaw);
            if (limits.roll != -1)  rot.z = Mathf.Clamp(rot.z, -limits.roll, limits.roll);

            return rot;
        }

        private Vector3 SignedVector(Vector3 v)
        {
            v.x = Mathf.DeltaAngle(0, v.x);
            v.y = Mathf.DeltaAngle(0, v.y);
            v.z = Mathf.DeltaAngle(0, v.z);
            
            return v;
        }
    }
}