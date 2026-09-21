using UnityEngine;

/// <summary>
/// Basic rally car controller using four WheelColliders.
/// Features: AWD/FWD/RWD toggle, speed-sensitive steering, braking,
/// anti-roll bars, downforce, and wheel mesh syncing.
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

    [Header("Drivetrain")]
    [SerializeField] private bool driveFront = true;   // both true = AWD
    [SerializeField] private bool driveRear = true;
    [SerializeField] private float motorTorque = 1800f; // total torque, split across driven wheels
    [SerializeField] private float maxSpeedKmh = 160f;
    [SerializeField] private float brakeTorque = 3000f;

    [Header("Steering (front wheels)")]
    [SerializeField] private float maxSteerAngle = 30f;       // at low speed
    [SerializeField] private float highSpeedSteerAngle = 8f;  // at max speed
    [SerializeField] private float steerSpeed = 6f;           // how fast wheels turn toward target angle

    [Header("Stability")]
    [SerializeField] private Vector3 centerOfMass = new Vector3(0f, -0.4f, 0.1f);
    [SerializeField] private float antiRollForce = 6000f;
    [SerializeField] private float downforce = 40f;           // extra grip at speed

    private Rigidbody rb;
    private Vector2 moveInput;
    private float steerInput, throttleInput;
    private float currentSteerAngle;

    public float SpeedKmh => rb.linearVelocity.magnitude * 3.6f;

    private void OnEnable()
    {
        inputReader.EnablePlayerInput();
        inputReader.MoveEvent += OnMove;
        Debug.Log("Car subscribed to MoveEvent");
    }

    private void OnDisable()
    {
        inputReader.MoveEvent -= OnMove;
        OnMove(Vector2.zero); 
    }

    private void OnMove(Vector2 moveInput)
    {
        this.moveInput = moveInput;
        Debug.Log($"Car OnMove: {moveInput}");
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = centerOfMass;
    }

    private void Update()
    {       
        UpdateWheelMeshes();
        steerInput = moveInput.x;
        throttleInput = moveInput.y;
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
        SyncWheel(frontLeft, frontLeftMesh);
        SyncWheel(frontRight, frontRightMesh);
        SyncWheel(rearLeft, rearLeftMesh);
        SyncWheel(rearRight, rearRightMesh);
    }

    private static void SyncWheel(WheelCollider wheelCollider, Transform mesh)
    {
        if (mesh == null) return;
        wheelCollider.GetWorldPose(out Vector3 pos, out Quaternion rot);
        mesh.SetPositionAndRotation(pos, rot);
    }
}