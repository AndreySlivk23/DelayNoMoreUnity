using UnityEngine;
using System;
using shared;
using static shared.Battle;
using static shared.CharacterState;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using Google.Protobuf.Collections;

public class OnlineMapController : AbstractMapController {
    Task wsTask, udpTask;
    CancellationTokenSource wsCancellationTokenSource;
    CancellationToken wsCancellationToken;
    int inputFrameUpsyncDelayTolerance;
    WsResp wsRespHolder;
    public NetworkDoctorInfo networkInfoPanel;
    int clientAuthKey;
    bool shouldLockStep = false;

    private RoomDownsyncFrame mockStartRdf() {
        var playerStartingCollisionSpacePositions = new Vector[roomCapacity];
        var (defaultColliderRadius, _) = PolygonColliderCtrToVirtualGridPos(12, 0);

        var grid = this.GetComponentInChildren<Grid>();
        foreach (Transform child in grid.transform) {
            switch (child.gameObject.name) {
                case "Barrier":
                    int i = 0;
                    foreach (Transform barrierChild in child) {
                        var barrierTileObj = barrierChild.gameObject.GetComponent<SuperTiled2Unity.SuperObject>();
                        var (tiledRectCx, tiledRectCy) = (barrierTileObj.m_X + barrierTileObj.m_Width * 0.5f, barrierTileObj.m_Y + barrierTileObj.m_Height * 0.5f);
                        var (rectCx, rectCy) = TiledLayerPositionToCollisionSpacePosition(tiledRectCx, tiledRectCy, spaceOffsetX, spaceOffsetY);
                        /*
                         [WARNING] 
                        
                        The "Unity World (0, 0)" is aligned with the top-left corner of the rendered "TiledMap (via SuperMap)".

                        It's noticeable that all the "Collider"s in "CollisionSpace" must be of positive coordinates to work due to the implementation details of "resolv". Thus I'm using a "Collision Space (0, 0)" aligned with the bottom-left of the rendered "TiledMap (via SuperMap)". 
                        */
                        var barrierCollider = NewRectCollider(rectCx, rectCy, barrierTileObj.m_Width, barrierTileObj.m_Height, 0, 0, 0, 0, 0, 0, null);
                        // Debug.Log(String.Format("new barrierCollider=[X: {0}, Y: {1}, Width: {2}, Height: {3}]", barrierCollider.X, barrierCollider.Y, barrierCollider.W, barrierCollider.H));
                        collisionSys.AddSingle(barrierCollider);
                        staticRectangleColliders[i++] = barrierCollider;
                    }
                    break;
                case "PlayerStartingPos":
                    int j = 0;
                    foreach (Transform playerPosChild in child) {
                        var playerPosTileObj = playerPosChild.gameObject.GetComponent<SuperTiled2Unity.SuperObject>();
                        var (playerCx, playerCy) = TiledLayerPositionToCollisionSpacePosition(playerPosTileObj.m_X, playerPosTileObj.m_Y, spaceOffsetX, spaceOffsetY);
                        playerStartingCollisionSpacePositions[j] = new Vector(playerCx, playerCy);
                        /// Debug.Log(String.Format("new playerStartingCollisionSpacePositions[i:{0}]=[X:{1}, Y:{2}]", j, playerCx, playerCy));
                        j++;
                        if (j >= roomCapacity) break;
                    }
                    break;
                default:
                    break;
            }
        }

        var startRdf = NewPreallocatedRoomDownsyncFrame(roomCapacity, 128);
        startRdf.Id = Battle.DOWNSYNC_MSG_ACT_BATTLE_START;
        startRdf.ShouldForceResync = false;
        for (int i = 0; i < roomCapacity; i++) {
            var collisionSpacePosition = playerStartingCollisionSpacePositions[i];
            var (playerWx, playerWy) = CollisionSpacePositionToWorldPosition(collisionSpacePosition.X, collisionSpacePosition.Y, spaceOffsetX, spaceOffsetY);
            spawnPlayerNode(i + 1, playerWx, playerWy);

            var characterSpeciesId = 0;
            var playerCharacter = Battle.characters[characterSpeciesId];

            var playerInRdf = startRdf.PlayersArr[i];
            var (playerVposX, playerVposY) = PolygonColliderCtrToVirtualGridPos(collisionSpacePosition.X, collisionSpacePosition.Y); // World and CollisionSpace coordinates have the same scale, just translated
            playerInRdf.JoinIndex = i + 1;
            playerInRdf.VirtualGridX = playerVposX;
            playerInRdf.VirtualGridY = playerVposY;
            playerInRdf.RevivalVirtualGridX = playerVposX;
            playerInRdf.RevivalVirtualGridY = playerVposY;
            playerInRdf.Speed = playerCharacter.Speed;
            playerInRdf.ColliderRadius = (int)defaultColliderRadius;
            playerInRdf.CharacterState = InAirIdle1NoJump;
            playerInRdf.FramesToRecover = 0;
            playerInRdf.DirX = (1 == playerInRdf.JoinIndex ? 2 : -2);
            playerInRdf.DirY = 0;
            playerInRdf.VelX = 0;
            playerInRdf.VelY = 0;
            playerInRdf.InAir = true;
            playerInRdf.OnWall = false;
            playerInRdf.Hp = 100;
            playerInRdf.MaxHp = 100;
            playerInRdf.SpeciesId = characterSpeciesId;
        }

        return startRdf;
    }

    void pollAndHandleWsRecvBuffer() {
        while (WsSessionManager.Instance.recvBuffer.TryDequeue(out wsRespHolder)) {
            //Debug.Log(String.Format("Handling wsResp in main thread: {0}", wsRespHolder));
            switch (wsRespHolder.Act) {
                case shared.Battle.DOWNSYNC_MSG_WS_CLOSED:
                    Debug.Log("Handling WsSession closed in main thread.");
                    WsSessionManager.Instance.ClearCredentials();
                    SceneManager.LoadScene("LoginScene", LoadSceneMode.Single);
                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_BATTLE_COLLIDER_INFO:
                    Debug.Log(String.Format("Handling DOWNSYNC_MSG_ACT_BATTLE_COLLIDER_INFO in main thread"));
                    inputFrameUpsyncDelayTolerance = wsRespHolder.BciFrame.InputFrameUpsyncDelayTolerance;
                    selfPlayerInfo.Id = WsSessionManager.Instance.GetPlayerId();
                    if (wsRespHolder.BciFrame.BoundRoomCapacity != roomCapacity) {
                        roomCapacity = wsRespHolder.BciFrame.BoundRoomCapacity;
                        preallocateHolders();
                    }
                    resetCurrentMatch();
                    clientAuthKey = wsRespHolder.BciFrame.BattleUdpTunnel.AuthKey;
                    selfPlayerInfo.JoinIndex = wsRespHolder.PeerJoinIndex;
                    var reqData = new WsReq {
                        PlayerId = selfPlayerInfo.Id,
                        Act = shared.Battle.UPSYNC_MSG_ACT_PLAYER_COLLIDER_ACK,
                        JoinIndex = selfPlayerInfo.JoinIndex
                    };
                    WsSessionManager.Instance.senderBuffer.Enqueue(reqData);
                    Debug.Log("Sent UPSYNC_MSG_ACT_PLAYER_COLLIDER_ACK.");

                    var initialPeerUdpAddrList = wsRespHolder.Rdf.PeerUdpAddrList;
                    udpTask = Task.Run(async () => {
                        var holePuncher = new WsReq {
                            PlayerId = selfPlayerInfo.Id,
                            Act = shared.Battle.UPSYNC_MSG_ACT_HOLEPUNCH,
                            JoinIndex = selfPlayerInfo.JoinIndex,
                            AuthKey = clientAuthKey
                        };
                        await UdpSessionManager.Instance.openUdpSession(roomCapacity, selfPlayerInfo.JoinIndex, initialPeerUdpAddrList, holePuncher, wsCancellationToken);
                    });

                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_PLAYER_ADDED_AND_ACKED:
                    // TODO
                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_BATTLE_START:
                    Debug.Log("Handling DOWNSYNC_MSG_ACT_BATTLE_START in main thread.");
                    var startRdf = mockStartRdf();
                    onRoomDownsyncFrame(startRdf, null);
                    enableBattleInput(true);
                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_BATTLE_STOPPED:
                    enableBattleInput(false);
                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_INPUT_BATCH:
                    // Debug.Log("Handling DOWNSYNC_MSG_ACT_INPUT_BATCH in main thread.");
                    onInputFrameDownsyncBatch(wsRespHolder.InputFrameDownsyncBatch);
                    break;
                case shared.Battle.DOWNSYNC_MSG_ACT_PEER_UDP_ADDR:
                    var newPeerUdpAddrList = wsRespHolder.Rdf.PeerUdpAddrList;
                    Debug.Log(String.Format("Handling DOWNSYNC_MSG_ACT_PEER_UDP_ADDR in main thread, newPeerUdpAddrList: {0}", newPeerUdpAddrList));
                    UdpSessionManager.Instance.updatePeerAddr(roomCapacity, selfPlayerInfo.JoinIndex, newPeerUdpAddrList);
                    break;
                default:
                    break;
            }
        }
    }

    void pollAndHandleUdpRecvBuffer() {
        WsReq wsReqHolder;
        while (UdpSessionManager.Instance.recvBuffer.TryDequeue(out wsReqHolder)) {
            // Debug.Log(String.Format("Handling udpSession wsReq in main thread: {0}", wsReqHolder));
            onPeerInputFrameUpsync(wsReqHolder.JoinIndex, wsReqHolder.InputFrameUpsyncBatch);
        }
    }

    void Start() {
        Physics.autoSimulation = false;
        Physics2D.simulationMode = SimulationMode2D.Script;
        selfPlayerInfo = new PlayerDownsync();
        inputFrameUpsyncDelayTolerance = TERMINATING_INPUT_FRAME_ID;
        Application.targetFrameRate = 60;

        enableBattleInput(false);

        // [WARNING] We should init "wsCancellationTokenSource", "wsCancellationToken" and "wsTask" only once during the whole lifecycle of this "OnlineMapController", even if the init signal is later given by a "button onClick" instead of "Start()".
        wsCancellationTokenSource = new CancellationTokenSource();
        wsCancellationToken = wsCancellationTokenSource.Token;

        // [WARNING] Must avoid blocking MainThread. See "GOROUTINE_TO_ASYNC_TASK.md" for more information.
        Debug.LogWarning(String.Format("About to start ws session: thread id={0} a.k.a. the MainThread.", Thread.CurrentThread.ManagedThreadId));

        wsTask = Task.Run(async () => {
            Debug.LogWarning(String.Format("About to start ws session within Task.Run(async lambda): thread id={0}.", Thread.CurrentThread.ManagedThreadId));

            await wsSessionTaskAsync();

            Debug.LogWarning(String.Format("Ends ws session within Task.Run(async lambda): thread id={0}.", Thread.CurrentThread.ManagedThreadId));

            if (null != udpTask) {
                if (null != wsCancellationTokenSource && !wsCancellationTokenSource.IsCancellationRequested) {
                    Debug.LogWarning(String.Format("Calling wsCancellationTokenSource.Cancel() for udpSession.", Thread.CurrentThread.ManagedThreadId));
                    wsCancellationTokenSource.Cancel();
                }

                Debug.LogWarning(String.Format("Calling UdpSessionManager.Instance.closeUdpSession()."));
                UdpSessionManager.Instance.closeUdpSession(); // Would effectively end "ReceiveAsync" if it's blocking "Receive" loop in udpTask.

            }
        });

        //wsTask = Task.Run(wsSessionActionAsync); // This doesn't make "await wsTask" synchronous in "OnDestroy".

        //wsSessionActionAsync(); // [c] no immediate thread switch till AFTER THE FIRST AWAIT
        //_ = wsSessionTaskAsync(); // [d] no immediate thread switch till AFTER THE FIRST AWAIT

        Debug.LogWarning(String.Format("Started ws session: thread id={0} a.k.a. the MainThread.", Thread.CurrentThread.ManagedThreadId));
    }

    private async Task wsSessionTaskAsync() {
        Debug.LogWarning(String.Format("In ws session TASK but before first await: thread id={0}.", Thread.CurrentThread.ManagedThreadId));
        string wsEndpoint = Env.Instance.getWsEndpoint();
        await WsSessionManager.Instance.ConnectWsAsync(wsEndpoint, wsCancellationToken, wsCancellationTokenSource);
        Debug.LogWarning(String.Format("In ws session TASK and after first await: thread id={0}.", Thread.CurrentThread.ManagedThreadId));
    }

    private async void wsSessionActionAsync() {
        Debug.LogWarning(String.Format("In ws session ACTION but before first await: thread id={0}.", Thread.CurrentThread.ManagedThreadId));
        string wsEndpoint = Env.Instance.getWsEndpoint();
        await WsSessionManager.Instance.ConnectWsAsync(wsEndpoint, wsCancellationToken, wsCancellationTokenSource);
        Debug.LogWarning(String.Format("In ws session ACTION and after first await: thread id={0}.", Thread.CurrentThread.ManagedThreadId));
    }

    // Update is called once per frame
    void Update() {
        try {
            pollAndHandleWsRecvBuffer();
            pollAndHandleUdpRecvBuffer();
            if (shouldLockStep) {
                NetworkDoctor.Instance.LogLockedStepCnt();
                shouldLockStep = false;
                return; // An early return here only stops "inputFrameIdFront" from incrementing, "int[] lastIndividuallyConfirmedInputFrameId" would keep increasing by the "pollXxx" calls above. 
            }
            doUpdate();
            var (tooFastOrNot, _, sendingFps, srvDownsyncFps, peerUpsyncFps, rollbackFrames, lockedStepsCnt, udpPunchedCnt) = NetworkDoctor.Instance.IsTooFast(roomCapacity, selfPlayerInfo.JoinIndex, lastIndividuallyConfirmedInputFrameId, renderFrameIdLagTolerance);
            shouldLockStep = tooFastOrNot;
            networkInfoPanel.SetValues(sendingFps, srvDownsyncFps, peerUpsyncFps, lockedStepsCnt, rollbackFrames, udpPunchedCnt);
        } catch (Exception ex) {
            Debug.LogError(String.Format("Error during OnlineMap.Update: {0}", ex));
            onBattleStopped();
        }
    }

    protected override bool shouldSendInputFrameUpsyncBatch(ulong prevSelfInput, ulong currSelfInput, int currInputFrameId) {
        /*
        For a 2-player-battle, this "shouldUpsyncForEarlyAllConfirmedOnBackend" can be omitted, however for more players in a same battle, to avoid a "long time non-moving player" jamming the downsync of other moving players, we should use this flag.

        When backend implements the "force confirmation" feature, we can have "false == shouldUpsyncForEarlyAllConfirmedOnBackend" all the time as well!
        */

        var shouldUpsyncForEarlyAllConfirmedOnBackend = (currInputFrameId - lastUpsyncInputFrameId >= inputFrameUpsyncDelayTolerance);
        return shouldUpsyncForEarlyAllConfirmedOnBackend || (prevSelfInput != currSelfInput);
    }

    protected override void sendInputFrameUpsyncBatch(int latestLocalInputFrameId) {
        // [WARNING] Why not just send the latest input? Because different player would have a different "latestLocalInputFrameId" of changing its last input, and that could make the server not recognizing any "all-confirmed inputFrame"!
        var inputFrameUpsyncBatch = new RepeatedField<InputFrameUpsync>();
        var batchInputFrameIdSt = lastUpsyncInputFrameId + 1;
        if (batchInputFrameIdSt < inputBuffer.StFrameId) {
            // Upon resync, "this.lastUpsyncInputFrameId" might not have been updated properly.
            batchInputFrameIdSt = inputBuffer.StFrameId;
        }
        NetworkDoctor.Instance.LogInputFrameIdFront(latestLocalInputFrameId);
        NetworkDoctor.Instance.LogSending(batchInputFrameIdSt, latestLocalInputFrameId);

        for (var i = batchInputFrameIdSt; i <= latestLocalInputFrameId; i++) {
            var (res1, inputFrameDownsync) = inputBuffer.GetByFrameId(i);
            if (false == res1 || null == inputFrameDownsync) {
                Debug.LogError(String.Format("sendInputFrameUpsyncBatch: recentInputCache is NOT having i={0}, latestLocalInputFrameId={1}", i, latestLocalInputFrameId));
            } else {
                var inputFrameUpsync = new InputFrameUpsync {
                    InputFrameId = i,
                    Encoded = inputFrameDownsync.InputList[selfPlayerInfo.JoinIndex - 1]
                };
                inputFrameUpsyncBatch.Add(inputFrameUpsync);
            }
        }

        var reqData = new WsReq {
            PlayerId = selfPlayerInfo.Id,
            Act = Battle.UPSYNC_MSG_ACT_PLAYER_CMD,
            JoinIndex = selfPlayerInfo.JoinIndex,
            AckingInputFrameId = lastAllConfirmedInputFrameId,
            AuthKey = clientAuthKey
        };
        reqData.InputFrameUpsyncBatch.AddRange(inputFrameUpsyncBatch);

        WsSessionManager.Instance.senderBuffer.Enqueue(reqData);
        UdpSessionManager.Instance.senderBuffer.Enqueue(reqData);
        lastUpsyncInputFrameId = latestLocalInputFrameId;
    }

    protected void onPeerInputFrameUpsync(int peerJoinIndex, RepeatedField<InputFrameUpsync> batch) {
        if (null == batch) {
            return;
        }
        if (null == inputBuffer) {
            return;
        }
        if (ROOM_STATE_IN_BATTLE != battleState) {
            return;
        }

        int effCnt = 0, batchCnt = batch.Count;
        int firstPredictedYetIncorrectInputFrameId = TERMINATING_INPUT_FRAME_ID;
        for (int k = 0; k < batchCnt; k++) {
            var inputFrameUpsync = batch[k];
            int inputFrameId = inputFrameUpsync.InputFrameId;
            ulong peerEncodedInput = inputFrameUpsync.Encoded;

            if (inputFrameId <= lastAllConfirmedInputFrameId) {
                // [WARNING] Don't reject it by "inputFrameId <= lastIndividuallyConfirmedInputFrameId[peerJoinIndex-1]", the arrival of UDP packets might not reserve their sending order!
                // Debug.Log(String.Format("Udp upsync inputFrameId={0} from peerJoinIndex={1} is ignored because it's already confirmed#1! lastAllConfirmedInputFrameId={2}", inputFrameId, peerJoinIndex, lastAllConfirmedInputFrameId));
                continue;
            }
            ulong peerJoinIndexMask = ((ulong)1 << (peerJoinIndex - 1));
            getOrPrefabInputFrameUpsync(inputFrameId, false, prefabbedInputListHolder); // Make sure that inputFrame exists locally
            var (res1, existingInputFrame) = inputBuffer.GetByFrameId(inputFrameId);
            if (!res1 || null == existingInputFrame) {
                throw new ArgumentNullException(String.Format("inputBuffer doesn't contain inputFrameId={0} after prefabbing! Now inputBuffer StFrameId={1}, EdFrameId={2}, Cnt/N={3}/{4}", inputFrameId, inputBuffer.StFrameId, inputBuffer.EdFrameId, inputBuffer.Cnt, inputBuffer.N));
            }
            ulong existingConfirmedList = existingInputFrame.ConfirmedList;
            if (0 < (existingConfirmedList & peerJoinIndexMask)) {
                // Debug.Log(String.Format("Udp upsync inputFrameId={0} from peerJoinIndex={1} is ignored because it's already confirmed#2! lastAllConfirmedInputFrameId={2}, existingInputFrame={3}", inputFrameId, peerJoinIndex, lastAllConfirmedInputFrameId, existingInputFrame));
                continue;
            }
            if (inputFrameId > lastIndividuallyConfirmedInputFrameId[peerJoinIndex - 1]) {
                lastIndividuallyConfirmedInputFrameId[peerJoinIndex - 1] = inputFrameId;
                lastIndividuallyConfirmedInputList[peerJoinIndex - 1] = peerEncodedInput;
            }
            effCnt += 1;

            bool isPeerEncodedInputUpdated = (existingInputFrame.InputList[peerJoinIndex - 1] != peerEncodedInput);
            existingInputFrame.InputList[peerJoinIndex - 1] = peerEncodedInput;
            existingInputFrame.ConfirmedList = (existingConfirmedList | peerJoinIndexMask);
            if (
              TERMINATING_INPUT_FRAME_ID == firstPredictedYetIncorrectInputFrameId
              &&
              isPeerEncodedInputUpdated
            ) {
                firstPredictedYetIncorrectInputFrameId = inputFrameId;
            }
        }
        NetworkDoctor.Instance.LogPeerInputFrameUpsync(batch[0].InputFrameId, batch[batchCnt - 1].InputFrameId);
        _handleIncorrectlyRenderedPrediction(firstPredictedYetIncorrectInputFrameId, true);
    }

    protected override void resetCurrentMatch() {
        base.resetCurrentMatch();

        // Reset lockstep
        shouldLockStep = false;
        NetworkDoctor.Instance.Reset();
    }

    protected void OnDestroy() {
        Debug.LogWarning(String.Format("OnlineMapController.OnDestroy#1"));
        if (null != wsCancellationTokenSource) {
            if (!wsCancellationTokenSource.IsCancellationRequested) {
                Debug.LogWarning(String.Format("OnlineMapController.OnDestroy#1.5, cancelling ws session"));
                wsCancellationTokenSource.Cancel();
            }
            wsCancellationTokenSource.Dispose();
        }

        if (null != wsTask) {
            wsTask.Wait();
            wsTask.Dispose(); // frontend of this project targets ".NET Standard 2.1", thus calling "Task.Dispose()" explicitly, reference, reference https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.dispose?view=net-7.0
        }

        if (null != udpTask) {
            udpTask.Wait();
            udpTask.Dispose(); // frontend of this project targets ".NET Standard 2.1", thus calling "Task.Dispose()" explicitly, reference, reference https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.dispose?view=net-7.0
        }

        Debug.LogWarning(String.Format("OnlineMapController.OnDestroy#2"));
    }

    void OnApplicationQuit() {
        Debug.LogWarning(String.Format("OnlineMapController.OnApplicationQuit"));
    }

}
