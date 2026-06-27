using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class ClickObject : MonoBehaviour
{
    public GameObject Agent;
    private bool followingAgent = false;
    Outline outlineScript = null;
    CameraOrbitFollow cameraOrbitFollow;

    // Start is called before the first frame update
    void Start()
    {
       cameraOrbitFollow = Camera.main.GetComponent<CameraOrbitFollow>();
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            GameObject objectClicked = GetClickedObject(out _);
            if (Agent.CompareTag(objectClicked.tag))
            {
                print("Clicked on agent: " + objectClicked.name);
                outlineScript = objectClicked.GetComponent<Outline>();
                outlineScript.enabled = true;
                Debug.Log("Object transform: " + objectClicked.transform);
                cameraOrbitFollow.setTarget(objectClicked.transform);
                followingAgent = true;
            }
        }
        if (Input.GetButtonDown("Cancel"))
        {
            if (followingAgent)
            {
                cameraOrbitFollow.target = null;
                outlineScript.enabled = false;
                followingAgent = false;
                Debug.Log("Stopped following agent");
            }
        }
    }

    GameObject GetClickedObject(out RaycastHit hit)
    {
        GameObject target = null;
        var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray.origin, ray.direction * 10, out hit))
        {
            if (!isPointerOverUIObject()) { target = hit.collider.gameObject; }
        }
        return target;
    }
    private bool isPointerOverUIObject()
    {
        PointerEventData ped = new PointerEventData(EventSystem.current);
        ped.position = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(ped, results);
        return results.Count > 0;
    }
}
