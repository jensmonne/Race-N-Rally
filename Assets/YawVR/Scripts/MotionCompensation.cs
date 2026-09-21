using UnityEngine;

namespace YawVR
{
    /// <summary>
    /// Cancels the camera's rotation based on IMU data
    /// </summary>
    public class MotionCompensation : MonoBehaviour
    {
        [SerializeField] private Transform cameraOffsetTransform;
        [SerializeField] private YawController yawController;

        [Tooltip("Higher values increase smoothing responsive time.")]
        [SerializeField][Range(1f, 30f)] private float smoothingSpeed = 10f;

        private Vector3 simData;
        private Vector3 offset;

        private void Awake()
        {
            if (yawController == null) yawController = YawController.Instance;
        }

        public void UpdateOffset()
        {
            offset.y = -yawController.Device.ActualPosition.yaw;
        }

        private void LateUpdate()
        {
            if (YawController.Instance.State != ControllerState.Started || YawController.Instance.State != ControllerState.Connected) return;

            simData.y = -yawController.Device.ActualPosition.yaw;
            Quaternion targetRotation = Quaternion.Euler(simData - offset);
            cameraOffsetTransform.rotation = Quaternion.Slerp(cameraOffsetTransform.rotation, targetRotation, Time.deltaTime * smoothingSpeed);
        }
    }
}