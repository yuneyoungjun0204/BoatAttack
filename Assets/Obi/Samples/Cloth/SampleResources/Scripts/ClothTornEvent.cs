using UnityEngine;

namespace Obi.Samples
{
    [RequireComponent(typeof(ObiTearableCloth))]
    public class ClothTornEvent : MonoBehaviour
    {

        ObiTearableCloth cloth;
        public GameObject thingToSpawn;

        void OnEnable () {
            cloth = GetComponent<ObiTearableCloth>();
            cloth.OnClothTorn += Cloth_OnClothTorn;
        }

        void OnDisable(){
            cloth.OnClothTorn -= Cloth_OnClothTorn;
        }

        private void Cloth_OnClothTorn(ObiTearableCloth c, ObiTearableCloth.ObiClothTornEventArgs tearInfo)
        {
            if (thingToSpawn != null)
                GameObject.Instantiate(thingToSpawn, cloth.solver.positions[cloth.solverIndices[tearInfo.particleIndex]], Quaternion.identity);
        }
    }
}