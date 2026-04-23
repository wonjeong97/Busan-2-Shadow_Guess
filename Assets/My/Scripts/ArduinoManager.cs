using System;
using System.Collections;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Wonjeong.Utils;

namespace My.Scripts
{
    /// <summary>
    /// 아두이노(ESP32)와의 시리얼 통신 연결 및 핸드셰이크를 관리하는 클래스.
    /// 보드의 초기화 시퀀스(Reset -> Ready -> Calibration -> Sensor_Ready)를 동기화하여 처리합니다.
    /// </summary>
    public class ArduinoManager : MonoBehaviour
    {
        public static ArduinoManager Instance { get; private set; }
        
        [SerializeField] private int baudRate;
        [SerializeField] private float handshakeTimeout;
        [SerializeField] private float reconnectInterval;

        private SerialPort connectedPort;
        private Coroutine connectionCoroutine;
        
        private string receiveBuffer;
        private bool isBoardReady;
        private bool isCalibrationDone;
        
        public event Action<string> OnDataReceived;
        
        private void Awake()
        {
            if (Instance)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (baudRate <= 0)
            {
                Debug.LogWarning("BaudRate is invalid or not set. Fallback to 9600.");
                baudRate = 9600;
            }

            if (handshakeTimeout <= 0f)
            {
                Debug.LogWarning("HandshakeTimeout is invalid or not set. Fallback to 2.0f.");
                handshakeTimeout = 2.0f;
            }

            if (reconnectInterval <= 0f)
            {
                Debug.LogWarning("ReconnectInterval is invalid or not set. Fallback to 3.0f.");
                reconnectInterval = 3.0f;
            }

            if (connectionCoroutine != null)
            {
                StopCoroutine(connectionCoroutine);
            }
            connectionCoroutine = StartCoroutine(ConnectionMonitorRoutine());
        }

        private IEnumerator ConnectionMonitorRoutine()
        {
            WaitForSeconds wait;
            wait = CoroutineData.GetWaitForSeconds(reconnectInterval);

            while (true)
            {
                if (connectedPort == null || !connectedPort.IsOpen)
                {
                    yield return StartCoroutine(FindAndConnectArduino());
                }
                
                yield return wait;
            }
        }
        
        /// <summary>
        /// 수신 버퍼를 읽고, 줄바꿈 문자를 기준으로 보드의 상태 및 센서 입력을 파싱합니다.
        /// </summary>
        private void Update()
        {
            if (connectedPort == null || !connectedPort.IsOpen)
            {
                return;
            }

            if (connectedPort.BytesToRead > 0)
            {
                receiveBuffer += connectedPort.ReadExisting();
                int newLineIndex;
                
                while ((newLineIndex = receiveBuffer.IndexOf('\n')) != -1)
                {
                    string line;
                    line = receiveBuffer.Substring(0, newLineIndex).Trim();
                    receiveBuffer = receiveBuffer.Substring(newLineIndex + 1);

                    if (!string.IsNullOrEmpty(line))
                    {
                        // 보드에서 수신되는 모든 데이터(Rebooting..., Off 등)를 로깅합니다.
                        Debug.Log(string.Format("Arduino Received: {0}", line));

                        if (line == "MPR121 not found")
                        {
                            Debug.LogError("[ArduinoManager] 하드웨어 에러: MPR121 터치 센서를 찾을 수 없습니다!");
                        }
                        else if (line == "Ready")
                        {
                            isBoardReady = true;
                        }
                        else if (line == "Sensor_Ready")
                        {
                            isCalibrationDone = true;
                        }
                        else if (line.EndsWith("On"))
                        {
                            string sensorId;
                            sensorId = line.Replace("On", "").Trim();
                            OnDataReceived?.Invoke(sensorId);
                        }
                    }
                }
            }
        }

        private IEnumerator FindAndConnectArduino()
        {
            string[] ports;
            Task<SerialPort>[] tasks;

            ports = SerialPort.GetPortNames();
            tasks = new Task<SerialPort>[ports.Length];

            for (int i = 0; i < ports.Length; i++)
            {
                string portName;
                portName = ports[i];
                tasks[i] = Task.Run(() => CheckPortAsync(portName));
            }

            while (!Task.WhenAll(tasks).IsCompleted)
            {
                yield return null;
            }

            foreach (Task<SerialPort> task in tasks)
            {
                if (task.Result != null)
                {
                    if (connectedPort == null)
                    {
                        connectedPort = task.Result;
                        Debug.Log("ESP32 successfully connected.");
                        
                        StartCoroutine(InitializeESP32Routine());
                    }
                    else
                    {
                        task.Result.Close();
                    }
                }
            }

            if (connectedPort == null)
            {
                Debug.LogError("ESP32 handshake failed. Please check the connection.");
            }
        }
        
        /// <summary>
        /// 포트 개방 후 ESP32 고유 시작 식별자를 탐색합니다.
        /// PC 재부팅 직후 백지상태의 COM 포트를 아두이노 IDE처럼 명시적으로 초기화합니다.
        /// </summary>
        private SerialPort CheckPortAsync(string portName)
        {
            SerialPort testPort;
            System.Diagnostics.Stopwatch stopwatch;
            string handshakeBuffer = ""; 

            testPort = new SerialPort(portName, baudRate);
            
            // 아두이노 IDE가 기본적으로 수행하는 시리얼 하위 통신 규격 강제 지정
            // 이 설정이 없으면 Windows 재부팅 직후 드라이버가 데이터를 정상적으로 해석하지 못합니다.
            testPort.DataBits = 8;
            testPort.Parity = Parity.None;
            testPort.StopBits = StopBits.One;
            testPort.Handshake = Handshake.None;

            try
            {
                testPort.Open();

                // ESP32 보드를 강제로 런타임 모드(정상 실행)로 부팅시키는 시퀀스
                // DTR과 RTS 신호를 짧게 주었다가 뺌으로써 하드웨어 리셋 핀(EN)을 자극합니다.
                testPort.DtrEnable = true;
                testPort.RtsEnable = true;
                Thread.Sleep(50); // 보드가 신호를 인식할 아주 짧은 대기 시간
                testPort.DtrEnable = false;
                testPort.RtsEnable = false;
                Thread.Sleep(50);

                testPort.DiscardInBuffer();
            }
            catch (Exception)
            {
                return null;
            }

            stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (stopwatch.Elapsed.TotalSeconds < 5.0)
            {
                if (testPort.BytesToRead > 0)
                {
                    string response;
                    response = testPort.ReadExisting();
                    
                    handshakeBuffer += response;
                    
                    Debug.Log(string.Format("[Port Scan] {0} 누적 버퍼: {1}", portName, handshakeBuffer.Replace("\n", "").Replace("\r", "")));

                    if (handshakeBuffer.Contains("ESP32_Start"))
                    {
                        return testPort;
                    }
                }
                
                Thread.Sleep(10);
            }

            testPort.Close();
            return null;
        }

        /// <summary>
        /// ESP32 하드웨어 리셋 및 캘리브레이션 요청을 보냅니다.
        /// 응답 10초 대기 초과 시 현재 포트를 강제로 닫고 객체를 비워, 
        /// 백그라운드 모니터링 루틴이 백지상태(전체 포트 재검색)에서 다시 시작하도록 유도합니다.
        /// </summary>
        private IEnumerator InitializeESP32Routine()
        {
            // 포트 개방 시 발생하는 DTR/RTS 하드웨어 리셋의 초기 부팅 완료("Ready")를 
            // 소프트웨어 리셋("R") 응답으로 오인하는 문제를 막기 위해 2초간 대기합니다.
            yield return new WaitForSeconds(2.0f);

            // C# 일반 객체이므로 명시적 null 비교 수행
            if (connectedPort != null && connectedPort.IsOpen)
            {
                // 대기하는 동안 쌓인 초기 부팅 로그(ESP32_Start, 가짜 Ready 등)를 완전히 비웁니다.
                connectedPort.DiscardInBuffer();
            }
            
            receiveBuffer = "";
            isBoardReady = false;
            isCalibrationDone = false;

            Debug.Log("[ArduinoManager] 보드 리셋 요청 (R) 전송 시작");
            SendCommand("R");
            
            float timer;
            timer = 0f;
            
            while (!isBoardReady && timer < 10.0f)
            {
                timer += Time.deltaTime;
                yield return null;
            }

            if (!isBoardReady)
            {
                Debug.LogWarning("[ArduinoManager] 리셋 응답 10초 대기 초과. 연결을 초기화하고 백지상태에서 재검색합니다.");
                if (connectedPort != null && connectedPort.IsOpen) connectedPort.Close();
                connectedPort = null;
                yield break;
            }

            Debug.Log("[ArduinoManager] 보드 리셋 성공. 센서 캘리브레이션 요청 (C) 전송");
            SendCommand("C");

            float calTimer;
            calTimer = 0f;
            
            while (!isCalibrationDone && calTimer < 10.0f)
            {
                calTimer += Time.deltaTime;
                yield return null;
            }

            if (!isCalibrationDone)
            {
                Debug.LogWarning("[ArduinoManager] 캘리브레이션 응답 10초 대기 초과. 연결을 초기화하고 백지상태에서 재검색합니다.");
                if (connectedPort != null && connectedPort.IsOpen) connectedPort.Close();
                connectedPort = null;
                yield break;
            }

            Debug.Log("[ArduinoManager] ESP32 초기화 및 캘리브레이션 완료");
        }
        
        public void SendCommand(string command)
        {
            if (connectedPort != null && connectedPort.IsOpen)
            {
                try
                {
                    connectedPort.WriteLine(command);
                }
                catch (Exception e)
                {
                    Debug.LogError("Failed to send command to ESP32: " + e.Message);
                }
            }
            else
            {
                Debug.LogWarning("ESP32 is not connected. Command ignored: " + command);
            }
        }
        
        private void OnApplicationQuit()
        {
            if (connectedPort != null && connectedPort.IsOpen)
            {
                connectedPort.Close();
            }
        }
    }
}