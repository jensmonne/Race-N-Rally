using UnityEngine;
using TMPro;

public class Timer : MonoBehaviour
{
    public static float currentTime = 0f;
    [SerializeField] private TextMeshProUGUI timerText;

    private void Start()    
    {
        currentTime = 0f;
    }

    private void Update()
    {
        currentTime += Time.deltaTime;
        timerText.text = $"Time: {currentTime:F2}";
    }

    public static void ResetTimer()
    {
            currentTime = 0f;
    }
}
