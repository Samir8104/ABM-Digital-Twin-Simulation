using UnityEngine;

public enum RoomPriorityLevel { Low, Mid, High }

public class RoomPriority : MonoBehaviour
{
    public RoomPriorityLevel priority = RoomPriorityLevel.Mid;
}