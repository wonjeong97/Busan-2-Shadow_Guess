using UnityEngine;
using Wonjeong.Reporter;
using Wonjeong.Utils;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    
    [SerializeField] private Reporter   reporter;
    [SerializeField] private GameObject systemCanvas;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        if (systemCanvas != null)
            DontDestroyOnLoad(systemCanvas);
        TimestampLogHandler.Attach();
    }

    private void Start()
    {
        Cursor.visible = false;
        if (reporter && reporter.show) reporter.show = false;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.D) && reporter)
        {
            reporter.showGameManagerControl = !reporter.showGameManagerControl;
            if (reporter.show) reporter.show = false;
        }
        else if (Input.GetKeyDown(KeyCode.M)) 
        {
            Cursor.visible = !Cursor.visible;
        }
    }
}