using UnityEngine;
using TMPro;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;
    
    [Header("Game variables that need to be sent to the Database")]
    public static float BestTime = 0f;

    [Header("Objects that need to be set in the inspector")]
    [SerializeField] private TextMeshProUGUI BestTimeText;

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

    public void UpdateBestTime(float newBestTime)
    {
        BestTime = newBestTime;
        BestTimeText.text = "Best Time: " + BestTime.ToString("F2") + "s";
    }
}
