using UnityEngine;

public class SpectatorCamera : MonoBehaviour
{
    [SerializeField] private Transform target;      // the car
    [SerializeField] private Vector3 offset = new Vector3(0f, 3f, -7f);
    [SerializeField] private float followSpeed = 5f;

    private void LateUpdate()
    {
        Vector3 desired = target.position + target.rotation * offset;
        transform.position = Vector3.Lerp(transform.position, desired, followSpeed * Time.deltaTime);
        transform.LookAt(target.position + Vector3.up);
    }
}