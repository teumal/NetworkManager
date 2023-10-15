#define USE_PHYSICS2D // when your project is for 3D, comment this line

using System;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using UnityEngine;

public enum SocketStatus {
    Closed,
    NotConnected,
    Connecting,
    Connected
};

public enum SocketExitCode {
    None,
    RoomIsFull,
    Denied,
    Timeout,
    NoResponse
};

public enum Simulator {
    Both,
    Server
};

public class NetworkManager : MonoBehaviour {

    public delegate void MessageHandler(ReadOnlySpan<byte> msg);
    public delegate void UpdateFunction();

    private enum MessageType {
        SetStatus,       
        Ping,            
        Pong,            
        ClientFrameRate, 
        ServerFrameRate, 
        SyncFrameRate,   
        SetBreakpoint,
        EndOfFrame,     
        CustomMessage,
        Physics,
        OnUpdate,
        Simulator
    };


    ///////////////////////
    // Properties        //
    ///////////////////////

    private static NetworkManager Inst = null; // the singleton instance

    #if DEVELOPMENT_BUILD //{
       private StringBuilder mLog = new StringBuilder(100000); // log string
    //}
    #endif

    private bool           mServerSimulation = false;           // checks if the simulation result is reliable
    private int            mCurrentFrame = 0;                   // current frame number
    private Simulator      mSimulator    = Simulator.Both;      // simulatorType
    private int            mPort         = 11111;               // port number
    private String         mHostIP       = null;                // a `String` object which shows the host IP
    private bool           mIsServer     = false;               // checks if the socket created is for server
    private SocketStatus   mStatus       = SocketStatus.Closed; // the current status of `this`
    private SocketExitCode mExitCode     = SocketExitCode.None; // a reason why the socket is closed

    private MessageHandler mOnReadMessage = null;  // the delegate which is called on the step,`onReadMessage`
    private UpdateFunction mOnUpdate      = null;  // the delegate which is called on the step,`onUpdate`
    private UpdateFunction mOnFixedUpdate = null;  // the delegate which is called on the step,`onFixedUpdate`

    private volatile int mEnterUpdate      = 0;  // for LockStep
    private float        mEnterFixedUpdate = 0f; // for scheduling the step, `Physics`

    private float mFixedDeltaTime   = 0f;  // a fixed timestamp which is never be changed
    private float mDeltaTime        = 0f;  // a synchronized frame per seconds (FPS) between `server` and `client`

    private Socket mServer   = null;  // the socket object for server
    private Socket mListener = null;  // the socket object for receiveing and sending a message

    private Thread mServerThread = null; // Thread object used for `ServerCode`
    private Thread mSocketThread = null; // Thread object used for `ServerCode1 or `ClientCode`
    private Thread mPingThread   = null; // Thread object used for `PingCode`

    private byte[] mRecvBuffer   = new byte[1024]; // contain the messages received from the opponent
    private int    mRecvBufferSz = 0;              // the current recvBuffer size

    private byte[] mSendBuffer   = new byte[1024]; // the message which will be sent
    private int    mSendBufferSz = 0;              // the current sendBuffer size

    private byte[] mMsgQueue    = new byte[4096]; // message queue
    private int    mQueueSz     = 0;              // the current queue size
    private int    mQueueOffset = 0;              // the current queue offset

    private Stopwatch mTimer = new Stopwatch(); // used to measure network latency (=ping)
    private float  mClientFrameRate;            // average client frame rate
    private float  mServerFrameRate;            // average server frame rate
    private double mAvgLatency;                 // average network latency 

    private object lk1, lk2;      // the lock objects for `PingThread`, for `mMsgQueue`
    private int msgBegin, msgEnd; // the range of the current read message


    /////////////////////////
    // Private Methods     //
    /////////////////////////

    // Awake() Method
    private void Awake() {
        if (Inst == null) {
            Inst = this;
            lk1  = Inst;
            lk2  = mRecvBuffer;
            Physics2D.simulationMode = SimulationMode2D.Script;
            Inst.mFixedDeltaTime     = Time.fixedDeltaTime;
            DontDestroyOnLoad(gameObject);
            StartCoroutine(EndOfFrame());
            return;
        }
        Destroy(gameObject); 
    }


    // OnApplicationQuit() Method
    private void OnApplicationQuit() {
        if (Inst != null) {
            Close();

            mServerThread?.Join();
            mSocketThread?.Join();
            mPingThread?.Join();
        }
    }


    // Update() Method
    private void Update() {
        try {
           
            if(mStatus != SocketStatus.Connected) {
                ReadMessage();

                #if DEVELOPMENT_BUILD //{
                   Log($"#ProductName: {Application.productName}\n#Date: {DateTime.Now}\n#deltaTime: {Inst.mDeltaTime}\n#fixedDeltaTime: {Inst.mFixedDeltaTime}\n#latency: {latency}\n\n");
                //}
                #endif


                if (mStatus != SocketStatus.Connected) return;
            }

            #if DEVELOPMENT_BUILD // {
                  Log($"-----------------------------\nframe {mCurrentFrame})");
            // }
            #endif

            lock (lk2) {
                if (mEnterUpdate == 0) { // Step 0) EnterUpdate
                    Monitor.Wait(lk2);
                }
                mEnterUpdate--;
                mEnterFixedUpdate += mDeltaTime;
            }
            ReadMessage(); // Step 1) ReadMessage_First


            // if simulator == Server, then the client can't the next steps directly..
            if(mSimulator == Simulator.Server && mIsServer == false) {
                ReadMessage(); // Step 1-2) ReadMessage_Second (read up to `onUpdate` message)
                return;
            }

            while (mEnterFixedUpdate >= mFixedDeltaTime) {
                mEnterFixedUpdate -= mFixedDeltaTime;
                mOnFixedUpdate?.Invoke();            // Step 2) onFixedUpdate

                if (mSimulator == Simulator.Server) {
                    SystemMessage.Physics();
                }

                #if USE_PHYSICS2D //{
                   Physics2D.Simulate(mFixedDeltaTime); // Step 3) Simulate
                //}
                #else //{
                   Physics.Simulate(mFixedDeltaTime); // Step 3) Simulate
                //}
                #endif
            }

            if (mSimulator == Simulator.Server) {
                SystemMessage.OnUpdate();
            }

            mOnUpdate?.Invoke(); // Step 4) onUpdate
        }
        catch (SocketException e) {
            UnityEngine.Debug.Log($"Update() throws {e}");

            if (e.SocketErrorCode == SocketError.TimedOut) {
                mExitCode = SocketExitCode.NoResponse;
                Close();
            }
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"Update() throws {e}");
        }
    }


    // GetFrameRate() Coroutine
    private IEnumerator GetFrameRate() {
        float totalSeconds = 0f;
        float nUpdated     = 0f;

        while (totalSeconds < 1f) {
            totalSeconds += Time.deltaTime;
            nUpdated     += 1f;
            yield return null;
        }
        float frameRate = totalSeconds / nUpdated;

        if (mIsServer) {
            lock (lk1) {
                mServerFrameRate = frameRate;
                Monitor.Pulse(lk1);
            }
        }
        else {
            mClientFrameRate = frameRate;
            SystemMessage.ClientFrameRate();
        }
    }


    // EndOfFrame() Coroutine
    private IEnumerator EndOfFrame() {
        WaitForEndOfFrame ret = new WaitForEndOfFrame();

        while (true) {
            try {

                if (mStatus == SocketStatus.Connected) {
                    SystemMessage.EndOfFrame();
                    mCurrentFrame++;
                    FlushSendBuffer();
                }
            }
            catch (Exception e) {
                UnityEngine.Debug.Log($"EndOfFrame() throws {e}");
            }
            yield return ret;
        }
    }


    // EnqueueMessage() Method (overload 1)
    private void EnqueueMessage(int bytesRecv) {

        lock (lk2) {
            int totalSize     = 0;
            int queueCapacity = mMsgQueue.Length;

            // decode the messages received
            while (totalSize < bytesRecv) {
                totalSize += DecodeMessage(totalSize);
            }

            // if msgQueue have insufficent capacity
            if ((mQueueSz + bytesRecv) > queueCapacity) {
                int msgSize = mQueueSz - mQueueOffset; // size used actually in msgQueue

                if (bytesRecv <= (queueCapacity - msgSize)) {
                    Buffer.BlockCopy(mMsgQueue, mQueueOffset, mMsgQueue, 0, msgSize); // solve fragmentation
                }
                else {
                    byte[] newBuffer = new byte[queueCapacity * 2];
                    Buffer.BlockCopy(mMsgQueue, mQueueOffset, newBuffer, 0, msgSize);
                    mMsgQueue = newBuffer;
                }
                mQueueSz     = msgSize;
                mQueueOffset = 0;
            }

            // if there is a partial message
            if (totalSize > 1024) {
                totalSize -= 1025;
                mRecvBufferSz = 1024 - totalSize;

                Buffer.BlockCopy(mRecvBuffer, 0, mMsgQueue, mQueueSz, totalSize);        // copy the messages to msgQueue
                Buffer.BlockCopy(mRecvBuffer, totalSize, mRecvBuffer, 0, mRecvBufferSz); // pull the partial message to the head of recvBuffer
                mQueueSz += totalSize;
                return;
            }
            Buffer.BlockCopy(mRecvBuffer, 0, mMsgQueue, mQueueSz, bytesRecv); // copy the messages to msgQueue
            mRecvBufferSz = 0;
            mQueueSz += bytesRecv;
        }
    }


    // EnqueueMessage() Method (overload 2)
    private void EnqueueMessage(byte[] msg, int startIndex, int length) {
        lock (lk2) {
            Buffer.BlockCopy(msg, startIndex, mMsgQueue, mQueueSz, length); // copy the message to msgQueue directly
            mQueueSz += length;
        }
    }


    // IsPartialMessage() Method
    private bool IsPartialMessage(int startIndex, int totalSize) {
        return (startIndex + totalSize) > 1024;
    }


    // DecodeMessage() Method (used in SocketThread)
    private int DecodeMessage(int startIndex) {
        MessageType opcode    = (MessageType) mRecvBuffer[startIndex];
        int         msgLength = 1;

        switch (opcode) {

            // SetStatus message
            case MessageType.SetStatus: {
                if (IsPartialMessage(startIndex, 3)) {
                    return 1025;
                }
                msgLength = 3;
                break;
            }

            // Ping message
            case MessageType.Ping: {
                SystemMessage.Pong();
                break;
            }

            // Pong message
            case MessageType.Pong: {
                lock (lk1) {
                    mTimer.Stop();

                    double latencySample = mTimer.Elapsed.TotalMilliseconds * 0.0005; // milli to sec, and ping-pong to ping
                    mAvgLatency += latencySample;
                    Monitor.Pulse(lk1);  // pulse to `PingThread`
                    break;
                }
            }


            // ClientFrameRate message
            case MessageType.ClientFrameRate: {
                if (mIsServer) {
                    if (IsPartialMessage(startIndex, 5)) {
                        return 1025;
                    }
                    mClientFrameRate = BitConverter.ToSingle(
                      Host2Network(mRecvBuffer, startIndex + 1, 4), startIndex + 1
                    );

                    lock (lk1) {
                        Monitor.Pulse(lk1); // pulse to `PingThread`
                        msgLength = 5;
                    }
                }
                break;
            }


            // ServerFrameRate message
            case MessageType.ServerFrameRate: {
                break;
            }


            // SyncFrameRate message
            case MessageType.SyncFrameRate: {
                msgLength = 13;
                break;
            }


            // SetBreakpoint message
            case MessageType.SetBreakpoint: {
                break;
            }


            // EndOfFrame message
            case MessageType.EndOfFrame: {
                if(mEnterUpdate++ == 0) {
                    Monitor.Pulse(lk2);
                }
                break;
            }


            // CustomMessage message
            case MessageType.CustomMessage: {
                if (IsPartialMessage(startIndex, 3)) {
                    return 1025;
                }
                int length = BitConverter.ToInt16(
                  Host2Network(mRecvBuffer, startIndex + 1, 2), startIndex + 1
                );
                if (IsPartialMessage(startIndex, length + 3)) {
                    Host2Network(mRecvBuffer, startIndex + 1, 2);
                    return 1025;
                }
                msgLength = length + 3;
                break;
            }


            // Physics message
            case MessageType.Physics: {
                break;
            }


            // onUpdate message
            case MessageType.OnUpdate: {
                break;
            }


            // Simulator
            case MessageType.Simulator: {

                lock(lk1) {
                    Monitor.Pulse(lk1); // pulse to `PingThread`
                    msgLength = 2;
                }
                break;
            }
        };
        return msgLength;
    }


    // DecodeMessage2() Method (used in MainThread)
    private int DecodeMessage2(int startIndex) {
        MessageType opcode    = (MessageType) mMsgQueue[startIndex];
        int         msgLength = 1;

        switch (opcode) {

            // SetStatus message
            case MessageType.SetStatus: {
                SocketStatus newStatus = (SocketStatus)mMsgQueue[startIndex + 1];

                if (newStatus == SocketStatus.Closed) {
                    mExitCode = (SocketExitCode)mMsgQueue[startIndex + 2];
                    Close();
                }
                mStatus   = newStatus;
                msgLength = 3;
                break;
            }

            // Ping message
            case MessageType.Ping: {
                break;
            }


            // Pong message
            case MessageType.Pong: {
                break;
            }


            // ClientFrameRate message
            case MessageType.ClientFrameRate: {
                if (mIsServer) {
                    msgLength = 5;
                    break;
                }
                StartCoroutine(GetFrameRate());
                break;
            }


            // ServerFrameRate message
            case MessageType.ServerFrameRate: {
                StartCoroutine(GetFrameRate());
                break;
            }


            // SyncFrameRate message
            case MessageType.SyncFrameRate: {
                if (mIsServer == false) {
                    mDeltaTime = BitConverter.ToSingle(
                      Host2Network(mMsgQueue, startIndex + 1, 4), startIndex + 1
                    );
                    mAvgLatency = BitConverter.ToDouble(
                      Host2Network(mMsgQueue, startIndex + 5, 8), startIndex + 5
                    );
                    msgLength = 13;
                }
                Application.targetFrameRate = (int)(1 / Inst.mDeltaTime);
                break;
            }


            // SetBreakpoint / EndOfFrame message
            case MessageType.SetBreakpoint: 
            case MessageType.EndOfFrame: {
                int breakpoint = startIndex + 1;

                if (breakpoint < mQueueSz) {    // when message on next frame is in the msgQueue,
                    mQueueOffset = breakpoint; // then set breakpoint
                    msgLength    = mQueueSz;
                    break;
                }
                mQueueOffset = mQueueSz = 0; // otherwise clear the msgQueue
                break;
            }


            // CustomMessage message
            case MessageType.CustomMessage: {
                int length = BitConverter.ToInt16(mMsgQueue, startIndex + 1);
                msgLength = length + 3;

                if (mOnReadMessage != null) {
                    msgBegin = startIndex + 3;
                    msgEnd   = msgBegin + length;
                    mOnReadMessage.Invoke(new ReadOnlySpan<byte>(mMsgQueue, msgBegin, length));
                }
                break;
            }

            // Physics message
            case MessageType.Physics: {
                onFixedUpdate?.Invoke(); // Step 1-1) onFixedUpdate

                mServerSimulation = false;

                #if USE_PHYSICS2D //{
                   Physics2D.Simulate(mFixedDeltaTime); // Step 1-2) Simulate
                //}
                #else //{
                   Physics.Simulate(mFixedDeltaTime); // Step 1-2) Simulate
                //}
                #endif

                mServerSimulation = true;
                break;
            }


            // OnUpdate message
            case MessageType.OnUpdate: {
                onUpdate?.Invoke();
                break;
            }


            // Simulator message
            case MessageType.Simulator: {
                Simulator value = (Simulator) mMsgQueue[startIndex+1];
                mSimulator = value;
                break;
            }
        };
        return msgLength;
    }


    // ReadMessage() Method
    private void ReadMessage() {
        lock (lk2) {

            for (int i = mQueueOffset; i < mQueueSz;) {
                i += DecodeMessage2(i);
            }
            msgBegin = msgEnd = 0;

            if(mStatus != SocketStatus.Connected) {
                mQueueSz = 0;
            }
        }
    }


    // FlushSendBuffer() Method
    private void FlushSendBuffer() {
        mListener.Send(mSendBuffer, mSendBufferSz, SocketFlags.None);
        mSendBufferSz = 0;
    }


    // ServerCode() Method
    private void ServerCode() {
        Socket client = null;

        try {
            mListener                  = mServer.Accept(); // accept first client
            mListener.SendTimeout      = 10000;
            mSocketThread              = new Thread(ServerCode2);
            mSocketThread.IsBackground = true;
            mSocketThread.Start();

            while (true) {
                client = mServer.Accept(); // accept following clients

                try {
                    client.SendTimeout    = 5000;  // `Socket.Send()` and `Socket.Receive()` have to be returned in 5 seconds
                    client.ReceiveTimeout = 5000;
                    SystemMessage.Closed(client, SocketExitCode.RoomIsFull);
                    client.Receive(mRecvBuffer); // expected to return zero..
                }
                catch (Exception e) {
                    UnityEngine.Debug.Log($"ServerCode() throws {e}");
                }
                finally {
                    client.Close();
                }
            }
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"ServerCode() throws {e}");
        }
        finally {
            client?.Close();
            mServer.Close();
            Close();
            UnityEngine.Debug.Log("ServerCode is returned");
        }
    }


    // ServerCode2() Method
    private void ServerCode2() {
        try {
            SystemMessage.Connecting();  // set `mStatus` from `NotConnected` to `Connecting`
            mPingThread              = new Thread(PingCode);
            mPingThread.IsBackground = true;
            mPingThread.Start();

            while (true) {
                int bytesRecv = mListener.Receive(
                  mRecvBuffer, mRecvBufferSz, 1024 - mRecvBufferSz, SocketFlags.None
                );
                if (bytesRecv == 0) { // true if client called to `Socket.Close()`
                    break;
                }
                EnqueueMessage(bytesRecv); // push back the message in msgQueue
            }
        }
        catch (SocketException e) {
            UnityEngine.Debug.Log($"ServerCode2() throws {e}");

            if (e.SocketErrorCode == SocketError.TimedOut) {
                mExitCode = SocketExitCode.NoResponse;
            }
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"ServerCode2() throws {e}");
        }
        finally {
            mListener.Close();
            mServer.Close();
            UnityEngine.Debug.Log($"ServerCode2 is returned");
        }
    }


    // PingCode() Method
    private void PingCode() {
        try {

            lock (lk1) {
                SystemMessage.Simulator();

                for (int i = 0; i < 20; ++i) {
                    SystemMessage.Ping();
                }
                mAvgLatency *= 0.05; // divide by 20

                SystemMessage.ClientFrameRate();
                SystemMessage.ServerFrameRate();
            }

            Inst.mDeltaTime = Mathf.Max(
              (float)mAvgLatency,
              mClientFrameRate,
              mServerFrameRate
            );

            SystemMessage.SyncServerFrameRate();
            SystemMessage.SyncClientFrameRate();

            SystemMessage.Connected(false);
            SystemMessage.Connected(true);
        }
        catch (SocketException e) {
            UnityEngine.Debug.Log($"PingCode() throws {e}");

            if (e.SocketErrorCode == SocketError.TimedOut) {
                mExitCode = SocketExitCode.NoResponse;
            }
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"PingCode() throws {e}");
        }
        finally {
            UnityEngine.Debug.Log("PingCode is returned");
        }
    }


    // ClientCode() Method
    private void ClientCode() {
        try {
            try {
                IPAddress ipAddr          = IPAddress.Parse(mHostIP);
                IPEndPoint remoteEndPoint = new IPEndPoint(ipAddr, mPort);

                IAsyncResult result = mListener.BeginConnect(
                   remoteEndPoint, null, null
                );

                if (result.AsyncWaitHandle.WaitOne(30000) == false) {
                    mExitCode = SocketExitCode.Timeout;
                    Close();
                }
                mListener.EndConnect(result);
                SystemMessage.Connecting(); // set `mStatus` from `NotConnected` to `Connecting`
            }
            catch (Exception e) {
                UnityEngine.Debug.Log($"ClientCode() throws {e}");

                if (mExitCode == SocketExitCode.None) {
                    mExitCode = SocketExitCode.Denied;
                }
                return;
            }


            while (true) {
                int bytesRecv = mListener.Receive(
                  mRecvBuffer, mRecvBufferSz, 1024 - mRecvBufferSz, SocketFlags.None
                );
                if (bytesRecv == 0) { // true if client called to `Socket.Close()`
                    break;
                }
                EnqueueMessage(bytesRecv);
            }
        }
        catch (SocketException e) {
            UnityEngine.Debug.Log($"ClientCode() throws {e}");

            if (e.SocketErrorCode == SocketError.TimedOut) {
                mExitCode = SocketExitCode.NoResponse;
            }
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"ClientCode() throws {e}");
        }
        finally {
            mListener.Close();
            Close();
            UnityEngine.Debug.Log("ClientCode is returned");
        }
    }

    
    // ShortToBytes() Method
    //private static unsafe byte[] ShortToBytes(short value, byte[] msgBuffer, int startIndex) {
    //    byte* ptr = (byte*) &value;
    //
    //    msgBuffer[startIndex]   = ptr[0];
    //    msgBuffer[startIndex+1] = ptr[1];
    //    return msgBuffer;
    //}


    ////////////////////////
    // Public Methods     //
    ////////////////////////

    // CreateServer() Method
    public static bool CreateServer() {

        if (Inst == null || Inst.mStatus != SocketStatus.Closed) {
            return false;
        }
        try {
            Inst.mSocketThread?.Join();  // wait for the end of `SocketThread`
            Inst.mServerThread?.Join();  // wait for the end of `ServerThread`
            Inst.mPingThread?.Join();    // wait for the end of `PingThread`

            IPHostEntry hostEntry = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress   localIP   = null;

            foreach (IPAddress ip in hostEntry.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) { // find IPv4 address in `hostEntry`
                    localIP = ip;
                    break;
                }
            }

            Inst.mServer = new Socket(
              localIP.AddressFamily,
              SocketType.Stream,
              ProtocolType.Tcp
            );

            IPEndPoint localEndPoint = new IPEndPoint(localIP, Inst.mPort);
            Inst.mServer.Bind(localEndPoint);
            Inst.mServer.Listen(10);

            Inst.msgBegin          = 0;
            Inst.msgEnd            = 0;
            Inst.mRecvBufferSz     = 0;
            Inst.mSendBufferSz     = 0;
            Inst.mQueueSz          = 0;
            Inst.mQueueOffset      = 0;
            Inst.mEnterUpdate      = 1;  // unlike CreateClient(), this method increases mEnterUpdate by 1
            Inst.mEnterFixedUpdate = 0f;
            Inst.mServerSimulation = true;

            Inst.mHostIP       = localIP.ToString();
            Inst.mExitCode     = SocketExitCode.None;
            Inst.mIsServer     = true;
            Inst.mStatus       = SocketStatus.NotConnected;
            Inst.mCurrentFrame = 0;

            Inst.mServerThread              = new Thread(Inst.ServerCode);
            Inst.mServerThread.IsBackground = true;
            Inst.mServerThread.Start();

            #if DEVELOPMENT_BUILD //{
               File.WriteAllText("./ServerLog.txt", "");
            //}
            #endif

            return true;
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"CreateServer() throws {e}");

            if (Inst.mServer != null) {
                Inst.mServer.Close();   // close the socket,`mServer`
                Inst.mServer.Dispose(); // release the all resources of `mServer`
            }
            return false;
        }
    }


    // CreateClient() Method
    public static bool CreateClient(string hostIP) {

        if (Inst == null || Inst.mStatus != SocketStatus.Closed) {
            return false;
        }

        try {
            Inst.mSocketThread?.Join();  // wait for the end of `SocketThread`
            Inst.mServerThread?.Join();  // wait for the end of `ServerThread`
            Inst.mPingThread?.Join();    // wait for the end of `PingThread`

            IPAddress ipAddr = IPAddress.Parse(hostIP); // parse `hostIP` to `IPAddress`

            Inst.mListener = new Socket(
              ipAddr.AddressFamily,
              SocketType.Stream,
              ProtocolType.Tcp
            );

            Inst.mListener.SendTimeout = 10000; // the `Socket.Send()` have to be returned in 10 seconds, 
            Inst.msgBegin              = 0;     // otherwise throws the exception
            Inst.msgEnd                = 0;
            Inst.mRecvBufferSz         = 0;
            Inst.mSendBufferSz         = 0;
            Inst.mQueueSz              = 0;
            Inst.mQueueOffset          = 0;
            Inst.mEnterUpdate          = 0; 
            Inst.mEnterFixedUpdate     = 0f;

            Inst.mHostIP       = hostIP;
            Inst.mExitCode     = SocketExitCode.None;
            Inst.mIsServer     = false;
            Inst.mStatus       = SocketStatus.NotConnected;
            Inst.mCurrentFrame = 0;

            Inst.mSocketThread              = new Thread(Inst.ClientCode);
            Inst.mSocketThread.IsBackground = true;
            Inst.mSocketThread.Start();

            #if DEVELOPMENT_BUILD //{
               File.WriteAllText("./ClientLog.txt", $"");
            //}
            #endif

            return true;
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"CreateClient() throws {e}");

            if (Inst.mListener != null) {
                Inst.mListener.Close();   // close the socket,`mListener`
                Inst.mListener.Dispose(); // release the all resources of `mListener`
            }
            return false;
        }
    }


    // Close() Method
    public static void Close() {
        if (Inst == null || Inst.mStatus == SocketStatus.Closed) {
            return;
        }
        try {
            if(Inst.mListener != null && Inst.mListener.Connected) Inst.mListener.Shutdown(SocketShutdown.Both);
            if(Inst.mServer   != null && Inst.mServer.Connected)   Inst.mServer.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) {
            UnityEngine.Debug.Log($"Close() throws {e}");
        }
        finally {
            Inst.mListener?.Close();
            Inst.mServer?.Close();
            SystemMessage.Closed();

            lock (Inst.lk1) {
                Monitor.Pulse(Inst.lk1);
            }
            lock (Inst.lk2) {

                #if DEVELOPMENT_BUILD //{

                    if (Inst.mLog.Length > 0) {
                       string path = isServer ? "./ServerLog.txt" : "./ClientLog.txt";
                       File.AppendAllText(path, Inst.mLog.ToString());
                       Inst.mLog.Clear();
                    }   
                //}
                #endif

                Inst.mEnterUpdate = 4;
                Monitor.Pulse(Inst.lk2);
            }
        }
    }


    // SendMessage() Method
    public static void SendMessage(byte[] msg, int startIndex, int length) {
        if (Inst == null || Inst.mStatus != SocketStatus.Connected) {
            return;
        }

        byte[] sendBuffer = Inst.mSendBuffer;
        int bufferSize    = Inst.mSendBufferSz;
        short msgLength   = (short) Mathf.Clamp(length, 0, 1021);

        if ((bufferSize + msgLength) > 1021) {
            Inst.FlushSendBuffer();
        }

        sendBuffer[bufferSize] = (byte) MessageType.CustomMessage;
        Host2Network(BitConverter.GetBytes(msgLength), 0, 2).CopyTo(sendBuffer, bufferSize + 1);
        //Host2Network(
        //    ShortToBytes(msgLength, sendBuffer, bufferSize+1), bufferSize+1, 2
        //);
        Buffer.BlockCopy(msg, startIndex, sendBuffer, bufferSize + 3, msgLength);
        Inst.mSendBufferSz += msgLength + 3;
    }


    // Host2Network() Method
    public static byte[] Host2Network(byte[] msg, int startIndex, int length) {
        if (BitConverter.IsLittleEndian) {
            Array.Reverse(msg, startIndex, length);
        }
        return msg;
    }


    // Network2Host() Method
    public static void Network2Host(uint startIndex, uint length) {
        if(Inst.msgBegin + startIndex + length > Inst.msgEnd) {
            return;
        }
        Host2Network(Inst.mMsgQueue, Inst.msgBegin + (int) startIndex, (int) length);
    }


    // Log() Method
    public static void Log(string message) {

        #if DEVELOPMENT_BUILD // {

           if(Inst.mStatus != SocketStatus.Connected) {
              return;
           }

           int logLength = Inst.mLog.Length;
           int msgLength = message.Length;
           int capacity  = Inst.mLog.Capacity;

           if(logLength + msgLength >= capacity) {
              string path = isServer ? "./ServerLog.txt" : "./ClientLog.txt";
              File.AppendAllText(path, Inst.mLog.ToString());
              File.AppendAllText(path, message);
              Inst.mLog.Clear();
              return;
           }
           
           Inst.mLog.AppendLine(message);
        // }
        #endif
    }


    ///////////////////////
    // Getter And Setter //
    ///////////////////////

    // port getter/setter
    public static int port {
        get { return Inst.mPort;  }
        set { Inst.mPort = value; }
    }


    // hostIP getter
    public static string hostIP {
        get { return Inst.mHostIP; }
    }


    // isServer getter
    public static bool isServer {
        get { return Inst.mIsServer; }
    }


    // status getter
    public static SocketStatus status {
        get { return Inst.mStatus; }
    }


    // exitCode getter
    public static SocketExitCode exitCode {
        get { return Inst.mExitCode; }
    }


    // deltaTime getter
    public static float deltaTime {
        get { return Inst.mDeltaTime; }
    }


    // fixedDeltaTime getter
    public static float fixedDeltaTime {
        get { return Inst.mFixedDeltaTime; }
    }


    // latency getter
    public static double latency {
        get { return Inst.mAvgLatency; }
    }


    // currentFrame getter
    public static int currentFrame {
        get { return Inst.mCurrentFrame; }
    }


    // simulator getter and setter
    public static Simulator simulator {
        get { return Inst.mSimulator;}
        set {
            
            if(Inst.mStatus == SocketStatus.Closed) {
                Inst.mSimulator = value;
            }
        }
    }


    // serverSimulation getter
    public static bool serverSimulation {
        get { return Inst.mServerSimulation; }
    }


    // onUpdate getter/setter
    public static UpdateFunction onUpdate {
        get { return Inst.mOnUpdate; }
        set { Inst.mOnUpdate = value; }
    }


    // onFixedUpdate getter/setter
    public static UpdateFunction onFixedUpdate {
        get { return Inst.mOnFixedUpdate; }
        set { Inst.mOnFixedUpdate = value; }
    }


    // onReadMessage getter/setter
    public static MessageHandler onReadMessage {
        get { return Inst.mOnReadMessage; }
        set { Inst.mOnReadMessage = value; }
    }


    /////////////////////////
    // class SystemMessage //
    /////////////////////////

    private class SystemMessage {

        //  +0                +3                 +8               +21              +32
        // -+------------------+------------------+----------------+----------------+-
        //  | for ServerThread | for SocketThread | for PingThread | for MainThread |
        // -+------------------+------------------+----------------+----------------+-
        //                                  msgBuffer

        private static byte[] msgBuffer = new byte[32];


        // Closed() Method (overload1, used in ServerThread)
        public static void Closed(Socket sock, SocketExitCode exitCode) {
            msgBuffer[0] = (byte)MessageType.SetStatus;
            msgBuffer[1] = (byte)SocketStatus.Closed;
            msgBuffer[2] = (byte)exitCode;

            sock.Send(msgBuffer, 0, 3, SocketFlags.None);
        }


        // Closed() Method (overload2, used in MainThread)
        public static void Closed() {
            msgBuffer[21] = (byte) MessageType.SetStatus;
            msgBuffer[22] = (byte) SocketStatus.Closed;
            msgBuffer[23] = (byte) Inst.mExitCode;

            Inst.EnqueueMessage(msgBuffer, 21, 3);
        }


        // Connected() Method (used in PingThread)
        public static void Connected(bool toClient) {
            msgBuffer[8]  = (byte)MessageType.SetStatus;
            msgBuffer[9]  = (byte)SocketStatus.Connected;
            msgBuffer[11] = (byte)MessageType.SetBreakpoint;
            msgBuffer[12] = (byte)MessageType.SetBreakpoint;

            if(toClient == false) {
                Inst.EnqueueMessage(msgBuffer, 8, 5); // server's mEnterUpdate is already 1, so the server enter to the first frame immediately
            }                                                         
            else {
                Inst.mListener.Send(msgBuffer, 8, 5, SocketFlags.None); // client's mEnterUpdate is zero before the server send `E.O.F` message.
            }                                                          
        }


        // Connecting() Method (used in SocketThread)
        public static void Connecting() {
            msgBuffer[3] = (byte)MessageType.SetStatus;
            msgBuffer[4] = (byte)SocketStatus.Connecting;

            Inst.EnqueueMessage(msgBuffer, 3, 3);
        }


        // Ping() Method (used in PingThread)
        public static void Ping() {
            msgBuffer[8] = (byte)MessageType.Ping;
            Inst.mTimer.Reset();
            Inst.mTimer.Start();
            Inst.mListener.Send(msgBuffer, 8, 1, SocketFlags.None);

            if (Monitor.Wait(Inst.lk1, 10000) == false) {
                throw new SocketException((int)SocketError.TimedOut);
            }
        }


        // Pong() Method (used in SocketThread)
        public static void Pong() {
            msgBuffer[3] = (byte)MessageType.Pong;
            Inst.mListener.Send(msgBuffer, 3, 1, SocketFlags.None);
        }


        // ClientFrameRate() Method (used in PingThread or MainThread)
        public static void ClientFrameRate() {
            if (Inst.mIsServer) {
                msgBuffer[8] = (byte)MessageType.ClientFrameRate;
                Inst.mListener.Send(msgBuffer, 8, 1, SocketFlags.None);

                if (Monitor.Wait(Inst.lk1, 10000) == false) {
                    Inst.mExitCode = SocketExitCode.NoResponse;
                    throw new SocketException((int)SocketError.TimedOut);
                }
            }
            else {
                msgBuffer[21] = (byte)MessageType.ClientFrameRate;
                Host2Network(BitConverter.GetBytes(Inst.mClientFrameRate), 0, 4).CopyTo(msgBuffer, 22);
                Inst.mListener.Send(msgBuffer, 21, 5, SocketFlags.None);
            }
        }


        // ServerFrameRate() Method (used in PingThread)
        public static void ServerFrameRate() {
            msgBuffer[8] = (byte)MessageType.ServerFrameRate;
            Inst.EnqueueMessage(msgBuffer, 8, 1);

            if (Monitor.Wait(Inst.lk1, 10000) == false) {
                throw new SocketException((int)SocketError.TimedOut);
            }
        }


        // SyncServerFrameRate() Method (used in PingThread)
        public static void SyncServerFrameRate() {
            msgBuffer[8] = (byte)MessageType.SyncFrameRate;
            Inst.EnqueueMessage(msgBuffer, 8, 1);
        }


        // SyncClientFrameRate() Method (used in PingThread)
        public static void SyncClientFrameRate() {
            msgBuffer[8] = (byte)MessageType.SyncFrameRate;
            Host2Network(BitConverter.GetBytes(Inst.mDeltaTime), 0, 4).CopyTo(msgBuffer, 9);
            Host2Network(BitConverter.GetBytes(Inst.mAvgLatency), 0, 8).CopyTo(msgBuffer, 13);
            Inst.mListener.Send(msgBuffer, 8, 13, SocketFlags.None);
        }


        // EndOfFrame() Method (used in MainThread)
        public static void EndOfFrame() {
            if (Inst.mSendBufferSz > 1023) {
                Inst.FlushSendBuffer();
            }
            Inst.mSendBuffer[Inst.mSendBufferSz++] = (byte) MessageType.EndOfFrame;
        }


        // Simulator() Method (used in PingThread)
        public static void Simulator() {
            msgBuffer[8] = (byte) MessageType.Simulator;
            msgBuffer[9] = (byte) Inst.mSimulator;
            Inst.mListener.Send(msgBuffer, 8, 2, SocketFlags.None);
        }


        // Physics() Method (used in MainThread)
        public static void Physics() {
            if (Inst.mSendBufferSz > 1023) {
                Inst.FlushSendBuffer();
            }
            Inst.mSendBuffer[Inst.mSendBufferSz++] = (byte)MessageType.Physics;
        }


        // OnUpdate() Method (used in MainThread)
        public static void OnUpdate() {
            if (Inst.mSendBufferSz > 1022) {
                Inst.FlushSendBuffer();
            }
            Inst.mSendBuffer[Inst.mSendBufferSz]   = (byte)MessageType.OnUpdate;
            Inst.mSendBuffer[Inst.mSendBufferSz+1] = (byte)MessageType.SetBreakpoint;
            Inst.mSendBufferSz += 2;
        }
    };
};
