# NetworkManager

`NetworkManager` script component 는 `Unity` 엔진에서 2인용 p2p 통신을 구축하기 위해서 디자인되었습니다. 

사용자는 제공되는 메소드를 사용하여, 서버와 클라이언트 소켓을 생성한 후  각 프레임에서의 게임 오브젝트들의

상태를 동기화하기 위한 메시지를 송수신해야 합니다.  또한, 게임 오브젝트들의 상태를 동기화하기 위한 방법을

제공하며, 이는 유니티의 물리 시뮬레이션이 최대한 결정론적(deterministic)이 될 수 있도록 돕습니다. 

메소드/속성들의 사용방법 및 자세한 설명은 `NetworkManager.html` 문서를 참고하시길 바랍니다.

다음은 최대한 간단하게 네트워크 통신을 구축하는 법을 소개합니다:

<br>
<br>

# Tutorial
## 1. 서버 측의 소켓 생성
``` c#
 NetworkManager.port = 12345; // optional
 
 if(NetworkManager.CreateServer()) {
   UnityEngine.Debug.Log($"hostIP : {NetworkManager.hostIP}"); // hostIP : 123.45.67.890
   StartCoroutine(Loading() );
 }
```
p2p 통신을 위해 서버 측은 먼저 `NetworkManager.CreateServer` 함수를 사용하여 서버 소켓을 생성해야 합니다.

위 코드는 `NetworkManager.port` 속성을 사용하여 포트 번호(port number)를 `12345` 로 정해주었습니다.

이 과정은 필수는 아니며, 생략 시 기본값인 `11111` 를 사용하게 됩니다. 
<br><br>

`NetworkManager.CreateServer` 함수는 성공 여부를 나타내는 `bool` 값을 반환합니다. 서버 소켓을 생성하는데

성공했다면,  `NetworkManager.hostIP` 를 사용하여 서버의 아이피 주소를 클라이언트 측에게 알려주어야 합니다.

생성된 서버 소켓은 비동기적으로 클라이언트의 연결 요청을 기다립니다.

<br>
<br>

## 2. 클라이언트 측의 소켓 생성
``` c#
 NetworkManager.port = 12345; // optional
 
 if(NetworkManager.CreateClient("123.45.67.890")) {
   UnityEngine.Debug.Log("wrong ip address!");
   StartCoroutine(Loading() );
 }
```
클라이언트 측은 서버 측에서 알려준 아이피 주소를 문자열의 형태로, `NetworkManager.CreateClient` 

함수로 넘겨줍니다. `NetworkManager.CreateServer` 와 마찬가지로, 성공여부를 나타내는 `bool` 값을

반환합니다. 생성된 클라이언트 소켓은 비동기적으로 서버로 연결을 요청합니다.
<br>
<br>

## 3. 연결 대기
``` c#
  // Loading() Coroutine
  private IEnumerator Loading() {
     String         opponent = NetworkManager.isServer ? "클라이언트" : "서버";
     String         postfix  = String.Empty;
     String         prefix   = NetworkManager.isServer ? NetworkManager.hostIP : String.Empty;
     WaitForSeconds delay    = new WaitForSeconds(0.2f);

     String[] reason = new String[] {
        "연결이 종료되었습니다",
        "방이 가득찼습니다",
        "서버를 찾을 수 없었습니다",
        "연결시간이 초과되었습니다",
       $"{opponent}로부터 응답이 없습니다"
     };

     while(true) {
         switch(NetworkManager.status) {
              case SocketStatus.NotConnected: {
                 output.text = $"{prefix}\n{opponent}를 찾는 중입니다{postfix}";
                 break;
              }
              case SocketStatus.Connecting: {
                 output.text = $"{prefix}\n{opponent}를 찾았습니다!{postfix}";
                 break;
              }
              case SocketStatus.Connected: {
                 output.text = $"{prefix}\n{opponent}와 연결되었습니다";
                 InitGame(); // 이 함수는 후술합니다.
                 yield break;
              }
              case SocketStatus.Closed: {
                 output.text = reason[(int) NetworkManager.exitCode];
                 yield break;
              }
         };

         if(postfix.Length > 3) {
            postfix = String.Empty;
         }
         else {
            postfix += ".";
         }
         yield return delay;
     }
  }
```
서버와 클라이언트를 생성했다고 해서, 연결이 바로 완료된 것은 아닙니다. 내부적으로 핑(ping) 체크 등의 과정이 이루어지고 있기

때문입니다. 그렇기에 사용자는 `NetworkManager.status` 속성을 사용하여, 연결이 완료되었는지 종료되었는지를 확인해야 합니다.

`NetworkManager.status == SocketStatus.Connected` 가 되면, 연결이 완료되었다는 의미입니다. 연결이 완료되면, 동기화되야할

내용들을 `NetworkManager.onUpdate`, `NetworkManager.onFixedUpdate`, `NetworkManager.onReadMessage` 에 등록합니다.

<br><br>

## 4. 동기화할 내용 등록
``` c#
Rigidbody2D player1, player2;
Vector2 velocity1, velocity2;
byte[] msgBuffer;

 void InitGame() {
    if(NetworkManager.isServer) {
      player1 = Find("ServerPlayer").GetComponent<Rigidbody2D>();
      player2 = Find("ClientPlayer").GetComponent<Rigidbody2D>();
    }
    else {
      player1 = Find("ServerPlayer").GetComponent<Rigidbody2D>();
      player2 = Find("ClientPlayer").GetComponent<Rigidbody2D>();
    }
 
    // Update() 에 해당
    NetworkManager.onUpdate = ()=>{
       Vector2 force = new Vector2(
         Input.GetAxis("Horizontal") * 5f * NetworkManager.deltaTime,
         Input.GetAxis("Vertical")   * 5f * NetworkManager.deltaTime
       );
       velocity1 += force;
       
       byte[] x = BitConverter.GetBytes(force.x);
       byte[] y = BitConverter.GetBytes(force.y);
       
       NetworkManager.Host2Network(x,0,4).CopyTo(msgBuffer,0);
       NetworkManager.Host2Network(y,0,4).CopyTo(msgBuffer,4);
       NetworkManager.SendMessage(msgBuffer,0,8);
    };
    
    
    // FixedUpdate() 에 해당
    NetworkManager.onFixedUpdate = ()=>{
       player1.MovePosition(player1.position + velocity1);
       player2.MovePosition(player2.position + velocity2);
       velocity1 *= 0.9f;
       velocity2 *= 0.9f;
    };
    
    
    // 이전 프레임에서 상대방이 보낸 메시지를 처리
    NetworkManager.onReadMessage = (msg)=>{
       NetworkManager.Host2Network(0,4);
       NetworkManager.Host2Network(4,4);
       
       float x = BitConverter.ToSingle(msg);
       float y = BitConverter.ToSingle(msg[4..]);
       velocity2 += new Vector2(x,y);
    };
 }
```
`Update()`, `FixedUpdate()` 단계에서 동기화가 되야할 내용들을 `NetworkManager.onUpdate`, `NetworkManager.onFixedUpdate`

에 등록합니다. `NetworkManager.onUpdate` 에서 키 입력을 받고, 자신이 조종하는 캐릭터에게 힘을 가해줍니다. 또한, 상대 측도

이 상태를 반영할 수 있도록, `NetworkManager.SendMessage` 함수를 사용하여 벡터 `force` 를 보내줍니다. 이때, 보낼 값들은

모두 `byte[]` 형태로 인코딩되어야 합니다. 이를 위해 `System.BitConverter` 의 함수들을 사용하는 것을 추천합니다.

<br>
<br>

받은 메시지는 `NetworkManager.onReadMessage` 에 등록한 콜백함수를 통해 읽어들일 수 있습니다. 위 코드에서는

`byte[]` 로 인코딩된 `force` 를 다시 `Vector2` 타입으로 읽어들여, 상대방의 캐릭터에 힘을 가해줍니다.
<br>
<br>
## 5. 마무리
위의 코드에서 서버와 클라이언트 측이 난수(random number)를 사용한다고 하면, 맨 처음에 연결이 되었을 때

한번만 서버 측에서 난수 생성기를 초기화하기 위해 쓰일 시드(seed)값을 클라이언트 측으로 보내줍니다.

유니티의 `Random.InitState(int seed)` 를 통해 항상 똑같은 난수를 얻을 수 있습니다.

<br>

시간 측정은 `NetworkManager.deltaTime` 를 통해 해줄 수 있습니다. 해당 속성은 기존의 `Time.deltaTime` 과 

다르게, 그 값이 절대 변하지 않기 때문에  서버 측과 클라이언트 측 모두 동일한 결과를 얻을 수 있기 때문입니다.

다만, 이것은 너무 단순합니다. 실제로는 부동소수점 오차 또는 결정론적이지 못한 유니티의 물리 시뮬레이션으로

인하여 동기화가 어긋날 수 있기 때문입니다. 즉, 서버가 동기화가 어긋나지 않도록 이를 보정하는 로직이

필요할 수 있습니다. 일반적으로 서버가 시뮬레이션한 결과를 클라이언트 측으로 보내주거나, 

일정 주기마다 서버가 클라이언트의 상태를 바로잡아주는 방법을 생각할 수 있습니다. 자세한 건,

`NetworkManager.html` 문서를 참고하시길 바랍니다.






