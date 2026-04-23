using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;
using Wonjeong.Data;
using Wonjeong.UI;
using Wonjeong.Utils;

namespace My.Scripts
{
    /// <summary>
    /// 센서 ID와 비디오 세팅을 연결하는 데이터 클래스입니다.
    /// Json 매핑을 위해 사용됩니다.
    /// </summary>
    [Serializable]
    public class ShadowMapping
    {
        public string sensorId;
        public VideoSetting videoSetting;
    }

    /// <summary>
    /// 그림자 프로젝트의 설정을 담는 데이터 클래스입니다.
    /// </summary>
    [Serializable]
    public class ShadowConfig
    {
        public ShadowMapping[] mappings;
    }

    public class ShadowVideoController : MonoBehaviour
    {
        [SerializeField] private GameObject videoPrefab; 
        [SerializeField] private Transform videoContainer; 

        private Dictionary<string, VideoPlaybackData> playbackDataMap;
        private int activeVideoCount;
        private Queue<VideoPlaybackData> pendingVideoQueue;

        /// <summary>
        /// 재생 상태와 참조를 관리하는 데이터 클래스입니다.
        /// 비디오 동시 재생 및 중복 재생 방지를 위해 개별 상태를 유지하기 위함입니다.
        /// </summary>
        private class VideoPlaybackData
        {
            public VideoSetting Setting;
            public GameObject VideoObject;
            public VideoPlayer Player;
            public RawImage Image;
            public AudioSource Audio;
            public bool IsPreparing;
            public bool IsQueued; 
        }

        private void Start()
        {
            LoadSettings();
            SubscribeToSensor(); // 주석 해제됨: 아두이노 센서 구독 활성화
        }

        /// <summary>
        /// JSON 데이터를 로드하고 배열 길이에 맞춰 비디오 오브젝트를 동적으로 풀링합니다.
        /// </summary>
        private void LoadSettings()
        {
            ShadowConfig config = JsonLoader.Load<ShadowConfig>("VideoSettings.json");
            
            // C# 일반 객체 명시적 검사
            if (config == null || config.mappings == null)
            {
                Debug.LogError("VideoSettings.json 로드 실패 또는 매핑 배열이 존재하지 않습니다.");
                return;
            }

            // Unity 객체 암시적 검사
            if (!videoPrefab || !videoContainer)
            {
                Debug.LogError("비디오 프리팹 또는 컨테이너 참조가 누락되었습니다.");
                return;
            }

            playbackDataMap = new Dictionary<string, VideoPlaybackData>();
            pendingVideoQueue = new Queue<VideoPlaybackData>();
            
            foreach (ShadowMapping mapping in config.mappings)
            {
                GameObject spawnedObj = Instantiate(videoPrefab, videoContainer);
                spawnedObj.name = mapping.videoSetting.name;

                VideoPlayer player = spawnedObj.GetComponent<VideoPlayer>();
                RawImage rawImage = spawnedObj.GetComponent<RawImage>();
                AudioSource audioSource = spawnedObj.GetComponent<AudioSource>();
                
                if (!player || !rawImage || !audioSource)
                {
                    Debug.LogWarning("생성된 프리팹 내 필수 컴포넌트가 누락되었습니다.");
                    continue;
                }

                RectTransform rt = rawImage.rectTransform;
                rt.anchoredPosition = mapping.videoSetting.position;
                rt.sizeDelta = mapping.videoSetting.size;
                rt.localEulerAngles = mapping.videoSetting.rotation;
                rt.localScale = mapping.videoSetting.scale;

                Vector2Int size = new Vector2Int((int)mapping.videoSetting.size.x, (int)mapping.videoSetting.size.y);
                VideoManager.Instance.WireRawImageAndRenderTexture(player, rawImage, size);

                VideoPlaybackData playbackData = new VideoPlaybackData();
                playbackData.Setting = mapping.videoSetting;
                playbackData.VideoObject = spawnedObj;
                playbackData.Player = player;
                playbackData.Image = rawImage;
                playbackData.Audio = audioSource;
                
                playbackDataMap[mapping.sensorId] = playbackData;

                player.url = VideoManager.Instance.ResolvePlayableUrl(mapping.videoSetting.fileName);

                player.errorReceived += (vp, msg) => 
                {
                    Debug.LogWarning(string.Format("비디오 디코더 작동 실패 ({0}): {1}", vp.name, msg));
                    playbackData.IsPreparing = false;
                    spawnedObj.SetActive(false);
                    
                    // 에러 시에도 카운트를 차감하고 큐를 비워야 교착 상태를 방지할 수 있음
                    activeVideoCount--;
                    CheckPendingQueue();
                };

                spawnedObj.SetActive(false);
            }
        }

        /// <summary>
        /// 수신된 데이터를 기반으로 비디오 재생을 요청.
        /// 동시 재생 한계(5개) 도달 시 큐에 적재하여 대기시킴.
        /// </summary>
        /// <param name="data">수신된 문자열 데이터</param>
        private void HandleSensorInput(string data)
        {
            string parsedSensorId = data.Trim();

            if (playbackDataMap.TryGetValue(parsedSensorId, out VideoPlaybackData targetData))
            {
                // 이미 진행 중이거나 대기열에 있다면 중복 실행 방지
                if (targetData.Player.isPlaying || targetData.IsPreparing || targetData.IsQueued)
                {
                    return;
                }

                if (activeVideoCount >= 5)
                {
                    targetData.IsQueued = true;
                    pendingVideoQueue.Enqueue(targetData);
                    return;
                }

                StartCoroutine(PlayVideoRoutine(targetData));
            }
            else
            {
                Debug.LogWarning("매핑되지 않은 데이터 수신");
            }
        }

        /// <summary>
        /// 특정 비디오를 재생합니다.
        /// 렌더 텍스처에 남아있는 이전 재생 프레임 잔상을 숨기기 위해 재생 극초반부에 알파값을 제어합니다.
        /// </summary>
        /// <param name="data">재생할 비디오의 관리 데이터</param>
        private IEnumerator PlayVideoRoutine(VideoPlaybackData data)
        {
            data.IsQueued = false;
            data.IsPreparing = true;
            activeVideoCount++;
            
            // 이전 잔상을 가리기 위해 준비 단계에서 투명도 0 적용
            Color startColor;
            startColor = data.Image.color;
            startColor.a = 0f;
            data.Image.color = startColor;
            
            data.VideoObject.SetActive(true);
            data.VideoObject.transform.SetAsLastSibling();
            
            yield return StartCoroutine(VideoManager.Instance.PrepareAndPlayRoutine(data.Player, data.Player.url, data.Audio, data.Setting.volume));
            
            data.IsPreparing = false;

            if (!data.Player.isPlaying)
            {
                data.VideoObject.SetActive(false);
                activeVideoCount--;
                CheckPendingQueue();
                yield break;
            }

            // 새로운 첫 프레임이 디코딩되어 렌더 텍스처에 씌워질 때까지 대기
            float renderWait;
            renderWait = 0f;
            
            while (data.Player.frame <= 0 && renderWait < 0.5f)
            {
                renderWait += Time.deltaTime;
                yield return null;
            }
            
            // GPU 파이프라인 갱신 보장을 위한 추가 1프레임 지연
            yield return null; 
            
            // 새 프레임 렌더링이 완료되었으므로 화면 출력 복구
            startColor.a = 1f;
            data.Image.color = startColor;

            double clipLength;
            clipLength = data.Player.length;
            
            float maxDuration;
            maxDuration = clipLength > 0.1 ? (float)clipLength + 1.0f : 30f;
            
            float timer;
            timer = 0f;

            while (data.Player.isPlaying && timer < maxDuration)
            {
                timer += Time.deltaTime;
                yield return null;
            }

            data.Player.Stop();
            data.VideoObject.SetActive(false);
            
            activeVideoCount--;
            CheckPendingQueue();
        }

        /// <summary>
        /// 대기열을 확인하여 여유가 생기면 다음 비디오를 재생.
        /// 하드웨어 동시 처리 한계를 넘지 않도록 순차적 실행을 보장하기 위함.
        /// </summary>
        private void CheckPendingQueue()
        {
            // 일반 C# 객체는 명시적 null 검사 수행
            if (pendingVideoQueue != null && pendingVideoQueue.Count > 0 && activeVideoCount < 5)
            {
                VideoPlaybackData nextData = pendingVideoQueue.Dequeue();
                StartCoroutine(PlayVideoRoutine(nextData));
            }
        }

        /// <summary>
        /// 아두이노 센서 입력을 구독합니다.
        /// 주석 해제됨: 아두이노 하드웨어로부터 데이터를 수신할 수 있도록 활성화.
        /// </summary>
        private void SubscribeToSensor()
        {
            if (!ArduinoManager.Instance)
            {
                Debug.LogError("ArduinoManager 참조 실패");
                return;
            }

            ArduinoManager.Instance.OnDataReceived += HandleSensorInput;
        }

        /// <summary>
        /// 매 프레임마다 디버그용 키보드 입력을 확인하여 비디오를 재생합니다.
        /// R키 입력 시 현재 씬을 다시 로드하여 비디오 설정(JSON)을 완전히 초기화하고 재적용합니다.
        /// </summary>
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha0)) HandleSensorInput("0");
            if (Input.GetKeyDown(KeyCode.Alpha1)) HandleSensorInput("1");
            if (Input.GetKeyDown(KeyCode.Alpha2)) HandleSensorInput("2");
            if (Input.GetKeyDown(KeyCode.Alpha3)) HandleSensorInput("3");
            if (Input.GetKeyDown(KeyCode.Alpha4)) HandleSensorInput("4");
            if (Input.GetKeyDown(KeyCode.Alpha5)) HandleSensorInput("5");
            if (Input.GetKeyDown(KeyCode.Alpha6)) HandleSensorInput("6");
            if (Input.GetKeyDown(KeyCode.Alpha7)) HandleSensorInput("7");

            if (Input.GetKeyDown(KeyCode.R))
            {
                int currentSceneIndex;
                
                currentSceneIndex = SceneManager.GetActiveScene().buildIndex;
                SceneManager.LoadScene(currentSceneIndex);
            }
        }
        
        /// <summary>
        /// 주석 해제됨: 안전한 메모리 관리를 위해 이벤트 구독을 해제합니다.
        /// </summary>
        private void OnDestroy()
        {
            if (ArduinoManager.Instance)
            {
                ArduinoManager.Instance.OnDataReceived -= HandleSensorInput;
            }
        }
    }
}