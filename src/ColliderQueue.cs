using System.Collections.Generic;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Dokladanie MeshColliderow po jednym na klatke.
    ///
    /// Dlaczego kolejka, a nie od razu przy budowie kafla: przypisanie siatki do
    /// MeshCollider uruchamia "cooking" PhysX, ktory dla kilkunastu tysiecy
    /// trojkatow kosztuje kilkanascie do kilkudziesieciu milisekund NA GLOWNYM
    /// WATKU. Zrobione hurtem przy wjezdzie w miasto daje sekundowa zwieche.
    /// Rozlozone po jednym obiekcie na klatke jest niezauwazalne.
    ///
    /// Watku roboczego tu nie ma swiadomie: Physics.BakeMesh dalby sie wywolac
    /// w tle, ale wymaga juz istniejacej siatki Unity, wiec i tak trzeba czekac
    /// na glowny watek. Zysk nie usprawiedliwia ryzyka.
    /// </summary>
    public static class ColliderQueue
    {
        private class Item
        {
            public GameObject Go;
            public bool Convex;
        }

        private static readonly Queue<Item> pending = new Queue<Item>();

        public static int Pending { get { return pending.Count; } }

        public static void Request(GameObject go)
        {
            if (go == null) return;
            pending.Enqueue(new Item { Go = go, Convex = false });
        }

        /// <summary>Wolane raz na klatke. Zwraca true, jesli cos zostalo zrobione.</summary>
        public static bool Step()
        {
            while (pending.Count > 0)
            {
                Item it = pending.Dequeue();

                // Kafel mogl zostac wyladowany, zanim doszedl do kolejki.
                if (it.Go == null) continue;
                if (it.Go.GetComponent<MeshCollider>() != null) continue;

                var mf = it.Go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mc = it.Go.AddComponent<MeshCollider>();

                // Siatki generujemy sami i sa juz czyste: nie ma zdegenerowanych
                // trojkatow ani zdublowanych wierzcholkow do sklejenia. Wylaczenie
                // czyszczenia i spawania skraca cooking o wiekszosc jego kosztu.
                mc.cookingOptions = MeshColliderCookingOptions.UseFastMidphase;
                mc.convex = it.Convex;
                mc.sharedMesh = mf.sharedMesh;
                return true;
            }
            return false;
        }

        /// <summary>Usuwa collider, gdy kafel oddalil sie poza zasieg fizyki.
        /// Sama siatka zostaje - wraca tanio, bo cooking jest cache'owany.</summary>
        public static void Drop(GameObject go)
        {
            if (go == null) return;
            var mc = go.GetComponent<MeshCollider>();
            if (mc != null) Object.Destroy(mc);
        }

        public static void Clear()
        {
            pending.Clear();
        }
    }
}
