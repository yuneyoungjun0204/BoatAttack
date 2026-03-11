using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Obi.Samples
{
    public class BoatFinishLine : MonoBehaviour
    {

        private void OnTriggerEnter(Collider c)
        {
            var gameController = c.GetComponentInParent<BoatGameController>();
            if (gameController != null)
                gameController.Finish();
        }
    }
}
