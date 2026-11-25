using UnityEngine;

namespace CPE494
{
    /// <summary>
    /// GEM STORE SHIM (our file, not part of the teammate's MG400 package).
    /// Their TeleopStation.cs (line 457) calls <c>FallenGemSpawner.Instance?.UpdateVacuum(...)</c>, a class from their
    /// Compatibility folder, which we do not import (it belongs to their old gem-pickup demo). This stand-in only lets the
    /// imported Runtime compile unchanged: Instance is always null, so the call does nothing.
    /// Remove this file if the teammate drops that line or guards it (see Docs/MG400_Integration_Plan.md).
    /// </summary>
    internal sealed class FallenGemSpawner
    {
        public static FallenGemSpawner Instance => null;

        public void UpdateVacuum(Vector3 tcpWorldPosition, bool vacuumOn)
        {
        }
    }
}
