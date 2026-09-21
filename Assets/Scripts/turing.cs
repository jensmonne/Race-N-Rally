using UnityEngine;

public class RotateAndSway : MonoBehaviour
{
    [Header("Global Stop Control")]
    [Tooltip("Check this in the Inspector or click the screen button to halt all motion")]
    public bool stop = false;

    [Tooltip("Renders a big STOP/RESUME button at the top of the screen in Play Mode")]
    public bool showScreenButton = true;

    [Header("Rotation & Ramping")]
    [Tooltip("Reverse the rotation direction")]
    public bool isReversed = false;

    [Tooltip("Maximum rotation speed magnitude (clamped between 0 and 255)")]
    [Range(0f, 255f)]
    public float maxSpeed = 255f;

    [Tooltip("Acceleration rate (degrees per second squared)")]
    public float rampSpeed = 150f;

    [Tooltip("Main rotation axis")]
    public Vector3 rotationAxis = Vector3.up;

    [Header("Z-Axis Rotation Sway")]
    [Tooltip("Enable or disable Z-axis rotation oscillation")]
    public bool enableSway = false;

    [Tooltip("Sway frequency / speed")]
    public float swaySpeed = 3f;

    [Tooltip("Sway angle limit in degrees along the local Z-axis")]
    public float swayAmount = 15f;

    private float currentSpeed = 0f;
    private float accumulatedAngle = 0f;

    void Update()
    {
        // 1. Calculate target speed (forces 0 when stopped)
        float targetSpeed = stop ? 0f : (isReversed ? -maxSpeed : maxSpeed);
        targetSpeed = Mathf.Clamp(targetSpeed, -260f, 260f);

        // 2. Smoothly ramp current speed toward target speed
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rampSpeed * Time.deltaTime);

        // 3. Accumulate base rotation angle
        accumulatedAngle += currentSpeed * Time.deltaTime;
        Quaternion baseRotation = Quaternion.AngleAxis(accumulatedAngle, rotationAxis);

        // 4. Calculate Z-axis rotation sway angle
        float zSwayDegrees = (enableSway && !stop) ? Mathf.Sin(Time.time * swaySpeed) * swayAmount : 0f;
        Quaternion swayRotation = Quaternion.Euler(zSwayDegrees, 0f, 0f);

        // 5. Combine base spin and Z-axis sway
        transform.localRotation = baseRotation * swayRotation;
    }

    void OnGUI()
    {
        if (!showScreenButton) return;

        // Render a prominent GUI button at top-center of the screen
        float btnWidth = 180f;
        float btnHeight = 50f;
        Rect btnRect = new Rect((Screen.width - btnWidth) / 2f, 15f, btnWidth, btnHeight);

        GUIStyle btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };

        string label = stop ? "▶ RESUME" : "⏹ STOP";
        if (GUI.Button(btnRect, label, btnStyle))
        {
            stop = !stop;
        }
    }
}