using UnityEngine;
using TMPro;

public class Timer : MonoBehaviour
{
    private static Timer instance;

    public static float currentTime = 0f;
    [SerializeField] private TextMeshProUGUI timerText;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

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
