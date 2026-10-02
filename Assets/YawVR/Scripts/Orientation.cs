using UnityEngine;

namespace YawVR
{
    /**
     * Orientation is a component that provides access to the pitch, yaw, and roll of a GameObject.
     * It calculates these values based on the GameObject's transform.
     */
    public class Orientation : MonoBehaviour
    {
        public (float pitch, float yaw, float roll) GetAll()
        {
            Vector3 fwd = transform.forward;
            Vector3 rgt = transform.right;
            Vector3 up = transform.up;

            float p = -Mathf.Asin(UnaryTrim(fwd.y)) * Mathf.Rad2Deg;
            float y = ComputeYaw(fwd, rgt);
            float r = Mathf.Atan2(rgt.y, up.y) * Mathf.Rad2Deg;

            return (p, y, r);
        }

        private void SetPitchYawRoll(Vector3 value)
        {
            transform.rotation = Quaternion.identity;
            Vector3 o = transform.position;
            transform.RotateAround(o, Vector3.forward, value.z);
            transform.RotateAround(o, Vector3.right, value.x);
            transform.RotateAround(o, Vector3.up, value.y);
        }

        /*
        * Pitch indicates whether a vehicle is pointing up or down.
        * It is the angle between the forward vector and the horizontal
        * plane.
        */
        public float Pitch
        {
            get
            {
                float sine = UnaryTrim(transform.forward.y);
                return -Mathf.Asin(sine) * Mathf.Rad2Deg;
            }
            set { SetPitchYawRoll(new Vector3(value, Yaw, Roll)); }
        }

        /*
        * Yaw is the angle between the forward vector's ground image
        * and the forward/north direction.
        * Yaw is the general 'direction' or 'course'.
        * if a vehicle is pointing all the way up or down,
        * we extract yaw from the right vector.
        */
        public float Yaw
        {
            get => ComputeYaw(transform.forward, transform.right);
            set { SetPitchYawRoll(new Vector3(Pitch, value, Roll)); }
        }

        /*
        * Roll is the angle between the right vector and its ground image.
        */
        public float Roll
        {
            get
            {
                return Mathf.Atan2(transform.right.y, transform.up.y) * Mathf.Rad2Deg;
            }
            set { SetPitchYawRoll(new Vector3(Pitch, Yaw, value)); }
        }

        private float ComputeYaw(Vector3 fwd, Vector3 rgt)
        {
            Vector3 vector = Ground(fwd);
            if (vector.magnitude < 0.5f)
            {
                return EvalAltYaw(rgt);
            }
            float alpha = Vector3.Angle(vector, Vector3.forward);
            return vector.x > 0 ? alpha : -alpha;
        }

        private float EvalAltYaw(Vector3 rgt)
        {
            Vector3 vector = Ground(rgt);
            float alpha = Vector3.Angle(vector, Vector3.right);
            return vector.z < 0 ? alpha : -alpha;
        }

        private Vector3 Ground(Vector3 u)
        {
            u.y = 0.0f;
            return u;
        }

        private float UnaryTrim(float w) => Mathf.Clamp(w, -1f, 1f);
    }
}