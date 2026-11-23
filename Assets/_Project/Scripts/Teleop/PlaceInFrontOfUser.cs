using System.Collections;
using UnityEngine;

namespace GemStore.Teleop
{
    /// <summary>
    /// Puts a world-space panel in front of the user once, when the scene starts (so a menu is never behind the user or out of view).
    /// The panel faces the user, stays level, and then stays where it is. Waits briefly for head tracking to deliver a pose.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlaceInFrontOfUser : MonoBehaviour
    {
        [SerializeField, Tooltip("Distance in front of the head, metres.")] private float distance = 0.8f;
        [SerializeField, Tooltip("Sideways offset to the user's right, metres (negative = left).")] private float sideOffset;
        [SerializeField, Tooltip("Height relative to the head, metres.")] private float heightOffset = -0.05f;
        [SerializeField, Tooltip("Extra yaw (degrees) so a side panel turns toward the user.")] private float yawOffset;

        private IEnumerator Start()
        {
            // head tracking may need a moment before the camera has its real height
            float waited = 0f;
            Camera cam = Camera.main;
            while ((cam == null || cam.transform.position.y < 0.3f) && waited < 1.5f)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
                cam = Camera.main;
            }

            if (cam == null)
            {
                yield break;
            }

            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 position = cam.transform.position + forward * distance + right * sideOffset;
            position.y = cam.transform.position.y + heightOffset;

            var rect = transform as RectTransform;
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(0f, yawOffset, 0f);
            transform.rotation = rotation;
            if (rect != null)
            {
                rect.position = position;
            }
            else
            {
                transform.position = position;
            }
        }
    }
}
