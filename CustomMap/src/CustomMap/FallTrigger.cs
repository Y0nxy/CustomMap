using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CustomMap
{
    public class FallTrigger : MonoBehaviour
    {
        Transform Checkpoint = null!;
        private void OnTriggerEnter(Collision other)
        {
            GameObject go = other.gameObject;
            var Log = Plugin.Log;
            Log.LogInfo("Entered " + other.gameObject.name);
            if (go.layer != LayerMask.NameToLayer("Character"))
            {
                Log.LogInfo("Not Character, Skipping..");
                return;
            }
            Log.LogInfo("is CharacterLayer!");
            if (go.GetComponent<Character>() != null) //if Player
            {
                Log.LogInfo("isPlayer");
                if (!go.GetComponent<Character>().IsLocal) return; //not local player
                Character localplayer = go.GetComponent<Character>();
                Plugin.Log.LogInfo($"TP Player {localplayer.name} to Checkpoint");
                localplayer.data.sinceGrounded = 0f;
                localplayer.data.sinceJump = 0f;
                foreach (Rigidbody rb in localplayer.GetComponentsInChildren<Rigidbody>())
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                if (Checkpoint != null)
                {
                    localplayer.WarpPlayerRPC(Checkpoint.position, false);
                    return;
                }
                StartCoroutine(Plugin.WarpToSpawnWhenReady());
            }
            if (go.GetComponent<RigidBodyStandable>() != null)
            {
                RigidBodyStandable rbs = go.GetComponent<RigidBodyStandable>();
                Rigidbody rb = go.GetComponent<Rigidbody>();
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                go.transform.position = rbs.originalPos;
                go.transform.rotation = rbs.originalRot;
            }
        }
    }
}
