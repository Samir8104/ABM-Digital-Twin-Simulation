using UnityEngine;

public class CameraOrbitFollow : MonoBehaviour
{
    [Header("Tracking Targets")]
    public Transform target;

    [Header("Orbit Settings")]
    public float distance = 10.0f;
    public float rotationSpeed = 5.0f;
    public float heightOffset = 3.0f;

    private float currentAngle = 0.0f;

    private void LateUpdate()
    {
        if (target == null) return; // Allows the camera to be used when we aint following no agents

        if (Input.GetMouseButton(1)) // Uses right click to circle around agent :D
        {
            currentAngle += Input.GetAxis("Mouse X") * rotationSpeed;
        }
        else
        {
            currentAngle += Input.GetAxis("Horizontal") * rotationSpeed * Time.deltaTime * 10f; // you can also use right arrow/left arrow or A & D
        }

        float radians = currentAngle * Mathf.Deg2Rad;

        float xOffset = Mathf.Sin(radians) * distance;
        float zOffset = -Mathf.Cos(radians) * distance;

        Vector3 newPosition = new Vector3(target.position.x + xOffset, target.position.y + heightOffset, target.position.z + zOffset);
        transform.position = newPosition;

        transform.LookAt(target.position + Vector3.up * heightOffset);
    }


    public void setTarget(Transform newTarget)
    {
        Debug.Log("Setting new target");
        target = newTarget;
    }
}
