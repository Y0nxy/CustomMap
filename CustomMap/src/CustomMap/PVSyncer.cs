using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace ScoutFallFlat
{
    public class PVSyncer : MonoBehaviourPunCallbacks
    {
        private const int ROOT_VIEW_ID = 999;

        private List<GameObject> modObjects = new List<GameObject>();
        private Dictionary<int, int> allocatedViewIDs = new Dictionary<int, int>();

        private void Awake()
        {
            Debug.Log($"[PVSyncer] Awake — IsMasterClient: {PhotonNetwork.IsMasterClient}, IsConnected: {PhotonNetwork.IsConnected}, InRoom: {PhotonNetwork.InRoom}");

            PhotonView rootPV = gameObject.GetComponent<PhotonView>() ?? gameObject.AddComponent<PhotonView>();
            Debug.Log($"[PVSyncer] Root PhotonView {(rootPV == null ? "FAILED TO CREATE" : "created/found")}");

            bool viewIDSet = rootPV.ViewID == ROOT_VIEW_ID;
            rootPV.ViewID = ROOT_VIEW_ID;
            Debug.Log($"[PVSyncer] Root ViewID set to {ROOT_VIEW_ID} — was already set: {viewIDSet}, actual ViewID after: {rootPV.ViewID}");

            CollectObjects();

            if (PhotonNetwork.IsMasterClient)
            {
                Debug.Log("[PVSyncer] We are master — allocating ViewIDs.");
                AllocateAllViewIDs();
            }
            else
            {
                Debug.Log($"[PVSyncer] We are client — sending RPC_RequestViewIDs to master. Our ActorNumber: {PhotonNetwork.LocalPlayer.ActorNumber}");

                if (photonView == null)
                {
                    Debug.LogError("[PVSyncer] photonView is NULL — RPC cannot be sent! Make sure ViewID 999 was set correctly.");
                    return;
                }

                photonView.RPC("RPC_RequestViewIDs", RpcTarget.MasterClient, PhotonNetwork.LocalPlayer.ActorNumber);
                Debug.Log("[PVSyncer] RPC_RequestViewIDs sent.");
            }
        }

        private void CollectObjects()
        {
            modObjects.Clear();

            Rigidbody[] rigidbodies = GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody rb in rigidbodies)
                modObjects.Add(rb.gameObject);

            Tilemap[] tilemaps = GetComponentsInChildren<Tilemap>(true);
            foreach (Tilemap tm in tilemaps)
            {
                if (!modObjects.Contains(tm.gameObject))
                    modObjects.Add(tm.gameObject);
            }

            Debug.Log($"[PVSyncer] CollectObjects — found {modObjects.Count} objects ({rigidbodies.Length} rigidbodies, {tilemaps.Length} tilemaps).");
            for (int i = 0; i < modObjects.Count; i++)
                Debug.Log($"[PVSyncer]   [{i}] {modObjects[i].name}");
        }

        private void AllocateAllViewIDs()
        {
            Debug.Log($"[PVSyncer] AllocateAllViewIDs — allocating for {modObjects.Count} objects.");

            for (int i = 0; i < modObjects.Count; i++)
            {
                if (!allocatedViewIDs.ContainsKey(i))
                {
                    int id = PhotonNetwork.AllocateViewID(true);
                    allocatedViewIDs[i] = id;
                    Debug.Log($"[PVSyncer]   [{i}] {modObjects[i].name} → ViewID {id}");
                }
            }

            ApplyViewIDs(allocatedViewIDs);
        }

        [PunRPC]
        private void RPC_RequestViewIDs(int requesterActorNumber)
        {
            Debug.Log($"[PVSyncer] RPC_RequestViewIDs received from ActorNumber {requesterActorNumber}. IsMaster: {PhotonNetwork.IsMasterClient}, allocated count: {allocatedViewIDs.Count}");

            if (!PhotonNetwork.IsMasterClient)
            {
                Debug.LogWarning("[PVSyncer] RPC_RequestViewIDs received but we are not master — ignoring.");
                return;
            }

            if (allocatedViewIDs.Count == 0)
            {
                Debug.LogError("[PVSyncer] allocatedViewIDs is empty — master may not have finished allocating yet!");
                return;
            }

            int[] indices = new int[allocatedViewIDs.Count];
            int[] viewIDs = new int[allocatedViewIDs.Count];
            int i = 0;
            foreach (var kvp in allocatedViewIDs)
            {
                indices[i] = kvp.Key;
                viewIDs[i] = kvp.Value;
                i++;
            }

            Photon.Realtime.Player target = PhotonNetwork.CurrentRoom.GetPlayer(requesterActorNumber);
            if (target == null)
            {
                Debug.LogError($"[PVSyncer] Could not find player with ActorNumber {requesterActorNumber} in room!");
                return;
            }

            Debug.Log($"[PVSyncer] Sending RPC_ReceiveViewIDs to ActorNumber {requesterActorNumber} with {indices.Length} entries.");
            photonView.RPC("RPC_ReceiveViewIDs", target, indices, viewIDs);
        }

        [PunRPC]
        private void RPC_ReceiveViewIDs(int[] indices, int[] viewIDs)
        {
            Debug.Log($"[PVSyncer] RPC_ReceiveViewIDs received — {indices.Length} entries.");

            var receivedMap = new Dictionary<int, int>();
            for (int i = 0; i < indices.Length; i++)
            {
                receivedMap[indices[i]] = viewIDs[i];
                Debug.Log($"[PVSyncer]   [{indices[i]}] → ViewID {viewIDs[i]}");
            }

            ApplyViewIDs(receivedMap);
        }

        private void ApplyViewIDs(Dictionary<int, int> map)
        {
            Debug.Log($"[PVSyncer] ApplyViewIDs — applying {map.Count} ViewIDs.");

            foreach (var kvp in map)
            {
                int index = kvp.Key;
                int viewID = kvp.Value;

                if (index >= modObjects.Count)
                {
                    Debug.LogWarning($"[PVSyncer] ViewID for index {index} but only {modObjects.Count} objects exist — skipping.");
                    continue;
                }

                GameObject obj = modObjects[index];
                bool hasRigidbody = obj.GetComponent<Rigidbody>() != null;
                bool hasTilemap = obj.GetComponent<Tilemap>() != null;

                Debug.Log($"[PVSyncer]   [{index}] {obj.name} — Rigidbody: {hasRigidbody}, Tilemap: {hasTilemap}");

                PhotonView pv = obj.GetComponent<PhotonView>() ?? obj.AddComponent<PhotonView>();
                pv.OwnershipTransfer = OwnershipOption.Fixed;

                var observed = new List<Component>();

                if (hasRigidbody)
                {
                    PhotonTransformView tv = obj.GetComponent<PhotonTransformView>() ?? obj.AddComponent<PhotonTransformView>();
                    tv.m_SynchronizePosition = true;
                    tv.m_SynchronizeRotation = true;
                    tv.m_SynchronizeScale = false;

                    PhotonRigidbodyView rv = obj.GetComponent<PhotonRigidbodyView>() ?? obj.AddComponent<PhotonRigidbodyView>();
                    rv.m_SynchronizeVelocity = true;
                    rv.m_SynchronizeAngularVelocity = true;

                    observed.Add(tv);
                    observed.Add(rv);
                }

                if (hasTilemap)
                {
                    Destroy(obj.GetComponent<Tilemap>());
                    Debug.Log($"[PVSyncer] Tilemap removed from '{obj.name}'");

                    TilemapRenderer tmr = obj.GetComponent<TilemapRenderer>();
                    if (tmr != null)
                    {
                        Destroy(tmr);
                        Debug.Log($"[PVSyncer] TilemapRenderer removed from '{obj.name}'");
                    }
                }

                pv.ObservedComponents = observed;
                pv.Synchronization = observed.Count > 0 ? ViewSynchronization.UnreliableOnChange : ViewSynchronization.Off;
                pv.ViewID = viewID;

                Debug.Log($"[PVSyncer] PhotonView on '{obj.name}' — ViewID: {pv.ViewID}, ObservedComponents: {pv.ObservedComponents.Count}, Sync: {pv.Synchronization}");
            }
        }

        public override void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
        {
            Debug.Log($"[PVSyncer] OnMasterClientSwitched — new master: {newMasterClient.ActorNumber}, IsLocal: {newMasterClient.IsLocal}");

            if (newMasterClient.IsLocal)
            {
                allocatedViewIDs.Clear();
                for (int i = 0; i < modObjects.Count; i++)
                {
                    PhotonView pv = modObjects[i].GetComponent<PhotonView>();
                    if (pv != null)
                        allocatedViewIDs[i] = pv.ViewID;
                    else
                        Debug.LogWarning($"[PVSyncer] OnMasterClientSwitched — no PhotonView on [{i}] {modObjects[i].name}");
                }
                Debug.Log($"[PVSyncer] Rebuilt allocatedViewIDs with {allocatedViewIDs.Count} entries.");
            }
        }
    }
}