using UnityEngine;

/// <summary>
/// Basic rally car controller using four WheelColliders.
/// Features: AWD/FWD/RWD toggle, speed-sensitive steering, braking,
/// anti-roll bars, downforce, wheel mesh syncing (with left-side flip),
/// and animated steering wheel + speedometer needle.
/// Input comes from an InputReader (MoveEvent: x = steering, y = throttle/brake).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RallyCarController : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputReader inputReader;

    [Header("Wheel Colliders")]
    [SerializeField] private WheelCollider frontLeft;
    [SerializeField] private WheelCollider frontRight;
    [SerializeField] private WheelCollider rearLeft;
    [SerializeField] private WheelCollider rearRight;

    [Header("Wheel Meshes")]
    [SerializeField] private Transform frontLeftMesh;
    [SerializeField] private Transform frontRightMesh;
    [SerializeField] private Transform rearLeftMesh;
    [SerializeField] private Transform rearRightMesh;

    [Header("Wheel Mesh Offsets")]
    [Tooltip("Extra rotation applied to the LEFT wheel meshes so they face outward. Tweak live in play mode.")]
    [SerializeField] private Vector3 leftWheelRotationOffset = new Vector3(0f, 180f, 0f);

    [Header("Drivetrain")]
    [SerializeField] private bool driveFront = true;    // both true = AWD
    [SerializeField] private bool driveRear = true;
    [SerializeField] private float motorTorque = 1800f; // total torque, split across driven wheels
    [SerializeField] private float maxSpeedKmh = 160f;
    [SerializeField] private float brakeTorque = 3000f;

    [Header("Steering (front wheels)")]
    [SerializeField] private float maxSteerAngle = 30f;       // real wheel angle at low speed
    [SerializeField] private float highSpeedSteerAngle = 8f;  // real wheel angle at max speed
    [SerializeField] private float steerSpeed = 6f;           // how fast wheels turn toward target angle

    [Header("Stability")]
    [SerializeField] private Vector3 centerOfMass = new Vector3(0f, -0.4f, 0.1f);
    [SerializeField] private float antiRollForce = 6000f;
    [SerializeField] private float downforce = 40f;           // extra grip at speed

    [Header("Visual Animations")]
    [SerializeField] private Transform steeringWheel;
    [Tooltip("Visual steering wheel turns this many times the real wheel angle (30 deg wheels x 3 = 90 deg).")]
    [SerializeField] private float steeringWheelRatio = 3f;
    [SerializeField] private Transform speedometerNeedle;
    [SerializeField] private float speedometerMaxKmh = 200f;
    [Tooltip("Needle Z rotation at 0 km/h and at the speedometer max. Adjust to match your model.")]
    [SerializeField] private float needleAngleAtZero = 135f;
    [SerializeField] private float needleAngleAtMax = -135f;

    private Rigidbody rb;
    private Vector2 moveInput;
    private float steerInput, throttleInput;
    private float currentSteerAngle;

    public float SpeedKmh => rb.linearVelocity.magnitude * 3.6f;

    private void OnEnable()
    {
        inputReader.EnablePlayerInput();
        inputReader.MoveEvent += OnMove;
    }

    private void OnDisable()
    {
        inputReader.MoveEvent -= OnMove;
        OnMove(Vector2.zero); // don't leave the car with throttle held
    }

    private void OnMove(Vector2 moveInput)
    {
        this.moveInput = moveInput;
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = centerOfMass;
    }

    private void Update()
    {
        steerInput = moveInput.x;
        throttleInput = moveInput.y;

        UpdateWheelMeshes();
        UpdateSteeringWheel();
        UpdateSpeedometer();
    }

    private void FixedUpdate()
    {
        ApplySteering();
        ApplyDrive();

        ApplyAntiRoll(frontLeft, frontRight);
        ApplyAntiRoll(rearLeft, rearRight);

        // Push the car into the ground harder as it goes faster
        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);
    }

    private void ApplySteering()
    {
        // Less steering angle at high speed so the car isn't twitchy
        float speedFactor = Mathf.Clamp01(SpeedKmh / maxSpeedKmh);
        float targetAngle = steerInput * Mathf.Lerp(maxSteerAngle, highSpeedSteerAngle, speedFactor);
        currentSteerAngle = Mathf.Lerp(currentSteerAngle, targetAngle, steerSpeed * Time.fixedDeltaTime);

        frontLeft.steerAngle = currentSteerAngle;
        frontRight.steerAngle = currentSteerAngle;
    }

    private void ApplyDrive()
    {
        // Are we pressing the opposite direction to the way we're rolling? Then brake.
        float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
        bool braking = throttleInput * forwardSpeed < -0.5f;

        int drivenWheels = (driveFront ? 2 : 0) + (driveRear ? 2 : 0);

        float torquePerWheel = 0f;
        if (!braking && SpeedKmh < maxSpeedKmh && drivenWheels > 0)
            torquePerWheel = throttleInput * motorTorque / drivenWheels;

        float brake = braking ? brakeTorque : 0f;

        float frontTorque = driveFront ? torquePerWheel : 0f;
        float rearTorque = driveRear ? torquePerWheel : 0f;

        frontLeft.motorTorque = frontTorque;
        frontRight.motorTorque = frontTorque;
        rearLeft.motorTorque = rearTorque;
        rearRight.motorTorque = rearTorque;

        frontLeft.brakeTorque = brake;
        frontRight.brakeTorque = brake;
        rearLeft.brakeTorque = brake;
        rearRight.brakeTorque = brake;
    }

    // Transfers load between left/right wheels on the same axle to reduce body roll
    private void ApplyAntiRoll(WheelCollider left, WheelCollider right)
    {
        float travelL = 1f, travelR = 1f;

        bool groundedL = left.GetGroundHit(out WheelHit hitL);
        if (groundedL)
            travelL = (-left.transform.InverseTransformPoint(hitL.point).y - left.radius)
                      / left.suspensionDistance;

        bool groundedR = right.GetGroundHit(out WheelHit hitR);
        if (groundedR)
            travelR = (-right.transform.InverseTransformPoint(hitR.point).y - right.radius)
                      / right.suspensionDistance;

        float force = (travelL - travelR) * antiRollForce;

        if (groundedL)
            rb.AddForceAtPosition(left.transform.up * -force, left.transform.position);
        if (groundedR)
            rb.AddForceAtPosition(right.transform.up * force, right.transform.position);
    }

    private void UpdateWheelMeshes()
    {
        Quaternion leftOffset = Quaternion.Euler(leftWheelRotationOffset);

        SyncWheel(frontLeft, frontLeftMesh, leftOffset);
        SyncWheel(frontRight, frontRightMesh, Quaternion.identity);
        SyncWheel(rearLeft, rearLeftMesh, leftOffset);
        SyncWheel(rearRight, rearRightMesh, Quaternion.identity);
    }

    private static void SyncWheel(WheelCollider wheelCollider, Transform mesh, Quaternion offset)
    {
        if (mesh == null) return;
        wheelCollider.GetWorldPose(out Vector3 pos, out Quaternion rot);
        mesh.SetPositionAndRotation(pos, rot * offset);
    }

    private void UpdateSteeringWheel()
    {
        if (steeringWheel == null) return;
        steeringWheel.localRotation = Quaternion.Euler(0f, 0f, -currentSteerAngle * steeringWheelRatio);
    }

    private void UpdateSpeedometer()
    {
        if (speedometerNeedle == null) return;
        float t = Mathf.Clamp01(SpeedKmh / speedometerMaxKmh);
        float angle = Mathf.Lerp(needleAngleAtZero, needleAngleAtMax, t);
        speedometerNeedle.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}