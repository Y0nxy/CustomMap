using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CustomMap
{
    public class PassTrigger : MonoBehaviour
    {
        
        private void OnTriggerEnter(Collision other)
        {
            Character c = other.gameObject.GetComponent<Character>();
            if (c != null)
            {
                Plugin.Log.LogInfo("Level Beat by "+c.name);
                //do stuff
            }
        }
    }
}
