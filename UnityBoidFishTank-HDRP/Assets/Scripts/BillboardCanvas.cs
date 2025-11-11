using UnityEngine;

public class BillboardCanvas : MonoBehaviour
{
    public Camera targetCamera;

    void Start() => targetCamera = Camera.main;

    void LateUpdate()
    {
        if (!targetCamera) targetCamera = Camera.main;
        transform.forward = targetCamera.transform.forward;
    }

}
