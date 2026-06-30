using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CustomMap
{//Check if this works!
    public class PassTrigger : MonoBehaviour
    {
        GameObject FallTriggerGameObject = null;
        private void OnTriggerEnter(Collider other)
        {
            RigCreatorCollider c = other.gameObject.GetComponent<RigCreatorCollider>();
            //Plugin.Log.LogInfo("PassTrigger Triggered!");
            if (c != null)
            {
                if (FallTriggerGameObject == null)
                     FallTriggerGameObject = GameObject.Find("FallTrigger");
                if (FallTriggerGameObject == null)
                {
                    Plugin.Log.LogError("FallTrigger not found in PassTrigger!");
                    return;
                }
                FallTriggerGameObject.GetComponent<FallTrigger>().isLevelBeat = true;
                Plugin.Log.LogInfo("PassTrigger Reached!");
                Destroy(this);
                //do stuff
            }
        }
    }
}
