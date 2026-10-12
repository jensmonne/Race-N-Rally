using UnityEngine;

public class Finish : MonoBehaviour
{
    private static Finish instance;
    private float finishTime;

    [SerializeField] private int TotalLapCount = 3;

    public int CurrentLapCount { get; private set; } = 0;

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

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            finishTime = Timer.currentTime;
            if (finishTime < GameManager.BestTime || GameManager.BestTime == 0f)
            {
                GameManager.instance.UpdateBestTime(finishTime);
            }
            Timer.ResetTimer();
                
        }
    }

    private void UpdateLapCount()
    {
        CurrentLapCount++;
        if (CurrentLapCount >= TotalLapCount)
        {
            Debug.Log("Race Finished!");
        }
    }
}
