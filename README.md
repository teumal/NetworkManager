# NetworkManager

`NetworkManager` script component 는 `Unity` 엔진에서 2인용 p2p 통신을 구축하기 위해서 디자인되었습니다. 

사용자는 제공되는 메소드를 사용하여, 서버와 클라이언트 소켓을 생성한 후  각 프레임에서의 게임 오브젝트들의

상태를 동기화하기 위한 메시지를 송수신해야 합니다.  또한, 게임 오브젝트들의 상태를 동기화하기 위한 방법을

제공하며, 이는 유니티의 물리 시뮬레이션이 최대한 결정론적(deterministic)이 될 수 있도록 돕습니다. 

메소드/속성들의 사용방법 및 자세한 설명은 `NetworkManager.html` 문서를 참고하시길 바랍니다.

다음은 `NetworkManager` 를 통해 네트워크 통신을 구축하는 법을 간략하게 소개합니다:

<br>
<br>

# Tutorial
## 1. CreateServer
``` c#
 NetworkManager.port = 12345; // optional
 
 if(NetworkManager.CreateServer()) {
   UnityEngine.Debug.Log($"hostIP : {NetworkManager.hostIP}"); // hostIP : 123.45.67.890
 }
```
p2p 통신을 위해 서버 측은 먼저 `NetworkManager.CreateServer` 함수를 사용하여 서버 소켓을 생성해야 합니다.

위 코드는 `NetworkManager.port` 속성을 사용하여 포트 번호(port number)를 `12345` 로 정해주었습니다.

이 과정은 필수는 아니며, 생략 시 기본값인 `11111` 를 사용하게 됩니다. 
<br><br>

`NetworkManager.CreateServer` 함수는 성공 여부를 나타내는 `bool` 값을 반환합니다. 서버 소켓을 생성하는데

성공했다면,  `NetworkManager.hostIP` 를 사용하여 서버의 아이피 주소를 클라이언트 측에게 알려주어야 합니다.

<br>
<br>

## 2. ClientClient
``` c#
 NetworkManager.port = 12345; // optional
 
 if(NetworkManager.CreateClient("123.45.67.890")) {
   UnityEngine.Debug.Log("wrong ip address!");
 }
```
클라이언트 측은 서버 측에서 알려준 아이피 주소를 문자열의 형태로, `NetworkManager.CreateClient` 함수로 넘겨줍니다.

